using System.Text;
using System.Text.Json.Nodes;
using Mpai.Mmm;
using Mpai.Mmm.Client;
using Microsoft.AspNetCore.Builder;
using Mpai.Mmm.Server;

namespace Mpai.Aif.Tests;

// THE WORLD OF THE MMM (MMM/World/world.json, the glTF buildings): the Metaverse Square, the MPAI building (a public atrium, and wings of rooms: the members', the offices of MPAI, the meeting rooms of the groups), a library and a cafe. A building is a Location,
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

    private const string Building = "Atrium", Wing = "MeetingsWing";     // the atrium is public; the wing and the room are the AVS Service's

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
                    foreach (var s in (r["seats"]?.AsArray() ?? []).OfType<JsonObject>())
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

        // The atrium is public: anybody goes in. The wing of the meeting rooms is the AVS Service's: before anybody has been given anything, its door is shut to all.
        Assert.Equal(200, (await Go(zed, "MVS", Building, 0, 0)).Http);
        Assert.Equal(200, (await Go(anna, "MVS", Building, 0, 0)).Http);
        Assert.Equal(200, (await Go(bruno, "MVS", Building, 0, 0)).Http);
        Assert.Equal(403, (await Go(anna, Building, Wing, 0, 0)).Http);

        // Invited, a User asks for its seat and is given it, with the right to the doors for the meeting; the one that is not invited is told so, and the doors stay shut to it.
        var a = await AskSeat(anna, w.Services); var b = await AskSeat(bruno, w.Services); var z = await AskSeat(zed, w.Services);
        Assert.Equal("1", a["convener"]); Assert.False(b.ContainsKey("convener"));
        Assert.Equal("none", z["seat"]);
        Assert.Equal(403, (await Go(zed, Building, Wing)).Http);
        Assert.Equal(403, (await Go(zed, Building, Meeting.Room)).Http);

        // The rights carry the period of the meeting.
        var held = w.M.RightsOf("Anna").Where(r => r["RightsTime"] is not null).ToList();
        Assert.NotEmpty(held);
        foreach (var r in held)
        {
            var t = r["RightsTime"]!["SimpleTimeData"]![0]!;
            Assert.True((long)t["EndTime"]! > (long)t["StartTime"]!);
        }

        // Anna walks through the door of the wing, through the meeting room's, and sits: three Process Actions, each a Location behind a door or at the table.
        Assert.Equal(200, (await Go(anna, Building, Wing)).Http);
        Assert.Equal(200, (await Go(anna, Wing, Meeting.Room)).Http);
        Assert.Equal(200, (await anna.PerformAsync("MM-Move", Uc2.C("Anna-Persona", from: Meeting.Room, to: a["seat"],
            with: [MeetingServices.SeatedAt("SA-Anna-Seat", double.Parse(a["yaw"], System.Globalization.CultureInfo.InvariantCulture)), Meeting.SitPA("Anna-Persona", a["seat"]),
                   Uc2.Rights("Seat-Move-Anna-Persona", "May", "Acquired", Meeting.SitID("Anna-Persona"))]))).Http);
        Assert.Equal(200, (await Go(bruno, Building, Wing)).Http);
        Assert.Equal(200, (await Capture(anna, Meeting.Room)).Http);

        // The convener ends the meeting: every right of every participant is withdrawn.
        await anna.PerformAsync("MM-Send", Uc2.C("End-1", to: Meeting.Avs, with: Uc2.Message("End-1", "end")));
        await w.Services.PumpAsync();
        Assert.True(w.Services.Ended);
        Assert.Equal(403, (await Capture(anna, Meeting.Room)).Http);                        // the room is not hers to see any more
        Assert.Equal(200, (await Go(anna, a["seat"], "MVS", 13.5, 0)).Http);               // she leaves: the Square is open to all
        Assert.Equal(200, (await Go(bruno, Wing, "MVS", 13.5, 0)).Http);
        Assert.Equal(200, (await Go(anna, "MVS", Building)).Http);                          // the atrium is still open to them
        Assert.Equal(403, (await Go(anna, Building, Wing)).Http);                           // the wing is not: they cannot come in again
        Assert.Equal(200, (await Go(bruno, "MVS", Building)).Http);
        Assert.Equal(403, (await Go(bruno, Building, Wing)).Http);
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
        Assert.Equal(200, (await Go(bruno, "MVS", Building)).Http);
        Assert.Equal(200, (await Go(bruno, Building, Wing)).Http);
        Assert.Equal(200, (await Go(bruno, Wing, "MVS", 13.5, 0)).Http);

        now = now.AddMinutes(61);                                           // the period is over: the M-Instance no longer holds the rights, even before the AVS Service withdraws them
        Assert.Equal(200, (await Go(anna, "MVS", Building)).Http);
        Assert.Equal(403, (await Go(anna, Building, Wing)).Http);
        await w.Services.PumpAsync();                                       // and the AVS Service ends the meeting at its time
        Assert.True(w.Services.Ended);
    }

    // A SHARED SCREEN: the convener alone has the Right to place one in the meeting room; whoever has an Item in the room sees it, as its frames come; whoever is not in the room does not;
    // and it is over with the meeting.
    [Fact]
    public async Task OnlyTheConvenerSharesAScreenAndWhoIsInTheRoomSeesIt()
    {
        await using var w = new Running();
        await w.StartAsync(DateTimeOffset.UtcNow.AddHours(2));
        var anna = await Arrive(w.Http, "Anna"); var bruno = await Arrive(w.Http, "Bruno"); var zed = await Arrive(w.Http, "Zed");
        await AskSeat(anna, w.Services); await AskSeat(bruno, w.Services);
        foreach (var u in new[] { anna, bruno, zed }) Assert.Equal(200, (await Go(u, "MVS", Building)).Http);
        foreach (var u in new[] { anna, bruno }) { Assert.Equal(200, (await Go(u, Building, Wing)).Http); Assert.Equal(200, (await Go(u, Wing, Meeting.Room)).Http); }

        JsonObject Screen(string id) => new()
        {
            ["Header"] = "OSD-BVO-V1.5", ["MInstanceID"] = "MI1", ["BasicVisualObjectID"] = id,
            ["BasicVisualObjectData"] = new JsonArray(new JsonObject { ["DataURI"] = "ws://x/watch" })
        };
        Task<MmmClient.Result> Share(MmmClient u, string id) => u.PerformAsync("MM-Add", Uc2.C(id, at: Meeting.Room, with: Screen(id)));
        Assert.Equal(403, (await Share(bruno, "Screen-Bruno")).Http);                  // not the convener: no Right
        Assert.Equal(200, (await Share(anna, "Screen-Anna")).Http);                    // the convener's
        Assert.Equal(409, (await Share(bruno, "Screen-Bruno-2")).Http);              // one at a time in a room

        var ws = w.App!.Urls.First().Replace("http", "ws");
        using var feed = new System.Net.WebSockets.ClientWebSocket();
        await feed.ConnectAsync(new Uri($"{ws}/ws/v2.2/screen/Screen-Anna?token={w.M.TokenOf("Anna")}"), CancellationToken.None);
        using var watch = new System.Net.WebSockets.ClientWebSocket();
        await watch.ConnectAsync(new Uri($"{ws}/ws/v2.2/screen/Screen-Anna/watch?token={w.M.TokenOf("Bruno")}"), CancellationToken.None);
        using var outside = new System.Net.WebSockets.ClientWebSocket();
        await Assert.ThrowsAnyAsync<Exception>(() => outside.ConnectAsync(new Uri($"{ws}/ws/v2.2/screen/Screen-Anna/watch?token={w.M.TokenOf("Zed")}"), CancellationToken.None));   // not in the room

        // A message is one byte, which says what it is (1: a frame, 2: sound), and the data. The sound reaches whoever watches, in order, and so does the latest frame.
        var frame = new byte[] { 1, 0xFF, 0xD8, 1, 2, 3, 0xFF, 0xD9 };
        var sound1 = new byte[] { 2, 10, 0, 20, 0 }; var sound2 = new byte[] { 2, 30, 0, 40, 0 };
        foreach (var m in new[] { sound1, sound2, frame }) await feed.SendAsync(m, System.Net.WebSockets.WebSocketMessageType.Binary, true, CancellationToken.None);
        var received = new List<byte[]>();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (received.Count < 3)
        {
            var got = new byte[64];
            var r = await watch.ReceiveAsync(got, limit.Token);
            received.Add(got.Take(r.Count).ToArray());
        }
        Assert.Equal([sound1, sound2, frame], received);                              // the sound as it was said, and the frame

        // In what MM-Capture returns, the screen is a Thing of Kind Screen, owned by Anna, streaming.
        var scene = (await Capture(bruno, Meeting.Room)).Response.Complements?.With?[0]?["ItemValueOrRef"]?[0]?["Things"]?.AsArray() ?? [];
        Assert.Contains(scene.OfType<JsonObject>(), t => (string?)t["Kind"] == "Screen" && (string?)t["Owner"] == "Anna" && (bool?)t["Streaming"] == true);

        // The convener ends the meeting: the screen is over.
        await anna.PerformAsync("MM-Send", Uc2.C("End-S", to: Meeting.Avs, with: Uc2.Message("End-S", "end")));
        await w.Services.PumpAsync();
        Assert.False(w.M.ScreenLive("Screen-Anna"));
    }
}
