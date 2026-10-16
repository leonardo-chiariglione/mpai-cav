using System.Text.Json.Nodes;

using AIF.Controller;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Mmc.Sir;
using Mpai.Osd.Aus;

namespace Mpai.Aif.Tests;

// AUDIO SEGMENTATION (OSD-AUS-V1.5). Two voices that are not the CAV's - Piper's
// en_GB-alan-medium and en_US-lessiac-medium - say a sentence each in the order A, B, A with a
// second of silence between; the AIM must find THREE turns of TWO speakers, in that order,
// beginning where the voices begin. Then the same through the AIM, by its Ports: a Speech
// Object in; a Speech Object of the turns, the times the speakers begin, and the Speech
// Overlap out. Needs Models (the speaker embedder and the voices): skipped where absent.
[Trait("Group", "Models")]
public class AudioSegmentationTests
{
    private const string Aim = "1OSD-AUS-V1.5-I01";
    private const double Silence = 1.0;                      // seconds between the voices

    private static (float[] Samples, double[] Starts) Dialogue(params (string Text, string Voice)[] says)
    {
        var all = new List<float>(); var starts = new List<double>();
        foreach (var (text, voice) in says)
        {
            all.AddRange(new float[(int)(Silence * AudioSegmenter.Rate)]);
            starts.Add(all.Count / (double)AudioSegmenter.Rate);
            all.AddRange(Say(text, voice));
        }
        all.AddRange(new float[(int)(Silence * AudioSegmenter.Rate)]);
        return (all.ToArray(), starts.ToArray());
    }

