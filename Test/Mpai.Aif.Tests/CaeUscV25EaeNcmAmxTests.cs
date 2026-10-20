using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.Metadata;
using Json.Schema;
using Mpai.Cae.Usc;
using Mpai.Cae.Usc.Eae;

namespace Mpai.Aif.Tests;

// CAE-USC V2.5, Enhanced Audioconference Experience: NOISE CANCELLATION and AUDIO DESCRIPTORS MULTIPLEXING, on what the AIMs before them give.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class CaeUscV25EaeNcmAmxTests
{
    private const string Shd = "OSD-BAO-V1.5#1", Tr = "OSD-BAO-V1.5#2", Geo = "OSD-BAS-V1.5#1", Asr = "CAE-ASR-V1.0#1", Out = "OSD-BAO-V1.5#9", Scene = "OSD-BAS-V1.5#9", Time = "OSD-SPT-V1.5#1";
    private const int Rate = 16000;

    private static bool Valid(string[] schema, string json)
    {
        var root = Path.GetFullPath(Repository.Schemas);
        var compiled = PublishedSchemas.At(root)[Path.Combine([root, .. schema])];
        using var document = JsonDocument.Parse(json);
        lock (PublishedSchemas.Lock) return compiled.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;
    }

    private static ArrayGeometry Array() => CaeUscV25EaeTransformTests.Octahedron(512, 0.012);

    // One source of 1 kHz, in a room with noise at every microphone, active from `from` to `to`.
    private static MultiAudio Room(double from, double to, int noise)
    {
        var tone = CaeUscV25EaeSfdTests.PlaneWaves(Array(), 1.2, (1000, 8000, 60, 20));
        var state = 12345u;
        return new MultiAudio(tone.Channels.Select(c => c.Select((v, i) =>
        {
            state = state * 1664525u + 1013904223u;
            var n = (int)(state >> 8) % (2 * noise + 1) - noise;
            return (short)((i >= from * Rate && i < to * Rate ? v : 0) + n);
        }).ToArray()).ToArray(), Rate);
    }

    // The chain up to the Noise Cancellation Module: the transform, the sound field, the sources.
    private static async Task<(string Shd, string Sources, string Scene)> Before(MultiAudio audio)
    {
        var array = Array();
        var transform = await new AatAimProcessor("aat", "in", "out").ProcessAsync(new Message { MessageId = "m", Ports = new() { ["in"] = AudioObject.MakeMultichannel("capture", audio, array) } });
        var shd = await new SfdAimProcessor("sfd", "g", "t", "o").ProcessAsync(new Message { MessageId = "m", Ports = new() { ["g"] = array.ToJson().ToJsonString(), ["t"] = transform.Ports["out"] } });
        var sds = await new SdsAimProcessor("sds", "s", "a", "t", "g", "r").ProcessAsync(new Message { MessageId = "m", Ports = new() { ["s"] = shd.Ports["o"] } });
        Assert.False(sds.IsError, sds.Payload);
        return (shd.Ports["o"], sds.Ports["t"], sds.Ports["g"]);
    }

    private static Task<Message> Clean(string shd, string sources, string scene, NcmAimProcessor? ncm = null) =>
        (ncm ?? new NcmAimProcessor("ncm", Shd, Tr, Geo, Asr, Out)).ProcessAsync(new Message { MessageId = "m", Ports = new() { [Shd] = shd, [Tr] = sources, [Geo] = scene } });

    private static double Power(TransformBlocks t, int channel, IEnumerable<int> blocks, int fromBin, int toBin) =>
        blocks.Average(n => Enumerable.Range(fromBin, toBin - fromBin + 1).Average(b => t.Blocks[n][channel][b].Magnitude * t.Blocks[n][channel][b].Magnitude));

    // ---- noise cancellation ------------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheNoiseMeasuredWhereNothingIsActiveIsTakenAwayWhereASourceIsAndTheSourceStays()
    {
        var (shd, sources, scene) = await Before(Room(0.3, 0.9, 300));
        var reply = await Clean(shd, sources, scene);
        Assert.False(reply.IsError, reply.Payload);
        Assert.True(Valid(["OSD", "V1.5", "data", "BasicAudioObject.json"], reply.Ports[Out]));
        var before = TransformObject.Read(sources).Blocks; var after = TransformObject.Read(reply.Ports[Out]).Blocks;
        var active = Enumerable.Range((int)(0.4 * Rate / 256), (int)(0.4 * Rate / 256));                           // well inside 0.3 to 0.9 s
        Assert.Equal(1, after.Channels);
        Assert.True(Power(after, 0, active, 80, 120) < 0.25 * Power(before, 0, active, 80, 120), "the noise between the tone and the top of the band is down by 6 dB");
        Assert.InRange(active.Average(n => after.Blocks[n][0][32].Magnitude / before.Blocks[n][0][32].Magnitude), 0.95, 1.02);       // the tone stays
        Assert.True(after.Blocks[2][0].All(v => v == Complex.Zero), "where the source is silent the channel is silent");
    }

    [Fact]
    public async Task WhereThereIsNoSilenceToMeasureTheNoiseInTheAIMIsAPassthrough()
    {
        var (shd, sources, scene) = await Before(Room(-1, 2, 300));                                                   // the source is active all the time
        var reply = await Clean(shd, sources, scene);
        Assert.False(reply.IsError, reply.Payload);
        var before = TransformObject.Read(sources).Blocks; var after = TransformObject.Read(reply.Ports[Out]).Blocks;
        Assert.Equal(before.Blocks.Count, after.Blocks.Count);
        Assert.All(Enumerable.Range(0, before.Blocks.Count), n => Assert.All(Enumerable.Range(0, before.Bins), b => Assert.True((before.Blocks[n][0][b] - after.Blocks[n][0][b]).Magnitude < 1e-3 * (1 + before.Blocks[n][0][b].Magnitude))));
    }

    [Fact]
    public async Task TheNoiseCancellationModuleSaysWhatItCannotDo()
    {
        var (shd, sources, scene) = await Before(Room(0.3, 0.9, 300));
        var quiet = await new NcmAimProcessor("ncm", Shd, Tr, Geo, Asr, Out).ProcessAsync(new Message { MessageId = "m", Ports = new() { [Shd] = shd } });
        Assert.False(quiet.IsError, quiet.Payload); Assert.Empty(quiet.Ports);                                                  // the sound field and no sources: a quiet room, nothing to clean
        Assert.True((await new NcmAimProcessor("ncm", Shd, Tr, Geo, Asr, Out).ProcessAsync(new Message { MessageId = "m", Ports = new() { [Tr] = sources } })).IsError);        // sources and no sound field
        var twoSources = SceneDescriptors.Make("x", [new("source-1", 0, 0, []), new("source-2", 90, 0, [])]);
        var mismatch = await Clean(shd, sources, twoSources);
        Assert.True(mismatch.IsError); Assert.Contains("2 sources and the Transform Audio 1 channels", mismatch.Payload);
        var notShd = await Clean(sources, sources, scene);                                                            // the source is not a decomposition: one channel
        Assert.True(notShd.IsError); Assert.Contains("(N + 1)^2", notShd.Payload);
    }

    // ---- multiplexing ----------------------------------------------------------------------------------------------------------------------------------------------------------------

    private static async Task<(string Audio, string Scene)> Enhanced()
    {
        var s1 = CaeUscV25EaeSfdTests.PlaneWaves(Array(), 1.0, (1000, 8000, 60, 20)); var s2 = CaeUscV25EaeSfdTests.PlaneWaves(Array(), 1.0, (2000, 6000, -90, 0));
        var mix = new MultiAudio(s1.Channels.Select((c, m) => c.Select((v, i) => (short)((i < 0.6 * Rate ? v : 0) + (i >= 0.4 * Rate ? s2.Channels[m][i] : 0))).ToArray()).ToArray(), Rate);
        var (shd, sources, scene) = await Before(mix);
        var clean = await Clean(shd, sources, scene);
        var audio = await new AstAimProcessor("ast", "i", "o").ProcessAsync(new Message { MessageId = "m", Ports = new() { ["i"] = clean.Ports[Out] } });
        Assert.False(audio.IsError, audio.Payload);
        return (audio.Ports["o"], scene);
    }

    [Fact]
    public async Task EachSourceOfTheGeometryCarriesItsAudioObject()
    {
        var (audio, scene) = await Enhanced();
        var spaceTime = new JsonObject { ["Header"] = "OSD-SPT-V1.5", ["SpaceTimeID"] = "room" }.ToJsonString();
        var reply = await new AmxAimProcessor("amx", Time, "OSD-BAO-V1.5#1", Geo, Scene).ProcessAsync(new Message { MessageId = "m", Ports = new() { ["OSD-BAO-V1.5#1"] = audio, [Geo] = scene, [Time] = spaceTime } });
        Assert.False(reply.IsError, reply.Payload);
        var json = reply.Ports[Scene];
        Assert.True(Valid(["OSD", "V1.5", "data", "BasicAudioSceneDescriptors.json"], json));
        var root = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(2, root["AudioObjectCount"]!.GetValue<int>()); Assert.Equal("room", root["BASSpaceTime"]!["SpaceTimeID"]!.GetValue<string>());
        foreach (var m in root["BasicAudioSceneDescriptorsData"]!.AsArray())
        {
            var o = m!["AObjectIDOrAObject"]![0]!.AsObject();
            Assert.Equal("OSD-BAO-V1.5", o["Header"]!.GetValue<string>());                                          // the Audio Object itself, in place of its ID
            var item = AudioObject.Read(o.ToJsonString()).Single();
            Assert.Equal(Rate, item.Audio.Rate); Assert.Equal(Rate, item.Audio.Samples.Length);                     // a second of audio
            Assert.NotNull(m["AudioObjectSpaceTime"]!["SpatialAttitude1"]);                                           // and where it is
        }
    }

    [Fact]
    public async Task ASourceWithNoAudioKeepsItsIdAndAnAudioObjectWithNoSourceIsAdded()
    {
        var (audio, scene) = await Enhanced();
        var withThird = SceneDescriptors.Make("g", [.. SceneDescriptors.Read(scene), new SceneSource("source-9", 10, 0, [])]);
        var reply = await new AmxAimProcessor("amx", Time, "OSD-BAO-V1.5#1", Geo, Scene).ProcessAsync(new Message { MessageId = "m", Ports = new() { ["OSD-BAO-V1.5#1"] = audio, [Geo] = withThird } });
        var members = JsonNode.Parse(reply.Ports[Scene])!["BasicAudioSceneDescriptorsData"]!.AsArray();
        Assert.Equal(3, members.Count);
        Assert.Equal("source-9", members.Single(m => m!["AObjectIDOrAObject"]![0] is JsonValue)!["AObjectIDOrAObject"]![0]!.GetValue<string>());

        var onlyOne = SceneDescriptors.Make("g", [SceneDescriptors.Read(scene)[0]]);                                    // the scene names one source, the audio holds two
        var added = await new AmxAimProcessor("amx", Time, "OSD-BAO-V1.5#1", Geo, Scene).ProcessAsync(new Message { MessageId = "m", Ports = new() { ["OSD-BAO-V1.5#1"] = audio, [Geo] = onlyOne } });
        var all = JsonNode.Parse(added.Ports[Scene])!["BasicAudioSceneDescriptorsData"]!.AsArray();
        Assert.Equal(2, all.Count); Assert.Null(all[1]!["AudioObjectSpaceTime"]);
        Assert.True(Valid(["OSD", "V1.5", "data", "BasicAudioSceneDescriptors.json"], added.Ports[Scene]));

        Assert.True((await new AmxAimProcessor("amx", Time, "OSD-BAO-V1.5#1", Geo, Scene).ProcessAsync(new Message { MessageId = "m", Ports = new() { [Geo] = scene } })).IsError);
        var notScene = await new AmxAimProcessor("amx", Time, "OSD-BAO-V1.5#1", Geo, Scene).ProcessAsync(new Message { MessageId = "m", Ports = new() { ["OSD-BAO-V1.5#1"] = audio, [Geo] = audio } });
        Assert.True(notScene.IsError); Assert.Contains("not Basic Audio Scene Descriptors", notScene.Payload);
    }
}
