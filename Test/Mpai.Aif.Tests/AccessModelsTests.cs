using System.Text.Json;

using AIF.Controller;
using AIF.SharedStorage;
using Mpai.Aims.Ocr;

namespace Mpai.Aif.Tests;

// THE MODELS OF AN AIM ARE OBTAINED FROM WHERE ITS L3 SAYS, checked against the SHA-256, and given to the program that reads
// them: the models of MMC-OCR are obtained from the sites that publish them (a file, and files of an archive), and the
// recognition engine is built from them.
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
public class AccessModelsTests : IDisposable
{
    private readonly string work = Path.Combine(Path.GetTempPath(), "mpai-models-" + Guid.NewGuid().ToString("N"));
    public AccessModelsTests() => Directory.CreateDirectory(work);
    public void Dispose() { try { Directory.Delete(work, true); } catch { } }

    [Fact]
    public void TheOcrModelsAreObtainedAndTheEngineIsBuiltFromThem()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository.Amds, "1MMC-OCR-V2.5-I01.json")));
        var entries = AccessEntry.ReadFrom(doc.RootElement);
        var store = new AccessStore(Path.Combine(work, "access"));
        var fetcher = new AccessFetcher(store);
        foreach (var e in entries) Assert.Equal(AccessOutcome.OK, fetcher.Ensure(e, "ocr", out var why));

        var aim = new BoundAccess(store.As(new StorageHolder("M", "ocr")), entries, fetcher, "ocr");
        var settings = AccessEntry.Overlay(new Dictionary<string, string>(), entries, aim, "ocr");
        Assert.Equal(4, settings.Count);
        Assert.All(settings.Values, path => Assert.True(File.Exists(path), path));
        Assert.Equal("ch_PP-OCRv5_server_det.onnx", Path.GetFileName(settings["DetModel"]));

        using var engine = OcrFactory.Create("1MMC-OCR-V2.5-I01", settings) as IDisposable;
        Assert.NotNull(engine);
    }

    // THE BLIP EXPORTS are published by MPAI (Git LFS in mpai-sw): the Controller obtains the small one and finds the others at
    // the size the L3 pins.
    [Fact]
    public void TheBlipFilesAreObtainedFromWhereMpaiPublishesThem()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository.Amds, "1MMC-TIQ-V2.5-I01.json")));
        var entries = AccessEntry.ReadFrom(doc.RootElement);
        var vision = entries.Single(e => e.Name == "tiq-vision");
        Assert.StartsWith("https://media.githubusercontent.com/media/mpai-community/mpai-sw/", vision.Uri);
        var store = new AccessStore(Path.Combine(work, "access"));
        Assert.Equal(AccessOutcome.OK, new AccessFetcher(store).Ensure(vision, "tiq", out var why));
        Assert.Equal("", why);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MPAI-AIF-Controller/3.0");
        foreach (var e in entries.SelectMany(e => e.Locations).Where(l => l.StartsWith("https://media.githubusercontent.com/")).Select(l => new { Uri = l }))
        {
            using var head = http.Send(new HttpRequestMessage(HttpMethod.Head, e.Uri));
            Assert.True(head.IsSuccessStatusCode, e.Uri);
        }
    }

    // THE PROGRAMS of whisper and piper are obtained as the archives they are published in, checked, unpacked, and are the
    // programs the L3 names (its Sha256 is the archive's, checked when obtained).
    [Theory]
    [InlineData("1MMC-ASR-V2.5-I01.json", "ExecutablePath")]
    [InlineData("1MMC-ASR-V2.5-I01.json", "ServerPath")]
    [InlineData("1MMC-TTS-V2.5-I01.json", "PiperExecutable")]
    public void TheProgramsAreObtainedUnpackedAndAreTheOnesPinned(string l3, string setting)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository.Amds, l3)));
        var entries = AccessEntry.ReadForThisMachine(doc.RootElement).Where(e => e.Unpack).ToList();
        var store = new AccessStore(Path.Combine(work, "access"));
        var fetcher = new AccessFetcher(store);
        foreach (var e in entries) Assert.Equal(AccessOutcome.OK, fetcher.Ensure(e, "aim", out var why));
        var aim = new BoundAccess(store.As(new StorageHolder("M", "aim")), entries, fetcher, "aim");
        var given = AccessEntry.Overlay(new Dictionary<string, string>(), entries, aim, "aim");

        var program = given[setting];
        Assert.True(File.Exists(program), program);
    }

    // THE PIPER VOICES: each voice and its configuration, obtained and checked, speaks through the piper program that Access
    // also unpacked.
    [Fact]
    public void EachPiperVoiceOfAccessSpeaksWithThePiperOfAccess()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository.Amds, "1MMC-TTS-V2.5-I01.json")));
        var entries = AccessEntry.ReadForThisMachine(doc.RootElement).Where(e => e.Name.StartsWith("voice-") || e.Name == "tts-program").ToList();
        var store = new AccessStore(Path.Combine(work, "access"));
        var fetcher = new AccessFetcher(store);
        foreach (var e in entries) Assert.Equal(AccessOutcome.OK, fetcher.Ensure(e, "tts", out var why));
        var aim = new BoundAccess(store.As(new StorageHolder("M", "tts")), entries, fetcher, "tts");
        var given = AccessEntry.Overlay(new Dictionary<string, string>(), entries, aim, "tts");
        var piper = given["PiperExecutable"];
        var spoken = new List<string>();
        foreach (var (lang, model, config) in new[] { ("en", "VoiceModel", "VoiceConfig"), ("pt", "Voice:pt", "VoiceConfig:pt"), ("es", "Voice:es", "VoiceConfig:es"),
                                                      ("de", "Voice:de", "VoiceConfig:de"), ("fr", "Voice:fr", "VoiceConfig:fr"), ("it", "Voice:it", "VoiceConfig:it") })
        {
            var wav = Path.Combine(work, lang + ".wav");
            var info = new System.Diagnostics.ProcessStartInfo(piper) { RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-m", given[model], "-c", given[config], "--output_file", wav }) info.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(info)!;
            p.StandardInput.Write("Hello. Ciao. Bonjour."); p.StandardInput.Close();
            var err = p.StandardError.ReadToEndAsync(); p.StandardOutput.ReadToEnd();
            Assert.True(p.WaitForExit(120000), lang);
            Assert.True(p.ExitCode == 0 && File.Exists(wav) && new FileInfo(wav).Length > 10000, $"{lang}: exit {p.ExitCode} {err.Result[^Math.Min(200, err.Result.Length)..]}");
            spoken.Add(lang);
        }
        Assert.Equal(6, spoken.Count);
    }

    // OPENVOICE: its code, the configuration and the checkpoint of its converter are obtained and given to the voice engine, which
    // makes the embedding of a voice from them (the Python with torch is the environment's).
    [SkippableFact]
    public void TheVoiceEngineWorksFromTheOpenVoiceOfAccess()
    {
        Skip.IfNot(File.Exists(Mpai.Core.MpaiPaths.Python), "The Python of the voice engine is absent.");
        var clip = Path.Combine(Mpai.Core.MpaiPaths.Clips, "1272", "clip.wav");
        Skip.IfNot(File.Exists(clip), "The LibriSpeech clip is absent.");
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository.Amds, "1CAE-SMC-V2.5-I01.json")));
        var entries = AccessEntry.ReadForThisMachine(doc.RootElement).Where(e => e.Name.StartsWith("openvoice-")).ToList();
        Assert.Equal(3, entries.Count);
        var store = new AccessStore(Path.Combine(work, "access"));
        var fetcher = new AccessFetcher(store);
        foreach (var e in entries) Assert.Equal(AccessOutcome.OK, fetcher.Ensure(e, "smc", out var why));
        var aim = new BoundAccess(store.As(new StorageHolder("M", "smc")), entries, fetcher, "smc");
        var settings = AccessEntry.Overlay(new Dictionary<string, string>(), entries, aim, "smc");
        var embedding = new Mpai.Cae.Usc.Voice.OpenVoiceWorker(settings).Extract([Mpai.Cae.Usc.Audio.FromWav(File.ReadAllBytes(clip))]);
        Assert.Equal(256, embedding.Length);
        Assert.All(embedding, v => Assert.True(float.IsFinite(v)));
    }
}
