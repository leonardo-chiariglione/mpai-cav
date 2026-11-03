using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.Metadata;
using Json.Schema;
using Mpai.Cae.Usc;
using Mpai.Cae.Usc.Eae;

namespace Mpai.Aif.Tests;

// CAE-USC V2.5, Enhanced Audioconference Experience: the Audio Analysis Transform and the Audio Synthesis Transform, and the data they carry (the multichannel audio of an array, its
// geometry, the transform in blocks).
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class CaeUscV25EaeTransformTests
{
    private const string In = "OSD-BAO-V1.5#1", Out = "OSD-BAO-V1.5#9";
    private const int Rate = 16000;

    private static bool Valid(string[] schema, string json)
    {
        var root = Path.GetFullPath(Repository.Schemas);
        var compiled = PublishedSchemas.At(root)[Path.Combine([root, .. schema])];
        using var document = JsonDocument.Parse(json);
        lock (PublishedSchemas.Lock) return compiled.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;
    }

    // A six-microphone array: one on each axis, 4.2 cm from the centre.
    public static ArrayGeometry Octahedron(int blockSize = 512, double radius = 0.042) =>
        new("array-1", "Spherical", Rate, blockSize, [[radius, 0, 0], [-radius, 0, 0], [0, radius, 0], [0, -radius, 0], [0, 0, radius], [0, 0, -radius]]);

    private static short[][] Noise(int channels, int length, int seed, int level = 3000)
    {
        var state = (uint)seed; var result = new short[channels][];
        for (var c = 0; c < channels; c++) { result[c] = new short[length]; for (var i = 0; i < length; i++) { state = state * 1664525u + 1013904223u; result[c][i] = (short)((int)(state >> 8) % (2 * level + 1) - level); } }
        return result;
    }

    private static Message Of(string json) => new() { MessageId = "m", Ports = new Dictionary<string, string> { [In] = json } };

    [Fact]
    public void TheMicrophoneArrayGeometryIsAValidOneAndIsReadBack()
    {
        var geometry = Octahedron();
        var root = Path.GetFullPath(Repository.Schemas); var compiled = PublishedSchemas.At(root)[Path.Combine(root, "CAE1", "V2.5", "data", "MicrophoneArrayGeometry.json")];
        using var doc = JsonDocument.Parse(geometry.ToJson().ToJsonString()); EvaluationResults result;
        lock (PublishedSchemas.Lock) result = compiled.Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, string.Join(" | ", result.Details.Where(d => !d.IsValid && d.Errors is not null).SelectMany(d => d.Errors!.Select(e => d.InstanceLocation + ": " + e.Value))));
        var back = ArrayGeometry.FromJson(geometry.ToJson());
        Assert.Equal(512, back.BlockSize); Assert.Equal(Rate, back.Rate); Assert.Equal(6, back.Count);
        Assert.Equal(geometry.Positions[3], back.Positions[3]);
    }

    [Fact]
    public void TheMultichannelAudioObjectCarriesTheChannelsAndTheGeometryAndIsValid()
    {
        var audio = new MultiAudio(Noise(6, 1000, 1), Rate);
        var json = AudioObject.MakeMultichannel("capture", audio, Octahedron());
        Assert.True(Valid(["OSD", "V1.5", "data", "BasicAudioObject.json"], json));
        var (read, geometry) = AudioObject.ReadMultichannel(json);
        Assert.Equal(audio.Channels, read.Channels);
        Assert.Equal(6, geometry!.Count);
    }

    [Fact]
    public async Task TheTransformGivesTheSpectrumOfEachChannelForEachBlockAndKeepsTheGeometry()
    {
        var tone = new short[8000]; for (var i = 0; i < tone.Length; i++) tone[i] = (short)(10000 * Math.Sin(2 * Math.PI * 1000 * i / Rate));       // 1 kHz
        var audio = new MultiAudio([tone, tone, tone, tone, tone, tone], Rate);
        var reply = await new AatAimProcessor("aat", In, Out).ProcessAsync(Of(AudioObject.MakeMultichannel("capture", audio, Octahedron())));
        Assert.False(reply.IsError, reply.Payload);
        Assert.True(Valid(["OSD", "V1.5", "data", "BasicAudioObject.json"], reply.Ports[Out]));
        var (blocks, geometry) = TransformObject.Read(reply.Ports[Out]);
        Assert.Equal(6, blocks.Channels); Assert.Equal(257, blocks.Bins); Assert.Equal(33, blocks.Blocks.Count);          // ceil(8000 / 256) + 1 blocks
        Assert.Equal(8000, blocks.Samples); Assert.NotNull(geometry);
        var middle = blocks.Blocks[10][0];
        Assert.Equal(32, Enumerable.Range(0, 257).MaxBy(b => middle[b].Magnitude));                                      // 1000 Hz / (16000 / 512) = bin 32
    }

    [Fact]
    public async Task TheSynthesisReturnsWhatTheAnalysisTook()
    {
        var audio = new MultiAudio(Noise(6, 8000 + 77, 2), Rate);                                 // not a whole number of blocks
        var transform = await new AatAimProcessor("aat", In, Out).ProcessAsync(Of(AudioObject.MakeMultichannel("capture", audio, Octahedron())));
        var back = await new AstAimProcessor("ast", In, Out).ProcessAsync(Of(transform.Ports[Out]));
        Assert.False(back.IsError, back.Payload);
        Assert.True(Valid(["OSD", "V1.5", "data", "BasicAudioObject.json"], back.Ports[Out]));
        var objects = AudioObject.Read(back.Ports[Out]);
        Assert.Equal(["source-1", "source-2", "source-3", "source-4", "source-5", "source-6"], objects.Select(o => o.Id));
        for (var c = 0; c < 6; c++)
        {
            Assert.Equal(audio.Length, objects[c].Audio.Samples.Length);
            Assert.All(Enumerable.Range(0, audio.Length), i => Assert.InRange(objects[c].Audio.Samples[i] - audio.Channels[c][i], -1, 1));      // to the rounding of a sample
        }
    }

    [Fact]
    public async Task TheTransformsSayWhatTheyCannotDo()
    {
        var audio = new MultiAudio(Noise(5, 500, 3), Rate);                                          // five channels for a six-microphone array
        var mismatch = await new AatAimProcessor("aat", In, Out).ProcessAsync(Of(AudioObject.MakeMultichannel("capture", audio, Octahedron())));
        Assert.True(mismatch.IsError); Assert.Contains("6 microphones and the audio 5 channels", mismatch.Payload);
        var badBlock = await new AatAimProcessor("aat", In, Out).ProcessAsync(Of(AudioObject.MakeMultichannel("capture", new MultiAudio(Noise(6, 500, 3), Rate), Octahedron(blockSize: 500))));
        Assert.True(badBlock.IsError); Assert.Contains("not a power of two", badBlock.Payload);
        var notTransform = await new AstAimProcessor("ast", In, Out).ProcessAsync(Of(AudioObject.Make("pcm", new Audio(new short[100], Rate))));
        Assert.True(notTransform.IsError); Assert.Contains("not in the transform domain", notTransform.Payload);
        Assert.True((await new AatAimProcessor("aat", In, Out).ProcessAsync(new Message { MessageId = "m", Ports = new() })).IsError);
    }
}
