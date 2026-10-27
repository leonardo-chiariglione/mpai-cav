using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using AIF.Controller;
using AIF.SharedStorage;
using AIF.Store;

namespace Mpai.Aif.Tests;

// THE ACCESS AN AIM DECLARES (AIM Metadata, AccessSources) IS WHAT IT GETS (AIF V3.0, Storage 6). Data is identified by the
// URI it is obtained from and pinned by its SHA-256; MPAI does not classify it. The Controller obtains it, checks it, and gives
// the AIM what its entries name and nothing else; a Module is refused when a required entry cannot be obtained.
[Trait("Group", "Fast")]
[Trait("Blocks", "No")]
public class AccessSourcesTests : IDisposable
{
    private readonly string work = Path.Combine(Path.GetTempPath(), "mpai-access-" + Guid.NewGuid().ToString("N"));
    private readonly AccessStore store;

    public AccessSourcesTests()
    {
        Directory.CreateDirectory(work);
        store = new AccessStore(Path.Combine(work, "access"));
    }

    public void Dispose() { try { Directory.Delete(work, true); } catch { } }

    private static readonly AccessTerms TestTerms = new("test licence", "https://example.org/terms", false);
    private static readonly StorageHolder Me = new("M", "aim");
    private static readonly StorageHolder Other = new("M", "other");

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    // A file in the folder of this test, and its file: URI.
    private (string Uri, string Sha) File1(string name, string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        var path = Path.Combine(work, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        return (new Uri(path).AbsoluteUri, Sha(data));
    }

    // A set: a folder with its manifest. Returns the URI of the folder (ending in a slash) and the SHA-256 of the manifest.
    private (string Uri, string Sha) Set(string folder, params (string Key, string Text)[] items)
    {
        var manifest = new JsonObject();
        var list = new JsonObject();
        foreach (var (key, text) in items)
        {
            var data = Encoding.UTF8.GetBytes(text);
            var path = Path.Combine(work, folder, key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
            list[key] = Sha(data);
        }
        manifest["Items"] = list;
        var bytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        File.WriteAllBytes(Path.Combine(work, folder, "manifest.json"), bytes);
        return (new Uri(Path.Combine(work, folder) + Path.DirectorySeparatorChar).AbsoluteUri, Sha(bytes));
    }

    private static AccessEntry Entry(string name, (string Uri, string Sha) at, bool optional = false, params string[] prefixes) =>
        new(name, at.Uri, at.Sha, prefixes, optional, Terms: TestTerms);

    [Fact]
    public void AFileIsObtainedCheckedAndReadByItsKey()
    {
        var file = File1("model.bin", "weights");
        var entry = Entry("model", file);
        var fetcher = new AccessFetcher(store);
        Assert.Equal(AccessOutcome.OK, fetcher.Ensure(entry, "aim", out _));

        var access = new BoundAccess(store.As(Me), [entry], fetcher, "aim");
        Assert.Equal(AccessOutcome.OK, access.Get(file.Uri, "", out var data));            // a file's Key is its name; an empty Key gives it
        Assert.Equal("weights", Encoding.UTF8.GetString(data));
        Assert.Equal(AccessOutcome.OK, access.Get(file.Uri, "model.bin", out _));
        Assert.Equal(AccessOutcome.NotAuthorised, access.Get(file.Uri, "other.bin", out _));
        Assert.Equal([file.Uri], access.SourcesOf("model"));
        Assert.Empty(access.SourcesOf("faces"));
    }

    [Fact]
    public void DataThatIsNotWhatTheMetadataNamesIsRefused()
    {
        var file = File1("model.bin", "weights");
        var entry = new AccessEntry("model", file.Uri, new string('0', 64), [], false);
        Assert.Equal(AccessOutcome.Refused, new AccessFetcher(store).Ensure(entry, "aim", out var why));
        Assert.Contains("not the data its Metadata names", why);
        Assert.Equal(-1, store.Version(file.Uri));                                          // nothing was kept
    }

    [Fact]
    public void AnAimThatDeclaresNothingReadsNothing()
    {
        var file = File1("model.bin", "weights");
        new AccessFetcher(store).Ensure(Entry("model", file), "aim", out _);
        var access = new BoundAccess(store.As(Other), []);                                 // not a reader of what the Controller obtained for another
        Assert.Equal(AccessOutcome.NotAuthorised, access.Get(file.Uri, "model.bin", out _));
        Assert.Empty(access.List(file.Uri));
    }

    // THE POINT OF A SET: the manifest is pinned, the items are obtained when read, each checked against the manifest; a set
    // can grow with its manifest, and the Metadata pins the manifest only.
    [Fact]
    public void ASetIsReadItemByItemAndEachItemIsChecked()
    {
        var set = Set("faces", ("lfw/ada.png", "ada"), ("lfw/bob.png", "bob"), ("other/cy.png", "cy"));
        var entry = Entry("faces", set);
        var fetcher = new AccessFetcher(store);
        Assert.Equal(AccessOutcome.OK, fetcher.Ensure(entry, "aim", out _));

        var access = new BoundAccess(store.As(Me), [entry], fetcher, "aim");
        Assert.Equal(["lfw/ada.png", "lfw/bob.png", "other/cy.png"], access.List(set.Uri));
        Assert.Equal(["lfw/bob.png"], access.List(set.Uri, "lfw/b"));
        Assert.Equal(AccessOutcome.OK, access.Get(set.Uri, "lfw/ada.png", out var ada));    // obtained now, on first read
        Assert.Equal("ada", Encoding.UTF8.GetString(ada));
        Assert.Equal(AccessOutcome.NotFound, access.Get(set.Uri, "lfw/none.png", out _));   // not in the manifest

        File.WriteAllText(Path.Combine(work, "faces", "other", "cy.png"), "tampered");        // an item that is not what its manifest says
        Assert.NotEqual(AccessOutcome.OK, access.Get(set.Uri, "other/cy.png", out _));
    }

    [Fact]
    public void KeyPrefixesNarrowTheKeys()
    {
        var set = Set("faces", ("lfw/ada.png", "ada"), ("other/cy.png", "cy"));
        var entry = Entry("faces", set, false, "lfw/");
        var fetcher = new AccessFetcher(store);
        fetcher.Ensure(entry, "aim", out _);
        var access = new BoundAccess(store.As(Me), [entry], fetcher, "aim");
        Assert.Equal(AccessOutcome.OK, access.Get(set.Uri, "lfw/ada.png", out _));
        Assert.Equal(AccessOutcome.NotAuthorised, access.Get(set.Uri, "other/cy.png", out _));
        Assert.Equal(["lfw/ada.png"], access.List(set.Uri));
    }

    // THE MODULE IS REFUSED where a required entry cannot be obtained; an optional one does not stop it.
    [Fact]
    public void AModuleIsRefusedWhenARequiredAccessCannotBeObtained()
    {
        var file = File1("model.bin", "weights");
        var amds = Path.Combine(work, "amds");
        Directory.CreateDirectory(amds);
        foreach (var f in Directory.EnumerateFiles(ControllerTests.Amds, "*.json")) File.Copy(f, Path.Combine(amds, Path.GetFileName(f)));
        var l3File = Path.Combine(amds, "1TST-NOP-V1.0-I01.json");

        void Declare(string uri, string sha, bool optional)
        {
            var l3 = JsonNode.Parse(File.ReadAllText(Path.Combine(ControllerTests.Amds, "1TST-NOP-V1.0-I01.json")))!.AsObject();
            var entry = new JsonObject { ["Name"] = "model", ["Uri"] = uri, ["Sha256"] = sha, ["Terms"] = new JsonObject { ["Licence"] = "test", ["Uri"] = "https://a.example/terms" } };
            if (optional) entry["IsOptional"] = true;
            l3["AccessSources"] = new JsonArray(entry);
            File.WriteAllText(l3File, l3.ToJsonString());
        }

        UserAgent Agent(string access)
        {
            var amdStore = new AmdStore(amds); amdStore.Scan();
            var ua = new UserAgent(amdStore) { Access = new AccessStore(access) };
            ua.MPAI_AIFU_Controller_Initialize();
            return ua;
        }

        Declare(file.Uri, file.Sha, false);
        Assert.Equal(AifError.OK, Agent(Path.Combine(work, "a1")).MPAI_AIFU_MODULE_Start("1TST-NOP-V1.0-I01", new TestAims(), AimSettings.Empty, out _));

        Declare(file.Uri, new string('0', 64), false);                                      // not the data it names
        var refused = Assert.ThrowsAny<Exception>(() => Agent(Path.Combine(work, "a2")).MPAI_AIFU_MODULE_Start("1TST-NOP-V1.0-I01", new TestAims(), AimSettings.Empty, out _));
        Assert.Contains("there is no such Source", refused.Message);

        Declare(new Uri(Path.Combine(work, "absent.bin")).AbsoluteUri, file.Sha, false);   // nothing there
        Assert.ThrowsAny<Exception>(() => Agent(Path.Combine(work, "a3")).MPAI_AIFU_MODULE_Start("1TST-NOP-V1.0-I01", new TestAims(), AimSettings.Empty, out _));

        Declare(new Uri(Path.Combine(work, "absent.bin")).AbsoluteUri, file.Sha, true);    // optional: the Module starts
        Assert.Equal(AifError.OK, Agent(Path.Combine(work, "a4")).MPAI_AIFU_MODULE_Start("1TST-NOP-V1.0-I01", new TestAims(), AimSettings.Empty, out _));
    }

    // THE HASH IS THE IDENTITY: a mirror holding the same bytes serves when the first place does not, and a place holding
    // other bytes is never trusted.
    [Fact]
    public void AMirrorServesWhenTheUriDoesNotAndOnlyIfItHoldsTheSameBytes()
    {
        var good = File1("mirror/model.bin", "weights");
        var absent = new Uri(Path.Combine(work, "absent", "model.bin")).AbsoluteUri;
        var entry = new AccessEntry("model", absent, good.Sha, [], false, Mirrors: [good.Uri]);
        Assert.Equal(AccessOutcome.OK, new AccessFetcher(store).Ensure(entry, "aim", out _));
        Assert.Equal(AccessOutcome.OK, new BoundAccess(store.As(Me), [entry], null, "aim").Get(absent, "", out var data));
        Assert.Equal("weights", Encoding.UTF8.GetString(data));

        var bad = File1("other/model.bin", "something else");
        var refused = new AccessEntry("model", new Uri(Path.Combine(work, "absent2", "model.bin")).AbsoluteUri, Sha(Encoding.UTF8.GetBytes("never obtained")), [], false, Mirrors: [bad.Uri]);
        Assert.Equal(AccessOutcome.Refused, new AccessFetcher(store).Ensure(refused, "aim", out var why));
        Assert.Contains("not the data its Metadata names", why);
    }

    // DATA THAT IS ONLY VALID WITH OTHER DATA: the Module is refused where the partner is not declared or cannot be obtained.
    [Fact]
    public void DataValidOnlyWithOtherDataIsNotGivenWithoutIt()
    {
        var model = File1("model.bin", "weights");
        var features = File1("features.bin", "vectors");
        var absent = new Uri(Path.Combine(work, "absent.bin")).AbsoluteUri;

        var paired = new[] { new AccessEntry("model", model.Uri, model.Sha, [], false, Terms: TestTerms), new AccessEntry("features", features.Uri, features.Sha, [], false, Terms: TestTerms, ValidWith: [model.Sha]) };
        Assert.Equal("", AccessEntry.Check(paired));
        Assert.Contains("states no Terms", AccessEntry.Check([paired[0] with { Terms = null }]));
        Assert.Contains("states no Terms", AccessEntry.Check([paired[0] with { Terms = new AccessTerms("", "", false) }]));
        Assert.Contains("which this AIM does not declare", AccessEntry.Check([paired[1]]));
        Assert.Contains("used by more than one", AccessEntry.Check([paired[0], paired[0] with { Uri = absent }]));
        Assert.Contains("two different SHA-256", AccessEntry.Check([paired[0], paired[0] with { Name = "again", Sha256 = new string('1', 64) }]));
        Assert.Contains("not of the kind", AccessEntry.Check([paired[0] with { Mirrors = [model.Uri + "/"] }]));

        var amds = Path.Combine(work, "amds");
        Directory.CreateDirectory(amds);
        foreach (var f in Directory.EnumerateFiles(ControllerTests.Amds, "*.json")) File.Copy(f, Path.Combine(amds, Path.GetFileName(f)));
        void Declare(params AccessEntry[] entries)
        {
            var l3 = JsonNode.Parse(File.ReadAllText(Path.Combine(ControllerTests.Amds, "1TST-NOP-V1.0-I01.json")))!.AsObject();
            var list = new JsonArray();
            foreach (var e in entries)
            {
                var o = new JsonObject { ["Name"] = e.Name, ["Uri"] = e.Uri, ["Sha256"] = e.Sha256, ["Terms"] = new JsonObject { ["Licence"] = "test", ["Uri"] = "https://a.example/terms" } };
                if (e.IsOptional) o["IsOptional"] = true;
                if (e.ValidWith is { Count: > 0 }) o["ValidWith"] = new JsonArray(e.ValidWith.Select(h => (JsonNode?)h).ToArray());
                list.Add(o);
            }
            l3["AccessSources"] = list;
            File.WriteAllText(Path.Combine(amds, "1TST-NOP-V1.0-I01.json"), l3.ToJsonString());
        }
        AifError Start(string access)
        {
            var amdStore = new AmdStore(amds); amdStore.Scan();
            var ua = new UserAgent(amdStore) { Access = new AccessStore(Path.Combine(work, access)) };
            ua.MPAI_AIFU_Controller_Initialize();
            return ua.MPAI_AIFU_MODULE_Start("1TST-NOP-V1.0-I01", new TestAims(), AimSettings.Empty, out _);
        }

        Declare(paired);
        Assert.Equal(AifError.OK, Start("b1"));

        Declare(paired[1]);                                                                       // the partner is not declared
        Assert.Contains("declares its Access wrongly", Assert.ThrowsAny<Exception>(() => Start("b2")).Message);

        Declare(paired[0] with { Uri = absent, IsOptional = true }, paired[1]);                  // the partner cannot be obtained
        Assert.Contains("valid only with data", Assert.ThrowsAny<Exception>(() => Start("b3")).Message);

        Declare(paired[0] with { Uri = absent, IsOptional = true }, paired[1] with { IsOptional = true });   // both optional: the Module starts, the data is not given
        Assert.Equal(AifError.OK, Start("b4"));
    }

    [Fact]
    public void TheTermsAndTheMirrorsAreReadFromTheMetadata()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {"AccessSources":[{"Name":"faces","Uri":"https://a.example/f.bin","Sha256":"%H%","Mirrors":["https://b.example/f.bin"],
              "ValidWith":["%H%"],"Terms":{"Licence":"CC-BY-4.0","Uri":"https://c.example/terms","PersonalData":true}}]}
            """.Replace("%H%", new string('A', 64)));
        var entry = Assert.Single(AccessEntry.ReadFrom(doc.RootElement));
        Assert.Equal(["https://a.example/f.bin", "https://b.example/f.bin"], entry.Locations);
        Assert.Equal(new AccessTerms("CC-BY-4.0", "https://c.example/terms", true), entry.Terms);
        Assert.Single(entry.ValidWith!);
    }

    // A LARGE ITEM IS READ FROM WHERE IT IS KEPT, not copied into memory.
    [Fact]
    public void AnItemIsReadAsAStream()
    {
        var file = File1("big.bin", new string('x', 100_000));
        var entry = Entry("big", file);
        var fetcher = new AccessFetcher(store);
        fetcher.Ensure(entry, "aim", out _);
        var access = new BoundAccess(store.As(Me), [entry], fetcher, "aim");
        Assert.Equal(AccessOutcome.OK, access.Open(file.Uri, "", out var stream));
        using (stream) { Assert.IsType<FileStream>(stream); Assert.Equal(100_000, stream.Length); }
        Assert.Equal(AccessOutcome.NotAuthorised, access.Open(file.Uri, "other", out _));
        Assert.Equal(AccessOutcome.NotAuthorised, new BoundAccess(store.As(Other), []).Open(file.Uri, "big.bin", out _));
    }

    // WHAT IS ALREADY IN ACCESS IS NOT DOWNLOADED AGAIN: the same bytes under another Uri are the data wanted, and are found
    // by their hash; bytes that no longer match what was recorded are not trusted.
    [Fact]
    public void DataAlreadyInAccessIsNotDownloadedAgain()
    {
        var first = File1("one/model.bin", "weights");
        var fetcher = new AccessFetcher(store);
        Assert.Equal(AccessOutcome.OK, fetcher.Ensure(Entry("model", first), "aim", out _));
        File.Delete(Path.Combine(work, "one", "model.bin"));                                     // nothing to download from any more

        var elsewhere = new Uri(Path.Combine(work, "two", "model.bin")).AbsoluteUri;             // another AIM, another Uri, the same bytes
        var second = new AccessEntry("model", elsewhere, first.Sha, [], false);
        Assert.Equal(AccessOutcome.OK, fetcher.Ensure(second, "aim", out var why));
        Assert.Equal("", why);
        Assert.Equal(AccessOutcome.OK, new BoundAccess(store.As(Me), [second], null, "aim").Get(elsewhere, "", out var data));
        Assert.Equal("weights", Encoding.UTF8.GetString(data));

        // the first copy is damaged: a third Uri cannot be served from it
        File.WriteAllText(Path.Combine(work, "access", AccessStore.DiskName(first.Uri), AccessStore.DiskName("model.bin") + ".data"), "damaged");
        File.WriteAllText(Path.Combine(work, "access", AccessStore.DiskName(elsewhere), AccessStore.DiskName("model.bin") + ".data"), "damaged");
        var third = new AccessEntry("model", new Uri(Path.Combine(work, "three", "model.bin")).AbsoluteUri, first.Sha, [], false);
        Assert.NotEqual(AccessOutcome.OK, fetcher.Ensure(third, "aim", out _));
    }

    // WHO MAY READ A DATUM OF ACCESS IS DECIDED AS IT IS IN SHARED STORAGE: by the rules of the datum. What the Controller
    // obtains for an AIM is readable by that AIM, by whoever else it names, and by the central control; data written by a
    // Provider or the User without readers is readable by every holder.
    [Fact]
    public void WhoMayReadADatumOfAccessIsDecidedByItsRules()
    {
        var file = File1("model.bin", "weights");
        var entry = Entry("model", file);
        var fetcher = new AccessFetcher(store);
        Assert.Equal(AccessOutcome.OK, fetcher.Ensure(entry, "aim", out _));

        Assert.Equal(AccessOutcome.OK, store.As(Me).Get(file.Uri, "model.bin", out _));
        Assert.Equal(AccessOutcome.NotAuthorised, store.As(Other).Get(file.Uri, "model.bin", out _));      // not among the readers
        Assert.Equal(AccessOutcome.NotAuthorised, store.As(Other).Open(file.Uri, "model.bin", out _));
        Assert.Empty(store.As(Other).List(file.Uri));
        Assert.Equal(["model.bin"], store.As(Me).List(file.Uri));
        Assert.Equal(AccessOutcome.OK, store.As(new StorageHolder("M", "control"), new StorageHolder("M", "control")).Get(file.Uri, "model.bin", out _));   // the central control reads everything
        Assert.Equal(AccessOutcome.NotFound, store.As(Me).Get(file.Uri, "none", out _));

        // another AIM asking for the same data is added to its readers, and nothing is downloaded again
        File.Delete(Path.Combine(work, "model.bin"));
        Assert.Equal(AccessOutcome.OK, fetcher.Ensure(entry, "other", out _));
        Assert.Equal(AccessOutcome.OK, store.As(Other).Get(file.Uri, "model.bin", out _));
        Assert.Equal(AccessOutcome.OK, store.As(Me).Get(file.Uri, "model.bin", out _));
        Assert.Equal(["aim", "other"], store.Trace(file.Uri, "model.bin", out var trace) == AccessOutcome.OK ? trace!.Readers : null);
        Assert.Equal(AccessFetcher.Writer, trace!.Writer);

        // the Controller writes with the readers it names, or with none: every holder reads
        store.Create("maps");
        store.Put("maps", "private", [1], "Data", ["aim"]);
        store.Put("maps", "public", [2]);
        Assert.Equal(AccessOutcome.OK, store.As(Me).Get("maps", "private", out _));
        Assert.Equal(AccessOutcome.NotAuthorised, store.As(Other).Get("maps", "private", out _));
        Assert.Equal(AccessOutcome.OK, store.As(Other).Get("maps", "public", out _));
        Assert.Equal(["public"], store.As(Other).List("maps"));
    }

    // AN IMPLEMENTATION THAT RUNS A PROGRAM which reads files is given the file where Access keeps the data, under the
    // setting the entry names; the rights of the datum apply.
    [Fact]
    public void TheSettingNamedByAnEntryIsGivenTheFileOfAccess()
    {
        var file = File1("model.bin", "weights");
        var entry = Entry("asr-model", file) with { Setting = "ModelPath" };
        new AccessFetcher(store).Ensure(entry, "aim", out _);
        var settings = new Dictionary<string, string> { ["ModelPath"] = @"Models\old.bin", ["Threads"] = "8" };

        var mine = AccessEntry.Overlay(settings, [entry], new BoundAccess(store.As(Me), [entry], null, "aim"));
        Assert.Equal("8", mine["Threads"]);
        Assert.NotEqual(settings["ModelPath"], mine["ModelPath"]);
        Assert.Equal("weights", File.ReadAllText(mine["ModelPath"]));
        Assert.True(mine["ModelPath"].Length - Path.Combine(work, "access").Length < 80, "the path must stay short enough for any program to open it");

        Assert.Equal(settings["ModelPath"], AccessEntry.Overlay(settings, [entry], new BoundAccess(store.As(Other), [entry], null, "other"))["ModelPath"]);   // not a reader: nothing is given
        Assert.Same(settings, AccessEntry.Overlay(settings, [Entry("x", file)], store.As(Me)));                       // no Setting named
    }

    // WHAT THE L3 OF ASR AND TTS DECLARE is consistent, and names the settings the programs read.
    [Theory]
    [InlineData("1MMC-ASR-V2.5-I01.json", "ModelPath")]
    [InlineData("1MMC-TTS-V2.5-I01.json", "VoiceModel")]
    [InlineData("1MMC-TTS-V2.5-I01.json", "Voice:it")]
    public void TheModelsOfTheSpeechAimsAreDeclared(string l3, string setting)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository.Amds, l3)));
        var entries = AccessEntry.ReadFrom(doc.RootElement);
        Assert.Equal("", AccessEntry.Check(entries));
        Assert.All(entries, e => Assert.StartsWith("https://", e.Uri));
        Assert.Contains(entries, e => e.Setting == setting);
    }

    // A MODEL WHOSE PARTS REFER TO ONE ANOTHER BY FILE NAME (an ONNX model and its external data) finds them: the entries of
    // an AIM Instance that name AsFile are together in one folder, under those names.
    [Fact]
    public void FilesThatNameAnAsFileAreGivenTogetherUnderThatName()
    {
        var model = File1("a/vision.onnx", "graph");
        var data = File1("b/vision.onnx.data", "weights");
        var entries = new[]
        {
            Entry("vision", model) with { Setting = "VisionModel", AsFile = "vision.onnx" },
            Entry("vision-data", data) with { AsFile = "vision.onnx.data", ValidWith = [model.Sha] }
        };
        Assert.Equal("", AccessEntry.Check(entries));
        var fetcher = new AccessFetcher(store);
        foreach (var e in entries) Assert.Equal(AccessOutcome.OK, fetcher.Ensure(e, "aim", out _));

        var access = new BoundAccess(store.As(Me), entries, fetcher, "aim");
        var given = AccessEntry.Overlay(new Dictionary<string, string>(), entries, access, "aim");
        var path = given["VisionModel"];
        Assert.Equal("vision.onnx", Path.GetFileName(path));
        Assert.Equal("graph", File.ReadAllText(path));
        Assert.Equal("weights", File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "vision.onnx.data")));   // beside it, under its name
        Assert.Equal(path, AccessEntry.Overlay(new Dictionary<string, string>(), entries, access, "aim")["VisionModel"]);   // again: the same

        Assert.Contains("given to more than one entry", AccessEntry.Check([entries[0], entries[1] with { AsFile = "vision.onnx" }]));
    }

    [Theory]
    [InlineData("1MMC-TTT-V2.5-I01.json", "TttEncoderModel", 6)]
    [InlineData("1MMC-TIQ-V2.5-I01.json", "VisionModel", 5)]
    public void TheModelsOfTheTranslationAndTheQuestionAimsAreDeclared(string l3, string setting, int count)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository.Amds, l3)));
        var entries = AccessEntry.ReadFrom(doc.RootElement);
        Assert.Equal(count, entries.Count);
        Assert.Equal("", AccessEntry.Check(entries));
        Assert.Contains(entries, e => e.Setting == setting && e.AsFile.Length > 0);
        Assert.Contains(entries, e => e.ValidWith is { Count: > 0 });
    }

    // DATA THAT IS A FILE OF AN ARCHIVE: the Uri locates the archive and the SHA-256 is that of the file, which is what the
    // AIM reads; several entries may name the same archive.
    [Fact]
    public void AFileOfAnArchiveIsObtainedByItsOwnHash()
    {
        var archive = Path.Combine(work, "pack.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var (name, text) in new[] { ("models/a.onnx", "alpha"), ("models/b.txt", "beta") })
                using (var w = new StreamWriter(zip.CreateEntry(name).Open())) w.Write(text);
        }
        var uri = new Uri(archive).AbsoluteUri;
        var a = new AccessEntry("a", uri, Sha(Encoding.UTF8.GetBytes("alpha")), [], false, Terms: TestTerms, Member: "models/a.onnx", Setting: "A");
        var b = new AccessEntry("b", uri, Sha(Encoding.UTF8.GetBytes("beta")), [], false, Terms: TestTerms, Member: "models/b.txt", Setting: "B");
        Assert.Equal("a.onnx", a.FileName);
        Assert.Equal("", AccessEntry.Check([a, b]));

        var fetcher = new AccessFetcher(store);
        Assert.Equal(AccessOutcome.OK, fetcher.Ensure(a, "aim", out _));
        Assert.Equal(AccessOutcome.OK, fetcher.Ensure(b, "aim", out _));
        var access = new BoundAccess(store.As(Me), [a, b], fetcher, "aim");
        Assert.Equal(AccessOutcome.OK, access.Get(uri, "a.onnx", out var alpha));
        Assert.Equal("alpha", Encoding.UTF8.GetString(alpha));
        Assert.Equal(AccessOutcome.OK, access.Get(uri, "b.txt", out var beta));
        Assert.Equal("beta", Encoding.UTF8.GetString(beta));
        Assert.Equal(AccessOutcome.NotAuthorised, access.Get(uri, "c.txt", out _));
        var given = AccessEntry.Overlay(new Dictionary<string, string>(), [a, b], access, "aim");
        Assert.Equal("alpha", File.ReadAllText(given["A"]));
        Assert.Equal("beta", File.ReadAllText(given["B"]));

        var wrong = a with { Name = "w", Sha256 = new string('0', 64), Member = "models/b.txt" };      // a file that is not the one named
        Assert.Equal(AccessOutcome.Refused, new AccessFetcher(new AccessStore(Path.Combine(work, "other"))).Ensure(wrong, "aim", out var why));
        Assert.Contains("not the data its Metadata names", why);
        var absent = a with { Name = "x", Member = "models/none.bin" };
        Assert.Equal(AccessOutcome.NotFound, new AccessFetcher(new AccessStore(Path.Combine(work, "other2"))).Ensure(absent, "aim", out _));
    }

    [Theory]
    [InlineData("1MMC-OCR-V2.5-I01.json", "DetModel", 4)]
    [InlineData("1MMC-SPE-V2.5-I01.json", "W2v2Model", 1)]
    public void TheModelsOfTheOcrAndTheSpeechEmotionAimsAreDeclared(string l3, string setting, int count)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository.Amds, l3)));
        var entries = AccessEntry.ReadFrom(doc.RootElement);
        Assert.Equal(count, entries.Count);
        Assert.Equal("", AccessEntry.Check(entries));
        Assert.Contains(entries, e => e.Setting == setting);
        Assert.All(entries, e => Assert.NotNull(e.Terms));
    }

    // A PROGRAM THAT COMES IN AN ARCHIVE is unpacked, once, with what it needs beside it, and the setting is given the file
    // Inside names; an archive that would write outside its folder is refused.
    [Fact]
    public void AnArchiveOfAProgramIsUnpackedAndTheSettingIsGivenTheFileInside()
    {
        var archive = Path.Combine(work, "tool.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Create))
            foreach (var (name, text) in new[] { ("tool/tool.exe", "program"), ("tool/lib.dll", "library"), ("tool/data/x.dat", "data") })
                using (var w = new StreamWriter(zip.CreateEntry(name).Open())) w.Write(text);
        var uri = new Uri(archive).AbsoluteUri;
        var sha = Sha(File.ReadAllBytes(archive));
        var program = new AccessEntry("program", uri, sha, [], false, Terms: TestTerms, Setting: "Exe", Unpack: true, Inside: "tool/tool.exe");
        var server = new AccessEntry("server", uri, sha, [], false, Terms: TestTerms, Setting: "Server", Unpack: true, Inside: "tool/lib.dll");
        Assert.Equal("", AccessEntry.Check([program, server]));
        Assert.Contains("path inside an unpacked archive", AccessEntry.Check([program with { Unpack = false }]));
        Assert.Contains("path inside an unpacked archive", AccessEntry.Check([program with { Inside = "../x" }]));

        var fetcher = new AccessFetcher(store);
        foreach (var e in new[] { program, server }) Assert.Equal(AccessOutcome.OK, fetcher.Ensure(e, "aim", out _));
        var access = new BoundAccess(store.As(Me), [program, server], fetcher, "aim");
        var given = AccessEntry.Overlay(new Dictionary<string, string>(), [program, server], access, "aim");
        Assert.Equal("program", File.ReadAllText(given["Exe"]));
        Assert.Equal("library", File.ReadAllText(given["Server"]));
        Assert.Equal("data", File.ReadAllText(Path.Combine(Path.GetDirectoryName(given["Exe"])!, "data", "x.dat")));      // what it needs is beside it
        Assert.Equal(Path.GetDirectoryName(given["Exe"]), Path.GetDirectoryName(given["Server"]));                         // one unpacking for both
        Assert.Equal(given["Exe"], AccessEntry.Overlay(new Dictionary<string, string>(), [program], access, "other")["Exe"]);   // and for another AIM

        var evil = Path.Combine(work, "evil.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(evil, System.IO.Compression.ZipArchiveMode.Create))
            using (var w = new StreamWriter(zip.CreateEntry("../escaped.txt").Open())) w.Write("x");
        var evilEntry = new AccessEntry("evil", new Uri(evil).AbsoluteUri, Sha(File.ReadAllBytes(evil)), [], false, Setting: "E", Unpack: true);
        Assert.Equal(AccessOutcome.OK, fetcher.Ensure(evilEntry, "aim", out _));
        Assert.ThrowsAny<Exception>(() => AccessEntry.Overlay(new Dictionary<string, string>(), [evilEntry], new BoundAccess(store.As(Me), [evilEntry], fetcher, "aim"), "aim"));
        Assert.False(File.Exists(Path.Combine(work, "access", "escaped.txt")));
    }

    // THE TERMS OF EVERYTHING THE CODE FETCHES: every Access entry of every L3 states the licence of the data and where the terms
    // are stated, so that those who use the code, which is BSD 3-clause, are told that the models and the data are not.
    [Fact]
    public void EveryAccessEntryOfEveryL3StatesItsTerms()
    {
        var problems = new List<string>();
        var seen = 0;
        foreach (var file in Directory.EnumerateFiles(Repository.Amds, "*.json"))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            var entries = AccessEntry.ReadFrom(doc.RootElement);
            seen += entries.Count;
            if (AccessEntry.Check(entries) is { Length: > 0 } wrong) problems.Add($"{Path.GetFileName(file)}: {wrong}");
        }
        Assert.True(seen > 50, $"only {seen} entries seen");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
