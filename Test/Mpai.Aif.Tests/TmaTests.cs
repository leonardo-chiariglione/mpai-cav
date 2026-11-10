using System.Linq;
using System.Text.Json.Nodes;
using Mpai.Osd.Tma;
using Xunit;

namespace Mpai.Aif.Tests;

// Television Media Analysis (OSD-TMA V1.5): the new AIMs against the schemas of their Data Types.
[Trait("Group", "Fast")]
public class TmaTests
{
    private static JsonObject Bso(string id, string words) => new()
    {
        ["Header"] = "OSD-BSO-V1.5", ["MInstanceID"] = "tma", ["BasicSpeechObjectID"] = id,
        ["BasicSpeechObjectProperties"] = new JsonObject { ["Words"] = words }
    };

    // An entry of a Speech Object, as the schema has it: the Basic Speech Object is held as BSObjectIDOrBSObject.
    private static JsonObject Held(JsonObject bso) => new() { ["BSObjectIDOrBSObject"] = new JsonArray(bso) };

    private static void Valid(JsonObject o) { var v = PgmRig.Check(o); Assert.True(v == "valid", v); }

    // A frame of one colour with a little noise, as a PNG in base64.
    private static string Frame(byte r, byte g, byte b, int seed)
    {
        var rnd = new System.Random(seed);
        using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(64, 48);
        for (var y = 0; y < 48; y++)
            for (var x = 0; x < 64; x++)
                img[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgb24((byte)(r + rnd.Next(-3, 4)), (byte)(g + rnd.Next(-3, 4)), (byte)(b + rnd.Next(-3, 4)));
        using var ms = new System.IO.MemoryStream();
        SixLabors.ImageSharp.ImageExtensions.SaveAsPng(img, ms);
        return System.Convert.ToBase64String(ms.ToArray());
    }

    [Fact]
    public void VideoOfThreeShotsGivesThreeScenes()
    {
        var frames = new JsonArray();
        var shots = new (byte, byte, byte)[] { (30, 60, 210), (210, 210, 40), (20, 110, 50) };
        var n = 0;
        foreach (var (r, g, b) in shots)
            for (var i = 0; i < 20; i++) frames.Add(new JsonObject { ["Data"] = Frame(r, g, b, n++) });
        var video = new JsonObject { ["Header"] = "OSD-BVO-V1.5", ["BasicVisualObjectID"] = "v1", ["BasicVisualObjectData"] = frames };

        var scenes = new VisualChangeDetection("vcd").Handle(TmaTypes.Bvo, 1, video).ToList();
        Assert.Equal(3, scenes.Count);
        Assert.Equal(new[] { 0, 20, 40 }, scenes.Select(s => (int)s.Json["BasicVisualObjectProperties"]!["FirstFrame"]!).ToArray());
        Assert.All(scenes, s => Valid(s.Json));

        var five = new VisualChangeDetection("vcd", new System.Collections.Generic.Dictionary<string, string> { ["ImagesPerScene"] = "5" }).Handle(TmaTypes.Bvo, 1, video).ToList();
        Assert.All(five, s => Assert.Equal(5, s.Json["BasicVisualObjectData"]!.AsArray().Count));
    }

    private static Mpai.Core.InstanceCandidate Candidate(string label, double confidence) => new() { InstanceLabel = label, LabelConfidenceLevel = confidence };

    [Fact]
    public void TheNamesInTheAuxiliaryTextRaiseTheirCandidates()
    {
        var identity = new Mpai.Core.InstanceIdentifier { InstanceIdentifierData = { Candidate("Anna", 0.52), Candidate("Bruno", 0.55), Candidate("Carla", 0.20) } };
        var text = Mpai.Core.OSD.AuxiliaryText.Words(new JsonObject { ["BasicTextData"] = new JsonArray(new JsonObject { ["Data"] = "Tonight Anna talks to Carla." }) }.ToJsonString());
        Assert.Equal("Tonight Anna talks to Carla.", text);
        var raised = Mpai.Core.OSD.AuxiliaryText.RaiseNamed(identity, text!);
        Assert.Equal(new[] { "Anna", "Bruno", "Carla" }, raised.InstanceIdentifierData.Select(c => c.InstanceLabel).ToArray());
        Assert.Equal(0.62, raised.InstanceIdentifierData[0].LabelConfidenceLevel, 3);
        Assert.Equal(0.55, raised.InstanceIdentifierData[1].LabelConfidenceLevel, 3);
        Assert.Equal(0.30, raised.InstanceIdentifierData[2].LabelConfidenceLevel, 3);
    }

    [Fact]
    public void FullToBasicGivesTheBasicObjectsInOrder()
    {
        var full = new JsonObject { ["Header"] = "OSD-SPO-V1.5", ["SpeechObjectID"] = "s1", ["BasicSpeechObjects"] = new JsonArray(Held(Bso("a", "one")), Held(Bso("b", "two")), Held(Bso("c", "three"))) };
        var given = new FullToBasicObject("fbo").Handle(TmaTypes.Spo, 1, full).ToList();
        Assert.Equal(new[] { "a", "b", "c" }, given.Select(g => (string?)g.Json["BasicSpeechObjectID"]).ToArray());
        Assert.All(given, g => { Assert.Equal(TmaTypes.Bso, g.DataType); Valid(g.Json); });
    }

    [Fact]
    public void ASceneIsMadeOfEachBasicSpeechObject()
    {
        var scene = new BasicSpeechSceneDescription("bss").Handle(TmaTypes.Bso, 1, Bso("a", "one")).Single();
        Assert.Equal(TmaTypes.Bss, scene.DataType);
        Valid(scene.Json);
    }

    [Fact]
    public void AnAudioSceneIsMultiplexedAsAScene()
    {
        var audio = new JsonObject { ["Header"] = "OSD-BAS-V1.5", ["MInstanceID"] = "tma", ["BasicAudioSceneDescriptorsID"] = "ears", ["BASSpaceTime"] = TmaTypes.SpaceTime(DateTimeOffset.UtcNow) };
        var bms = new MultimodalSceneMultiplexing("msm").Handle(TmaTypes.Bas, 1, audio).Single();
        Assert.Equal(TmaTypes.Bms, bms.DataType);
        Assert.Equal("ears", (string)bms.Json["BasicMultimodalSceneDescriptorsData"]![0]!["BXSOrBXSID"]!["BasicAudioSceneDescriptorsID"]!);
        Assert.Equal(audio["BASSpaceTime"]!["SpaceTimeID"]!.ToString(), bms.Json["BMSDescriptorsSpaceTime"]!["SpaceTimeID"]!.ToString());     // the Space-Time of the Audio Scene is the one of the Multimodal Scene
    }

    [Fact]
    public void CombiningTheSceneLastReceivedOfEachKindStandsInTheMultimodalScene()
    {
        JsonObject Visual(string id) => new() { ["Header"] = "OSD-BVS-V1.5", ["MInstanceID"] = "hmc", ["BasicVisualSceneDescriptorsID"] = id, ["BasicVisualSceneDescriptorsSpaceTime"] = TmaTypes.SpaceTime(DateTimeOffset.UtcNow) };
        JsonObject Audio(string id) => new() { ["Header"] = "OSD-BAS-V1.5", ["MInstanceID"] = "hmc", ["BasicAudioSceneDescriptorsID"] = id, ["BASSpaceTime"] = TmaTypes.SpaceTime(DateTimeOffset.UtcNow) };
        var msm = new MultimodalSceneMultiplexing("msm", combine: true);

        var first = msm.Handle(TmaTypes.Bvs, 1, Visual("eyes-1")).Single().Json;
        Assert.Single(first["BasicMultimodalSceneDescriptorsData"]!.AsArray());                  // only the visual scene has come

        var second = msm.Handle(TmaTypes.Bas, 1, Audio("ears-1")).Single().Json;
        var held = second["BasicMultimodalSceneDescriptorsData"]!.AsArray().Select(e => (string)e!["BXSOrBXSID"]!["Header"]!).ToArray();
        Assert.Equal(["OSD-BAS-V1.5", "OSD-BVS-V1.5"], held);                                    // both, in one Multimodal Scene
        Assert.Equal(2, (int)second["MultimodalObjectCount"]!);

        var third = msm.Handle(TmaTypes.Bvs, 1, Visual("eyes-2")).Single().Json;                  // a newer visual scene replaces the older one
        var ids = third["BasicMultimodalSceneDescriptorsData"]!.AsArray().Select(e => (string)(e!["BXSOrBXSID"]!["BasicVisualSceneDescriptorsID"] ?? e["BXSOrBXSID"]!["BasicAudioSceneDescriptorsID"])!).ToArray();
        Assert.Equal(["ears-1", "eyes-2"], ids);
    }

    [Fact]
    public void WithoutCombiningEachSceneIsAMultimodalSceneOfItsOwn()
    {
        var msm = new MultimodalSceneMultiplexing("msm");
        var visual = new JsonObject { ["Header"] = "OSD-BVS-V1.5", ["BasicVisualSceneDescriptorsID"] = "eyes" };
        var audio = new JsonObject { ["Header"] = "OSD-BAS-V1.5", ["BasicAudioSceneDescriptorsID"] = "ears" };
        msm.Handle(TmaTypes.Bvs, 1, visual).Single();
        Assert.Single(msm.Handle(TmaTypes.Bas, 1, audio).Single().Json["BasicMultimodalSceneDescriptorsData"]!.AsArray());
    }

    [Fact]
    public void SpeechTextAndIdAreMultiplexedAndCollected()
    {
        var smx = new MultimodalSceneMultiplexing("smx");
        var scene = new BasicSpeechSceneDescription("bss").Handle(TmaTypes.Bso, 1, Bso("a", "one")).Single().Json;
        Assert.Empty(smx.Handle(TmaTypes.Bto, 1, new JsonObject { ["Header"] = "OSD-BTO-V1.5", ["BasicTextObjectID"] = "t1" }));
        Valid(new JsonObject { ["Header"] = "OSD-IID-V1.5", ["MInstanceID"] = "tma", ["InstanceIdentifier"] = "user-1", ["InstanceIdentifierData"] = new JsonArray(new JsonObject { ["InstanceLabel"] = "user-1", ["LabelConfidenceLevel"] = 0.9, ["Taxonomy"] = new JsonObject { ["TaxonomyLevelIDs"] = new JsonArray("person"), ["TaxonomyDataURI"] = "https://example.org/taxonomy" } }) });
        Assert.Empty(smx.Handle(TmaTypes.Iid, 1, new JsonObject { ["Header"] = "OSD-IID-V1.5", ["MInstanceID"] = "tma", ["InstanceIdentifier"] = "user-1", ["InstanceIdentifierData"] = new JsonArray(new JsonObject { ["InstanceLabel"] = "user-1", ["LabelConfidenceLevel"] = 0.9, ["Taxonomy"] = new JsonObject { ["TaxonomyLevelIDs"] = new JsonArray("person"), ["TaxonomyDataURI"] = "https://example.org/taxonomy" } }) }));
        var bms = smx.Handle(TmaTypes.Bss, 1, scene).Single();
        Assert.Equal(TmaTypes.Bms, bms.DataType);
        Valid(bms.Json);
        Assert.Equal(2, bms.Json["BasicObjectsData"]!.AsArray().Count);          // the text and the User ID

        var med = new MultimodalEventDescription("med");
        med.Handle(TmaTypes.Bms, 1, bms.Json).ToList();
        var mev = med.Handle(TmaTypes.Bms, 1, bms.Json).Last();
        Assert.Equal(2, mev.Json["MultimodalSceneDescriptors"]!.AsArray().Count);
        Valid(mev.Json);
    }
}
