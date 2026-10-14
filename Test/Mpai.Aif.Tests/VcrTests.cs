using System.Text.Json;
using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Cav.Ess;
using Mpai.Paf.Pdx;
using Mpai.Paf.Vcr;

namespace Mpai.Aif.Tests;

// MULTIMODAL SCENE CREATION OF THE AVATAR-BASED VIDEOCONFERENCE (PAF-MSC), after Portable Avatar Demultiplexing (PAF-PDX, the one of the Virtual Meeting Secretary too).
// The data of one Portable Avatar travel together: its ID, its Avatar (which holds its speech) and its Spatial Attitude.
public class MscTests
{
    private static readonly string[] Names = ["anna", "bruno", "carla", "dario"];

    // The seats of the table: four places on a circle of 1.2 m around its centre, a quarter turn apart.
    private static (double X, double Y) Seat(int i) => (Math.Round(1.2 * Math.Cos(i * Math.PI / 2), 3), Math.Round(1.2 * Math.Sin(i * Math.PI / 2), 3));

    // A Portable Avatar of the meeting: the avatar's ID - its nickname - and its model; where it is; its face and body animation; what it says.
    private static JsonObject Portable(string id, long ms, string? says, int seat)
    {
        var pav = new JsonObject
        {
            ["Header"] = "PAF-PAV-V1.6", ["PortableAvatarID"] = $"PAV-{id}-{ms}", ["PortableAvatarTime"] = EssJson.SimpleTime($"PAV-{id}-{ms}-T", ms),
            ["AvatarOrAvatarID"] = new JsonArray(id, new JsonObject { ["Header"] = "OSD-B3O-V1.5", ["Basic3DModelObjectID"] = "model-of-" + id, ["Basic3DModelObjectData"] = new JsonArray(new JsonObject { ["Data"] = "AAEC" }) }),
            ["AvatarSpaceTime"] = EssJson.SimpleTime($"st-{id}", ms),
            ["AvatarSpatialAttitude"] = EssJson.Attitude($"osa-{id}", ms, (Seat(seat).X, Seat(seat).Y, 0), (0.1, 0.1, 0.1), (0, 0, 0)),
            ["FaceDescriptorsObject"] = new JsonObject { ["Header"] = "PAF-FDO-V1.6", ["FaceDescriptorsObjectID"] = "face-" + id },
            ["BodyDescriptorsObject"] = new JsonObject { ["Header"] = "PAF-BDO-V1.6", ["BodyDescriptorsObjectID"] = "body-" + id }
        };
        if (says is not null) pav["SPOOrSPOID"] = new JsonArray(new JsonObject { ["Header"] = "OSD-BSO-V1.5", ["BasicSpeechObjectID"] = "speech-" + id });
        return pav;
    }

    // The Portable Avatars through PDX, and what it gives through MSC: the Messages, in the order they leave.
    private static async Task<List<(TimeSpan At, string DataType, int PortNumber, string Json)>> Chain(params string[] speakers)
    {
        var store = new AIF.Store.AmdStore(Repository.Amds); store.Scan();
        var ports = AimPortReader.Load(store, "1PAF-PDX-V1.6-I01");
        var pdx = new PdxAimProcessor("1PAF-PDX-V1.6-I01", ports);
        var fromPdx = new List<(TimeSpan, string, int, string)>();
        for (var i = 0; i < Names.Length; i++)
        {
            var pav = Portable(Names[i], 100 * (i + 1), speakers.Contains(Names[i]) ? "Hello from " + Names[i] : null, i);
            var made = await pdx.ProcessAsync(new Message { MessageId = Guid.NewGuid().ToString(), Ports = new() { [ports.Input("PAF-PAV-V1.6")] = pav.ToJsonString() } });
            foreach (var (key, json) in made.Ports)
            {
                var hash = key.LastIndexOf('#');
                fromPdx.Add((TimeSpan.FromMilliseconds(100 * (i + 1)), key[..hash], int.Parse(key[(hash + 1)..]), json));
            }
        }
        var msc = new DrivePorts(fromPdx);
        await new MultimodalSceneCreation("1PAF-MSC-V1.6-I01").RunAsync(msc, AimContext.None);
        return msc.Written;
    }

    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    // What the Rendering needs - the Avatar of each ID, with its speech where it spoke.
    [Fact]
    public async Task TheAvatarAndItsSpeechAreGivenOnToTheRendering()
    {
        var written = await Chain("anna", "carla");
        var avatars = written.Where(w => w.DataType == "PAF-AVT-V1.6").Select(w => Parse(w.Json)).ToList();
        Assert.Equal(Names, avatars.Select(a => (string)a["AvatarID"]!));
        Assert.Equal(["speech-anna", "speech-carla"], written.Where(w => w.DataType == "OSD-BSO-V1.5").Select(w => (string)Parse(w.Json)["BasicSpeechObjectID"]!));
        Assert.DoesNotContain(written, w => w.DataType == "XRV-SAV-V1.0");                              // the Speaking Avatar is formed by the rendering
    }

