using System.Text.Json.Nodes;
using Mpai.Mmm;
using Mpai.Mmm.Client;
using Mpai.Mmm.Server;

namespace Mpai.Aif.Tests;

// THE CLIENT AND MM-CAPTURE (MMM Phase 2). Two Users join the Metaverse Square from their clients: each registers, adds its Persona at the
// Square, and moves it - where it stands (x east, y north, metres) and which way it looks (yaw, degrees). What a User sees is what MM-Capture returns
// for the M-Location: the Personae of the others, with their place and yaw, nothing it may not see.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class MmmCaptureTests
{
    private static JsonObject Profile(string name) => new()
    {
        ["Header"] = "MMM-PPR-V2.2", ["MInstanceID"] = "MI1", ["humanID"] = $"{name}-human", ["PersonalProfileID"] = $"Profile-{name}-human",
        ["PersonalProfile"] = new JsonObject
        {
            ["Data"] = new JsonArray(new JsonObject { ["FirstName"] = name }),
            ["Processes"] = new JsonArray(new JsonObject
            {
                ["ProcessIDOrProcess"] = new JsonArray(new JsonObject { ["ProcessID"] = name }),
                ["PersonaIDOrPersona"] = new JsonArray(new JsonObject { ["PersonaID"] = $"{name}-Persona" })
            })
        }
    };

    private static JsonObject Attitude(string id, double x, double y, double yaw) => new()
    {
        ["Header"] = "OSD-OSA-V1.5", ["ObjectSpatialAttitudeID"] = id,
        ["Position"] = new JsonObject { ["Header"] = "OSD-OPS-V1.5", ["PositionID"] = $"{id}-P", ["CartPosition"] = new JsonArray(x, y, 0.0) },
        ["Orientation"] = new JsonObject { ["Header"] = "OSD-OOR-V1.5", ["OrientationID"] = $"{id}-O", ["Orientation"] = new JsonArray(yaw, 0.0, 0.0) }
    };

    private static JsonObject PointOfView() => new()
    {
        ["Header"] = "OSD-OPV-V1.5", ["PointOfViewID"] = "POV", ["CartPosition"] = new JsonArray(0.0, 0.0, 1.6), ["Orientation"] = new JsonArray(0.0, 0.0, 0.0)
    };

    private static async Task<MmmClient> Join(HttpClient http, string name, double x, double y)
    {
        var human = new MmmClient(http, $"{name}-human");
        var reg = await human.PerformAsync("Register", new Complements { With = [Profile(name)] });
        Assert.Equal(201, reg.Http);
        var user = new MmmClient(http, name, reg.Tokens[name]);
        var add = await user.PerformAsync("MM-Add", new Complements { Nil = $"{name}-Persona", At = "MVS", With = [Attitude($"SA-{name}", x, y, 0)] });
        Assert.Equal(200, add.Http);
        return user;
    }

    [Fact]
    public async Task AUserSeesThePersonaeOfTheOthersWhereTheyStandAndLook()
    {
        var m = new MInstance(Repository.Schemas);
        Uc2.Setup(m);
        m.DefaultModels = ["Persona1.glb", "Persona2.glb"];
        await using var app = MmmServer.Build(m, "http://127.0.0.1:0");
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        var anna = await Join(http, "Anna", -2, 1);
        var bruno = await Join(http, "Bruno", 2, -1);
        var turn = await anna.PerformAsync("MM-Move", new Complements { Nil = "Anna-Persona", From = "MVS", To = "MVS", With = [Attitude("SA-Anna", -2, 1, 90)] });
        Assert.Equal(200, turn.Http);

        var seen = await bruno.PerformAsync("MM-Capture", new Complements { At = "MVS", With = [PointOfView()] });
        Assert.Equal(200, seen.Http);
        var things = seen.Response.Complements!.With![0]!["ItemValueOrRef"]![0]!["Things"]!.AsArray().OfType<JsonObject>().ToList();
        var a = things.Single(t => (string?)t["ID"] == "Anna-Persona");
        Assert.Equal(-2.0, (double)a["X"]!, 3);                    // east
        Assert.Equal(1.0, (double)a["Y"]!, 3);                     // north
        Assert.Equal(90.0, (double)a["Yaw"]!, 3);
        Assert.Equal("Persona1.glb", (string?)a["Model"]);         // the Models the M-Instance gave in turn
        Assert.Equal("Persona2.glb", (string?)things.Single(t => (string?)t["ID"] == "Bruno-Persona")["Model"]);

        var nowhere = await bruno.PerformAsync("MM-Capture", new Complements { At = "Nowhere", With = [PointOfView()] });
        Assert.Equal(404, nowhere.Http);
        await app.StopAsync();
    }
}
