using System.Text.Json;
using System.Text.Json.Nodes;

using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Cav.Hci;
using Mpai.Cav.Map;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Aif.Tests;

// STEP 3 (M3243 3.3): HCI HEARING AND SPEAKING. The Module 1CAV-HCI-V2.0-I01 under
// its Controller, every Sub-AIM built; given the Offline Map of Step 2 and, turn by
// turn, what the passenger says - recorded speech, synthesised with a voice that is
// not the CAV's, as the cabin's microphone gives it - and what the AMS answers. The
// speech path runs: Audio Qualifier Conversion, the audio scene, Audio Source
// separation, Automatic Speech Recognition, Natural Language Understanding, EDP, and
// Response and Scene Rendering speaking the answer. Two passengers, two voices.
// Judged: what each says recognised well enough to be understood - a place asked
// for, one named ambiguously asked about, yes; the Routes and the arrival spoken -
// what the CAV says, heard by speech recognition, names the place; every AMS-HCI
// Message valid. Reported: what was recognised, what was answered, the time of a turn.
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
[Collection(Timing.Name)]
public class HciSpeechTests
{
    private const string Hci = HciProvider.Hci;
    private static readonly string[] Voices = ["en_GB-alan-medium", "en_US-lessiac-medium"];

    // The repository's settings, their paths made absolute.
    private static Dictionary<string, string> Settings(string aim)
    {
        var all = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Root, "AIMs", "aim-settings.json")))!;
        return all[aim]!.AsObject().ToDictionary(p => p.Key, p =>
        {
            var v = (string)p.Value!;
            return File.Exists(Path.Combine(Repository.Root, v)) ? Path.Combine(Repository.Root, v) : v;
        });
    }

    // What a passenger says, as the cabin's microphone gives it: a Basic Audio Object
    // of PCM samples, at the rate the voice speaks.
    public static string Passenger(string text, string voice)
    {
        var settings = Settings("1MMC-TTS-V2.5-I01");
        settings["VoiceModel"] = Path.Combine(Repository.Root, "Models", "Piper", "voices", voice, voice + ".onnx");
        settings["VoiceConfig"] = settings["VoiceModel"] + ".json";
        foreach (var k in settings.Keys.Where(k => k.StartsWith("Voice:") || k.StartsWith("VoiceConfig:")).ToList()) settings.Remove(k);
        var wav = Mpai.Aims.Tts.TtsFactory.Create(settings).ProcessAsync(BasicTextObject.FromText(text)).GetAwaiter().GetResult().Data;
        var (rate, pcm) = Pcm(wav);
        var qualifier = new AudioQualifier
        {
            AudioQualifierID = Guid.NewGuid().ToString(),
            Formats = new AudioFormats { ContentFormat = new AudioContentFormat { RawData = new AudioRawData { SampleSpace = new Pcm { SamplingFrequency = rate, Precision = 16 } } } },
            Attributes = new AudioAttributes { Source = "Real", Device = new AudioDevice { DeviceRole = "Capture", DeviceType = "Microphone", CaptureConfiguration = new CaptureConfiguration { ChannelCount = 1, SamplingMode = "Mono" } } }
        };
        return MpaiJson.ToJson(BasicAudioObject.FromData(pcm, qualifier));
    }

    // The sampling rate and samples of a 16-bit mono WAV.
    private static (int Rate, byte[] Pcm) Pcm(byte[] wav)
    {
        var rate = BitConverter.ToInt32(wav, 24);
        for (var pos = 12; pos + 8 <= wav.Length;)
        {
            var id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
            var length = BitConverter.ToInt32(wav, pos + 4);
            if (id == "data") return (rate, wav[(pos + 8)..Math.Min(wav.Length, pos + 8 + length)]);
            pos += 8 + length + (length & 1);
        }
        return (rate, wav[44..]);
    }

    private sealed class Cabin : IDisposable
    {
        private readonly ControllerApi api;
        private readonly string location = Path.Combine(Path.GetTempPath(), "mpai-p11-hci-" + Guid.NewGuid().ToString("N"));
        public readonly List<string> Turns = [];
        public readonly List<JsonNode> Sent = [];
        public readonly List<double> Seconds = [];

        public Cabin()
        {
            api = new ControllerApi(Repository.Amds, Path.Combine(Repository.Root, "AIMs", "aim-settings.json"), store => new HciProvider(store, Repository.Root));
            var started = api.StartFlow(Hci);
            if (started != AifError.OK) throw new InvalidOperationException($"{Hci} did not start: {started}");
            api.SharedStorageInit(Hci, location);
        }

        public (string? Recognised, string? Reply, BasicSpeechObject? Speech, JsonNode? Sent) Turn(string said, params ControllerApi.Datum[] inputs)
        {
            // What the cabin senses goes with its LiDAR frame, as the HCI App's does - empty here.
            var all = inputs.Any(i => i.DataType == "OSD-BAO-V1.5")
                ? inputs.Append(new ControllerApi.Datum("OSD-BLO-V1.5", 1, MpaiJson.ToJson(new BasicLiDARObject { BasicLiDARObjectID = Guid.NewGuid().ToString("N"), BasicLiDARData = [] }))).ToList()
                : inputs.ToList();
            all.Add(TestAvatar.Datum);   // the Avatar the reply animates
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var r = api.Advance(Hci, all);
            Seconds.Add(clock.Elapsed.TotalSeconds);
            if (r.Error != AifError.OK) throw new InvalidOperationException($"{said}: {r.Error} {JsonSerializer.Serialize(api.Status(Hci).Aims)}");
            var recognised = r.ByType("OSD-BTO-V1.5", 2) is { } t2 ? MpaiJson.FromJson<BasicTextObject>(t2)?.GetText() : null;
            var reply = r.ByType("OSD-BTO-V1.5", 1) is { } t1 ? MpaiJson.FromJson<BasicTextObject>(t1)?.GetText() : null;
            var speech = TestAvatar.Speech(r);
            var sent = r.ByType("CAV-AHM-V2.0") is { } a ? JsonNode.Parse(a) : null;
            if (sent is not null) Sent.Add(sent);
            Turns.Add($"{said} | heard: {recognised ?? "-"} | answered: {reply ?? "-"} ({speech?.Data.Length ?? 0} bytes of speech){(sent is null ? "" : " | sent " + sent["HCIMessage"]!.ToJsonString())} | {clock.Elapsed.TotalSeconds:0.0} s");
            return (recognised, reply, speech, sent);
        }

        // The Offline Map, given with the first thing said.
        private string? map;
        public void Know(string offlineMap) => map = offlineMap;

        public (string? Recognised, string? Reply, BasicSpeechObject? Speech, JsonNode? Sent) Hears(string text, string voice)
        {
            var inputs = new List<ControllerApi.Datum> { new("OSD-BAO-V1.5", 1, Passenger(text, voice)) };
            if (map is not null) { inputs.Add(new("OSD-BOO-V1.5", 1, map)); map = null; }
            return Turn($"\"{text}\" ({voice})", [.. inputs]);
        }

        public (string? Recognised, string? Reply, BasicSpeechObject? Speech, JsonNode? Sent) Told(JsonObject ams, string what) =>
            Turn($"(the AMS: {what})", new ControllerApi.Datum("CAV-AHM-V2.0", 1, new JsonObject { ["Header"] = "CAV-AHM-V2.0", ["AMSHCIMessageID"] = "AHM-A", ["AMSMessage"] = ams }.ToJsonString()));


        public void Dispose()
        {
            api.StopFlow(Hci);
            api.Dispose();
            try { Directory.Delete(location, recursive: true); } catch { }
        }
    }

    // What the CAV said, heard by speech recognition.
    private static string Heard(BasicSpeechObject? speech) =>
        speech is null || speech.Data.Length < 100 ? "" : Mpai.Aims.Asr.AsrFactory.Create(Settings("1MMC-ASR-V2.5-I01")).ProcessAsync(speech).GetAwaiter().GetResult().GetText();

    private static string? To(JsonNode? sent) => sent?["HCIMessage"]?["RequestedRoutes"]?[0]?["Route"]?["RouteSegments"]?[0]?["WayPoint2ID"]?.GetValue<string>();
    private static string? Command(JsonNode? sent) => sent is null ? null : $"{sent["HCIMessage"]?["RouteCommand"]} {sent["HCIMessage"]?["SelectedRouteID"]}".Trim();

    [SkippableFact]
    public void Step3HearingAndSpeaking()
    {
        Skip.IfNot(File.Exists(Path.Combine(Repository.Root, "Models", "Whisper", "models", "ggml-small.bin")), "the Whisper model is absent: the model files are obtained separately.");
        var result = new Dictionary<string, string>();
        var report = new Dictionary<string, object>();
        var sent = new List<JsonNode>();
        var map = RoadMap.Grid(3).Named(CavDialogueTests.Places).ToOfflineMapObject(0);
        foreach (var voice in Voices)
        {
            using var cabin = new Cabin();
            cabin.Know(map);
            var (heard, reply, _, s) = cabin.Hears("Please take me to the hospital.", voice);
            result[$"{voice}: a place"] = To(s) == "W20" ? "understood as its way point, requested" : $"heard \"{heard}\"; sent {To(s) ?? "nothing"}";
            (heard, reply, _, s) = cabin.Hears("No, take me to the station.", voice);
            result[$"{voice}: a place named ambiguously"] = s is null && reply?.Contains("Which one") == true ? "asked which" : $"heard \"{heard}\"; answered \"{reply}\"";
            (heard, reply, _, s) = cabin.Hears("The central one.", voice);
            result[$"{voice}: the answer to which"] = To(s) == "W21" ? "understood, requested" : $"heard \"{heard}\"; sent {To(s) ?? "nothing"}";
            var (_, _, speech, _) = cabin.Told(new JsonObject
            {
                ["RouteList"] = new JsonArray(new JsonObject
                {
                    ["Header"] = "CAV-RTE-V2.0", ["RouteID"] = "RTE0001", ["OfflineMapID"] = "MAP", ["RouteTime"] = Mpai.Cav.Ess.EssJson.SimpleTime("R-T", 0),
                    ["RouteSegments"] = new JsonArray(new JsonObject { ["WayPoint1ID"] = "W00", ["WayPoint2ID"] = "W21", ["EstimatedArrDepSpaceTime"] = Mpai.Cav.Ess.EssJson.SimpleTime("R-E", 44_000) })
                })
            }, "one Route, 44 s");
            var spoken = Heard(speech);
            result[$"{voice}: the Route spoken"] = spoken.Contains("Central Station", StringComparison.OrdinalIgnoreCase) && (spoken.Contains("40", StringComparison.Ordinal) || spoken.Contains("forty", StringComparison.OrdinalIgnoreCase)) ? "heard with the place and its time" : $"heard \"{spoken}\"";
            (heard, reply, _, s) = cabin.Hears("Yes, let's go.", voice);
            result[$"{voice}: yes"] = Command(s) == "Execute RTE0001" ? "the Route executed" : $"heard \"{heard}\"; sent {Command(s) ?? "nothing"}";
            cabin.Told(new JsonObject { ["RouteStatus"] = new JsonObject { ["RouteID"] = "RTE0001", ["Status"] = "Executing" } }, "Executing");
            (_, _, speech, _) = cabin.Told(new JsonObject { ["RouteStatus"] = new JsonObject { ["RouteID"] = "RTE0001", ["Status"] = "Arrived" } }, "Arrived");
            spoken = Heard(speech);
            result[$"{voice}: the arrival spoken"] = spoken.Contains("arrived", StringComparison.OrdinalIgnoreCase) && spoken.Contains("Central Station", StringComparison.OrdinalIgnoreCase) ? "heard with the place" : $"heard \"{spoken}\"";
            report[voice] = cabin.Turns;
            report[$"{voice}: a turn"] = $"median {cabin.Seconds.Order().ElementAt(cabin.Seconds.Count / 2):0.0} s, longest {cabin.Seconds.Max():0.0} s";
            sent.AddRange(cabin.Sent);
        }
        var schemas = AIF.Metadata.PublishedSchemas.At(Repository.Schemas);
        var schema = schemas[Path.GetFullPath(Path.Combine(Repository.Schemas, "CAV2", "V2.0", "data", "AMSHCIMessage.json"))];
        var valid = sent.Count(t => { using var doc = JsonDocument.Parse(t.ToJsonString()); lock (AIF.Metadata.PublishedSchemas.Lock) return schema.Evaluate(doc.RootElement).IsValid; });
        result["every AMS-HCI Message against its schema"] = valid == sent.Count ? "valid" : $"{valid} of {sent.Count} valid";
        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "cav-stage1-hci-speech.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + Environment.NewLine);
        Expected.Match("cav-stage1-hci-speech.json", result);
    }
}
