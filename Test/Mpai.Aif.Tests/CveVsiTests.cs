using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Cve.Vsi;
using Mpai.Osd.VisualScene;
using AIF.Controller;
using AIF.Store;

namespace Mpai.Aif.Tests;

// CVE-VSI-V1.0, VISUAL SCENE OBJECT IDENTIFICATION, on its own. The Reference Model (the author, 2026-11-08): one input, the Visual Scene; two outputs of Basic Visual Object, the
// Face Visual Objects (Port 1) and the Other Visual Objects (Port 2). The Visual Objects of the Scene are told by a face detector: one with a face leaves at Port 1 tagged Face, one
// without at Port 2 tagged Object. The AIM is given the ports its AMD declares.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class CveVsiTests
{
    private const string Aim = "1CVE-VSI-V1.0-I01";

    private static AimPortReader Ports() { var store = new AmdStore(Repository.Amds); store.Scan(); return AimPortReader.Load(store, Aim); }

    private static string Picture(params string[] parts) => Path.Combine([Repository.Root, "Test", "Data", .. parts]);

    private static BasicVisualSceneEntry Entry(string id, byte[] jpeg) => new()
    {
        VObjectIDOrVObject = new BasicVisualObject { BasicVisualObjectID = id, Data = jpeg }
    };

    [SkippableFact]
    public async Task TheFaceLeavesAtPort1TaggedFaceAndTheOtherAtPort3TaggedObject()
    {
        var model = Path.Combine(Repository.Root, "Models", "scrfd_10g_bnkps.onnx");
        Skip.IfNot(File.Exists(model), "Models/scrfd_10g_bnkps.onnx is absent: it is obtained by the Controller as the AIM's AccessSources say.");
        var face = Directory.EnumerateFiles(Picture("Images", "Faces"), "*.jpg", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).First();
        using var detector = new ScrfdFaceDetector(model);
        var ports = Ports();
        var processor = new CveVsiAimProcessor(Aim, detector, ports);

        var scene = new BasicVisualSceneDescriptors
        {
            BasicVisualSceneDescriptorsID = "scene", VisualObjectCount = 2,
            BasicVisualSceneDescriptorsEntries = [Entry("person", File.ReadAllBytes(face)), Entry("plain", File.ReadAllBytes(Picture("red.jpg")))]
        };
        var reply = await processor.ProcessAsync(new Message { MessageId = "m", Ports = new Dictionary<string, string> { [ports.Input("OSD-BVS-V1.5")] = MpaiJson.ToJson(scene) } });
        Assert.False(reply.IsError, reply.Payload);

        // two outputs of the same Data Type, numbered 1 and 2 in the AMD
        var faceKey = ports.Output("OSD-BVO-V1.5", 1); var otherKey = ports.Output("OSD-BVO-V1.5", 3);
        Assert.NotEqual(faceKey, otherKey);
        var faces = MpaiJson.FromJson<BasicVisualObject>(reply.Ports[faceKey])!;
        var others = MpaiJson.FromJson<BasicVisualObject>(reply.Ports[otherKey])!;
        Assert.Equal("person", faces.BasicVisualObjectID);
        Assert.Equal("Face", faces.VisualQualifier!.Attributes!.VisualObjectType);
        Assert.Equal("plain", others.BasicVisualObjectID);
        Assert.Equal("Object", others.VisualQualifier!.Attributes!.VisualObjectType);
    }

    [Fact]
    public void TheAmdDeclaresOneInputAndThreeOutputsOfBasicVisualObject()
    {
        var ports = Ports();
        Assert.NotNull(ports.Input("OSD-BVS-V1.5"));
        Assert.Equal(3, new[] { 1, 2, 3 }.Select(n => ports.Output("OSD-BVO-V1.5", n)).Distinct().Count());        // Face 1, Body 2, Other 3
        Assert.Throws<InvalidOperationException>(() => ports.Output("OSD-BVO-V1.5", 4));
    }
}
