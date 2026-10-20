using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Core;
using Mpai.Providers;

namespace Mpai.Aif.Tests;

// NEWSPAPER READING (MMC-NPR-V2.5) through the Controller, as a User Agent drives it: the image of the author's
// page (D:\Images\NPR, kept outside the repository) and a Point at the top of a column; the Portable Avatar
// that comes back carries the text of that column. Judged against the column text the author typed
// (npr-page-truth.txt): the words of the answer against the true words, and where the column ends.
[Trait("Group", "Models")]
public class NprModuleTests
{
    private const string NPR = "1MMC-NPR-V2.5-I01";
    private const string Folder = @"D:\Images\NPR";

    private static ControllerApi Controller()
    {
        var store = new AIF.Store.AmdStore(Repository.Amds); store.Scan();
        return new ControllerApi(Repository.Amds, Path.Combine(Repository.Root, "AIMs", "aim-settings.json"),
            _ => new CompositeProvider(new NprProvider(store)));
    }

    private static string[] Words(string s) =>
        Regex.Matches(s.ToLowerInvariant(), @"[\p{L}\p{N}]+").Select(m => m.Value).ToArray();

    private static string Truth(int column)
    {
        var text = File.ReadAllText(Path.Combine(Folder, "npr-page-truth.txt"));
        var m = Regex.Match(text, $@"\[column {column}\]\s*(.*?)(?=\r?\n\s*\[|\z)", RegexOptions.Singleline);
        return m.Groups[1].Value.Trim();
    }

    private static string Read(ControllerApi api, double? x, double? y)
    {
        var inputs = new List<ControllerApi.Datum>
        {
            new("OSD-BVO-V1.5", 1, MpaiJson.ToJson(BasicVisualObject.FromFile("npr-page.png", File.ReadAllBytes(Path.Combine(Folder, "npr-page.png")), "Picture"))),
            new("string", 1, "\"Thalia\"")
        };
        if (x is not null)
            inputs.Add(new("OSD-PNT-V1.5", 1, $"{{\"Header\":\"OSD-PNT-V1.5\",\"Coordinates\":{{\"X\":{x},\"Y\":{y}}}}}"));
        var r = api.Advance(NPR, inputs);
        Assert.True(r.Ok, $"{NPR}: {r.Error}");
        var pav = JsonNode.Parse(r.ByType("PAF-PAV-V1.6") ?? "{}") as JsonObject;
        return pav?["TextObject"] is JsonObject txo ? MpaiJson.FromJson<BasicTextObject>(txo.ToJsonString()).GetText() : "";
    }

    // THE ANSWER IS A PART OF THE TRUE COLUMN: the page is read in a band about the point (some twenty-five lines), which is
    // enough. Judged: the words of the answer are the truth's (at least 90%), and there are enough of them - 30, or the whole
    // column when it is shorter.
    private static void Judge(string answer, string truth, string what)
    {
        var want = Words(truth).GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
        var read = Words(answer);
        int hit = 0;
        foreach (var w in read) if (want.TryGetValue(w, out var n) && n > 0) { hit++; want[w] = n - 1; }
        double precision = read.Length == 0 ? 0 : (double)hit / read.Length;
        Assert.True(precision >= 0.9 && read.Length >= Math.Min(30, Words(truth).Length),
            $"{what}: {precision:P0} of its {read.Length} words are the truth's.\nANSWER: {answer}\nTRUTH:  {truth}");
    }
    [SkippableTheory]
    [InlineData(1, 560, 472)]
    [InlineData(2, 1383, 472)]
    [InlineData(3, 1995, 472)]
    [InlineData(1, 198, 438)]
    [InlineData(1, 560, 1500)]
    [InlineData(2, 1383, 1900)]
    [InlineData(3, 1995, 1300)]
    [InlineData(1, 203, 303)]
    [InlineData(1, 183, 373)]
    public void ThePointedColumnIsRead(int column, double x, double y)
    {
        Skip.IfNot(Directory.Exists(Path.Combine(Repository.Root, "Models", "OCR")) && Directory.Exists(Folder), "Models/OCR or the page in D:\\Images\\NPR is absent.");
        using var api = Controller();
        Assert.Equal(AifError.OK, api.StartFlow(NPR));
        try { Judge(Read(api, x, y), Truth(column), $"column {column}"); }
        finally { api.StopFlow(NPR); }
    }

