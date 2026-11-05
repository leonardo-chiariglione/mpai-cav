using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Mpai.Mmm;
using Mpai.Mmm.Client;
using Mpai.Mmm.Server;

namespace Mpai.Aif.Tests;

// THE DIALOGUE WITH THE SECRETARY (DRAFT, M3283). The Secretary is a User of the meeting. A participant that speaks to it sends its Speech Object, a stream, to the Secretary (MM-Send) and not to the
// room: the room hears nothing of it and the minutes do not have it. The Secretary has the words recognised, has them turned into a request (the Voice Command Interface: here a script, the language model
// is not the point), carries it out for that participant, and answers in a few words - a Message that only that participant is given.
[Trait("Group", "Fast")]
[Collection("MmmMeeting")]
[Trait("Blocks", "Yes")]
public class MmmSecretaryDialogueTests
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

    private static async Task Sit(MeetingServices services, MmmClient user)
    {
        var persona = $"{user.ProcessID}-Persona";
        var ask = $"Join-{user.ProcessID}";
        await user.PerformAsync("MM-Send", Uc2.C(ask, to: Meeting.Avs, with: Uc2.Message(ask, Meeting.JoinText(persona))));
        await services.PumpAsync();
        var seat = Meeting.ParseSeat(Meeting.TextOf((await user.ReadAsync($"Seat-{ask}")).Item)!);
        var move = await user.PerformAsync("MM-Move", Uc2.C(persona, from: "MVS", to: seat["seat"],
            with: [MeetingServices.SeatedAt($"SA-{persona}-Seat", 0), Meeting.SitPA(persona, seat["seat"]), Uc2.Rights($"Seat-Move-{persona}", "May", "Acquired", Meeting.SitID(persona))]));
        Assert.Equal(200, move.Http);
    }

    private static byte[] Pcm(double seconds)
    {
        var pcm = new byte[(int)(seconds * Speech.Rate) * 2];
        for (var i = 0; i < pcm.Length / 2; i++) BitConverter.GetBytes((short)(6000 * Math.Sin(i * 0.05))).CopyTo(pcm, i * 2);
        return pcm;
    }

    private static JsonObject Command(JsonObject rq) => new() { ["Header"] = "MMM-UCM-V2.2", ["UserCommandID"] = Guid.NewGuid().ToString(), ["UserCommandRQ"] = rq };

    [Fact]
    public async Task AParticipantSpeaksToTheSecretaryAndNobodyElseHearsIt()
    {
        var m = new MInstance(Repository.Schemas);
        Uc2.Setup(m);
        Meeting.Setup(m);
        await using var app = MmmServer.Build(m, "http://127.0.0.1:0");
        await app.StartAsync();
        var url = app.Urls.First();
        var ws = url.Replace("http", "ws");
        using var http = new HttpClient { BaseAddress = new Uri(url), Timeout = Timeout.InfiniteTimeSpan };
        var recognised = new Queue<string>();
        var services = new MeetingServices(http, m, _ => Task.FromResult(recognised.Count > 0 ? recognised.Dequeue() : "more"))
        {
            Synthesise = (text, language) => Task.FromResult(new byte[3200]),
            Interpret = (user, words) => Task.FromResult<(JsonObject?, string?)>((words switch
            {
                "raise my hand" => Command(new JsonObject { ["Hand"] = "Raise" }),
                "summarise in greek" => Command(new JsonObject { ["Summary"] = new JsonObject { ["Language"] = "el" } }),
                "mute me" => Command(new JsonObject { ["Microphone"] = "Off" }),
                "close the meeting" => Command(new JsonObject { ["CloseMeeting"] = true }),
                "thank you" => Command(new JsonObject { ["ReturnToMeeting"] = true }),
                _ => null
            }, null))
        };

        var anna = await Register(http, "Anna");
        var bruno = await Register(http, "Bruno");
        await services.StartAsync(); await services.PumpAsync(); await services.PumpAsync();
        foreach (var (id, text) in new[] { ("Convene-Anna", "convene"), ("Invite-Bruno", "invite;user=Bruno") })
            await anna.PerformAsync("MM-Send", Uc2.C(id, to: Meeting.Avs, with: Uc2.Message(id, text)));
        await services.PumpAsync();
        await Sit(services, anna);
        await Sit(services, bruno);

        var n = 0;
        // What Anna says to the Secretary: a Speech Object that is a stream, sent to the Secretary; then the answer, a Message that is hers.
        async Task<JsonObject> Say(string words)
        {
            recognised.Enqueue(words);
            var id = $"Speech-Anna-{++n}";
            var q = Speech.StreamQualifier(id);
            Assert.Equal(201, (await anna.PerformAsync("UM-Capture", Uc2.C(id, at: "ULoc-Anna", with: q))).Http);
            Assert.Equal(201, (await anna.PerformAsync("Identify", Uc2.C(id, with: q.DeepClone()))).Http);
            var sent = await anna.PerformAsync("MM-Send", Uc2.C($"Say-{id}", to: Meeting.Secretary, with: new JsonObject { ["Header"] = "OSD-BSO-V1.5", ["MInstanceID"] = "MI1", ["BasicSpeechObjectID"] = id }));
            Assert.Equal(200, sent.Http);
            using var feed = new ClientWebSocket();
            await feed.ConnectAsync(new Uri($"{ws}/ws/v2.2/speech/{id}?token={m.TokenOf("Anna")}"), CancellationToken.None);
            await feed.SendAsync(Pcm(0.8), WebSocketMessageType.Binary, true, CancellationToken.None);
            for (var i = 0; i < 20; i++) await feed.SendAsync(new byte[3200], WebSocketMessageType.Binary, true, CancellationToken.None);       // 2 s of silence: the request is over
            var reply = $"Secretary-Anna-{n}";
            for (var i = 0; i < 100; i++)
            {
                await services.PumpAsync(); await services.PumpAsrAsync(); await services.PumpAsync(); await services.PumpDialogueAsync();
                if ((await anna.ReadAsync(reply)).Http == 200) break;
                await Task.Delay(100);
            }
            await feed.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            var told = (await anna.ReadAsync(reply)).Item;
            Assert.NotNull(told);
            return (JsonObject)JsonNode.Parse(Meeting.TextOf(told)!)!;
        }

        var raised = await Say("raise my hand");
        Assert.Equal("Your hand is up.", (string?)raised["Say"]);
        Assert.Contains("Anna", services.HandsUp);                                                    // the Secretary did it
        Assert.Null((string?)raised["Speech"]);                                                       // text is the default: no voice unless asked
        Assert.True((bool)raised["Written"]!);
        Assert.DoesNotContain(services.TheMinutes, line => line.Contains("raise my hand"));          // it is not the meeting: not in the minutes

        // The room does not hear it: Bruno, in the room, is not given the Speech Object, and cannot read the answer.
        Assert.NotEqual(200, (await bruno.ReadAsync("Speech-Anna-1")).Http);
        Assert.NotEqual(200, (await bruno.ReadAsync("Secretary-Anna-1")).Http);

        var summary = await Say("summarise in greek");
        Assert.Equal("el", (string?)summary["Do"]!["Summary"]!["Language"]);                           // the client is told which summary to ask for
        Assert.Equal("written", (string?)summary["Do"]!["Summary"]!["Form"]);                         // text is the default

        var mute = await Say("mute me");
        Assert.Equal("Off", (string?)mute["Do"]!["Microphone"]);

        var close = await Say("close the meeting");                                                    // never on the first request: it asks to be sure
        Assert.Contains("close the meeting for everybody", (string?)close["Say"]);
        Assert.False(services.Ended);
        var yes = await Say("yes");
        Assert.True(services.Ended);

        await app.StopAsync();
    }
}
