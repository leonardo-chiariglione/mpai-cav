using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.Metadata;
using Json.Schema;
using Mpai.Cae.Usc;
using Mpai.Cae.Usc.Eae;

namespace Mpai.Aif.Tests;

// CAE-USC V2.5, Enhanced Audioconference Experience: SPEECH DETECTION AND SEPARATION. Two sources, in two directions, active at partly the same time: the AIM finds both, says where they are
// and when they are active, and gives each in a channel of its own with the other much reduced.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class CaeUscV25EaeSdsTests
{
    private const string Shd = "OSD-BAO-V1.5#1", Asr = "CAE-ASR-V1.0#1", OutAudio = "OSD-BAO-V1.5#9", OutScene = "OSD-BAS-V1.5#9", OutAsr = "CAE-ASR-V1.0#9";
    private const int Rate = 16000;

    private static bool Valid(string[] schema, string json)
    {
        var root = Path.GetFullPath(Repository.Schemas);
        var compiled = PublishedSchemas.At(root)[Path.Combine([root, .. schema])];
        using var document = JsonDocument.Parse(json);
        lock (PublishedSchemas.Lock) return compiled.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;
    }

    private static MultiAudio Gate(MultiAudio a, double from, double to) =>
        new(a.Channels.Select(c => c.Select((v, i) => i >= from * a.Rate && i < to * a.Rate ? v : (short)0).ToArray()).ToArray(), a.Rate);

    private static MultiAudio Sum(MultiAudio a, MultiAudio b) => new(a.Channels.Select((c, m) => c.Select((v, i) => (short)(v + b.Channels[m][i])).ToArray()).ToArray(), a.Rate);

    private static ArrayGeometry Array() => CaeUscV25EaeTransformTests.Octahedron(512, 0.012);

    // The array hears the audio, the transform and the sound field description give the coefficients, and the AIM under test works on them.
    private static async Task<Message> Separate(MultiAudio audio, string? asr = null, SdsAimProcessor? sds = null)
    {
        var array = Array();
        var transform = await new AatAimProcessor("aat", "in", "out").ProcessAsync(new Message { MessageId = "m", Ports = new() { ["in"] = AudioObject.MakeMultichannel("capture", audio, array) } });
        var shd = await new SfdAimProcessor("sfd", "g", "t", "o").ProcessAsync(new Message { MessageId = "m", Ports = new() { ["g"] = array.ToJson().ToJsonString(), ["t"] = transform.Ports["out"] } });
        Assert.False(shd.IsError, shd.Payload);
        var ports = new Dictionary<string, string> { [Shd] = shd.Ports["o"] }; if (asr is not null) ports[Asr] = asr;
        return await (sds ?? new SdsAimProcessor("sds", Shd, Asr, OutAudio, OutScene, OutAsr)).ProcessAsync(new Message { MessageId = "m", Ports = ports });
    }

    private static double[] Unit(double az, double el) => [Math.Cos(el * Math.PI / 180) * Math.Cos(az * Math.PI / 180), Math.Cos(el * Math.PI / 180) * Math.Sin(az * Math.PI / 180), Math.Sin(el * Math.PI / 180)];

    // The angle, in degrees, between two directions.
    private static double Angle((double Az, double El) a, double az, double el)
    {
        var u = Unit(a.Az, a.El); var v = Unit(az, el);
        return Math.Acos(Math.Clamp(u[0] * v[0] + u[1] * v[1] + u[2] * v[2], -1, 1)) * 180 / Math.PI;
    }

    private static MultiAudio TwoSources(double secondsEach = 1.0)
    {
        var s1 = CaeUscV25EaeSfdTests.PlaneWaves(Array(), 1.0, (1000, 8000, 60, 20));
        var s2 = CaeUscV25EaeSfdTests.PlaneWaves(Array(), 1.0, (2000, 6000, -90, 0));
        return Sum(Gate(s1, 0, 0.6), Gate(s2, 0.4, 1.0));
    }

    [Fact]
    public async Task TwoSourcesAreFoundWhereTheyAreAndWhenTheyAreActive()
    {
        var reply = await Separate(TwoSources());
        Assert.False(reply.IsError, reply.Payload);
        Assert.True(Valid(["OSD", "V1.5", "data", "BasicAudioSceneDescriptors.json"], reply.Ports[OutScene]));
        Assert.True(Valid(["OSD", "V1.5", "data", "BasicAudioObject.json"], reply.Ports[OutAudio]));
        var sources = SceneDescriptors.Read(reply.Ports[OutScene]);
        Assert.Equal(2, sources.Count);
        var one = sources.Single(s => Angle((s.Azimuth, s.Elevation), 60, 20) < 8); var two = sources.Single(s => Angle((s.Azimuth, s.Elevation), -90, 0) < 8);
        Assert.InRange(one.Active[0].Start, 0, 0.05); Assert.InRange(one.Active[^1].End, 0.55, 0.7);       // the first, from the start to 0.6 s
        Assert.InRange(two.Active[0].Start, 0.3, 0.45); Assert.InRange(two.Active[^1].End, 0.95, 1.05);     // the second, from 0.4 s to the end
    }

    [Fact]
    public async Task EachSourceIsInAChannelOfItsOwnWithTheOtherMuchReduced()
    {
        var reply = await Separate(TwoSources());
        var sources = SceneDescriptors.Read(reply.Ports[OutScene]);
        var (blocks, _) = TransformObject.Read(reply.Ports[OutAudio]);
        Assert.Equal(2, blocks.Channels);
        var channelOne = sources.ToList().FindIndex(s => Angle((s.Azimuth, s.Elevation), 60, 20) < 8); var channelTwo = 1 - channelOne;
        var overlap = blocks.Blocks[(int)(0.5 * Rate / 256)];                                                  // both are active at 0.5 s
        double Level(int channel, int bin) => overlap[channel][bin].Magnitude;
        Assert.True(Level(channelOne, 32) > 3.2 * Level(channelOne, 64), "the first source is at 1 kHz and has little at 2 kHz");     // 10 dB
        Assert.True(Level(channelTwo, 64) > 3.2 * Level(channelTwo, 32), "the second source is at 2 kHz and has little at 1 kHz");
        Assert.True(blocks.Blocks[(int)(0.8 * Rate / 256)][channelOne].All(v => v.Magnitude == 0), "the first source is silent after 0.6 s");
        Assert.True(blocks.Blocks[(int)(0.2 * Rate / 256)][channelTwo].All(v => v.Magnitude == 0), "the second source is silent before 0.4 s");
    }

    [Fact]
    public async Task TheAudioSceneReferenceIsGivenOnAndABadOneIsRefused()
    {
        var asr = new JsonObject { ["Header"] = "CAE-ASR-V1.0", ["PolynomialVariable"] = "AngleSine", ["Polynomial"] = new JsonArray(new JsonObject { ["Coefficient"] = 1.0, ["Exponent"] = 0 }) }.ToJsonString();
        var reply = await Separate(TwoSources(), asr);
        Assert.False(reply.IsError, reply.Payload); Assert.Equal(asr, reply.Ports[OutAsr]);
        Assert.False((await Separate(TwoSources())).Ports.ContainsKey(OutAsr));                                // none in, none out
        var bad = await Separate(TwoSources(), "{\"Header\":\"OSD-BAS-V1.5\"}");
        Assert.True(bad.IsError); Assert.Contains("not a CAE-ASR-V1.0", bad.Payload);
    }

    [Fact]
    public async Task ASilentRoomAndCoefficientsThatAreNotWholeOrdersAreSaidSo()
    {
        var silence = await Separate(new MultiAudio(Enumerable.Range(0, 6).Select(_ => new short[8000]).ToArray(), Rate));
        Assert.False(silence.IsError, silence.Payload); Assert.Empty(silence.Ports);                                            // a quiet room: nothing is given, and it is not a fault
        var array = Array();                                                                                      // the six channels of the array are not (N + 1)^2 coefficients
        var transform = await new AatAimProcessor("aat", "in", "out").ProcessAsync(new Message { MessageId = "m", Ports = new() { ["in"] = AudioObject.MakeMultichannel("capture", TwoSources(), array) } });
        var notShd = await new SdsAimProcessor("sds", Shd, Asr, OutAudio, OutScene, OutAsr).ProcessAsync(new Message { MessageId = "m", Ports = new() { [Shd] = transform.Ports["out"] } });
        Assert.True(notShd.IsError); Assert.Contains("(N + 1)^2", notShd.Payload);
        Assert.True((await new SdsAimProcessor("sds", Shd, Asr, OutAudio, OutScene, OutAsr).ProcessAsync(new Message { MessageId = "m", Ports = new() })).IsError);
    }
}