    // The speech is at the mouth of the avatar, named by its own ID.
    [Fact]
    public async Task TheSpeechIsAddedAtTheMouth()
    {
        var written = await Chain("anna", "carla");
        var scene = Parse(written.Last(w => w.DataType == "OSD-BMS-V1.5").Json);
        var members = scene["BasicObjectsData"]!.AsArray().Select(m => m!).ToList();
        var ids = members.Select(m => (string)m["BXOOrBXOID"]!).ToList();
        Assert.Equal(["anna", "bruno", "carla", "dario", "speech-anna", "speech-carla"], ids);          // the four avatars, each placed, and the speech of two
        var speech = members.First(m => (string)m["BXOOrBXOID"]! == "speech-anna")["BXOSpaceTime"]!["SpatialAttitude1"]!["Position"]!["CartPosition"]!.AsArray();
        var avatar = members.First(m => (string)m["BXOOrBXOID"]! == "anna")["BXOSpaceTime"]!["SpatialAttitude1"]!["Position"]!["CartPosition"]!.AsArray();
        Assert.Equal((double)avatar[0]!, (double)speech[0]!, 3);
        Assert.Equal((double)avatar[2]! + 1.6, (double)speech[2]!, 3);                                  // at the mouth: the standard 1.6 m above the avatar
        for (var i = 0; i < 4; i++)                                                                     // each avatar at its seat: where PDX said
        {
            var at = members[i]["BXOSpaceTime"]!["SpatialAttitude1"]!["Position"]!["CartPosition"]!.AsArray();
            Assert.Equal(Seat(i).X, (double)at[0]!, 3);
            Assert.Equal(Seat(i).Y, (double)at[1]!, 3);
        }
        Assert.DoesNotContain("3DModel", scene.ToJsonString());                                         // there is nobody yet to display the Scene to: no model in it
        Assert.Equal(6, (int)scene["AVObjectCount"]!);

        var schema = AIF.Metadata.PublishedSchemas.At(Repository.Schemas)[Path.GetFullPath(Path.Combine(Repository.Schemas, "OSD", "V1.5", "data", "BasicAudioVisualSceneDescriptors.json"))];
        foreach (var w in written.Where(w => w.DataType == "OSD-BMS-V1.5"))
        {
            using var doc = JsonDocument.Parse(w.Json);
            lock (AIF.Metadata.PublishedSchemas.Lock) Assert.True(schema.Evaluate(doc.RootElement).IsValid, w.Json);
        }
    }

    [Fact]
    public async Task TheIDAndTheSpatialAttitudeArePassedOn()
    {
        var written = await Chain("anna");
        Assert.Equal(Names, written.Where(w => w.DataType == "string").Select(w => (string)Parse(w.Json)!));
        Assert.Equal(["osa-anna", "osa-bruno", "osa-carla", "osa-dario"],
                     written.Where(w => w.DataType == "OSD-OSA-V1.5").Select(w => (string)Parse(w.Json)["ObjectSpatialAttitudeID"]!));
    }

    // Nobody speaks: the avatars are given out all the same, and the Scene has none of their speech.
    [Fact]
    public async Task AnAvatarThatSaysNothingIsGivenOutWithoutSpeech()
    {
        var written = await Chain();
        Assert.Equal(4, written.Count(w => w.DataType == "PAF-AVT-V1.6"));
        Assert.DoesNotContain(written, w => w.DataType == "OSD-BSO-V1.5");
        Assert.DoesNotContain("speech-", written.Last(w => w.DataType == "OSD-BMS-V1.5").Json);
    }

    [Fact]
    public void TheProviderBuildsIt()
    {
        var store = new AIF.Store.AmdStore(Repository.Amds); store.Scan();
        var provider = new VcrProvider(store);
        Assert.True(provider.CanCreate(VcrProvider.Msc));
        Assert.IsType<MultimodalSceneCreation>(provider.Create(VcrProvider.Msc, new Dictionary<string, string>(), null));
        Assert.IsType<PdxAimProcessor>(provider.Create(VcrProvider.Pdx, new Dictionary<string, string>(), null));
    }
}
