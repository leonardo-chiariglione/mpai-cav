using System.Text.Json;
using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Cav.Ess;
using Mpai.Paf.Vcr;

namespace Mpai.Aif.Tests;

// PORTABLE AVATAR DE-MULTIPLEXING OF THE AVATAR-BASED VIDEOCONFERENCE (PAF-PDX), running continuously on Portable Avatars as they come.
public class PdxTests
{
    // A Portable Avatar of the meeting: the avatar's ID - its nickname - and, when it is introduced, its model; where it is; its face and body
    // animation; what it says; its Personal Status; the scene it is in.
    // The seats of the table: four places on a circle of 1.2 m around its centre, a quarter turn apart.
    public static (double X, double Y) Seat(int i) => (Math.Round(1.2 * Math.Cos(i * Math.PI / 2), 3), Math.Round(1.2 * Math.Sin(i * Math.PI / 2), 3));

    public static JsonObject Portable(string id, long ms, bool introduce, string? says = null, int seat = 0)
    {
        var avatar = new JsonArray(id);
        if (introduce) avatar.Add(new JsonObject { ["Header"] = "OSD-B3O-V1.5", ["Basic3DModelObjectID"] = "model-of-" + id, ["Basic3DModelObjectData"] = new JsonArray(new JsonObject { ["Data"] = "AAEC" }) });
        var pav = new JsonObject
        {
            ["Header"] = "PAF-PAV-V1.6", ["PortableAvatarID"] = $"PAV-{id}-{ms}", ["PortableAvatarTime"] = EssJson.SimpleTime($"PAV-{id}-{ms}-T", ms),
            ["AvatarOrAvatarID"] = avatar, ["AvatarSpaceTime"] = EssJson.SimpleTime($"st-{id}", ms),
            ["AvatarSpatialAttitude"] = EssJson.Attitude($"osa-{id}", ms, (Seat(seat).X, Seat(seat).Y, 0), (0.1, 0.1, 0.1), (0, 0, 0)),
            ["FaceDescriptorsObject"] = new JsonObject { ["Header"] = "PAF-FDO-V1.6", ["FaceDescriptorsObjectID"] = "face-" + id },
            ["BodyDescriptorsObject"] = new JsonObject { ["Header"] = "PAF-BDO-V1.6", ["BodyDescriptorsObjectID"] = "body-" + id },
            ["EntityPersonalStatus"] = new JsonObject { ["Header"] = "MMC-EPS-V2.5", ["EntityPersonalStatusID"] = "eps-" + id },
            ["AVSceneDescriptors"] = new JsonObject
            {
                ["Header"] = "OSD-BMS-V1.5", ["MInstanceID"] = "", ["BasicAVSceneDescriptorsID"] = "scene", ["AVObjectCount"] = 0,
                ["BAVSDescriptorsSpaceTime"] = new JsonObject { ["Header"] = "OSD-SPT-V1.5", ["MInstanceID"] = "", ["SpaceTimeID"] = "scene-st" }
            }
        };
        if (says is not null)
        {
            pav["SPOOrSPOID"] = new JsonArray(new JsonObject { ["Header"] = "OSD-BSO-V1.5", ["BasicSpeechObjectID"] = "speech-" + id });
            pav["TextObject"] = new JsonObject { ["Header"] = "OSD-BTO-V1.5", ["BasicTextObjectID"] = "text-" + id, ["Text"] = says };
        }
        return pav;
    }

    private static async Task<List<(TimeSpan At, string DataType, int PortNumber, string Json)>> Run(params (long Ms, JsonObject Pav)[] arrivals)
    {
        var ports = new DrivePorts(arrivals.Select(a => (TimeSpan.FromMilliseconds(a.Ms), PortableAvatarDemultiplexing.Pav, 1, a.Pav.ToJsonString())));
        await new PortableAvatarDemultiplexing("1PAF-PDX-V1.6-I01").RunAsync(ports, AimContext.None);
        return ports.Written;
    }

    private static string Id(string json, string property) => (string)JsonNode.Parse(json)![property]!;

    [Fact]
    public async Task APortableAvatarIsGivenOutAsItsParts()
    {
        var written = await Run((100, Portable("anna", 100, introduce: true, says: "Hello")));
        var types = written.Select(w => w.DataType).ToList();
        foreach (var expected in new[] { "OSD-IID-V1.5", "PAF-AVT-V1.6", "OSD-OSA-V1.5", "PAF-FDO-V1.6", "PAF-BDO-V1.6", "OSD-BSO-V1.5", "OSD-BTO-V1.5", "MMC-EPS-V2.5", "OSD-BMS-V1.5", "OSD-SPT-V1.5" })
            Assert.Contains(expected, types);
        Assert.DoesNotContain("OSD-SEL-V1.5", types);                       // the Portable Avatar has none
        Assert.All(written, w => Assert.Equal(1, w.PortNumber));

        // The Avatar ID is the nickname, and is in the ID of every object of the avatar.
        Assert.Equal("anna", Id(written.First(w => w.DataType == "OSD-IID-V1.5").Json, "InstanceIdentifier"));
        Assert.Equal("anna#attitude", Id(written.First(w => w.DataType == "OSD-OSA-V1.5").Json, "ObjectSpatialAttitudeID"));
        Assert.Equal("anna#face", Id(written.First(w => w.DataType == "PAF-FDO-V1.6").Json, "FaceDescriptorsObjectID"));
        Assert.Equal("anna#body", Id(written.First(w => w.DataType == "PAF-BDO-V1.6").Json, "BodyDescriptorsObjectID"));
        Assert.Equal("anna#speech", Id(written.First(w => w.DataType == "OSD-BSO-V1.5").Json, "BasicSpeechObjectID"));
        // The avatar is the model and its animation, with its ID.
        var avatar = JsonNode.Parse(written.First(w => w.DataType == "PAF-AVT-V1.6").Json)!;
        Assert.Equal("anna", (string)avatar["AvatarID"]!);
        Assert.Equal("model-of-anna", (string)avatar["AvatarData"]!["ModelOrModelID"]![0]!["3DModel"]!["Basic3DModelObjectID"]!);
        Assert.Equal("anna#face", (string)avatar["AvatarData"]!["FaceDescriptorsObject"]!["FaceDescriptorsObjectID"]!);
        Assert.Equal("anna#body", (string)avatar["AvatarData"]!["BodyDescriptorsObject"]!["BodyDescriptorsObjectID"]!);
        Assert.Equal("anna", AvatarIds.AvatarOf("anna#attitude"));
    }

