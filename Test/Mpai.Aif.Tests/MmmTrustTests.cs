using System.Text.Json.Nodes;
using Mpai.Mmm;
using Mpai.Mmm.Client;
using Mpai.Mmm.Server;

namespace Mpai.Aif.Tests;

// RIGHTS AND TRUST IN THE MEETING (MMM Phase 5). Who is in the meeting is the convener's decision - a human's - and the AVS Service carries it out with Rights:
// the room is not open (no meeting code): a User is seated only if it has been invited; the minutes are read by the participants only; a User the convener
// takes out of the meeting can no longer enter, see the room, or be given the minutes.
[Trait("Group", "Fast")]
[Collection("MmmMeeting")]                       // they share the static state of the Meeting: one at a time
[Trait("Blocks", "Yes")]
public class MmmTrustTests
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

    private static async Task<MmmClient> Register(HttpClient http, string name)
    {
        var reg = await new MmmClient(http, $"{name}-human").PerformAsync("Register", new Complements { With = [Profile(name)] });
        var user = new MmmClient(http, name, reg.Tokens[name]);
        Assert.Equal(200, (await user.PerformAsync("MM-Add", Uc2.C($"{name}-Persona", at: "MVS", with: Uc2.SpatialAttitude($"SA-{name}", -2, -2)))).Http);
        return user;
    }

    private static Task<MmmClient.Result> Tell(MmmClient from, string id, string text) =>
        from.PerformAsync("MM-Send", Uc2.C(id, to: Meeting.Avs, with: Uc2.Message(id, text)));

    private static int asked;

    // Joins: asks for a seat; what it is told; and, if it was given one, goes there.
    private static async Task<string> Join(MeetingServices services, MmmClient user)
    {
        var ask = $"Join-{user.ProcessID}-{++asked}";                 // each request has its own identifier, and so its own answer
        await Tell(user, ask, Meeting.JoinText($"{user.ProcessID}-Persona"));
        await services.PumpAsync();
        var told = Meeting.TextOf((await user.ReadAsync($"Seat-{ask}")).Item)!;
        if (!told.StartsWith("seat=", StringComparison.Ordinal)) return told;
        var seat = Meeting.ParseSeat(told);
        var persona = $"{user.ProcessID}-Persona";
        var move = await user.PerformAsync("MM-Move", Uc2.C(persona, from: "MVS", to: seat["seat"],
            with: [MeetingServices.SeatedAt($"SA-{user.ProcessID}-Seat", double.Parse(seat["yaw"], System.Globalization.CultureInfo.InvariantCulture)), Meeting.SitPA(persona, seat["seat"]),
                   Uc2.Rights($"Seat-Move-{persona}", "May", "Acquired", Meeting.SitID(persona))]));
        return move.Http == 200 ? "seated" : $"move {move.Http}";
    }

    private static JsonObject PointOfView() => new()
    {
        ["Header"] = "OSD-OPV-V1.5", ["PointOfViewID"] = "POV", ["CartPosition"] = new JsonArray(0.0, 0.0, 1.6), ["Orientation"] = new JsonArray(0.0, 0.0, 0.0)
    };

    [Fact]
    public async Task OnlyTheInvitedAreSeatedOnlyParticipantsReadTheMinutesAndTheRemovedAreOut()
    {
        var m = new MInstance(Repository.Schemas);
        Uc2.Setup(m);
        Meeting.Setup(m);
        await using var app = MmmServer.Build(m, "http://127.0.0.1:0");
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        var services = new MeetingServices(http, m);

        var anna = await Register(http, "Anna");
        var bruno = await Register(http, "Bruno");
        var carla = await Register(http, "Carla");
        await services.StartAsync(); await services.PumpAsync(); await services.PumpAsync();      // the Secretary sits

        // Anna convenes the meeting: from then on it is hers to say who is in. Nobody else may convene it, invite, or take anyone out.
        await Tell(anna, "Convene-Anna", "convene"); await services.PumpAsync();
        Assert.Equal("convened", Meeting.TextOf((await anna.ReadAsync("Convened-Anna")).Item));
        await Tell(bruno, "Convene-Bruno", "convene"); await services.PumpAsync();
        Assert.StartsWith("already convened by Anna", Meeting.TextOf((await bruno.ReadAsync("Convened-Bruno")).Item));
        await Tell(bruno, "Invite-Carla-by-Bruno", "invite;user=Carla"); await services.PumpAsync();
        Assert.Equal(404, (await carla.ReadAsync("Invitation-Carla")).Http);                    // no invitation was made: Bruno is not the convener

        // Not invited: no seat, and the right to move there is not given; the room cannot be captured either.
        Assert.Equal("not invited", await Join(services, bruno));
        var in1 = await bruno.PerformAsync("MM-Move", Uc2.C("Bruno-Persona", from: "MVS", to: Meeting.Seats[1].ID, with: Uc2.SpatialAttitude("SA-B")));
        Assert.Equal(403, in1.Http);
        Assert.Equal(403, (await bruno.PerformAsync("MM-Capture", Uc2.C(at: Meeting.Room, with: PointOfView()))).Http);

        // Anna invites Bruno: the invitation is a Message of the AVS Service; now he is seated.
        await Tell(anna, "Invite-Bruno", "invite;user=Bruno"); await services.PumpAsync();
        Assert.StartsWith("invitation;room=MeetingRoom", Meeting.TextOf((await bruno.ReadAsync("Invitation-Bruno")).Item));
        Assert.Equal("seated", await Join(services, anna));
        Assert.Equal("seated", await Join(services, bruno));
        await services.PumpAsync();
        Assert.Equal(200, (await bruno.PerformAsync("MM-Capture", Uc2.C(at: Meeting.Room, with: PointOfView()))).Http);

        // What is said is in the minutes, which the participants are given a copy of; Carla, who is not in the meeting, cannot read them.
        await Tell(anna, "Said-1", "say: We have a quorum.");
        await anna.PerformAsync("MM-Send", Uc2.C("Said-A1", to: Meeting.Secretary, with: Uc2.Message("Said-A1", "say: We have a quorum.")));
        await services.PumpAsync(); await services.PumpAsync();
        Assert.Equal("Anna: We have a quorum.", Meeting.TextOf((await bruno.ReadAsync($"{Meeting.Minutes}-for-Bruno")).Item));
        Assert.Equal("Anna: We have a quorum.", Meeting.TextOf((await anna.ReadAsync($"{Meeting.Minutes}-for-Anna")).Item));
        Assert.Equal(403, (await carla.ReadAsync($"{Meeting.Minutes}-for-Bruno")).Http);
        Assert.Equal(403, (await carla.ReadAsync(Meeting.Minutes)).Http);

        // Anna takes Bruno out: he can no longer capture the room and is given no more minutes; what he had stays his.
        await Tell(anna, "Revoke-Bruno", "revoke;user=Bruno"); await services.PumpAsync(); await services.PumpAsync();
        Assert.Equal(403, (await bruno.PerformAsync("MM-Capture", Uc2.C(at: Meeting.Room, with: PointOfView()))).Http);
        await anna.PerformAsync("MM-Send", Uc2.C("Said-A2", to: Meeting.Secretary, with: Uc2.Message("Said-A2", "say: Bruno has left.")));
        await services.PumpAsync(); await services.PumpAsync();
        Assert.Equal("Anna: We have a quorum.", Meeting.TextOf((await bruno.ReadAsync($"{Meeting.Minutes}-for-Bruno")).Item));
        Assert.Equal("Anna: We have a quorum.\nAnna: Bruno has left.", Meeting.TextOf((await anna.ReadAsync($"{Meeting.Minutes}-for-Anna")).Item));
        Assert.Equal("not invited", await Join(services, bruno));                              // and he cannot come back
        await app.StopAsync();
    }
}
