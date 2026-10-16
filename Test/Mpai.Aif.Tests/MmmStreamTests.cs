using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Mpai.Mmm;
using Mpai.Mmm.Client;
using Mpai.Mmm.Server;

namespace Mpai.Aif.Tests;

// A SPEECH OBJECT THAT IS A STREAM (MMM Phase 8). Anna's client declares a Speech Object when her speech starts - the time it lasts, its Speech Qualifier (raw PCM, stream format
// WebSocket), the address its data is delivered from - and the data goes out as she captures it. Bruno, who has chosen to hear her, opens that address and receives the first of
// it before she has finished; Carla, outside the room, may not; and only Anna feeds it. When the stream is over its data is the Speech Object's, and the Secretary puts the
// words in the minutes.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class MmmStreamTests
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

    private static async Task<string> Sit(MeetingServices services, MmmClient user)
    {
        var persona = $"{user.ProcessID}-Persona";
        var ask = $"Join-{user.ProcessID}";
        await user.PerformAsync("MM-Send", Uc2.C(ask, to: Meeting.Avs, with: Uc2.Message(ask, Meeting.JoinText(persona))));
        await services.PumpAsync();
        var seat = Meeting.ParseSeat(Meeting.TextOf((await user.ReadAsync($"Seat-{ask}")).Item)!);
        var move = await user.PerformAsync("MM-Move", Uc2.C(persona, from: "MVS", to: seat["seat"],
            with: [MeetingServices.SeatedAt($"SA-{persona}-Seat", 0), Meeting.SitPA(persona, seat["seat"]), Uc2.Rights($"Seat-Move-{persona}", "May", "Acquired", Meeting.SitID(persona))]));
        Assert.Equal(200, move.Http);
        return seat["seat"];
    }

    private static byte[] Pcm(double seconds, double f)
    {
        var pcm = new byte[(int)(seconds * Speech.Rate) * 2];
        for (var i = 0; i < pcm.Length / 2; i++) BitConverter.GetBytes((short)(6000 * Math.Sin(i * f))).CopyTo(pcm, i * 2);
        return pcm;
    }

    // Receives from a WebSocket until n bytes are in, or the sender closes.
    private static async Task<byte[]> Take(ClientWebSocket s, int n, TimeSpan within)
    {
        using var stop = new CancellationTokenSource(within);
        var got = new List<byte>(); var buf = new byte[8192];
        while (got.Count < n)
        {
            var r = await s.ReceiveAsync(buf, stop.Token);
            if (r.MessageType == WebSocketMessageType.Close) break;
            got.AddRange(buf.AsSpan(0, r.Count).ToArray());
        }
        return got.ToArray();
    }

    [Fact]
    public async Task ASpeechObjectIsHeardWhileItIsStillBeingSpoken()
    {
        var m = new MInstance(Repository.Schemas);
        Uc2.Setup(m);
        Meeting.Setup(m);
        await using var app = MmmServer.Build(m, "http://127.0.0.1:0");
        await app.StartAsync();
        var url = app.Urls.First();
        var ws = url.Replace("http", "ws");
        using var http = new HttpClient { BaseAddress = new Uri(url), Timeout = Timeout.InfiniteTimeSpan };
        var services = new MeetingServices(http, m, _ => Task.FromResult("this was said while it was streamed"));

        var anna = await Register(http, "Anna");
        var bruno = await Register(http, "Bruno");
        var carla = await Register(http, "Carla");
        await services.StartAsync(); await services.PumpAsync(); await services.PumpAsync();
        foreach (var (id, text) in new[] { ("Convene-Anna", "convene"), ("Invite-Bruno", "invite;user=Bruno") })
            await anna.PerformAsync("MM-Send", Uc2.C(id, to: Meeting.Avs, with: Uc2.Message(id, text)));
        await services.PumpAsync();
        var annaSeat = await Sit(services, anna);
        await Sit(services, bruno);

        // Anna's speech starts: the Speech Object is declared, for 3 s, with its Qualifier; its data is not there yet.
        var id1 = "Speech-Anna-1";
        var declared = Speech.Declare(id1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 3000, $"{ws}/ws/v2.2/speech/{id1}/listen");
        Assert.Equal("WebSocket", (string?)declared["BasicSpeechDataQualifier"]!["Formats"]!["TransportFormats"]!["StreamFormat"]);
        var added = await anna.PerformAsync("MM-Add", Uc2.C(id1, at: annaSeat, with: declared));
        Assert.Equal(200, added.Http);                                                              // the Qualifier satisfies TFA-SPQ, WebSocket being in its list
        Assert.True(m.IsStream(id1));
        var thing = m.Snapshot()["Things"]!.AsArray().OfType<JsonObject>().Single(t => (string?)t["ID"] == id1);
        Assert.True((bool)thing["Streaming"]!);

        // Only Anna feeds it.
        using (var intruder = new ClientWebSocket())
            await Assert.ThrowsAnyAsync<Exception>(() => intruder.ConnectAsync(new Uri($"{ws}/ws/v2.2/speech/{id1}?token={m.TokenOf("Bruno")}"), CancellationToken.None));

        using var feed = new ClientWebSocket();
        await feed.ConnectAsync(new Uri($"{ws}/ws/v2.2/speech/{id1}?token={m.TokenOf("Anna")}"), CancellationToken.None);
        var first = Pcm(0.2, 0.05); var second = Pcm(0.2, 0.07);
        await feed.SendAsync(first, WebSocketMessageType.Binary, true, CancellationToken.None);

        // Bruno has chosen to hear Anna: his client opens the address the Speech Object declares and receives the first of it while Anna is still speaking.
        using var brunoEars = new ClientWebSocket();
        await brunoEars.ConnectAsync(new Uri($"{ws}/ws/v2.2/speech/{id1}/listen?token={m.TokenOf("Bruno")}"), CancellationToken.None);
        var heardFirst = await Take(brunoEars, first.Length, TimeSpan.FromSeconds(5));
        Assert.Equal(first, heardFirst);                                                            // before the second part was even sent
        Assert.True(m.IsStream(id1));

        // Carla, outside the room, may not.
        using var carlaEars = new ClientWebSocket();
        await Assert.ThrowsAnyAsync<Exception>(() => carlaEars.ConnectAsync(new Uri($"{ws}/ws/v2.2/speech/{id1}/listen?token={m.TokenOf("Carla")}"), CancellationToken.None));
        Assert.Equal(403, (await carla.ReadAsync(id1)).Http);

        // The Secretary does not take it while it is going on.
        await services.PumpAsync(); await services.PumpAsrAsync(); await services.PumpAsync();
        Assert.Empty(services.TheMinutes);

        // Anna goes on, and stops: the rest reaches Bruno, and the stream ends.
        await feed.SendAsync(second, WebSocketMessageType.Binary, true, CancellationToken.None);
        await feed.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        Assert.Equal(second, await Take(brunoEars, second.Length, TimeSpan.FromSeconds(5)));
        Assert.Empty(await Take(brunoEars, 1, TimeSpan.FromSeconds(5)));                            // the end of the stream

        // The data is now the Speech Object's, and the Secretary puts the words in the minutes.
        for (var i = 0; i < 20 && m.IsStream(id1); i++) await Task.Delay(50);
        Assert.False(m.IsStream(id1));
        Assert.Equal(first.Concat(second).ToArray(), m.SpeechData(id1));
        Assert.Equal(first.Length + second.Length, Speech.PcmOf((await bruno.ReadAsync(id1)).Item).Length);
        await services.PumpAsync(); await services.PumpAsrAsync(); await services.PumpAsync();
        Assert.Equal(["Anna: this was said while it was streamed"], services.TheMinutes);

        // A late listener is given what it was, and then the end.
        using var late = new ClientWebSocket();
        await late.ConnectAsync(new Uri($"{ws}/ws/v2.2/speech/{id1}/listen?token={m.TokenOf("Bruno")}"), CancellationToken.None);
        Assert.Equal(first.Length + second.Length, (await Take(late, first.Length + second.Length, TimeSpan.FromSeconds(5))).Length);
        await app.StopAsync();
    }
}
