using System.Text.Json;
using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Cav.Ess;
using Mpai.Paf.Vcr;

namespace Mpai.Aif.Tests;

// VIDEOCONFERENCE CLIENT RECEIVER OF THE AVATAR-BASED VIDEOCONFERENCE (PAF-VCR, slide 22): ONE AIM, the former Multimodal Scene Creation. It holds the state of each avatar by its ID,
// merges into it each Portable Avatar it receives (an update: the ID and what changed; the Avatar Model comes once), places the avatars where they sit, adds their speech at the mouth,
// and gives out the Multimodal Scene Descriptors and the Portable Avatars. The Point of View and the Text go through.
public class VcrTests
{
    private static readonly string[] Names = ["anna", "bruno", "carla", "dario"];

    // The seats of the table: four places on a circle of 1.2 m around its centre, a quarter turn apart.
    private static (double X, double Y) Seat(int i) => (Math.Round(1.2 * Math.Cos(i * Math.PI / 2), 3), Math.Round(1.2 * Math.Sin(i * Math.PI / 2), 3));

    // A Portable Avatar of the meeting: the avatar's ID - its nickname - and, at the introduction, its model; where it sits; what it says.
    private static JsonObject Portable(string id, long ms, bool model, string? says, int? seat)
    {
        var avatar = new JsonArray(id);
        if (model) avatar.Add(new JsonObject { ["Header"] = "OSD-B3O-V1.5", ["Basic3DModelObjectID"] = "model-of-" + id, ["Basic3DModelObjectData"] = new JsonArray(new JsonObject { ["Data"] = "AAEC" }) });
        var pav = new JsonObject
        {
            ["Header"] = "PAF-PAV-V1.6", ["PortableAvatarID"] = $"PAV-{id}-{ms}", ["PortableAvatarTime"] = EssJson.SimpleTime($"PAV-{id}-{ms}-T", ms),
            ["AvatarOrAvatarID"] = avatar
        };
        if (seat is { } s) pav["AvatarSpatialAttitude"] = EssJson.Attitude($"osa-{id}", ms, (Seat(s).X, Seat(s).Y, 0), (0.1, 0.1, 0.1), (0, 0, 0));
        if (says is not null) pav["SPOOrSPOID"] = new JsonArray(new JsonObject { ["Header"] = "OSD-BSO-V1.5", ["BasicSpeechObjectID"] = "speech-" + id });
        return pav;
    }

    private static async Task<List<(TimeSpan At, string DataType, int PortNumber, string Json)>> Receive(params (string DataType, JsonNode Json)[] arrivals)
    {
        var drive = new DrivePorts(arrivals.Select((a, i) => (TimeSpan.FromMilliseconds(100 * (i + 1)), a.DataType, 1, a.Json.ToJsonString())));
        await new VideoconferenceClientReceiver("1PAF-VCR-V1.6-I01").RunAsync(drive, AimContext.None);
        return drive.Written;
    }

    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;
    private static List<JsonNode> Scenes(List<(TimeSpan At, string DataType, int PortNumber, string Json)> w) => w.Where(x => x.DataType == "OSD-BMS-V1.5").Select(x => Parse(x.Json)).ToList();
    private static List<JsonNode> Avatars(List<(TimeSpan At, string DataType, int PortNumber, string Json)> w) => w.Where(x => x.DataType == "PAF-PAV-V1.6").Select(x => Parse(x.Json)).ToList();

    // The four are introduced, each with its model and its seat: the Scene has the four, each at its seat.
    [Fact]
    public async Task TheAvatarsAreSeatedWhereTheirPortableAvatarsSay()
    {
        var written = await Receive(Names.Select((n, i) => ("PAF-PAV-V1.6", (JsonNode)Portable(n, 100 * (i + 1), true, null, i))).ToArray());
        var scene = Scenes(written).Last();
        var members = scene["BasicObjectsData"]!.AsArray().Select(m => m!).ToList();
        Assert.Equal(Names, members.Select(m => (string)m["BXOOrBXOID"]!));
        for (var i = 0; i < 4; i++)
        {
            var at = members[i]["BXOSpaceTime"]!["SpatialAttitude1"]!["Position"]!["CartPosition"]!.AsArray();
            Assert.Equal(Seat(i).X, (double)at[0]!, 3);
            Assert.Equal(Seat(i).Y, (double)at[1]!, 3);
        }
        Assert.Equal(4, (int)scene["AVObjectCount"]!);
        Assert.Equal(4, Avatars(written).Count);                                                        // a Portable Avatar out for each one in
        Assert.DoesNotContain("3DModel", scene.ToJsonString());                                         // there is nobody yet to display the Scene to: no model in it
    }

