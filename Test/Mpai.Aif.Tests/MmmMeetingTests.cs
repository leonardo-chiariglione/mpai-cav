using System.Globalization;
using System.Text.Json.Nodes;
using Mpai.Mmm;
using Mpai.Mmm.Client;
using Mpai.Mmm.Server;

namespace Mpai.Aif.Tests;

// THE MEETING AS SERVICES OF THE M-INSTANCE (MMM Phase 3). Two participants and the Secretary meet in a room - a Location with seat Locations - with no
// ABV server. The AVS Service, a Process, assigns the seats and gives each User the right to move its Persona to its own; the Secretary, a machine
// Process with a Persona, takes a seat like the others and takes the minutes of what it is told. All through the MMM-API.
[Trait("Group", "Fast")]
[Collection("MmmMeeting")]                       // they share the static state of the Meeting: one at a time
[Trait("Blocks", "Yes")]
public class MmmMeetingTests
{
    // A User may have several Personae, all its own.
    private static JsonObject Profile(string name, params string[] personae) => new()
    {
        ["Header"] = "MMM-PPR-V2.2", ["MInstanceID"] = "MI1", ["humanID"] = $"{name}-human", ["PersonalProfileID"] = $"Profile-{name}-human",
        ["PersonalProfile"] = new JsonObject
        {
            ["Data"] = new JsonArray(new JsonObject { ["FirstName"] = name }),
            ["Processes"] = new JsonArray(new JsonObject
            {
                ["ProcessIDOrProcess"] = new JsonArray(new JsonObject { ["ProcessID"] = name }),
                ["PersonaIDOrPersona"] = new JsonArray(personae.Select(p => (JsonNode)new JsonObject { ["PersonaID"] = p }).ToArray())
            })
        }
    };

    // The User registers with its Personae and puts the one it chooses at the Square.
    private static async Task<MmmClient> Join(HttpClient http, string name, string brings, params string[] personae)
    {
        var reg = await new MmmClient(http, $"{name}-human").PerformAsync("Register", new Complements { With = [Profile(name, personae)] });
        var user = new MmmClient(http, name, reg.Tokens[name]);
        Assert.Equal(200, (await user.PerformAsync("MM-Add", Uc2.C(brings, at: "MVS", with: Uc2.SpatialAttitude($"SA-{name}", -2, -2)))).Http);
        return user;
    }

    // Asks the AVS Service for a seat, and goes there.
    private static async Task<(string Seat, double X, double Y, double Yaw)> SitDown(HttpClient http, MeetingServices services, MmmClient user, string persona)
    {
        Assert.Equal(200, (await user.PerformAsync("MM-Send", Uc2.C($"Join-{user.ProcessID}", to: Meeting.Avs, with: Uc2.Message($"Join-{user.ProcessID}", Meeting.JoinText(persona))))).Http);
        await services.PumpAsync();
        var told = Meeting.ParseSeat(Meeting.TextOf((await user.ReadAsync($"Seat-Join-{user.ProcessID}")).Item)!);
        var yaw = double.Parse(told["yaw"], CultureInfo.InvariantCulture);
        Assert.Equal(persona, told["persona"]);                     // the right is for the Persona the User brought
        var pa = Meeting.SitPA(persona, told["seat"]);
        var move = await user.PerformAsync("MM-Move", Uc2.C(persona, from: "MVS", to: told["seat"],
            with: [MeetingServices.SeatedAt($"SA-{user.ProcessID}-Seat", yaw), pa, Uc2.Rights($"Seat-Move-{persona}", "May", "Acquired", Meeting.SitID(persona))]));
        Assert.Equal(200, move.Http);
        return (told["seat"], double.Parse(told["x"], CultureInfo.InvariantCulture), double.Parse(told["y"], CultureInfo.InvariantCulture), yaw);
    }

