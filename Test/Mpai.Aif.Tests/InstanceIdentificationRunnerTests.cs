using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.Store;
using Mpai.Aif.PortData;
using Mpai.Cae.Aii;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Osd.Vii;
using Mpai.Osd.VisualScene;

namespace Mpai.Aif.Tests;

// CAE-AII-V2.5 and CVE-VII-V1.0 as the HMC Entity and Context Understanding needs them: the Scene Descriptors (OSD-BAS, OSD-BVS) come in and go out unchanged, and every object that arrives is identified,
// one Instance Identifier for each. Run as processors, for one Message, the same: the scene goes on, and the one object is identified.
[Trait("Group", "Fast")]
public class InstanceIdentificationRunnerTests
{
    private static string Models => Path.Combine(Repository.Root, "Models");
    private static readonly AmdStore Amds = Scan();
    private static AmdStore Scan() { var s = new AmdStore(Repository.Amds); s.Scan(); return s; }

    private const string Aii = "1CAE-AII-V2.5-I01", Vii = "1CVE-VII-V1.0-I01";
    private static AimContext Context() => new(CancellationToken.None, Task.CompletedTask);

    private static string AudioObject(string id, double seconds = 1) => MpaiJson.ToJson(new BasicAudioObject
    {
        BasicAudioObjectID = id,
        BasicAudioObjectData = { new InlineAudioData(Convert.ToBase64String(new byte[(int)(16000 * 2 * seconds)])) }          // silence, mono 16 kHz 16 bit
    });

    private static string AudioScene(string id) => new JsonObject { ["Header"] = "OSD-BAS-V1.5", ["BasicAudioSceneDescriptorsID"] = id }.ToJsonString();
    private static string VisualScene(string id) => new JsonObject { ["Header"] = "OSD-BVS-V1.5", ["BasicVisualSceneDescriptorsID"] = id }.ToJsonString();

    private static string Picture(string id) => PortObjects.WriteVisual(new BasicVisualObject
    {
        BasicVisualObjectID = id,
        Data = File.ReadAllBytes(Directory.GetFiles(Path.Combine(Repository.Root, "Test", "Data", "Images", "Animals", "cat"), "*.jpg").Order().First())
    });

    // ------------------------------------------------------------------ AII

    [Fact]
    public async Task AiiGivesTheSceneOnAsItCameAndIdentifiesTheObjectOfTheSameMessage()
    {
        using var classifier = new SoundClassifier(Path.Combine(Models, "yamnet.onnx"), Path.Combine(Models, "yamnet_class_map.csv"));
        var ports = AimPortReader.Load(Amds, Aii);
        var aim = new CaeAiiAimProcessor(Aii, classifier, ports);

        var sceneOnly = await aim.ProcessAsync(new Message { MessageId = "m", Ports = new Dictionary<string, string> { [ports.Input("OSD-BAS-V1.5")] = AudioScene("ears") } });
        Assert.False(sceneOnly.IsError, sceneOnly.Payload);
        Assert.Equal(AudioScene("ears"), sceneOnly.Ports[ports.Output("OSD-BAS-V1.5")]);
        Assert.False(sceneOnly.Ports.ContainsKey(ports.Output("OSD-IID-V1.5")));                  // no object, no identifier

        var both = await aim.ProcessAsync(new Message { MessageId = "m", Ports = new Dictionary<string, string> { [ports.Input("OSD-BAS-V1.5")] = AudioScene("ears"), [ports.Input("OSD-BAO-V1.5")] = AudioObject("one") } });
        Assert.False(both.IsError, both.Payload);
        Assert.Equal(AudioScene("ears"), both.Ports[ports.Output("OSD-BAS-V1.5")]);
        Assert.Equal("OSD-IID-V1.5", (string)JsonNode.Parse(both.Ports[ports.Output("OSD-IID-V1.5")])!["Header"]!);

        var neither = await aim.ProcessAsync(new Message { MessageId = "m", Ports = new Dictionary<string, string>() });
        Assert.True(neither.IsError);
    }