    private static float[] Say(string text, string voice)
    {
        var all = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Root, "AIMs", "aim-settings.json")))!;
        var settings = all["1MMC-TTS-V2.5-I01"]!.AsObject().ToDictionary(p => p.Key, p =>
        {
            var v = (string)p.Value!; return File.Exists(Path.Combine(Repository.Root, v)) ? Path.Combine(Repository.Root, v) : v;
        });
        settings["VoiceModel"] = Path.Combine(Repository.Root, "Models", "Piper", "voices", voice, voice + ".onnx");
        settings["VoiceConfig"] = settings["VoiceModel"] + ".json";
        foreach (var k in settings.Keys.Where(k => k.StartsWith("Voice:") || k.StartsWith("VoiceConfig:")).ToList()) settings.Remove(k);
        var wav = Mpai.Aims.Tts.TtsFactory.Create(settings).ProcessAsync(BasicTextObject.FromText(text)).GetAwaiter().GetResult().Data;
        return WavReader.ReadMono16k(new MemoryStream(wav), voice + ".wav");
    }

    private static SpeakerEmbedder Embedder() => new(Path.Combine(Repository.Root, "Models", "ecapa-tdnn.onnx"));

    private static void RequireModels() =>
        Skip.IfNot(File.Exists(Path.Combine(Repository.Root, "Models", "ecapa-tdnn.onnx")) &&
                   Directory.Exists(Path.Combine(Repository.Root, "Models", "Piper", "voices", "en_GB-alan-medium")), "Models is absent: the model files are obtained separately.");

    private const string A = "en_GB-alan-medium", B = "en_US-lessiac-medium";
    private const string SayA1 = "Good morning, I would like to go to the central hospital please.";
    private const string SayB = "Certainly, the route takes about eleven minutes. Shall we go now?";
    private const string SayA2 = "Yes, please start the trip.";

    [SkippableFact]
    public void ThreeTurnsOfTwoSpeakersInOrder()
    {
        RequireModels();
        var (samples, starts) = Dialogue((SayA1, A), (SayB, B), (SayA2, A));
        using var embedder = Embedder();
        var turns = new AudioSegmenter(embedder.Embed).Segment(samples);

        var shown = string.Join("; ", turns.Select(t => $"speaker {t.Speaker} {t.Start:F2}-{t.End:F2} s (overlap {t.Overlap:F2})"));
        Assert.True(turns.Count == 3, $"expected 3 turns, found {turns.Count}: {shown}");
        Assert.Equal(new[] { 1, 2, 1 }, turns.Select(t => t.Speaker).ToArray());
        for (int i = 0; i < 3; i++)
            Assert.True(Math.Abs(turns[i].Start - starts[i]) < 0.6, $"turn {i + 1} begins at {turns[i].Start:F2} s, the voice at {starts[i]:F2} s: {shown}");
    }

    // For comparing another implementation (the pyannote of the V1.4 reference software) on the same
    // audio: writes the dialogue as a 16 kHz mono WAV where MPAI_AUS_DIALOGUE_WAV names a file.
    [SkippableFact]
    public void WritesTheDialogueWhereAskedForComparison()
    {
        var path = Environment.GetEnvironmentVariable("MPAI_AUS_DIALOGUE_WAV");
        Skip.If(string.IsNullOrEmpty(path), "MPAI_AUS_DIALOGUE_WAV is not set.");
        RequireModels();
        var (samples, starts) = Dialogue((SayA1, A), (SayB, B), (SayA2, A));
        var pcm = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short v = (short)Math.Clamp((int)Math.Round(samples[i] * 32767f), short.MinValue, short.MaxValue);
            pcm[2 * i] = (byte)(v & 0xFF); pcm[2 * i + 1] = (byte)((v >> 8) & 0xFF);
        }
        using var fs = File.Create(path!);
        using var bw = new BinaryWriter(fs);
        bw.Write("RIFF"u8.ToArray()); bw.Write(36 + pcm.Length); bw.Write("WAVE"u8.ToArray()); bw.Write("fmt "u8.ToArray());
        bw.Write(16); bw.Write((short)1); bw.Write((short)1); bw.Write(16000); bw.Write(32000); bw.Write((short)2); bw.Write((short)16);
        bw.Write("data"u8.ToArray()); bw.Write(pcm.Length); bw.Write(pcm);
        File.WriteAllText(path + ".txt", "voices begin at " + string.Join(", ", starts.Select(s => s.ToString("F2"))) + " s; speakers A, B, A");
    }
    [SkippableFact]
    public void OneSpeakerIsOneSpeaker()
    {
        RequireModels();
        var (samples, _) = Dialogue((SayA1, A), (SayA2, A));
        using var embedder = Embedder();
        var turns = new AudioSegmenter(embedder.Embed).Segment(samples);
        Assert.True(turns.Select(t => t.Speaker).Distinct().Count() == 1,
            "one voice found as several speakers: " + string.Join("; ", turns.Select(t => $"speaker {t.Speaker} {t.Start:F2}-{t.End:F2}")));
    }

    [SkippableFact]
    public void SilenceHasNoTurns()
    {
        RequireModels();
        using var embedder = Embedder();
        Assert.Empty(new AudioSegmenter(embedder.Embed).Segment(new float[AudioSegmenter.Rate * 3]));
    }

    // Through the Ports: what a Controller gives the AIM and takes from it.
    [SkippableFact]
    public async Task ThroughItsPorts()
    {
        RequireModels();
        var (samples, _) = Dialogue((SayA1, A), (SayB, B), (SayA2, A));
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short v = (short)Math.Clamp((int)Math.Round(samples[i] * 32767f), short.MinValue, short.MaxValue);
            bytes[2 * i] = (byte)(v & 0xFF); bytes[2 * i + 1] = (byte)((v >> 8) & 0xFF);
        }
        var bso = BasicSpeechObject.FromData(bytes, new SpeechQualifier
        {
            SpeechQualifierID = Guid.NewGuid().ToString(),
            Format = new SpeechFormat { ContentFormats = new SpeechContentFormats { RawData = new Pcm { SamplingFrequency = 16000, Precision = 16 } } }
        });
        var input = MpaiJson.ToJson(SpeechObject.FromBasic(bso));

        var store = new AIF.Store.AmdStore(Repository.Amds); store.Scan();
        var ports = AimPortReader.Load(store, Aim);
        var plugin = new AusPlugin();
        var aim = plugin.Create(ports, new Dictionary<string, string> { ["EcapaModel"] = Path.Combine(Repository.Root, "Models", "ecapa-tdnn.onnx") });

        var reply = await aim.ProcessAsync(new Message
        {
            MessageId = "t1", MessageType = "SpeechObject", DataType = "OSD-SPO-V1.5", Payload = input,
            Ports = new Dictionary<string, string> { [ports.Input("OSD-SPO-V1.5")] = input }
        });
        Assert.False(reply.IsError, reply.Payload);

        var speech = JsonNode.Parse(reply.Ports[ports.Output("OSD-SPO-V1.5")])!;
        Assert.Equal("OSD-SPO-V1.5", (string?)speech["Header"]);
        Assert.Equal(3, speech["BasicSpeechObjects"]!.AsArray().Count);
        var who = speech["BasicSpeechObjects"]!.AsArray().Select(e => (string?)e!["BSObjectIDOrBSObject"]!["DescrMetadata"]).ToArray();
        Assert.Equal(new[] { "Speaker 1", "Speaker 2", "Speaker 1" }, who);

        var times = JsonNode.Parse(reply.Ports[ports.Output("OSD-STM-V1.5")])!;
        Assert.Equal("OSD-STM-V1.5", (string?)times["Header"]);
        Assert.Equal(3, times["SimpleTimeData"]!.AsArray().Count);

        var overlap = JsonNode.Parse(reply.Ports[ports.Output("OSD-SOL-V1.5")])!;
        Assert.Equal("OSD-SOL-V1.5", (string?)overlap["Header"]);
        Assert.Equal(3, overlap["SpeechOverlapData"]!.AsArray().Count);
        foreach (var o in overlap["SpeechOverlapData"]!.AsArray())
        {
            var c = (double)o!["Confidence"]!;
            Assert.InRange(c, 0.0, 1.0);
        }
    }
}