    [Fact]
    public async Task TheAvatarIDIsAValidInstanceIdentifier()
    {
        var written = await Run((100, Portable("anna", 100, introduce: true)));
        var schema = AIF.Metadata.PublishedSchemas.At(Repository.Schemas)[Path.GetFullPath(Path.Combine(Repository.Schemas, "OSD", "V1.5", "data", "InstanceIdentifier.json"))];
        using var doc = JsonDocument.Parse(written.First(w => w.DataType == "OSD-IID-V1.5").Json);
        lock (AIF.Metadata.PublishedSchemas.Lock) Assert.True(schema.Evaluate(doc.RootElement).IsValid);
    }

    // The model goes with the ID when the avatar is introduced; afterwards the ID alone is enough.
    [Fact]
    public async Task AnAvatarIntroducedIsFoundByItsID()
    {
        var written = await Run((100, Portable("anna", 100, introduce: true)), (200, Portable("anna", 200, introduce: false, says: "Hi")));
        var avatars = written.Where(w => w.DataType == "PAF-AVT-V1.6").ToList();
        Assert.Equal(2, avatars.Count);                                      // the second Portable Avatar had no model: it was found
        Assert.All(avatars, a => Assert.Equal("model-of-anna", (string)JsonNode.Parse(a.Json)!["AvatarData"]!["ModelOrModelID"]![0]!["3DModel"]!["Basic3DModelObjectID"]!));
        Assert.Equal(1, written.Count(w => w.DataType == "OSD-BTO-V1.5"));   // what the second said, and not what it did not
    }

    // Four participants, in the order they arrived: each one's parts are told apart by the Avatar ID.
    [Fact]
    public async Task TheParticipantsAreToldApartByTheirIDs()
    {
        var names = new[] { "anna", "bruno", "carla", "dario" };
        var written = await Run(names.Select((n, i) => ((long)(100 * (i + 1)), Portable(n, 100 * (i + 1), introduce: true))).ToArray());
        var ids = written.Where(w => w.DataType == "OSD-IID-V1.5").Select(w => Id(w.Json, "InstanceIdentifier")).ToList();
        Assert.Equal(names, ids);                                            // in the order of arrival
        foreach (var n in names)
            Assert.Equal(1, written.Count(w => w.DataType == "OSD-OSA-V1.5" && AvatarIds.AvatarOf(Id(w.Json, "ObjectSpatialAttitudeID")) == n));
    }

    [Fact]
    public async Task APortableAvatarWithoutAnIDIsNotDemultiplexed()
    {
        var pav = Portable("anna", 100, introduce: false);
        pav["AvatarOrAvatarID"] = new JsonArray();
        Assert.Empty(await Run((100, pav)));
    }

    [Fact]
    public void TheProviderBuildsIt()
    {
        var provider = new VcrProvider();
        Assert.True(provider.CanCreate(VcrProvider.Pdx));
        Assert.IsType<PortableAvatarDemultiplexing>(provider.Create(VcrProvider.Pdx, new Dictionary<string, string>(), null));
    }
}

// THE BODY DESCRIPTORS AS THE SPATIAL ATTITUDES OF THE NODES BETWEEN JOINTS: an Object - the data and a Qualifier that names the format.
public class BodyDescriptorsTests
{
    [Fact]
    public void TheBodyIsTheSpatialAttitudesOfItsNodes()
    {
        // Three nodes of an arm: shoulder, elbow, wrist.
        var nodes = new[] { (0.0, 0.2, 1.4), (0.0, 0.5, 1.3), (0.1, 0.75, 1.15) }
            .Select((p, i) => EssJson.Attitude($"node-{i}", 1000, p, (0.01, 0.01, 0.01), (0, 0, 0)).ToJsonString()).ToList();
        var bdo = Mpai.Core.OSD.BodyDescriptorsObject.FromNodeAttitudes(nodes);
        Assert.Equal(Mpai.Core.OSD.BodyDescriptorsObject.NodeAttitudes, bdo.GetContentFormat());
        Assert.Equal(3, JsonNode.Parse(bdo.Content()!)!.AsArray().Count);

        var json = Mpai.Core.MpaiJson.ToJson(bdo);
        var schema = AIF.Metadata.PublishedSchemas.At(Repository.Schemas)[Path.GetFullPath(Path.Combine(Repository.Schemas, "PAF", "V1.6", "data", "BodyDescriptorsObject.json"))];
        using var doc = JsonDocument.Parse(json);
        lock (AIF.Metadata.PublishedSchemas.Lock) Assert.True(schema.Evaluate(doc.RootElement).IsValid, json);
    }
}
