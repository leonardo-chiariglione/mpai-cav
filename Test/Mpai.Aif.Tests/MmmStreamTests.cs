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
[Collection("MmmMeeting")]                       // they share the static state of the Meeting: one at a time
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

        // Anna's speech starts: her Device UM-Captures it at her U-Location With a Speech Qualifier that says it is a WebSocket stream (it has no duration yet); the Data is Identified as a Speech Object,
        // which she MM-Adds at her seat; its data is not there yet.
        var id1 = "Speech-Anna-1";
        var qualifier = Speech.StreamQualifier(id1);
        Assert.Equal("WebSocket", (string?)qualifier["Formats"]!["TransportFormats"]!["StreamFormat"]);
        Assert.Equal(201, (await anna.PerformAsync("UM-Capture", Uc2.C(id1, at: "ULoc-Anna", with: qualifier))).Http);
        Assert.Equal(201, (await anna.PerformAsync("Identify", Uc2.C(id1, with: qualifier.DeepClone()))).Http);
        var added = await anna.PerformAsync("MM-Add", Uc2.C(id1, at: annaSeat));
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

    // A speech has no duration fixed beforehand: it goes on as long as the person speaks. The Secretary does not wait for it to end: it listens to the stream and has what it hears recognised
    // at a pause, so the words of the first sentence are in the minutes while the person is still speaking.
    [Fact]
    public async Task TheSecretaryHasASentenceRecognisedAtAPauseWhileTheSpeechGoesOn()
    {
        var m = new MInstance(Repository.Schemas);
        Uc2.Setup(m);
        Meeting.Setup(m);
        await using var app = MmmServer.Build(m, "http://127.0.0.1:0");
        await app.StartAsync();
        var url = app.Urls.First();
        var ws = url.Replace("http", "ws");
        using var http = new HttpClient { BaseAddress = new Uri(url), Timeout = Timeout.InfiniteTimeSpan };
        var said = new Queue<string>(["the first sentence", "the second sentence"]);
        var services = new MeetingServices(http, m, _ => Task.FromResult(said.Count > 0 ? said.Dequeue() : "more"));

        var anna = await Register(http, "Anna");
        var bruno = await Register(http, "Bruno");
        await services.StartAsync(); await services.PumpAsync(); await services.PumpAsync();
        foreach (var (id, text) in new[] { ("Convene-Anna", "convene"), ("Invite-Bruno", "invite;user=Bruno") })
            await anna.PerformAsync("MM-Send", Uc2.C(id, to: Meeting.Avs, with: Uc2.Message(id, text)));
        await services.PumpAsync();
        var seat = await Sit(services, anna);
        await Sit(services, bruno);

        var id1 = "Speech-Anna-1";
        var q = Speech.StreamQualifier(id1);
        Assert.Equal(201, (await anna.PerformAsync("UM-Capture", Uc2.C(id1, at: "ULoc-Anna", with: q))).Http);
        Assert.Equal(201, (await anna.PerformAsync("Identify", Uc2.C(id1, with: q.DeepClone()))).Http);
        Assert.Equal(200, (await anna.PerformAsync("MM-Add", Uc2.C(id1, at: seat))).Http);
        using var feed = new ClientWebSocket();
        await feed.ConnectAsync(new Uri($"{ws}/ws/v2.2/speech/{id1}?token={m.TokenOf("Anna")}"), CancellationToken.None);

        // A sentence, then a pause of a second: the stream stays open.
        await feed.SendAsync(Pcm(0.8, 0.05), WebSocketMessageType.Binary, true, CancellationToken.None);
        for (var i = 0; i < 10; i++) await feed.SendAsync(new byte[3200], WebSocketMessageType.Binary, true, CancellationToken.None);
        for (var i = 0; i < 60 && !services.TheMinutes.Contains("Anna: the first sentence"); i++)
        {
            await services.PumpAsync(); await services.PumpAsrAsync(); await services.PumpAsync();
            await Task.Delay(100);
        }
        Assert.Contains("Anna: the first sentence", services.TheMinutes);
        Assert.True(m.IsStream(id1));                                                              // she is still speaking: the Speech Object is still a stream

        // She goes on and stops: the second sentence, and the Speech Object is complete with its own Time.
        await feed.SendAsync(Pcm(0.8, 0.07), WebSocketMessageType.Binary, true, CancellationToken.None);
        await feed.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        for (var i = 0; i < 60 && !services.TheMinutes.Contains("Anna: the second sentence"); i++)
        {
            await services.PumpAsync(); await services.PumpAsrAsync(); await services.PumpAsync();
            await Task.Delay(100);
        }
        Assert.Equal(["Anna: the first sentence", "Anna: the second sentence"], services.TheMinutes);
        Assert.False(m.IsStream(id1));
        Assert.True(Speech.DurationMs((await bruno.ReadAsync(id1)).Item) >= 1500);                // its duration is in it now: the sentences and the pause
        await app.StopAsync();
    }

    // THE MOUTH OF A PERSONA FOLLOWS THE SPEECH OF ITS HUMAN: an Animation Stream. The User UM-Captures it (an Animation Qualifier, TFA-AMQ, content format Viseme, transport format
    // AudioVisemeStream), has it Identified and MM-Animates its Persona with it; its frames go out as they are produced; whoever may see the Persona watches and receives them as they come.
    [Fact]
    public async Task APersonaIsAnimatedByAStreamThatEveryoneInTheRoomWatches()
    {
        var m = new MInstance(Repository.Schemas);
        Uc2.Setup(m);
        Meeting.Setup(m);
        await using var app = MmmServer.Build(m, "http://127.0.0.1:0");
        await app.StartAsync();
        var url = app.Urls.First();
        var ws = url.Replace("http", "ws");
        using var http = new HttpClient { BaseAddress = new Uri(url), Timeout = Timeout.InfiniteTimeSpan };
        var services = new MeetingServices(http, m, _ => Task.FromResult("words"));
        var anna = await Register(http, "Anna");
        var bruno = await Register(http, "Bruno");
        var carla = await Register(http, "Carla");
        await services.StartAsync(); await services.PumpAsync(); await services.PumpAsync();
        foreach (var (id, text) in new[] { ("Convene-Anna", "convene"), ("Invite-Bruno", "invite;user=Bruno") })
            await anna.PerformAsync("MM-Send", Uc2.C(id, to: Meeting.Avs, with: Uc2.Message(id, text)));
        await services.PumpAsync();
        await Sit(services, anna);
        await Sit(services, bruno);

        var q = new JsonObject
        {
            ["Header"] = "TFA-AMQ-V1.5", ["AnimationQualifierID"] = "Mouth-Anna-Q", ["SubTypes"] = "Animation",
            ["Formats"] = new JsonObject { ["ContentFormat"] = "Viseme", ["TransportFormat"] = "AudioVisemeStream" }
        };
        Assert.Equal(201, (await anna.PerformAsync("UM-Capture", Uc2.C("Mouth-Anna", at: "ULoc-Anna", with: q))).Http);
        var identified = await anna.PerformAsync("Identify", Uc2.C("Mouth-Anna", with: q.DeepClone()));
        Assert.Equal(201, identified.Http);
        var stream = identified.Response.Complements!.Nil!;
        Assert.True(m.AnimationToFeed("Anna", stream));
        Assert.False(m.AnimationToFeed("Bruno", stream));                                                   // only its User feeds it
        Assert.Equal(403, (await bruno.PerformAsync("MM-Animate", Uc2.C("Anna-Persona", with: stream))).Http);   // nor does another animate Anna's Persona with it
        Assert.Equal(200, (await anna.PerformAsync("MM-Animate", Uc2.C("Anna-Persona", with: stream))).Http);

        using var brunoWatch = new ClientWebSocket();
        await brunoWatch.ConnectAsync(new Uri($"{ws}/ws/v2.2/animation/{stream}/watch?token={m.TokenOf("Bruno")}"), CancellationToken.None);    // in the room: he sees her Persona
        using var carlaWatch = new ClientWebSocket();
        await Assert.ThrowsAnyAsync<Exception>(() => carlaWatch.ConnectAsync(new Uri($"{ws}/ws/v2.2/animation/{stream}/watch?token={m.TokenOf("Carla")}"), CancellationToken.None));   // outside it: she does not
        using var intruder = new ClientWebSocket();
        await Assert.ThrowsAnyAsync<Exception>(() => intruder.ConnectAsync(new Uri($"{ws}/ws/v2.2/animation/{stream}?token={m.TokenOf("Bruno")}"), CancellationToken.None));

        using var feed = new ClientWebSocket();
        await feed.ConnectAsync(new Uri($"{ws}/ws/v2.2/animation/{stream}?token={m.TokenOf("Anna")}"), CancellationToken.None);
        var frame = "{\"f\":[[10,0.8],[11,0.5],[0,0]]}";
        await feed.SendAsync(System.Text.Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[1024];
        using var within = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var got = await brunoWatch.ReceiveAsync(buffer, within.Token);
        Assert.Equal(frame, System.Text.Encoding.UTF8.GetString(buffer, 0, got.Count));                    // as it was produced, to whoever watches
        Assert.Contains("Anna-Persona", m.Snapshot()["Things"]!.AsArray().OfType<JsonObject>().Where(t => (string?)t["Animation"] == stream).Select(t => (string?)t["ID"]));

        await feed.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        for (var i = 0; i < 40 && m.Snapshot()["Things"]!.AsArray().OfType<JsonObject>().Any(t => (string?)t["Animation"] == stream); i++) await Task.Delay(50);
        Assert.DoesNotContain(m.Snapshot()["Things"]!.AsArray().OfType<JsonObject>(), t => (string?)t["Animation"] == stream);      // over: it animates nothing, and it is gone
        await app.StopAsync();
    }
}