    [Fact]
    public async Task TwoParticipantsAndTheSecretaryMeetInARoom()
    {
        var m = new MInstance(Repository.Schemas);
        Uc2.Setup(m);
        m.DefaultModels = ["Persona1.glb", "Persona2.glb"];
        Meeting.Setup(m);
        await using var app = MmmServer.Build(m, "http://127.0.0.1:0");
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        var services = new MeetingServices(http, m);

        // Anna has two Personae and brings the formal one to this meeting; Bruno has one.
        var anna = await Join(http, "Anna", "Anna-Formal", "Anna-Persona", "Anna-Formal");
        var bruno = await Join(http, "Bruno", "Bruno-Persona", "Bruno-Persona");

        // A User that has not asked for a seat may not go to one: the room is not open.
        var intruder = await anna.PerformAsync("MM-Move", Uc2.C("Anna-Formal", from: "MVS", to: Meeting.Seats[0].ID, with: Uc2.SpatialAttitude("SA-X")));
        Assert.Equal(403, intruder.Http);

        await services.StartAsync();                                  // the Secretary asks to join
        await services.PumpAsync();                                   // the AVS Service gives it a seat
        await services.PumpAsync();                                   // the Secretary goes to it
        // Anna convenes the meeting and invites Bruno (the Secretary is always invited): the room is not open.
        foreach (var (id, text) in new[] { ("Convene-Anna", "convene"), ("Invite-Bruno", "invite;user=Bruno") })
            await anna.PerformAsync("MM-Send", Uc2.C(id, to: Meeting.Avs, with: Uc2.Message(id, text)));
        await services.PumpAsync();
        var a = await SitDown(http, services, anna, "Anna-Formal");
        var b = await SitDown(http, services, bruno, "Bruno-Persona");
        Assert.NotEqual(a.Seat, b.Seat);

        // Each Persona is at its own seat, in the room, looking at the table; the Secretary's too.
        Assert.Equal(a.Seat, m.Get("Anna-Formal")!.Location);
        Assert.Null(m.Get("Anna-Persona")!.Location);                  // the one she did not bring is not in the meeting
        Assert.Equal(b.Seat, m.Get("Bruno-Persona")!.Location);
        var secretarySeat = m.Get(Meeting.SecretaryPersona)!.Location!;
        Assert.StartsWith(Meeting.Room + "-S", secretarySeat);
        Assert.Equal(3, new[] { a.Seat, b.Seat, secretarySeat }.Distinct().Count());

        // The seat of a User is the one it was told: the snapshot puts its Persona there (x east, y north, metres).
        var things = m.Snapshot()["Things"]!.AsArray().OfType<JsonObject>().ToList();
        var annaThing = things.Single(t => (string?)t["ID"] == "Anna-Formal");
        Assert.Equal(a.X, (double)annaThing["X"]!, 3);
        Assert.Equal(a.Y, (double)annaThing["Y"]!, 3);
        Assert.Equal(a.Yaw, (double)annaThing["Yaw"]!, 3);

        // Anna and Bruno cannot take each other's seats: the right is for one seat.
        var swap = await anna.PerformAsync("MM-Move", Uc2.C("Anna-Formal", from: a.Seat, to: b.Seat, with: Uc2.SpatialAttitude("SA-Y")));
        Assert.Equal(403, swap.Http);

        // The room, as the Secretary's neighbour sees it: MM-Capture of the Room returns the three Personae.
        var seen = await anna.PerformAsync("MM-Capture", Uc2.C(at: Meeting.Room, with: new JsonObject
        {
            ["Header"] = "OSD-OPV-V1.5", ["PointOfViewID"] = "POV", ["CartPosition"] = new JsonArray(0.0, 0.0, 1.6), ["Orientation"] = new JsonArray(a.Yaw, 0.0, 0.0)
        }));
        Assert.Equal(200, seen.Http);
        var inRoom = seen.Response.Complements!.With![0]!["ItemValueOrRef"]![0]!["Things"]!.AsArray().OfType<JsonObject>()
                         .Where(t => (string?)t["Kind"] == "Persona").Select(t => (string)t["ID"]!).Order().ToList();
        Assert.Equal(["Anna-Formal", "Bruno-Persona", Meeting.SecretaryPersona], inRoom);

        // A User cannot bring a Persona that is not its own: the right the AVS Service gives names it, but MM-Move is the owner's alone.
        var theft = await bruno.PerformAsync("MM-Move", Uc2.C("Anna-Formal", from: a.Seat, to: b.Seat, with: Uc2.SpatialAttitude("SA-Z")));
        Assert.Equal(403, theft.Http);

        // What is said goes to the Secretary; the minutes are the room's, kept by the AVS Service.
        await anna.PerformAsync("MM-Send", Uc2.C("Said-Anna-1", to: Meeting.Secretary, with: Uc2.Message("Said-Anna-1", "say: Welcome, Bruno.")));
        await bruno.PerformAsync("MM-Send", Uc2.C("Said-Bruno-1", to: Meeting.Secretary, with: Uc2.Message("Said-Bruno-1", "say: Thank you, Anna.")));
        await services.PumpAsync();
        await services.PumpAsync();
        Assert.Equal(["Anna: Welcome, Bruno.", "Bruno: Thank you, Anna."], services.TheMinutes);
        Assert.Equal("Anna: Welcome, Bruno.\nBruno: Thank you, Anna.", Meeting.TextOf(new MmmClient(http, Meeting.Avs, m.TokenOf(Meeting.Avs)).ReadAsync(Meeting.Minutes).Result.Item));
        await app.StopAsync();
    }
}
