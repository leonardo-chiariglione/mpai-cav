using System.Text.Json;
using Mpai.SpatialAudio;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Aif.Tests;

// THE USER AGENT'S SPATIAL AUDIO (the author, 2026/10/02: "ideally, an AOD is a
// spatial audio renderer"; 2026/10/04: "AOD is UA's business"; on Steam Audio,
// Apache-2.0). Judged,
// with broadband noise heard binaurally: a source on the left is louder in the left
// ear, on the right in the right ear; a source twice as far is about 6 dB quieter; a
// source whose directional pattern is 20 dB down behind it is about 20 dB quieter
// facing away than facing the user. Then a Basic Audio Scene of two sounds - 440 Hz
// on the left, 880 Hz on the right - rendered as the User hears it: a stereo Basic Audio
// Object at 48 kHz, valid against its schema, each tone stronger in its own ear.
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
public class SpatialAudioTests
{
    private static readonly string Steam = Path.Combine(Repository.Root, "Models", "SteamAudio");

    private static float[] Noise(int n, int seed) { var r = new Random(seed); return Enumerable.Range(0, n).Select(_ => (float)(0.3 * (r.NextDouble() * 2 - 1))).ToArray(); }
    private static float[] Tone(double hz, int n) => Enumerable.Range(0, n).Select(i => (float)(0.3 * Math.Sin(2 * Math.PI * hz * i / 48000.0))).ToArray();
    private static double Rms(float[] m, int channels, int channel) { double s = 0; var n = 0; for (var i = channel; i < m.Length; i += channels) { s += m[i] * (double)m[i]; n++; } return Math.Sqrt(s / Math.Max(1, n)); }
    private static double Db(double a, double b) => 20 * Math.Log10(a / b);

    // The energy of one frequency in one channel (Goertzel).
    private static double Energy(short[] pcm, int channels, int channel, double hz, int rate)
    {
        double w = 2 * Math.PI * hz / rate, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
        for (var i = channel; i < pcm.Length; i += channels) { var s0 = pcm[i] + c * s1 - s2; s2 = s1; s1 = s0; }
        return s1 * s1 + s2 * s2 - c * s1 * s2;
    }

    [SkippableFact]
    public void ScenesHeardFromTheUser()
    {
        Skip.IfNot(SpatialRenderer.Available(Steam), "Models/SteamAudio is absent: Steam Audio is obtained separately (docs/models-provenance.md).");
        var r = new Dictionary<string, string>();

        // 1. The renderer.
        using (var renderer = new SpatialRenderer(Steam))
        {
            var noise = Noise(96000, 1);
            var user = new SpatialRenderer.User([0, 0, 1.6], 0, 0);
            var pattern = new DirectionalPattern([(0, 0, 0), (90, 0, -6), (-90, 0, -6), (180, 0, -20)]);
            float[] At(double x, double y, double yaw, DirectionalPattern? p) => renderer.Render([new SpatialRenderer.Source(noise, [x, y, 1.6], yaw, 0, 1, p)], user);

            var left = At(-2, 0, -90, null); var right = At(2, 0, 90, null);
            r["a source on the left, on the right"] =
                $"left ear {(Db(Rms(left, 2, 0), Rms(left, 2, 1)) > 6 ? "louder" : "not louder")}, right ear {(Db(Rms(right, 2, 1), Rms(right, 2, 0)) > 6 ? "louder" : "not louder")}";
            var near = At(0, 2, 180, null); var far = At(0, 4, 180, null);
            var d = Db(Rms(near, 2, 0) + Rms(near, 2, 1), Rms(far, 2, 0) + Rms(far, 2, 1));
            r["twice as far"] = Math.Abs(d - 6) < 1.5 ? "about 6 dB quieter" : $"{d:0.0} dB quieter";
            var facing = At(0, 2, 180, pattern); var away = At(0, 2, 0, pattern);
            var f = Db(Rms(facing, 2, 0) + Rms(facing, 2, 1), Rms(away, 2, 0) + Rms(away, 2, 1));
            r["a pattern 20 dB down behind, facing away"] = Math.Abs(f - 20) < 1.5 ? "about 20 dB quieter" : $"{f:0.0} dB quieter";
        }

        // 2. A Basic Audio Scene, as the User hears it.
        BasicAudioObject Sound(string id, double hz) => new()
        {
            BasicAudioObjectID = id,
            BasicAudioObjectData = [new InlineAudioData(Convert.ToBase64String(SceneAudio.Wav(Tone(hz, 48000), 1, 48000)))]
        };
        SpaceTime Place(double x, double y) => new()
        {
            SpaceTimeID = Guid.NewGuid().ToString(),
            SpatialAttitude1 = new SpatialAttitude
            {
                ObjectSpatialAttitudeID = Guid.NewGuid().ToString(),
                Position = new Position { PositionID = Guid.NewGuid().ToString(), CartPosition = [x, y, 1.6] },
                Orientation = new Orientation { OrientationID = Guid.NewGuid().ToString(), EulerAngles = [0, 0, 180] }
            }
        };
        var scene = new BasicAudioSceneDescriptors
        {
            MInstanceID = "ASM", BasicAudioSceneDescriptorsID = "duet", BASSpaceTime = Place(0, 0), AudioObjectCount = 2,
            UserPoV = new PointOfView { PointOfViewID = "user", CartPosition = [0, 0, 1.6], Orientation = [0, 0, 0] },
            BasicAudioSceneDescriptorsEntries =
            [
                new BasicAudioSceneEntry { AudioObjectSpaceTime = Place(-1.5, 1.5), AudioObjectIDOrAudioObject = Sound("low", 440) },
                new BasicAudioSceneEntry { AudioObjectSpaceTime = Place(1.5, 1.5), AudioObjectIDOrAudioObject = Sound("high", 880) }
            ]
        };
        var played = new AudioRendering(Steam).Render(MpaiJson.ToJson(scene));
        var payload = MpaiJson.ToJson(played);
        var wav = played.Data;
        int channels = BitConverter.ToInt16(wav, 22), rate = BitConverter.ToInt32(wav, 24);
        var pcm = new short[(wav.Length - 44) / 2];
        Buffer.BlockCopy(wav, 44, pcm, 0, pcm.Length * 2);

        var schemas = AIF.Metadata.PublishedSchemas.At(Repository.Schemas);
        var bao = schemas[Path.GetFullPath(Path.Combine(Repository.Schemas, "OSD/V1.5/data/BasicAudioObject.json"))];
        bool valid; string why = "";
        using (var doc = JsonDocument.Parse(payload))
            lock (AIF.Metadata.PublishedSchemas.Lock)
            {
                var ev = bao.Evaluate(doc.RootElement, new Json.Schema.EvaluationOptions { OutputFormat = Json.Schema.OutputFormat.List });
                valid = ev.IsValid;
                why = string.Join("; ", ev.Details.Where(x => x.Errors is { Count: > 0 }).Select(x => $"{x.InstanceLocation} {x.Errors!.First().Value}").Take(4));
            }

        r["a Basic Audio Scene heard"] = $"{played.Header}, {channels} channels at {rate} Hz, {(valid ? "valid" : "not valid: " + why)}";
        r["440 Hz on the left"] = Energy(pcm, 2, 0, 440, rate) > Energy(pcm, 2, 1, 440, rate) * 2 ? "stronger in the left ear" : "not stronger in the left ear";
        r["880 Hz on the right"] = Energy(pcm, 2, 1, 880, rate) > Energy(pcm, 2, 0, 880, rate) * 2 ? "stronger in the right ear" : "not stronger in the right ear";
        Expected.Match("spatial-audio.json", r);
    }
}
