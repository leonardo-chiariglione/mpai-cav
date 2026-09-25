using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AIF.Controller;
using Mpai.Core;
using Mpai.Aif.Api;
using Mpai.Mas.Client;
using Xunit.Abstractions;

namespace Mpai.Aif.Tests;

// Starts the MAS-App Service of this repository for the Service tests (M3207 3.4):
// from its own build output and from the repository's root, on port 5105 - never
// 5005, where another Service may be running - with a configuration written for
// the purpose. Stops it when the tests end. Where something it needs is absent,
// it says what, and the tests are skipped rather than failed.
public sealed class ServiceFixture : IDisposable
{
    public const string Url = "https://localhost:5105/";

    public string? SkipReason { get; }
    public string Log => log.ToString();

    private readonly Process? process;
    private readonly StringBuilder log = new();

    public ServiceFixture()
    {
        var root = Repository.Root;
        var exe = Path.Combine(root, "MAS", "Service", "src", "bin", "Debug", "net10.0", "MasService.exe");

        SkipReason =
            !Directory.Exists(Path.Combine(root, "Models")) ? "Models is absent: the model files are obtained separately." :
            !File.Exists(exe) ? "The Service is not built: " + exe :
            !OllamaHas("llama3.2:3b") ? "Ollama is not running with llama3.2:3b." :
            PortInUse(5105) ? "Port 5105 is in use." :
            null;
        if (SkipReason is not null) return;

        var config = Path.Combine(Path.GetTempPath(), "mpai-test-service.json");
        File.WriteAllText(config, new JsonObject
        {
            ["ListenUrl"]    = Url,
            ["AppDirectory"] = Path.Combine(root, "Apps"),
            ["Apps"]         = new JsonArray("MAD", "AMQ", "MAT", "MPD"),
            ["AmdDirectory"] = Repository.Amds,
            ["SettingsPath"] = Path.Combine(root, "AIMs", "aim-settings.json")
        }.ToJsonString());

        var ready = new ManualResetEventSlim();
        process = new Process
        {
            StartInfo = new ProcessStartInfo(exe, $"\"{config}\"")
            {
                WorkingDirectory = root,          // the models' relative paths are resolved from here
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (log) log.AppendLine(e.Data);
            if (e.Data.Contains("[MAS] Listening")) ready.Set();
        };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!ready.Wait(TimeSpan.FromMinutes(5)))
            SkipReason = "The Service did not start listening within 5 minutes:\n" + Tail(Log, 20);
    }

    public void Dispose()
    {
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
        process?.Dispose();
    }

    private static bool OllamaHas(string model)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            return http.GetStringAsync("http://localhost:11434/api/tags").GetAwaiter().GetResult().Contains($"\"{model}\"");
        }
        catch { return false; }
    }

    private static bool PortInUse(int port) =>
        System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
              .GetActiveTcpListeners().Any(e => e.Port == port);

    private static string Tail(string text, int lines) =>
        string.Join("\n", text.Split('\n').TakeLast(lines));
}

[CollectionDefinition("Service")]
public sealed class ServiceCollection : ICollectionFixture<ServiceFixture> { }

// The Service tests of M3207 3.4. They concern MAS-App, and inform.
[Collection("Service")]
[Trait("Group", "Service")]
[Trait("Blocks", "No")]
public class ServiceTests
{
    private const string MAD = "1MMC-MAD-V2.5-I01", AMQ = "1MMC-AMQ-V2.5-I01";

    private readonly ServiceFixture service;
    private readonly ITestOutputHelper output;

    public ServiceTests(ServiceFixture service, ITestOutputHelper output)
    {
        this.service = service;
        this.output = output;
    }

