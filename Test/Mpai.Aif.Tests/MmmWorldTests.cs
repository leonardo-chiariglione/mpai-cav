using System.Text;
using System.Text.Json.Nodes;
using Mpai.Mmm;
using Mpai.Mmm.Client;
using Microsoft.AspNetCore.Builder;
using Mpai.Mmm.Server;

namespace Mpai.Aif.Tests;

// THE WORLD OF THE MMM (MMM/World/world.json, the glTF buildings): the Metaverse Square, the Town Hall with its meeting room, a library and a cafe. A building is a Location,
// its rooms are Locations in it, a door is a Right: the AVS Service gives it to the Users it invites, for the meeting - the rights carry the period of the meeting and are
// withdrawn when it ends - and the others are refused (403). All through the MMM-API, as the browser client does it.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
[Collection("MmmMeeting")]                       // they share the static state of the Meeting: one at a time
public class MmmWorldTests
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

    // A User of the world: it registers and its Persona arrives at the Square.
    private static async Task<MmmClient> Arrive(HttpClient http, string name)
    {
        var reg = await new MmmClient(http, $"{name}-human").PerformAsync("Register", new Complements { With = [Profile(name)] });
        var user = new MmmClient(http, name, reg.Tokens[name]);
        Assert.Equal(200, (await user.PerformAsync("MM-Add", Uc2.C($"{name}-Persona", at: "MVS", with: Uc2.SpatialAttitude($"SA-{name}", 3, 0)))).Http);
        return user;
    }

    private static Task<MmmClient.Result> Go(MmmClient u, string from, string to, double x = 0, double y = 0) =>
        u.PerformAsync("MM-Move", Uc2.C($"{u.ProcessID}-Persona", from: from, to: to, with: Uc2.SpatialAttitude($"SA-{u.ProcessID}-{to}", x, y)));

    private static async Task<Dictionary<string, string>> AskSeat(MmmClient u, MeetingServices services)
    {
        var id = $"Join-{u.ProcessID}-{Guid.NewGuid():N}";
        await u.PerformAsync("MM-Send", Uc2.C(id, to: Meeting.Avs, with: Uc2.Message(id, Meeting.JoinText($"{u.ProcessID}-Persona"))));
        await services.PumpAsync();
        return Meeting.ParseSeat(Meeting.TextOf((await u.ReadAsync($"Seat-{id}")).Item)!.Replace("not invited", "seat=none").Replace("the meeting is over", "over=1"));
    }

    private static Task<MmmClient.Result> Capture(MmmClient u, string at) => u.PerformAsync("MM-Capture", Uc2.C(at: at, with: new JsonObject
    {
        ["Header"] = "OSD-OPV-V1.5", ["PointOfViewID"] = "POV", ["CartPosition"] = new JsonArray(0.0, 0.0, 1.6), ["Orientation"] = new JsonArray(0.0, 0.0, 0.0)
    }));

    private sealed class Running : IAsyncDisposable
    {
        public MInstance M = new(Repository.Schemas);
        public WebApplication? App;
        public HttpClient Http = null!;
        public MeetingServices Services = null!;
        public Func<DateTimeOffset>? Clock;
        public async Task StartAsync(DateTimeOffset? endsAt)
        {
            World.Setup(M, Repository.Root);
            if (Clock is not null) M.Clock = Clock;
            M.DefaultModels = ["Persona1.glb", "Persona2.glb"];
            App = MmmServer.Build(M, "http://127.0.0.1:0");
            await App.StartAsync();
            Http = new HttpClient { BaseAddress = new Uri(App.Urls.First()) };
            Services = new MeetingServices(Http, M) { EndsAt = endsAt };
            if (Clock is not null) Services.Clock = Clock;
            Services.Preinvite(["Anna", "Bruno"]);               // Anna is the convener
            await Services.StartAsync();
            await Services.PumpAsync(); await Services.PumpAsync();
        }
        public async ValueTask DisposeAsync() { Http?.Dispose(); if (App is not null) await App.DisposeAsync(); Meeting.Reset(); }
    }

    // What the viewer reads: the buildings and their doors are where the Locations are, a walker can pass a door, a seat is in its room, the glTF files are glTF.
    [Fact]
    public void TheWorldFileAgreesWithTheLocationsAndTheBuildingsAreGltf()
    {
        var world = World.Load(Repository.Root);
        var m = new MInstance(Repository.Schemas);
        World.Setup(m, Repository.Root);
        try
        {
            bool Blocked(double x, double y, IEnumerable<double[]> solids) => solids.Any(r => x > r[0] - 0.3 && x < r[2] + 0.3 && y > r[1] - 0.3 && y < r[3] + 0.3);
            foreach (var b in world["buildings"]!.AsArray().OfType<JsonObject>())
            {
                var id = (string)b["id"]!;
                Assert.NotNull(m.Get(id));
                var solids = b["solids"]!.AsArray().Select(r => r!.AsArray().Select(v => (double)v!).ToArray()).ToList();
                var doors = b["doors"]!.AsArray().OfType<JsonObject>().Concat(b["rooms"]!.AsArray().OfType<JsonObject>().Select(r => r["door"]).OfType<JsonObject>()).ToList();
                Assert.NotEmpty(doors);
                foreach (var d in doors)
                {
                    Assert.NotNull(m.Get((string)d["from"]!)); Assert.NotNull(m.Get((string)d["to"]!));
                    var (x, y, outside) = ((double)d["x"]!, (double)d["y"]!, (int)d["outside"]!);
                    var along = (string)d["axis"]! == "x" ? (1.0, 0.0) : (0.0, 1.0);
                    foreach (var t in new[] { -0.8, 0, 0.8 })                      // a walker (30 cm) passes the door: nothing of the walls is in the way, outside, in the opening or inside
                        Assert.False(Blocked(x + along.Item1 * t, y + along.Item2 * t, solids), $"{d["id"]}: blocked at {t}");
                }
                var glb = File.ReadAllBytes(Path.Combine(Repository.Root, "MMM", "Viewer", (string)b["model"]!));
                Assert.Equal("glTF", Encoding.ASCII.GetString(glb, 0, 4));
                var jsonLength = BitConverter.ToInt32(glb, 12);
                var gltf = JsonNode.Parse(Encoding.UTF8.GetString(glb, 20, jsonLength))!;
                Assert.Equal("2.0", (string?)gltf["asset"]!["version"]);
                Assert.NotEmpty(gltf["meshes"]!.AsArray());
                foreach (var r in b["rooms"]!.AsArray().OfType<JsonObject>())
                {
                    Assert.NotNull(m.Get((string)r["id"]!));
                    Assert.NotNull(m.Get((string)r["id"]!)!.Body);          // the room is a Location Object whose Attributes carry its Acoustic Profile
                    Assert.Equal("CAE-ACP-V1.0", (string?)m.Get((string)r["id"]!)!.Body!["LocationQualifier"]!["Attributes"]!["AcousticProfile"]!["Header"]);
                    var f = r["footprint"]!.AsArray().Select(v => (double)v!).ToArray();
                    foreach (var s in r["seats"]!.AsArray().OfType<JsonObject>())
                    {
                        Assert.NotNull(m.Get((string)s["id"]!));
                        Assert.InRange((double)s["x"]!, f[0], f[2]); Assert.InRange((double)s["y"]!, f[1], f[3]);
                    }
                }
            }
            Assert.True(Meeting.Seats.Length >= 8);                               // five humans, the Secretary, and room to spare
        }
        finally { Meeting.Reset(); }
    }

    [Fact]
    public async Task TheDoorIsARightThatTheMeetingGivesAndTheEndOfTheMeetingWithdraws()
    {
        await using var w = new Running();
        await w.StartAsync(DateTimeOffset.UtcNow.AddHours(2));
        var anna = await Arrive(w.Http, "Anna"); var bruno = await Arrive(w.Http, "Bruno"); var zed = await Arrive(w.Http, "Zed");

        // Before anybody has been given anything, the door is shut to all: the Town Hall is the AVS Service's.
        Assert.Equal(403, (await Go(anna, "MVS", Meeting.Parcel, 0, 0)).Http);

        // Invited, a User asks for its seat and is given it, with the right to the doors for the meeting; the one that is not invited is told so, and the doors stay shut to it.
        var a = await AskSeat(anna, w.Services); var b = await AskSeat(bruno, w.Services); var z = await AskSeat(zed, w.Services);
        Assert.Equal("1", a["convener"]); Assert.False(b.ContainsKey("convener"));
        Assert.Equal("none", z["seat"]);
        Assert.Equal(403, (await Go(zed, "MVS", Meeting.Parcel)).Http);
        Assert.Equal(403, (await Go(zed, "MVS", Meeting.Room)).Http);

        // The rights carry the period of the meeting.
        var held = w.M.RightsOf("Anna").Where(r => r["RightsTime"] is not null).ToList();
        Assert.NotEmpty(held);
        foreach (var r in held)
        {
            var t = r["RightsTime"]!["SimpleTimeData"]![0]!;
            Assert.True((long)t["EndTime"]! > (long)t["StartTime"]!);
        }

        // Anna walks through the main door, through the meeting room's, and sits: three Process Actions, each a Location behind a door or at the table.
        Assert.Equal(200, (await Go(anna, "MVS", Meeting.Parcel)).Http);
        Assert.Equal(200, (await Go(anna, Meeting.Parcel, Meeting.Room)).Http);
        Assert.Equal(200, (await anna.PerformAsync("MM-Move", Uc2.C("Anna-Persona", from: Meeting.Room, to: a["seat"],
            with: [MeetingServices.SeatedAt("SA-Anna-Seat", double.Parse(a["yaw"], System.Globalization.CultureInfo.InvariantCulture)), Meeting.SitPA("Anna-Persona", a["seat"]),
                   Uc2.Rights("Seat-Move-Anna-Persona", "May", "Acquired", Meeting.SitID("Anna-Persona"))]))).Http);
        Assert.Equal(200, (await Go(bruno, "MVS", Meeting.Parcel)).Http);
        Assert.Equal(200, (await Capture(anna, Meeting.Room)).Http);

        // The convener ends the meeting: every right of every participant is withdrawn.
        await anna.PerformAsync("MM-Send", Uc2.C("End-1", to: Meeting.Avs, with: Uc2.Message("End-1", "end")));
        await w.Services.PumpAsync();
        Assert.True(w.Services.Ended);
        Assert.Equal(403, (await Capture(anna, Meeting.Room)).Http);                        // the room is not hers to see any more
        Assert.Equal(200, (await Go(anna, a["seat"], "MVS", 13.5, 0)).Http);               // she leaves: the Square is open to all
        Assert.Equal(200, (await Go(bruno, Meeting.Parcel, "MVS", 13.5, 0)).Http);
        Assert.Equal(403, (await Go(anna, "MVS", Meeting.Parcel)).Http);                    // and cannot come in again
        Assert.Equal(403, (await Go(bruno, "MVS", Meeting.Parcel)).Http);
        Assert.Equal("1", (await AskSeat(anna, w.Services)).GetValueOrDefault("over"));    // nor ask for a seat: the meeting is over
    }

    [Fact]
    public async Task OnlyTheConvenerEndsTheMeetingAndTheRightsEndWithItsTimeToo()
    {
        var now = DateTimeOffset.UtcNow;                                    // the time of this test: it is moved, not waited for
        await using var w = new Running { Clock = () => now };
        await w.StartAsync(now.AddHours(1));                                // a meeting of one hour
        var anna = await Arrive(w.Http, "Anna"); var bruno = await Arrive(w.Http, "Bruno");
        await AskSeat(anna, w.Services); await AskSeat(bruno, w.Services);

        await bruno.PerformAsync("MM-Send", Uc2.C("End-B", to: Meeting.Avs, with: Uc2.Message("End-B", "end")));
        await w.Services.PumpAsync();
        Assert.False(w.Services.Ended);                                     // Bruno is not the convener
        Assert.Equal(200, (await Go(bruno, "MVS", Meeting.Parcel)).Http);
        Assert.Equal(200, (await Go(bruno, Meeting.Parcel, "MVS", 13.5, 0)).Http);

        now = now.AddMinutes(61);                                           // the period is over: the M-Instance no longer holds the rights, even before the AVS Service withdraws them
        Assert.Equal(403, (await Go(anna, "MVS", Meeting.Parcel)).Http);
        await w.Services.PumpAsync();                                       // and the AVS Service ends the meeting at its time
        Assert.True(w.Services.Ended);
    }
}
