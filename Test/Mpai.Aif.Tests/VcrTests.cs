using System.Text.Json;
using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Cav.Ess;
using Mpai.Paf.Vcr;

namespace Mpai.Aif.Tests;

// MULTIMODAL SCENE CREATION OF THE AVATAR-BASED VIDEOCONFERENCE (PAF-MSC), after PDX, both running continuously.
public class MscTests
{
    private static readonly string[] Names = ["anna", "bruno", "carla", "dario"];

    // Four participants, each seated at a place around the table; those in speakers say something.
    private static async Task<List<(TimeSpan At, string DataType, int PortNumber, string Json)>> Chain(params string[] speakers)
    {
        var pavs = Names.Select((n, i) => (TimeSpan.FromMilliseconds(100 * (i + 1)), PortableAvatarDemultiplexing.Pav, 1,
                                           PdxTests.Portable(n, 100 * (i + 1), introduce: true, says: speakers.Contains(n) ? "Hello from " + n : null, seat: i).ToJsonString()));
        var pdx = new DrivePorts(pavs);
        await new PortableAvatarDemultiplexing("1PAF-PDX-V1.6-I01").RunAsync(pdx, AimContext.None);

        var msc = new DrivePorts(pdx.Written.Select(w => (w.At, w.DataType, w.PortNumber, w.Json)));
        await new MultimodalSceneCreation("1PAF-MSC-V1.6-I01").RunAsync(msc, AimContext.None);
        return msc.Written;
    }

    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    // What the Response and Scene Rendering needs to form the Speaking Avatar - an Avatar and its speech: the Avatar of each ID, and the speech of those who spoke.
    [Fact]
    public async Task TheAvatarAndItsSpeechAreGivenToTheRendering()
    {
        var written = await Chain("anna", "carla");
        var avatars = written.Where(w => w.DataType == "PAF-AVT-V1.6").Select(w => Parse(w.Json)).ToList();
        Assert.Equal(Names, avatars.Select(a => (string)a["AvatarID"]!));
        Assert.Equal(["anna#speech", "carla#speech"], written.Where(w => w.DataType == "OSD-BSO-V1.5").Select(w => (string)Parse(w.Json)["BasicSpeechObjectID"]!));
        Assert.DoesNotContain(written, w => w.DataType == "XRV-SAV-V1.0");                              // the Speaking Avatar is formed by the rendering
    }

    // The speech is at the mouth of the avatar, named by its ID.
    [Fact]
    public async Task TheSpeechIsAddedAtTheMouth()
    {
        var written = await Chain("anna", "carla");
        var scene = Parse(written.Last(w => w.DataType == "OSD-BMS-V1.5").Json);
        var members = scene["BasicObjectsData"]!.AsArray().Select(m => m!).ToList();
        var ids = members.Select(m => (string)m["BXOOrBXOID"]!).ToList();
        Assert.Equal(["anna", "bruno", "carla", "dario", "anna#speech", "carla#speech"], ids);          // the four avatars, each placed, and the speech of two
        var speech = members.First(m => (string)m["BXOOrBXOID"]! == "anna#speech")["BXOSpaceTime"]!["SpatialAttitude1"]!["Position"]!["CartPosition"]!.AsArray();
        var avatar = members.First(m => (string)m["BXOOrBXOID"]! == "anna")["BXOSpaceTime"]!["SpatialAttitude1"]!["Position"]!["CartPosition"]!.AsArray();
        Assert.Equal((double)avatar[0]!, (double)speech[0]!, 3);
        Assert.Equal((double)avatar[2]! + 1.6, (double)speech[2]!, 3);                                  // at the mouth: the standard 1.6 m above the avatar
        for (var i = 0; i < 4; i++)                                                                     // each avatar at its seat: where PDX said
        {
            var at = members[i]["BXOSpaceTime"]!["SpatialAttitude1"]!["Position"]!["CartPosition"]!.AsArray();
            Assert.Equal(PdxTests.Seat(i).X, (double)at[0]!, 3);
            Assert.Equal(PdxTests.Seat(i).Y, (double)at[1]!, 3);
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
        Assert.Equal(Names, written.Where(w => w.DataType == "OSD-IID-V1.5").Select(w => (string)Parse(w.Json)["InstanceIdentifier"]!));
        Assert.Equal(["anna#attitude", "bruno#attitude", "carla#attitude", "dario#attitude"],
                     written.Where(w => w.DataType == "OSD-OSA-V1.5").Select(w => (string)Parse(w.Json)["ObjectSpatialAttitudeID"]!));
    }

    // Nobody speaks: the avatars are given out all the same, and the Scene has none of their speech.
    [Fact]
    public async Task AnAvatarThatSaysNothingIsGivenOutWithoutSpeech()
    {
        var written = await Chain();
        Assert.Equal(4, written.Count(w => w.DataType == "PAF-AVT-V1.6"));
        Assert.DoesNotContain(written, w => w.DataType == "OSD-BSO-V1.5");
        Assert.DoesNotContain("#speech", written.Last(w => w.DataType == "OSD-BMS-V1.5").Json);
    }

    [Fact]
    public void TheProviderBuildsIt()
    {
        var provider = new VcrProvider();
        Assert.True(provider.CanCreate(VcrProvider.Msc));
        Assert.IsType<MultimodalSceneCreation>(provider.Create(VcrProvider.Msc, new Dictionary<string, string>(), null));
    }
}