    [SkippableFact]
    public void WithNoPointTheFirstColumnUnderTheHeadlineIsRead()
    {
        Skip.IfNot(Directory.Exists(Path.Combine(Repository.Root, "Models", "OCR")) && Directory.Exists(Folder), "Models/OCR or the page in D:\\Images\\NPR is absent.");
        using var api = Controller();
        Assert.Equal(AifError.OK, api.StartFlow(NPR));
        try { Judge(Read(api, null, null), Truth(1), "no point"); }
        finally { api.StopFlow(NPR); }
    }

    // THE APP, NOT ONLY THE MODULE: Apps/MMC/NPR/MMC-NPR.orch run by the User Agent's interpreter with simulated
    // devices - the page chosen, a click at the top of the third column, a stage and an avatar that record what
    // they are given. The avatar is given the third column's text.
    [SkippableFact]
    public async Task NprAppReadsTheClickedColumnThroughItsWorkflow()
    {
        Skip.IfNot(Directory.Exists(Path.Combine(Repository.Root, "Models", "OCR")) && Directory.Exists(Folder), "Models/OCR or the page in D:\\Images\\NPR is absent.");
        using var api = Controller();
        using var done = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        string? shown = null, staged = null;
        var page = MpaiJson.ToJson(BasicVisualObject.FromFile("npr-page.png", File.ReadAllBytes(Path.Combine(Folder, "npr-page.png")), "Picture"));

        var devices = new Mpai.Ua.DeviceRegistry()
            .RegisterAcquire("OSD-BVO-V1.5", (_, _) => Task.FromResult<string?>(page))
            .RegisterAcquire("OSD-PNT-V1.5", (_, _) => Task.FromResult<string?>("{\"Header\":\"OSD-PNT-V1.5\",\"Coordinates\":{\"X\":1995,\"Y\":472}}"))
            .RegisterPresent("stage", data => { if (data.TryGetValue("OSD-BVO-V1.5", out var v)) staged = v; return Task.CompletedTask; })
            .RegisterPresent("screen", _ => Task.CompletedTask)
            .RegisterPresent("avatar", data =>
            {
                if (data.TryGetValue("PAF-PAV-V1.6", out var pav) && AvatarUtterance.TextOfPortableAvatar(pav) is { } words)
                {
                    shown = words;
                    done.Cancel();                           // one column is enough: the person leaves (Stop)
                }
                return Task.CompletedTask;
            });

        var said = new List<string>();
        var workflow = new Mpai.Wdl.WorkflowReader().Read(File.ReadAllText(Path.Combine(Repository.Root, "Apps", "MMC", "NPR", "MMC-NPR.orch")));
        var interpreter = new Mpai.Ua.WorkflowInterpreter(api.Async(), devices, line => { lock (said) said.Add(line); });
        try { await interpreter.RunAsync(workflow, done.Token); } catch (OperationCanceledException) { }

        Assert.True(staged is not null, "the page was never put on the stage: " + string.Join(" / ", said));
        Assert.True(shown is not null, "the avatar was given nothing: " + string.Join(" / ", said));
        Judge(shown!, Truth(3), "column 3 through the workflow");
    }

