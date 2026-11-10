using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.Store;
using Mpai.Hmc.Sid;
using Mpai.Osd.Mdx;

namespace Mpai.Aif.Tests;

// OSD-MDX-V1.5 (Multimodal Scene Demultiplexing) and HMC-SID-V2.2 (Multimodal Scene Integration and Description), on synthetic scenes: they work on the data by the Header of each element and need no model.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class HmcMdxSidTests
{
    private const string Mdx = "1OSD-MDX-V1.5-I01", Sid = "1HMC-SID-V2.2-I01";

    private static AimPortReader Ports(string aim) { var store = new AmdStore(Repository.Amds); store.Scan(); return AimPortReader.Load(store, aim); }

    private static JsonObject Bao(string id) => new() { ["Header"] = "OSD-BAO-V1.5", ["BasicAudioObjectID"] = id, ["AudioQualifier"] = new JsonObject { ["Header"] = "TFA-AUQ-V1.5", ["AudioQualifierID"] = "q-" + id } };
    private static JsonObject Qualified(string id)
    {
        var o = Bao(id);
        o["AudioQualifier"] = new JsonObject { ["Header"] = "TFA-AUQ-V1.5", ["AudioQualifierID"] = "q-" + id, ["SubTypes"] = "Speech", ["Formats"] = new JsonObject() };
        return o;
    }
    private static JsonObject Bvo(string id, string? type) => new()
    {
        ["Header"] = "OSD-BVO-V1.5", ["BasicVisualObjectID"] = id,
        ["VisualQualifier"] = new JsonObject { ["Header"] = "TFA-VIQ-V1.5", ["Attributes"] = new JsonObject { ["VisualObjectType"] = type } }
    };
    private static JsonObject Bso(string id) => new() { ["Header"] = "OSD-BSO-V1.5", ["BasicSpeechObjectID"] = id };
    private static JsonObject Bto(string id) => new() { ["Header"] = "OSD-BTO-V1.5", ["BasicTextObjectID"] = id };
    private static JsonObject Space() => new() { ["Header"] = "OSD-SPT-V1.5", ["SpaceTimeID"] = "t" };

    private static JsonObject Entry(JsonObject o) => new() { ["BXOSpaceTime"] = Space(), ["BXOOrBXOID"] = o };

    private static JsonObject Bms(params JsonObject[] objects) => new()
    {
        ["Header"] = "OSD-BMS-V1.5", ["MInstanceID"] = "m", ["BasicMultimodalSceneDescriptorsID"] = "scene", ["BMSDescriptorsSpaceTime"] = Space(),
        ["BasicObjectsData"] = new JsonArray(objects.Select(o => (JsonNode)Entry(o)).ToArray())
    };

    private static async Task<Message> Demultiplex(JsonObject scene)
    {
        var ports = Ports(Mdx);
        return await new OsdMdxAimProcessor(Mdx, ports).ProcessAsync(new Message { MessageId = "m", Ports = new Dictionary<string, string> { [ports.Input("OSD-BMS-V1.5")] = scene.ToJsonString() } });
    }

    [Fact]
    public async Task TheObjectsOfASceneComeOutEachOnItsOwnPort()
    {
        var ports = Ports(Mdx);
        var said = await Demultiplex(Bms(Bao("sound"), Bvo("thing", "Object"), Bvo("person", "Face"), Bso("words"), Bto("typed")));
        Assert.False(said.IsError, said.Payload);
        string Id(string port, string key) => (string)JsonNode.Parse(said.Ports[port])![key]!;
        Assert.Equal("sound", Id(ports.Output("OSD-BAO-V1.5"), "BasicAudioObjectID"));
        Assert.Equal("thing", Id(ports.Output("OSD-BVO-V1.5", 1), "BasicVisualObjectID"));          // a Visual Object that is not a face: Port 1
        Assert.Equal("person", Id(ports.Output("OSD-BVO-V1.5", 2), "BasicVisualObjectID"));         // the face: Port 2
        Assert.Equal("words", Id(ports.Output("OSD-BSO-V1.5"), "BasicSpeechObjectID"));
        Assert.Equal("typed", Id(ports.Output("OSD-BTO-V1.5"), "BasicTextObjectID"));
        Assert.True(said.Ports.ContainsKey(ports.Output("OSD-SPT-V1.5")));
    }

    [Fact]
    public void ASceneHoldsManyObjectsOfAKindAndEachLeavesOnThePortOfItsKindInTheOrderItStands()
    {
        var scene = Bms(Bso("s1"), Bao("a1"), Bvo("face-1", "Face"), Bvo("thing-1", "Object"), Bso("s2"), Bao("a2"), Bvo("face-2", "Face"), Bao("a3"));
        var given = OsdMdxAimProcessor.Demultiplexed(scene.ToJsonString());
        string[] Ids(string dataType, int port, string key) => given.Where(g => g.DataType == dataType && g.Port == port).Select(g => (string)JsonNode.Parse(g.Json)![key]!).ToArray();
        Assert.Equal(["s1", "s2"], Ids("OSD-BSO-V1.5", 1, "BasicSpeechObjectID"));
        Assert.Equal(["a1", "a2", "a3"], Ids("OSD-BAO-V1.5", 1, "BasicAudioObjectID"));
        Assert.Equal(["thing-1"], Ids("OSD-BVO-V1.5", 1, "BasicVisualObjectID"));                // not faces: Port 1
        Assert.Equal(["face-1", "face-2"], Ids("OSD-BVO-V1.5", 2, "BasicVisualObjectID"));       // faces: Port 2
    }

    [Fact]
    public async Task AsAProcessorItGivesTheFirstOfEachKind()
    {
        var ports = Ports(Mdx);
        var said = await Demultiplex(Bms(Bao("a1"), Bao("a2"), Bvo("face-1", "Face"), Bvo("face-2", "Face")));
        Assert.Equal("a1", (string)JsonNode.Parse(said.Ports[ports.Output("OSD-BAO-V1.5")])!["BasicAudioObjectID"]!);
        Assert.Equal("face-1", (string)JsonNode.Parse(said.Ports[ports.Output("OSD-BVO-V1.5", 2)])!["BasicVisualObjectID"]!);
    }

    [Fact]
    public async Task WhatTheSceneDoesNotHoldIsNotProduced()
    {
        var ports = Ports(Mdx);
        var said = await Demultiplex(Bms(Bto("typed")));
        Assert.True(said.Ports.ContainsKey(ports.Output("OSD-BTO-V1.5")));
        Assert.False(said.Ports.ContainsKey(ports.Output("OSD-BAO-V1.5")));
        Assert.False(said.Ports.ContainsKey(ports.Output("OSD-BVO-V1.5", 2)));
        Assert.False(said.Ports.ContainsKey(ports.Output("OSD-BAS-V1.5")));
    }

    [Fact]
    public async Task ABasicAudioSceneGivesItsAudioSceneDescriptorsAndItsGeometry()
    {
        var ports = Ports(Mdx);
        var audio = new JsonObject
        {
            ["Header"] = "OSD-BAS-V1.5", ["MInstanceID"] = "m", ["BasicAudioSceneDescriptorsID"] = "ears", ["BASSpaceTime"] = Space(),
            ["BasicAudioSceneDescriptorsData"] = new JsonArray(
                new JsonObject { ["AudioObjectSpaceTime"] = Space(), ["AObjectIDOrAObject"] = new JsonArray(Bao("one")) },
                new JsonObject { ["AudioObjectSpaceTime"] = Space(), ["AObjectIDOrAObject"] = new JsonArray(Bao("two")) })
        };
        var scene = Bms(Bto("typed"));
        scene["BasicMultimodalSceneDescriptorsData"] = new JsonArray(new JsonObject { ["BXSSpaceTime"] = Space(), ["BXSOrBXSID"] = new JsonArray(audio) });
        var said = await Demultiplex(scene);
        Assert.Equal("ears", (string)JsonNode.Parse(said.Ports[ports.Output("OSD-BAS-V1.5")])!["BasicAudioSceneDescriptorsID"]!);
        var geometry = JsonNode.Parse(said.Ports[ports.Output("OSD-BAG-V1.5")])!;
        Assert.Equal("OSD-BAG-V1.5", (string)geometry["Header"]!);
        Assert.Equal(2, geometry["BasicAudioSceneObjects"]!.AsArray().Count);                       // each member with where it is and its qualifier
        Assert.Equal("q-one", (string)geometry["BasicAudioSceneObjects"]![0]!["AudioSceneObjectQualifiers"]!["AudioQualifierID"]!);
        Assert.Equal("one", (string)JsonNode.Parse(said.Ports[ports.Output("OSD-BAO-V1.5")])!["BasicAudioObjectID"]!);       // an Object found only inside a scene is given too
    }

    // What the published schema of a data type says about a datum: the violations, none when it is what the schema describes.
    private static IReadOnlyList<string> Violations(string header, string json)
    {
        var file = header switch
        {
            "OSD-BAG-V1.5" => "BasicAudioSceneGeometry", "OSD-ASG-V1.5" => "AudioSceneGeometry", "OSD-BSG-V1.5" => "BasicSpeechSceneGeometry", "OSD-SSG-V1.5" => "SpeechSceneGeometry",
            "OSD-BVG-V1.5" => "BasicVisualSceneGeometry", "OSD-VSG-V1.5" => "VisualSceneGeometry", "OSD-BMS-V1.5" => "BasicMultimodalSceneDescriptors", _ => throw new ArgumentException(header)
        };
        var check = new Mpai.Mmc.Jsi.SchemaCheck("https://schemas.mpai.community/OSD/V1.5/data/" + file + ".json", Path.Combine(Repository.Schemas, "OSD", "V1.5", "data", file + ".json"), Repository.Schemas);
        return check.Violations(json) ?? throw new InvalidOperationException("the schema " + file + " could not be read");
    }

    // A full Multimodal Scene Descriptors is taken apart as a Basic one is: the Objects leave as they stand, full ones as full ones.
    [Fact]
    public void AMultimodalSceneDescriptorsGivesItsFullObjectsAsFullObjects()
    {
        var scene = new JsonObject
        {
            ["Header"] = "OSD-MSD-V1.5", ["MultimodalSceneDescriptorsID"] = "full", ["MultimodalSceneDescriptorsSpaceTime"] = Space(),
            ["Objects"] = new JsonArray(
                new JsonObject { ["ObjectSpaceTime"] = Space(), ["ObjectIDOrObject"] = new JsonArray(new JsonObject { ["Header"] = "OSD-SPO-V1.5", ["SpeechObjectID"] = "spo" }) },
                new JsonObject { ["ObjectSpaceTime"] = Space(), ["ObjectIDOrObject"] = new JsonArray(new JsonObject { ["Header"] = "OSD-AUO-V1.5", ["AudioObjectID"] = "auo" }) },
                new JsonObject { ["ObjectSpaceTime"] = Space(), ["ObjectIDOrObject"] = new JsonArray(new JsonObject { ["Header"] = "OSD-VIO-V1.5", ["VisualObjectID"] = "vio" }) },
                new JsonObject { ["ObjectSpaceTime"] = Space(), ["ObjectIDOrObject"] = new JsonArray(new JsonObject { ["Header"] = "OSD-TXO-V1.5", ["TextObjectID"] = "txo" }) })
        };
        var given = OsdMdxAimProcessor.Demultiplexed(scene.ToJsonString());
        Assert.Equal(["OSD-SPT-V1.5", "OSD-SPO-V1.5", "OSD-TXO-V1.5", "OSD-AUO-V1.5", "OSD-VIO-V1.5"], given.Select(g => g.DataType).ToArray());
    }

    // The geometry of a Scene is made of the members of the Scene, Basic for a Basic Scene, full for a full one, in each medium.
    [Theory]
    [InlineData("OSD-BAS-V1.5", "BasicAudioSceneDescriptorsID", "BASSpaceTime", "AudioObjectSpaceTime", "AObjectIDOrAObject", "OSD-BAO-V1.5", "AudioQualifier", "OSD-BAG-V1.5", "BasicAudioSceneObjects")]
    [InlineData("OSD-ASD-V1.5", "AudioSceneDescriptorsID", "AudioSceneDescriptorsSpaceTime", "AudioObjectSpaceTime", "AObjectIDOrAObject", "OSD-BAO-V1.5", "AudioQualifier", "OSD-ASG-V1.5", "AudioSceneObjects")]
    [InlineData("OSD-BSS-V1.5", "BasicSpeechSceneDescriptorsID", "BasicSpeechSceneDescriptorsSpaceTime", "ObjectSpaceTime", "ObjectIDOrObject", "OSD-BSO-V1.5", "SpeechQualifier", "OSD-BSG-V1.5", "BasicSpeechSceneObjects")]
    [InlineData("OSD-SSD-V1.5", "SpeechSceneDescriptorsID", "SpeechSceneDescriptorsSpaceTime", "ObjectSpaceTime", "ObjectIDOrObject", "OSD-BSO-V1.5", "SpeechQualifier", "OSD-SSG-V1.5", "SpeechSceneObjects")]
    [InlineData("OSD-BVS-V1.5", "BasicVisualSceneDescriptorsID", "BVSDescriptorsSpaceTime", "VisualObjectSpaceTime", "VObjectIDOrVObject", "OSD-BVO-V1.5", "VisualQualifier", "OSD-BVG-V1.5", "VisualObjects")]
    [InlineData("OSD-VSD-V1.5", "VisualDescriptorsID", "VisualDescriptorsSpaceTime", "ObjectSpaceTime", "ObjectIDOrObject", "OSD-BVO-V1.5", "VisualQualifier", "OSD-VSG-V1.5", "Objects")]
    public void EverySceneGivesItsGeometryInTheFormOfItsOwnKind(string header, string idKey, string spaceKey, string entrySpace, string entryObject, string objectHeader, string qualifierKey, string geometryHeader, string listKey)
    {
        var qualifier = qualifierKey switch
        {
            "AudioQualifier" => new JsonObject { ["Header"] = "TFA-AUQ-V1.5", ["AudioQualifierID"] = "q1", ["SubTypes"] = "Speech", ["Formats"] = new JsonObject() },
            "SpeechQualifier" => new JsonObject { ["Header"] = "TFA-SPQ-V1.5", ["SpeechQualifierID"] = "q1", ["SubTypes"] = new JsonObject(), ["Formats"] = new JsonObject() },
            _ => new JsonObject { ["Header"] = "TFA-VIQ-V1.5", ["VisualQualifierID"] = "q1", ["SubTypes"] = new JsonObject(), ["Formats"] = new JsonObject() }
        };
        var member = new JsonObject { ["Header"] = objectHeader, [qualifierKey] = qualifier };
        var scene = new JsonObject
        {
            ["Header"] = header, [idKey] = "the-scene", [spaceKey] = Space(),
            ["Members"] = new JsonArray(new JsonObject { [entrySpace] = Space(), [entryObject] = new JsonArray(member) }, new JsonObject { [entrySpace] = Space(), [entryObject] = new JsonArray(member.DeepClone()) })
        };
        var whole = Bms(Bto("typed"));
        whole["BasicMultimodalSceneDescriptorsData"] = new JsonArray(new JsonObject { ["BXSSpaceTime"] = Space(), ["BXSOrBXSID"] = new JsonArray(scene) });
        var geometry = OsdMdxAimProcessor.Demultiplexed(whole.ToJsonString()).Single(g => g.DataType == geometryHeader);
        var node = JsonNode.Parse(geometry.Json)!;
        Assert.Equal(geometryHeader, (string)node["Header"]!);
        Assert.Equal(2, node[listKey]!.AsArray().Count);                                          // each member, with where it is
        Assert.True(Violations(geometryHeader, geometry.Json).Count == 0, string.Join(" || ", Violations(geometryHeader, geometry.Json)));                                  // and it is what the schema of that geometry says
    }

    [Fact]
    public async Task SomethingThatIsNotASceneIsRefused()
    {
        var ports = Ports(Mdx);
        var said = await new OsdMdxAimProcessor(Mdx, ports).ProcessAsync(new Message { MessageId = "m", Ports = new Dictionary<string, string> { [ports.Input("OSD-BMS-V1.5")] = Bao("x").ToJsonString() } });
        Assert.True(said.IsError);
    }

    private static JsonObject Avatar(bool scene = false, bool placed = false)
    {
        var avatar = new JsonObject
        {
            ["Header"] = "PAF-PAV-V1.6", ["MInstanceID"] = "m", ["PortableAvatarID"] = "pav",
            ["AvatarOrAvatarID"] = new JsonArray("avatar-1"),
            ["FaceDescriptorsObject"] = new JsonObject { ["Header"] = "PAF-FDO-V1.6", ["FaceDescriptorsObjectID"] = "fdo", ["FaceDescriptorsData"] = new JsonArray() },
            ["BodyDescriptorsObject"] = new JsonObject { ["Header"] = "PAF-BDO-V1.6", ["BodyDescriptorsObjectID"] = "bdo", ["BodyDescriptorsData"] = new JsonArray() }
        };
        if (scene) avatar["MultimodalSceneDescriptors"] = new JsonArray(Bms(Bto("already-there")));
        if (placed) avatar["AvatarSpatialAttitude"] = new JsonObject { ["Header"] = "OSD-OSA-V1.5", ["ObjectSpatialAttitudeID"] = "mine" };
        return avatar;
    }

    private static async Task<Message> Integrate(JsonObject avatar)
    {
        var ports = Ports(Sid);
        return await new HmcSidAimProcessor(Sid, ports).ProcessAsync(new Message { MessageId = "m", Ports = new Dictionary<string, string> { [ports.Input("PAF-PAV-V1.6")] = avatar.ToJsonString() } });
    }

    private static string Out(string dataType, int port = 1) => Ports(Sid).Output(dataType, port);

    // The Reference Model: MSID gives the Face, Body and other Visual Objects on Ports 1, 2 and 3 of OSD-BVO, and the Audio and the Speech Objects, of the scene the Portable Avatar conveys.
    [Fact]
    public async Task TheObjectsOfTheConveyedSceneLeaveOnTheirPorts()
    {
        var avatar = Avatar();
        avatar["MultimodalSceneDescriptors"] = new JsonArray(Bms(Bvo("a-face", "Face"), Bvo("a-body", "Body"), Bvo("a-chair", "Object"), Bao("a-sound"), Bso("a-voice")));
        var said = await Integrate(avatar);
        Assert.False(said.IsError, said.Payload);
        Assert.Equal("a-face", (string)JsonNode.Parse(said.Ports[Out("OSD-BVO-V1.5", 1)])!["BasicVisualObjectID"]!);
        Assert.Equal("a-body", (string)JsonNode.Parse(said.Ports[Out("OSD-BVO-V1.5", 2)])!["BasicVisualObjectID"]!);
        Assert.Equal("a-chair", (string)JsonNode.Parse(said.Ports[Out("OSD-BVO-V1.5", 3)])!["BasicVisualObjectID"]!);
        Assert.Equal("a-sound", (string)JsonNode.Parse(said.Ports[Out("OSD-BAO-V1.5")])!["BasicAudioObjectID"]!);
        Assert.Equal("a-voice", (string)JsonNode.Parse(said.Ports[Out("OSD-BSO-V1.5")])!["BasicSpeechObjectID"]!);
    }

    [Fact]
    public async Task TheScenesThemselvesLeaveToo()
    {
        var audio = new JsonObject
        {
            ["Header"] = "OSD-BAS-V1.5", ["MInstanceID"] = "m", ["BasicAudioSceneDescriptorsID"] = "ears", ["BASSpaceTime"] = Space(),
            ["BasicAudioSceneDescriptorsData"] = new JsonArray(new JsonObject { ["AudioObjectSpaceTime"] = Space(), ["AObjectIDOrAObject"] = new JsonArray(Qualified("one")) })
        };
        var conveyed = Bms(Bto("typed"));
        conveyed["BasicMultimodalSceneDescriptorsData"] = new JsonArray(new JsonObject { ["BXSSpaceTime"] = Space(), ["BXSOrBXSID"] = new JsonArray(audio) });
        var avatar = Avatar();
        avatar["MultimodalSceneDescriptors"] = new JsonArray(conveyed);
        var said = await Integrate(avatar);
        Assert.False(said.IsError, said.Payload);
        Assert.Equal("ears", (string)JsonNode.Parse(said.Ports[Out("OSD-BAS-V1.5")])!["BasicAudioSceneDescriptorsID"]!);       // the Audio Scene Descriptors, as the scene holds them
    }

    [Fact]
    public async Task APortableAvatarWithNoSceneGivesNothingToTheOtherPorts()
    {
        var said = await Integrate(Avatar());                                                    // the Avatar is placed in a generic scene that holds no Face, Body, Visual, Audio or Speech Object
        Assert.False(said.IsError, said.Payload);
        Assert.Empty(said.Ports);
    }

    [Fact]
    public async Task ASceneThatIsNotBasicMultimodalIsRefusedWithTheReason()
    {
        var avatar = Avatar();
        avatar["MultimodalSceneDescriptors"] = new JsonArray(new JsonObject { ["Header"] = "OSD-MSD-V1.5" });
        var said = await Integrate(avatar);
        Assert.True(said.IsError);
        Assert.Contains("OSD-BMS", said.Payload);
    }
}