    [Fact]
    public async Task AiiRunnerIdentifiesEveryObjectAndPassesEveryScene()
    {
        using var classifier = new SoundClassifier(Path.Combine(Models, "yamnet.onnx"), Path.Combine(Models, "yamnet_class_map.csv"));
        var ports = AimPortReader.Load(Amds, Aii);
        var aim = new CaeAiiAimProcessor(Aii, classifier, ports);
        var at = TimeSpan.Zero;
        var drive = new DrivePorts([
            (at += TimeSpan.FromMilliseconds(10), "OSD-BAS-V1.5", 1, AudioScene("first")),
            (at += TimeSpan.FromMilliseconds(10), "OSD-BAO-V1.5", 1, AudioObject("a")),
            (at += TimeSpan.FromMilliseconds(10), "OSD-BAO-V1.5", 1, AudioObject("b")),
            (at += TimeSpan.FromMilliseconds(10), "OSD-BAO-V1.5", 1, AudioObject("c")),
            (at += TimeSpan.FromMilliseconds(10), "OSD-BAS-V1.5", 1, AudioScene("second"))]);

        await aim.RunAsync(drive, Context());

        Assert.Equal(["first", "second"], drive.Written.Where(w => w.DataType == "OSD-BAS-V1.5").Select(w => (string)JsonNode.Parse(w.Json)!["BasicAudioSceneDescriptorsID"]!));
        Assert.Equal(3, drive.Written.Count(w => w.DataType == "OSD-IID-V1.5"));                  // one Instance Identifier for each of the three objects
    }

    // ------------------------------------------------------------------ VII

    [Fact]
    public async Task ViiGivesTheSceneOnAsItCameAndIdentifiesTheObjectOfTheSameMessage()
    {
        using var detector = new YoloxObjectDetector(Path.Combine(Models, "yolox_s.onnx"));
        var ports = AimPortReader.Load(Amds, Vii);
        var aim = new ViiAimProcessor(Vii, detector, ports);

        var sceneOnly = await aim.ProcessAsync(new Message { MessageId = "m", Ports = new Dictionary<string, string> { [ports.Input("OSD-BVS-V1.5")] = VisualScene("eyes") } });
        Assert.False(sceneOnly.IsError, sceneOnly.Payload);
        Assert.Equal(VisualScene("eyes"), sceneOnly.Ports[ports.Output("OSD-BVS-V1.5")]);
        Assert.False(sceneOnly.Ports.ContainsKey(ports.Output("OSD-IID-V1.5")));

        var both = await aim.ProcessAsync(new Message { MessageId = "m", Ports = new Dictionary<string, string> { [ports.Input("OSD-BVS-V1.5")] = VisualScene("eyes"), [ports.Input("OSD-BVO-V1.5")] = Picture("cat") } });
        Assert.False(both.IsError, both.Payload);
        Assert.Equal(VisualScene("eyes"), both.Ports[ports.Output("OSD-BVS-V1.5")]);
        Assert.Equal("OSD-IID-V1.5", (string)JsonNode.Parse(both.Ports[ports.Output("OSD-IID-V1.5")])!["Header"]!);
    }

    [Fact]
    public async Task ViiRunnerIdentifiesEveryObjectAndPassesEveryScene()
    {
        using var detector = new YoloxObjectDetector(Path.Combine(Models, "yolox_s.onnx"));
        var ports = AimPortReader.Load(Amds, Vii);
        var aim = new ViiAimProcessor(Vii, detector, ports);
        var at = TimeSpan.Zero;
        var drive = new DrivePorts([
            (at += TimeSpan.FromMilliseconds(10), "OSD-BVS-V1.5", 1, VisualScene("first")),
            (at += TimeSpan.FromMilliseconds(10), "OSD-BVO-V1.5", 1, Picture("a")),
            (at += TimeSpan.FromMilliseconds(10), "OSD-BVO-V1.5", 1, Picture("b")),
            (at += TimeSpan.FromMilliseconds(10), "OSD-BVS-V1.5", 1, VisualScene("second"))]);

        await aim.RunAsync(drive, Context());

        Assert.Equal(["first", "second"], drive.Written.Where(w => w.DataType == "OSD-BVS-V1.5").Select(w => (string)JsonNode.Parse(w.Json)!["BasicVisualSceneDescriptorsID"]!));
        Assert.Equal(2, drive.Written.Count(w => w.DataType == "OSD-IID-V1.5"));
    }
}