    // TRY AGAINST A RUNNING SERVICE (https://localhost:5105/) as the Desktop client does: the page, the point, the machine's ID.
    // Skipped when no Service answers there.
    [SkippableFact]
    public void ARunningServiceReadsTheClickedColumn()
    {
        Skip.IfNot(File.Exists(Path.Combine(Folder, "npr-page.png")), "no page");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3), BaseAddress = new Uri("https://localhost:5105/") };
        try { http.GetAsync("MPAI/AIFU/Apps").Wait(); } catch { Skip.If(true, "no Service on 5105"); }
        using var api = new Mpai.Mas.Client.RemoteControllerApi("https://localhost:5105/");
        Assert.Equal(AifError.OK, api.StartFlow(NPR));
        try
        {
            var r = api.Advance(NPR, new[]
            {
                new ControllerApi.Datum("OSD-BVO-V1.5", 1, MpaiJson.ToJson(BasicVisualObject.FromFile("npr-page.png", File.ReadAllBytes(Path.Combine(Folder, "npr-page.png")), "Picture"))),
                new ControllerApi.Datum("OSD-PNT-V1.5", 1, "{\"Header\":\"OSD-PNT-V1.5\",\"Coordinates\":{\"X\":560,\"Y\":472}}"),
                new ControllerApi.Datum("string", 1, "\"Thalia\"")
            });
            Assert.True(r.Ok, r.Error.ToString());
            var pav = JsonNode.Parse(r.ByType("PAF-PAV-V1.6") ?? "{}") as JsonObject;
            var words = pav?["TextObject"] is JsonObject txo ? MpaiJson.FromJson<BasicTextObject>(txo.ToJsonString()).GetText() : "";
            Judge(words, Truth(1), "column 1 through the running Service");
        }
        finally { api.StopFlow(NPR); }
    }

    // A DENSE PAGE: Le Petit Journal, 19 August 1893 (Gallica, public domain), six narrow columns. A point in the middle of
    // the second column gives that column's text and nothing of the others; the third column's headline is not in it.
    [SkippableFact]
    public void AnyPointInAColumnOfADensePageReadsThatColumn()
    {
        var page = Path.Combine(Folder, "db", "fr-le-petit-journal-1893-08-19.png");
        Skip.IfNot(Directory.Exists(Path.Combine(Repository.Root, "Models", "OCR")) && File.Exists(page), "Models/OCR or the page is absent.");
        using var api = Controller();
        Assert.Equal(AifError.OK, api.StartFlow(NPR));
        try
        {
            var r = api.Advance(NPR, new[]
            {
                new ControllerApi.Datum("OSD-BVO-V1.5", 1, MpaiJson.ToJson(BasicVisualObject.FromFile("page.png", File.ReadAllBytes(page), "Picture"))),
                new ControllerApi.Datum("OSD-PNT-V1.5", 1, "{\"Header\":\"OSD-PNT-V1.5\",\"Coordinates\":{\"X\":596,\"Y\":994}}"),
                new ControllerApi.Datum("string", 1, "\"Thalia\"")
            });
            Assert.True(r.Ok, r.Error.ToString());
            var pav = JsonNode.Parse(r.ByType("PAF-PAV-V1.6") ?? "{}") as JsonObject;
            var words = pav?["TextObject"] is JsonObject txo ? MpaiJson.FromJson<BasicTextObject>(txo.ToJsonString()).GetText() : "";
            File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "npr-petit-journal.txt"), words);
            Assert.Equal("fr", AvatarUtterance.LanguageOfPortableAvatar(r.ByType("PAF-PAV-V1.6")));   // the language is stamped on the text: the French voice speaks it
            Assert.True(Words(words).Length > 40, "too little text: " + words);
            Assert.DoesNotContain("Clemenceau", words, StringComparison.OrdinalIgnoreCase);
        }
        finally { api.StopFlow(NPR); }
    }

    // THE LANGUAGE OF THE COLUMN IS STAMPED on its text: English for the author's page.
    [SkippableFact]
    public void TheColumnsLanguageIsOnItsText()
    {
        Skip.IfNot(Directory.Exists(Path.Combine(Repository.Root, "Models", "OCR")) && Directory.Exists(Folder), "Models/OCR or the page in D:\\Images\\NPR is absent.");
        using var api = Controller();
        Assert.Equal(AifError.OK, api.StartFlow(NPR));
        try
        {
            var r = api.Advance(NPR, new[]
            {
                new ControllerApi.Datum("OSD-BVO-V1.5", 1, MpaiJson.ToJson(BasicVisualObject.FromFile("npr-page.png", File.ReadAllBytes(Path.Combine(Folder, "npr-page.png")), "Picture"))),
                new ControllerApi.Datum("OSD-PNT-V1.5", 1, "{\"Header\":\"OSD-PNT-V1.5\",\"Coordinates\":{\"X\":560,\"Y\":1500}}"),
                new ControllerApi.Datum("string", 1, "\"Thalia\"")
            });
            Assert.True(r.Ok, r.Error.ToString());
            Assert.Equal("en", AvatarUtterance.LanguageOfPortableAvatar(r.ByType("PAF-PAV-V1.6")));
        }
        finally { api.StopFlow(NPR); }
    }

    // A BLACKLETTER PAGE: the Correspondent für Untersteiermark, Marburg, 30 March 1862 (Europeana, public domain). A point in the
    // middle column gives that column from its first line, in German, and nothing of the sidebar or of the masthead.
    [SkippableFact]
    public void AGermanPageInBlackletterIsReadFromItsColumn()
    {
        var page = Path.Combine(Folder, "db", "de-correspondent-untersteiermark-1862-03-30.jpg");
        Skip.IfNot(Directory.Exists(Path.Combine(Repository.Root, "Models", "OCR")) && File.Exists(page), "Models/OCR or the page is absent.");
        using var api = Controller();
        Assert.Equal(AifError.OK, api.StartFlow(NPR));
        try
        {
            var r = api.Advance(NPR, new[]
            {
                new ControllerApi.Datum("OSD-BVO-V1.5", 1, MpaiJson.ToJson(BasicVisualObject.FromFile("page.jpg", File.ReadAllBytes(page), "Picture"))),
                new ControllerApi.Datum("OSD-PNT-V1.5", 1, "{\"Header\":\"OSD-PNT-V1.5\",\"Coordinates\":{\"X\":1662,\"Y\":1431}}"),
                new ControllerApi.Datum("string", 1, "\"Thalia\"")
            });
            Assert.True(r.Ok, r.Error.ToString());
            var pav = JsonNode.Parse(r.ByType("PAF-PAV-V1.6") ?? "{}") as JsonObject;
            var words = pav?["TextObject"] is JsonObject txo ? MpaiJson.FromJson<BasicTextObject>(txo.ToJsonString()).GetText() : "";
            Assert.Equal("de", AvatarUtterance.LanguageOfPortableAvatar(r.ByType("PAF-PAV-V1.6")));
            Assert.True(Words(words).Length >= 30, "too little text: " + words);
            Assert.Contains("Blatt", words);
            Assert.DoesNotContain("Sonntag", words);
        }
        finally { api.StopFlow(NPR); }
    }

    [SkippableFact]
    public void TempWhatTheRecogniserMakesOfTheLastRecording()
    {
        var wav = Path.Combine(Repository.Root, "Test", "Reports", "mic-last.wav");
        Skip.IfNot(File.Exists(wav), "no recording");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3), BaseAddress = new Uri("https://localhost:5105/") };
        try { http.GetAsync("MPAI/AIFU/Apps").Wait(); } catch { Skip.If(true, "no Service on 5105"); }
        using var api = new Mpai.Mas.Client.RemoteControllerApi("https://localhost:5105/");
        const string mas = "1MAS-APP-V1.0-I01";
        Assert.Equal(AifError.OK, api.StartFlow(mas));
        try
        {
            var speech = BasicSpeechObject.FromData(File.ReadAllBytes(wav), new SpeechQualifier
            {
                SpeechQualifierID = Guid.NewGuid().ToString(),
                Format = new SpeechFormat { ContentFormats = new SpeechContentFormats { RawData = new Pcm { SamplingFrequency = 16000, Precision = 16 } }, TransportFormats = new SpeechTransportFormats { FileFormat = SpeechFileFormat.Wav } },
                Attributes = new SpeechAttributes { Metadata = new SpeechMetadata { Language = new Language { LanguageCode = "en", LanguageFormat = LanguageFormat.Iso639_1 } } }
            });
            var r = api.Advance(mas, new[]
            {
                new ControllerApi.Datum("OSD-BSO-V1.5", 1, MpaiJson.ToJson(speech)),
                new ControllerApi.Datum("OSD-B3O-V1.5", 1, AvatarUtterance.AvatarDatum("cav-avatar.glb"))
            });
            File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "mic-last-asr.txt"), $"ok={r.Ok} error={r.Error} text=[{(r.ByType("OSD-BTO-V1.5") ?? "").Replace("\r"," ")}]");
        }
        finally { api.StopFlow(mas); }
    }
}