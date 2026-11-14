using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;

using AIF.Controller;
using AIF.Store;

namespace Mpai.Aif.Tests;

// THE HASHES OF THE IMPLEMENTER-TO-CONTROLLER WORKFLOW. The Implementer submits the L3s of AMQ to the Store; the Store measures BinaryName.dll in the package each
// names and records its fingerprint. A Controller that fetches the packages measures what it has and puts it to the Store (MPAI_AIFS_VerifyImplementation): the
// packages as submitted are approved, a package altered after submission is refused - and refused, not built by another provider in its place.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class PackageFingerprintTests
{
    private static readonly string[] Leaves = ["1MMC-ASR-V2.5-I03", "1MMC-TIQ-V2.5-I03", "1MMC-TTS-V2.5-I03", "1PAF-PMX-V1.6-I03"];
    private const string Composite = "1MMC-AMQ-V2.5-I02";

    // The L3s of MPAIStore/L3s/AMQ with the address of the packages replaced by a folder.
    private static string Copy(string source, string packages, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source, "*.json"))
        {
            var text = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(file), "(file:///[^\"]*/(Packages|upload)|https://github.com/leonardo-chiariglione/packages/releases/download/amq-v[0-9.]+)", new Uri(packages).AbsoluteUri.TrimEnd('/'));
            File.WriteAllText(Path.Combine(target, Path.GetFileName(file)), text);
        }
        return target;
    }

    private static AmdStore Scanned(string folder) { var store = new AmdStore(folder); store.Scan(); return store; }

    [SkippableFact]
    public async Task AnAlteredPackageIsRefusedAndTheOnesSubmittedAreApproved()
    {
        var source = Path.Combine(Repository.Root, "MPAIStore", "Packages");
        var l3s = Path.Combine(Repository.Root, "MPAIStore", "L3s", "AMQ");
        Skip.IfNot(Directory.Exists(l3s) && File.Exists(Path.Combine(source, "MMC-ASR-V2.5-I03-win-x64.zip")), "The packages of AMQ are not in " + source);

        using var store = new StoreTests.RunningStore();
        foreach (var zip in Directory.GetFiles(source, "*-I03-win-x64.zip")) File.Copy(zip, Path.Combine(store.Packages, Path.GetFileName(zip)));

        // 1. THE IMPLEMENTER SUBMITS: the leaves first, then the Composite AIM that names them.
        var submitted = Copy(l3s, store.Packages, Path.Combine(store.Root, "submitted"));
        foreach (var id in Leaves.Append(Composite))
        {
            var (status, body) = await StoreTests.Submit(store.Http, JsonNode.Parse(File.ReadAllText(Path.Combine(submitted, id + ".json")))!, null, StoreTests.Admin);
            Assert.True(status == HttpStatusCode.Created, $"{id}: {status} {body}");
        }
        foreach (var id in Leaves)
            Assert.True(Directory.GetFiles(Path.Combine(store.StoreFolder, "approved"), id + ".json").Length == 1, $"{id}: the Store recorded no fingerprint");

        // 2. THE CONTROLLER FETCHES what the L3s name - the packages as submitted - and the Store approves each.
        var said = new List<string>();
        var approvals = new HttpAimSource(store.Url);
        var provider = new PackageAimProvider(Scanned(submitted), Path.Combine(store.Root, "cache-good"), line => said.Add(line), approvals);
        foreach (var id in Leaves)
        {
            Assert.True(provider.CanCreate(id), $"{id}: {string.Join(" / ", said)}");
            Assert.True(provider.ImplementationOf(id) is not null, $"{id}: {string.Join(" / ", said)}");
        }

        // 3. ONE PACKAGE IS ALTERED after submission: a byte added to its binary, the zip put where the L3 of that AIM names it.
        var altered = Path.Combine(store.Root, "altered"); Directory.CreateDirectory(altered);
        foreach (var zip in Directory.GetFiles(store.Packages, "*.zip")) File.Copy(zip, Path.Combine(altered, Path.GetFileName(zip)));
        var tts = Path.Combine(altered, "MMC-TTS-V2.5-I03-win-x64.zip");
        using (var archive = ZipFile.Open(tts, ZipArchiveMode.Update))
        {
            var entry = archive.Entries.First(e => e.FullName == "Mpai.Mmc.Tts.dll");
            byte[] bytes; using (var s = entry.Open()) { using var m = new MemoryStream(); s.CopyTo(m); bytes = m.ToArray(); }
            entry.Delete();
            using var again = archive.CreateEntry("Mpai.Mmc.Tts.dll").Open();
            again.Write(bytes); again.WriteByte(0);
        }
        var changed = Copy(l3s, altered, Path.Combine(store.Root, "changed"));
        said.Clear();
        var strict = new PackageAimProvider(Scanned(changed), Path.Combine(store.Root, "cache-altered"), line => said.Add(line), approvals);

        Assert.True(strict.CanCreate("1MMC-TTS-V2.5-I03"), "a refused AIM is still the provider's: another provider must not build it in its place");
        Assert.Null(strict.ImplementationOf("1MMC-TTS-V2.5-I03"));
        var refusal = Assert.Throws<TrustRefusedException>(() => strict.Create("1MMC-TTS-V2.5-I03", new Dictionary<string, string>(), null));
        Assert.Contains("not the binary the Store approved", refusal.Message);
        // the packages left as they were are still approved
        foreach (var id in Leaves.Where(l => l != "1MMC-TTS-V2.5-I03")) Assert.NotNull(strict.ImplementationOf(id));

        // 4. WITHOUT A STORE nothing is asked: the same altered package is taken, which is what the Service says when it prints that packages are not verified.
        var unchecked_ = new PackageAimProvider(Scanned(changed), Path.Combine(store.Root, "cache-unchecked"), _ => { });
        Assert.NotNull(unchecked_.ImplementationOf("1MMC-TTS-V2.5-I03"));
    }

    // THE FINGERPRINT OF AN IMPLEMENTATION IS THAT OF ITS PACKAGE (AIF V3.0, MPAI Store 4; Zero Trust and Profiles 5; 2026-11-13): the SHA-256 of the list of the
    // files the Implementation loads - the binary its BinaryName names and the assemblies of the package it refers to, at any depth - not of the binary alone.
    // Judged on XRV-LTP's Action Generation (Mpai.Xrv.Acg, most of whose code is in Mpai.Xrv.Common): a library it loads changed changes the fingerprint; the
    // assemblies of MPAI-AIF, which the Controller gives every package, are not in it; a file nothing loads does not count; as a folder and as a .zip, one fingerprint.
    [Fact]
    public void ALibraryTheImplementationLoadsIsPartOfItsFingerprint()
    {
        var package = Path.Combine(Path.GetTempPath(), "mpai-package-" + Guid.NewGuid().ToString("N"));
        var zipFile = package + ".zip";
        try
        {
            Directory.CreateDirectory(package);
            foreach (var dll in Directory.GetFiles(AppContext.BaseDirectory, "*.dll")) File.Copy(dll, Path.Combine(package, Path.GetFileName(dll)));
            var binary = Path.Combine(package, "Mpai.Xrv.Acg.dll");
            var names = ImplementationFingerprints.PackageFiles("Mpai.Xrv.Acg.dll", n => File.Exists(Path.Combine(package, n)) ? File.ReadAllBytes(Path.Combine(package, n)) : null)
                                                  .Select(f => f.Path).ToList();
            Assert.Contains("Mpai.Xrv.Acg.dll", names);
            Assert.Contains("Mpai.Xrv.Common.dll", names);
            Assert.DoesNotContain(names, n => n.StartsWith(ImplementationFingerprints.FrameworkPrefix, StringComparison.Ordinal));
            Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), names);

            var before = ImplementationFingerprints.OfImplementation(binary);
            Assert.NotEqual(ImplementationFingerprints.Of(binary), before);                                     // not the binary alone
            File.WriteAllText(Path.Combine(package, "notes.txt"), "a file nothing loads");
            Assert.Equal(before, ImplementationFingerprints.OfImplementation(binary));
            ZipFile.CreateFromDirectory(package, zipFile);
            using (var zip = File.OpenRead(zipFile)) Assert.Equal(before, ImplementationFingerprints.OfPackage(zip, "Mpai.Xrv.Acg"));

            File.AppendAllText(Path.Combine(package, "Mpai.Xrv.Common.dll"), " ");                          // the library changed, the binary the same
            Assert.NotEqual(before, ImplementationFingerprints.OfImplementation(binary));
        }
        finally
        {
            try { Directory.Delete(package, true); } catch { }
            File.Delete(zipFile);
        }
    }
}