    // Two clients ask AMQ about a red and a blue picture at the same time, eight
    // rounds. Each answer must be the answer that picture gets when asked alone.
    [SkippableFact]
    public void TwoClientsPictures()
    {
        Skip.If(service.SkipReason is not null, service.SkipReason);

        using var a = new RemoteControllerApi(ServiceFixture.Url);
        using var b = new RemoteControllerApi(ServiceFixture.Url);
        Assert.Equal(AifError.OK, a.StartFlow(AMQ));
        Assert.Equal(AifError.OK, b.StartFlow(AMQ));

        var alone = new Dictionary<string, string>
        {
            ["red.jpg"]  = Answer(Ask(a, "red.jpg")),
            ["blue.jpg"] = Answer(Ask(b, "blue.jpg"))
        };
        output.WriteLine($"alone: red -> \"{alone["red.jpg"]}\", blue -> \"{alone["blue.jpg"]}\"");

        var wrong = new List<string>();
        for (int round = 1; round <= 8; round++)
        {
            var ta = Task.Run(() => Answer(Ask(a, "red.jpg")));
            var tb = Task.Run(() => Answer(Ask(b, "blue.jpg")));
            if (ta.Result != alone["red.jpg"])  wrong.Add($"round {round}: red answered \"{ta.Result}\"");
            if (tb.Result != alone["blue.jpg"]) wrong.Add($"round {round}: blue answered \"{tb.Result}\"");
        }
        a.StopFlow(AMQ); b.StopFlow(AMQ);

        output.WriteLine($"{16 - wrong.Count} of 16 answers as when asked alone");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    // One client stops MAD while the other's turn is running; the other's turn,
    // and its next, must still answer with speech.
    [SkippableFact]
    public void TwoClientsStop()
    {
        Skip.If(service.SkipReason is not null, service.SkipReason);

        using var a = new RemoteControllerApi(ServiceFixture.Url);
        using var b = new RemoteControllerApi(ServiceFixture.Url);
        Assert.Equal(AifError.OK, a.StartFlow(MAD));
        Assert.Equal(AifError.OK, b.StartFlow(MAD));

        var turn = Task.Run(() => b.Advance(MAD, new[] { Text(2, "Tell me a short story about a cat called Tom.") }));
        Thread.Sleep(1500);
        var stillRunning = !turn.IsCompleted;
        a.StopFlow(MAD);
        output.WriteLine($"A stopped MAD while B's turn was {(stillRunning ? "running" : "already done")}");

        Assert.True(Spoke(turn.Result), "B's turn that was running when A stopped did not answer with speech");
        Assert.True(Spoke(b.Advance(MAD, new[] { Text(2, "What was the cat called?") })), "B's next turn did not answer with speech");
        b.StopFlow(MAD);
    }

    // Six runs each, after one not counted, against the baseline in timing.json:
    // fails beyond 1.5 times the baseline, warns beyond 1.25 times.
    [SkippableFact]
    public void Timing()
    {
        Skip.If(service.SkipReason is not null, service.SkipReason);

        var measured = new Dictionary<string, double>
        {
            ["typed MAD turn"]  = Median(MAD, new[] { Text(2, "Name one fruit. Answer in five words.") }),
            ["spoken MAD turn"] = Median(MAD, new[] { Speech("question.wav") }),
            ["AMQ question"]    = Median(AMQ, new[] { Picture("red.jpg"), Text(2, "What color is the picture?") })
        };

        var file = Path.Combine(Repository.Root, "Test", "Expected", "timing.json");
        var baseline = JsonNode.Parse(File.ReadAllText(file))!;
        output.WriteLine($"baseline measured on {baseline["Machine"]}, {baseline["Measured"]}");

        var slow = new List<string>();
        foreach (var (name, seconds) in measured)
        {
            var expected = baseline["Seconds"]![name]!.GetValue<double>();
            var ratio = seconds / expected;
            var mark = ratio > 1.5 ? "FAIL" : ratio > 1.25 ? "WARN" : "ok";
            output.WriteLine($"{name,-16} {seconds,5:F2} s   baseline {expected,5:F2} s   x{ratio:F2}  {mark}");
            if (ratio > 1.5) slow.Add($"{name}: {seconds:F2} s, {ratio:F2} times the baseline");
        }
        Assert.True(slow.Count == 0, string.Join("\n", slow));
    }

    // ---------------------------------------------------------------------

    private double Median(string module, ControllerApi.Datum[] inputs)
    {
        using var client = new RemoteControllerApi(ServiceFixture.Url);
        client.StartFlow(module);
        client.Advance(module, inputs);                      // not counted
        var times = new List<double>();
        for (int i = 0; i < 6; i++)
        {
            var clock = Stopwatch.StartNew();
            var r = client.Advance(module, inputs);
            times.Add(clock.Elapsed.TotalSeconds);
            Assert.True(r.Ok, $"{module}: {r.Error}");

            // A TURN IS TIMED ONLY IF IT ANSWERED. A turn in which an AIM failed -
            // the language model unreachable, say - returns quickly with nothing to
            // say, and would pass for a fast one.
            Assert.True(r.ByType("OSD-BSO-V1.5") is not null, $"{module}: the turn produced no speech");
        }
        client.StopFlow(module);
        times.Sort();
        return times[times.Count / 2];
    }

    private static ControllerApi.Result Ask(RemoteControllerApi client, string picture) =>
        client.Advance(AMQ, new[] { Picture(picture), Text(2, "What color is the picture?") });

    private static string Answer(ControllerApi.Result r)
    {
        var json = r.ByType("OSD-BTO-V1.5");
        try { return json is null ? "" : MpaiJson.FromJson<BasicTextObject>(json).GetText(); } catch { return json ?? ""; }
    }

    private static bool Spoke(ControllerApi.Result r) =>
        r.Ok && r.Outputs.Any(o => o.DataType == "OSD-BSO-V1.5" && o.Json.Length > 200);

    private static string Data(string file) => Path.Combine(Repository.Root, "Test", "Data", file);

    private static ControllerApi.Datum Text(int port, string text) =>
        new("OSD-BTO-V1.5", port, MpaiJson.ToJson(BasicTextObject.FromText(text)));

    private static ControllerApi.Datum Picture(string file) =>
        new("OSD-BVO-V1.5", 1, MpaiJson.ToJson(BasicVisualObject.FromFile(file, File.ReadAllBytes(Data(file)), "Picture")));

    private static ControllerApi.Datum Speech(string file) =>
        new("OSD-BSO-V1.5", 1, MpaiJson.ToJson(BasicSpeechObject.FromData(File.ReadAllBytes(Data(file)), new SpeechQualifier
        {
            SpeechQualifierID = Guid.NewGuid().ToString(),
            Format = new SpeechFormat
            {
                ContentFormats   = new SpeechContentFormats { RawData = new Pcm { SamplingFrequency = 16000, Precision = 16 } },
                TransportFormats = new SpeechTransportFormats { FileFormat = SpeechFileFormat.Wav }
            },
            Attributes = new SpeechAttributes
            {
                Metadata = new SpeechMetadata { Language = new Language { LanguageCode = "en", LanguageFormat = LanguageFormat.Iso639_1 } }
            }
        })));
}