    // The speech is at the mouth of the avatar, named by its own ID: the standard 1.6 m above its Spatial Attitude.
    [Fact]
    public async Task TheSpeechIsAddedAtTheMouth()
    {
        var written = await Receive(Names.Select((n, i) => ("PAF-PAV-V1.6", (JsonNode)Portable(n, 100 * (i + 1), true, null, i))).Append(("PAF-PAV-V1.6", Portable("carla", 900, false, "hello", null))).ToArray());
        var scene = Scenes(written).Last();
        var members = scene["BasicObjectsData"]!.AsArray().Select(m => m!).ToList();
        Assert.Equal(["anna", "bruno", "carla", "dario", "speech-carla"], members.Select(m => (string)m["BXOOrBXOID"]!));
        var speech = members.First(m => (string)m["BXOOrBXOID"]! == "speech-carla")["BXOSpaceTime"]!["SpatialAttitude1"]!["Position"]!["CartPosition"]!.AsArray();
        var avatar = members.First(m => (string)m["BXOOrBXOID"]! == "carla")["BXOSpaceTime"]!["SpatialAttitude1"]!["Position"]!["CartPosition"]!.AsArray();
        Assert.Equal((double)avatar[0]!, (double)speech[0]!, 3);
        Assert.Equal((double)avatar[2]! + 1.6, (double)speech[2]!, 3);

        var schema = AIF.Metadata.PublishedSchemas.At(Repository.Schemas)[Path.GetFullPath(Path.Combine(Repository.Schemas, "OSD", "V1.5", "data", "BasicAudioVisualSceneDescriptors.json"))];
        foreach (var w in written.Where(w => w.DataType == "OSD-BMS-V1.5"))
        {
            using var doc = JsonDocument.Parse(w.Json);
            lock (AIF.Metadata.PublishedSchemas.Lock) Assert.True(schema.Evaluate(doc.RootElement).IsValid, w.Json);
        }
    }

    // THE STATE IS HELD BY ID: the update that carries only the ID and the speech leaves the avatar with the model and the seat it had.
    [Fact]
    public async Task AnUpdateIsMergedIntoTheAvatarThatTheReceiverHolds()
    {
        var written = await Receive(("PAF-PAV-V1.6", Portable("anna", 100, true, null, 0)), ("PAF-PAV-V1.6", Portable("anna", 200, false, "hello", null)));
        var last = Avatars(written).Last();
        Assert.Equal("anna", (string)last["AvatarOrAvatarID"]![0]!);
        Assert.Equal("model-of-anna", (string)last["AvatarOrAvatarID"]![1]!["Basic3DModelObjectID"]!);   // the model of the introduction is still there
        Assert.Equal("osa-anna", (string)last["AvatarSpatialAttitude"]!["ObjectSpatialAttitudeID"]!);    // and so is the seat
        Assert.Equal("speech-anna", (string)last["SPOOrSPOID"]![0]!["BasicSpeechObjectID"]!);            // with what it now says
        Assert.Equal(1, Scenes(written).Last()["BasicObjectsData"]!.AsArray().Count(m => (string)m!["BXOOrBXOID"]! == "anna"));   // one avatar of that ID
    }

    // No model has ever been sent: a default avatar is what is shown.
    [Fact]
    public async Task AnAvatarThatHasNoModelIsShownAsTheDefaultAvatar()
    {
        var written = await Receive(("PAF-PAV-V1.6", Portable("bruno", 100, false, null, 1)));
        var model = Avatars(written).Single()["AvatarOrAvatarID"]!.AsArray().OfType<JsonObject>().Single();
        Assert.Equal("default-avatar", (string)model["Basic3DModelObjectID"]!);

        var drive = new DrivePorts([(TimeSpan.FromMilliseconds(100), "PAF-PAV-V1.6", 1, Portable("bruno", 100, false, null, 1).ToJsonString())]);
        await new VideoconferenceClientReceiver("1PAF-VCR-V1.6-I01", new Dictionary<string, string> { ["DefaultAvatar"] = "Thalia" }).RunAsync(drive, AimContext.None);
        Assert.Contains("Thalia", drive.Written.Single(w => w.DataType == "PAF-PAV-V1.6").Json);
    }

    // An avatar nobody has seated is not in the Scene (it has no place to be); its Portable Avatar is given out all the same.
    [Fact]
    public async Task AnAvatarWithoutASeatIsNotInTheScene()
    {
        var written = await Receive(("PAF-PAV-V1.6", Portable("dario", 100, true, "hi", null)));
        Assert.Empty(Scenes(written).Single()["BasicObjectsData"]!.AsArray());
        Assert.Single(Avatars(written));
    }

    [Fact]
    public async Task ThePointOfViewAndTheTextGoThrough()
    {
        var pov = new JsonObject { ["Header"] = "OSD-OPV-V1.5", ["PointOfViewID"] = "pov-anna" };
        var text = new JsonObject { ["Header"] = "OSD-BTO-V1.5", ["BasicTextObjectID"] = "t1" };
        var written = await Receive(("OSD-OPV-V1.5", pov), ("OSD-BTO-V1.5", text));
        Assert.Equal(pov.ToJsonString(), written.Single(w => w.DataType == "OSD-OPV-V1.5").Json);
        Assert.Equal(text.ToJsonString(), written.Single(w => w.DataType == "OSD-BTO-V1.5").Json);
        Assert.DoesNotContain(written, w => w.DataType == "OSD-BMS-V1.5");                               // no avatar yet: no Scene
    }

    [Fact]
    public void TheProviderBuildsIt()
    {
        var provider = new VcrProvider();
        Assert.True(provider.CanCreate(VcrProvider.Vcr));
        Assert.IsType<VideoconferenceClientReceiver>(provider.Create(VcrProvider.Vcr, new Dictionary<string, string>(), null));
        Assert.False(provider.CanCreate("1PAF-MSC-V1.6-I01"));
    }
}
