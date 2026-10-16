using System.Globalization;
using System.Text.Json.Nodes;

namespace Mpai.Mmm.Client;

// A MEETING IN THE MMM, AS A USE CASE (MMM Phase 6). Three participants - bots - and the Secretary meet in a room of an M-Instance, through its MMM-API only:
// Anna convenes the meeting and decides who is in; Carla, not invited, is not let in; Bruno is invited and takes his seat; what they say, as Speech Objects, goes
// through the Secretary and the ASR Service into the minutes, which only the participants are given; Carla is invited, comes late and is given the minutes so
// far; Anna takes Bruno out, and he has neither the room nor the minutes after. Run by the test at full speed, and by the server (--meeting-demo), a step at a time,
// for the viewer to show. The bots have no voices: the ASR Service recognises, here, the words of a script.
public static class MeetingUseCase
{
    // What each of them says, in the order they speak.
    public static readonly string[] Script =
    [
        "Good morning. We have a quorum, so let us begin.",
        "Thank you, Anna. I propose that we approve the budget.",
        "Sorry I am late. I second the proposal.",
        "Bruno has left the meeting. The budget is approved."
    ];

    // The ASR Service's recognition for the bots: the next sentence of the script for each Speech Object it is given.
    public static Recognizer ScriptedRecognizer()
    {
        var next = 0;
        return _ => Task.FromResult(Script[Math.Min(next++, Script.Length - 1)]);
    }

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

    private static JsonObject PointOfView() => new()
    {
        ["Header"] = "OSD-OPV-V1.5", ["PointOfViewID"] = "POV", ["CartPosition"] = new JsonArray(0.0, 0.0, 1.6), ["Orientation"] = new JsonArray(0.0, 0.0, 0.0)
    };

    private static byte[] Tone(double seconds)
    {
        var pcm = new byte[(int)(seconds * Speech.Rate) * 2];
        for (var i = 0; i < pcm.Length / 2; i++) BitConverter.GetBytes((short)(6000 * Math.Sin(i * 0.05))).CopyTo(pcm, i * 2);
        return pcm;
    }

    // after: called after each step with its name and what it means - to look at the M-Instance then, or to wait before the next.
    public static async Task<Dictionary<string, string>> RunAsync(HttpClient http, MInstance m, MeetingServices services, Func<string, string, Task>? after = null)
    {
        var result = new Dictionary<string, string>();
        var step = 0;
        async Task Done(string what, string says, string note)
        {
            result[$"{++step:00} {what}"] = says;
            if (after is not null) await after($"{step:00} {what}", note);
        }
        async Task Settle() { await services.PumpAsync(); await services.PumpAsrAsync(); await services.PumpAsync(); }
        Task<MmmClient.Result> Tell(MmmClient who, string id, string text) => who.PerformAsync("MM-Send", Uc2.C(id, to: Meeting.Avs, with: Uc2.Message(id, text)));
        string Text(MmmClient who, string id) => Meeting.TextOf(who.ReadAsync(id).Result.Item) ?? "-";
        var asked = 0;
        var seatOfUser = new Dictionary<string, string>();
        // Asks for a seat with the Persona it brings; goes there if it was given one.
        async Task<string> Sit(MmmClient who, string persona)
        {
            var ask = $"Join-{who.ProcessID}-{++asked}";
            await Tell(who, ask, Meeting.JoinText(persona)); await Settle();
            var told = Text(who, $"Seat-{ask}");
            if (!told.StartsWith("seat=", StringComparison.Ordinal)) return told;
            var seat = Meeting.ParseSeat(told);
            var move = await who.PerformAsync("MM-Move", Uc2.C(persona, from: "MVS", to: seat["seat"],
                with: [MeetingServices.SeatedAt($"SA-{persona}-Seat", double.Parse(seat["yaw"], CultureInfo.InvariantCulture)), Meeting.SitPA(persona, seat["seat"]),
                       Uc2.Rights($"Seat-Move-{persona}", "May", "Acquired", Meeting.SitID(persona))]));
            if (move.Http == 200) seatOfUser[who.ProcessID] = seat["seat"];
            return move.Http == 200 ? $"seated at {seat["seat"]}" : $"move {move.Http}";
        }
        var spoken = 0;
        var lastSpeech = "";
        // What a User says is placed, as a Speech Object, at its seat: that is where it is heard from. The Secretary hears it as any participant does, by capturing the room.
        async Task<string> Speak(MmmClient who)
        {
            var id = lastSpeech = $"Speech-{who.ProcessID}-{++spoken}";
            var added = await who.PerformAsync("MM-Add", Uc2.C(id, at: seatOfUser[who.ProcessID], with: Speech.Make(id, Tone(2), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));
            await Settle();
            return $"{added.Http}; minutes: {services.TheMinutes.Count} lines";
        }
        async Task<MmmClient> Register(string name, string brings, params string[] personae)
        {
            var reg = await new MmmClient(http, $"{name}-human").PerformAsync("Register", new Complements { With = [Profile(name, personae)] });
            var user = new MmmClient(http, name, reg.Tokens[name]);
            await user.PerformAsync("MM-Add", Uc2.C(brings, at: "MVS", with: Uc2.SpatialAttitude($"SA-{name}", name == "Anna" ? -3 : name == "Bruno" ? 0 : 3, -3)));
            return user;
        }

        var anna = await Register("Anna", "Anna-Formal", "Anna-Persona", "Anna-Formal");
        var bruno = await Register("Bruno", "Bruno-Persona", "Bruno-Persona");
        var carla = await Register("Carla", "Carla-Persona", "Carla-Persona");
        await Done("Anna, Bruno and Carla register and come to the Square", "registered: 3 Users; Anna has two Personae and brings the formal one",
            "Anna, Bruno and Carla are Users of the M-Instance. Each puts its Persona at the Metaverse Square. Anna has two Personae, all her own, and brings the formal one.");

        await services.StartAsync(); await Settle(); await Settle();
        await Done("The Secretary takes a seat", $"Secretary-Persona at {m.Get(Meeting.SecretaryPersona)?.Location}",
            "The Secretary is a machine Process with a Persona of its own. It is always invited, asks the AVS Service for a seat like anyone, and sits.");

        await Tell(anna, "Convene-Anna", "convene"); await Settle();
        await Done("Anna convenes the meeting", Text(anna, "Convened-Anna"),
            "A human decides who is in the meeting. Anna convenes it, and from then on it is hers to say who is in.");

        var refused = await Sit(carla, "Carla-Persona");
        var barred = await carla.PerformAsync("MM-Move", Uc2.C("Carla-Persona", from: "MVS", to: Meeting.Seats[3].ID, with: Uc2.SpatialAttitude("SA-X")));
        await Done("Carla, not invited, asks for a seat", $"{refused}; move to a seat {barred.Http}",
            "There is no meeting code: the room is not open. Carla is told she is not invited, and the right to move to a seat is not hers.");

        await Tell(anna, "Invite-Bruno", "invite;user=Bruno"); await Settle();
        await Done("Anna invites Bruno", Text(bruno, "Invitation-Bruno").Split(';')[0],
            "The invitation is a Message of the AVS Service, sent to Bruno because the convener said so.");

        var a = await Sit(anna, "Anna-Formal");
        var b = await Sit(bruno, "Bruno-Persona");
        await Done("Anna and Bruno take their seats", $"Anna {a}; Bruno {b}",
            "The AVS Service gives each the right to move the Persona it brings to a free seat. The places of the meeting are the seats the AVS Service assigns.");

        await Done("Anna speaks", await Speak(anna),
            "What Anna says is cut by her client and placed once, at her seat, as a Speech Object with its own duration. The Secretary hears the room as any participant does, has the ASR Service recognise the speech, and writes the words in the minutes.");

        var heardNow = await bruno.PerformAsync("MM-Capture", Uc2.C(at: Meeting.Room, with: PointOfView()));
        var heardIds = (heardNow.Response.Complements?.With?[0]?["ItemValueOrRef"]?[0]?["Things"]?.AsArray() ?? []).OfType<JsonObject>()
                                .Where(t => (string?)t["Kind"] == "Speech").Select(t => $"{t["ID"]} at {t["At"]}").ToList();
        var brunoHears = (await bruno.ReadAsync(lastSpeech)).Http;
        var carlaHears = (await carla.ReadAsync(lastSpeech)).Http;
        await Done("Bruno hears Anna; Carla, outside, does not", $"Bruno\'s capture shows {string.Join(", ", heardIds)}; he reads it {brunoHears}; Carla reads it {carlaHears}",
            "The room's participants see the Speech Object when they capture the room and read its data, to play it from the seat it was said at. Carla is not in the room.");
        await Done("Bruno speaks", await Speak(bruno), "The same for Bruno.");
        var carlaMinutes = (await carla.ReadAsync($"{Meeting.Minutes}-for-Anna")).Http;
        await Done("Carla cannot read the minutes", $"read {carlaMinutes}", "The minutes belong to the room. Only the participants are given a copy; Carla is not one.");

        await Tell(anna, "Invite-Carla", "invite;user=Carla"); await Settle();
        var c = await Sit(carla, "Carla-Persona");
        await Done("Carla is invited and comes", c, "Anna invites Carla. She asks again and is seated.");
        await Done("Carla is given the minutes so far", Text(carla, $"{Meeting.Minutes}-for-Carla").Replace("\n", " | "),
            "A participant that comes late is given the minutes so far.");
        await Done("Carla speaks", await Speak(carla), "Carla speaks.");

        await Tell(anna, "Revoke-Bruno", "revoke;user=Bruno"); await Settle();
        var inRoom = await bruno.PerformAsync("MM-Capture", Uc2.C(at: Meeting.Room, with: PointOfView()));
        await Done("Anna takes Bruno out of the meeting", $"Bruno captures the room: {inRoom.Http}",
            "Anna says Bruno is out. The AVS Service withdraws his right to a seat. He can no longer see the room.");
        await Done("Anna speaks again", await Speak(anna), "Anna speaks again. Bruno is given no more of the minutes.");
        await Done("Bruno no longer hears", $"he reads it {(await bruno.ReadAsync(lastSpeech)).Http}", "He is out of the meeting: what is said in the room is not his to hear.");
        var final = Text(anna, $"{Meeting.Minutes}-for-Anna");
        await Done("The minutes", final.Replace("\n", " | "), "The minutes, as the participants have them.");
        await Done("What Bruno has", Text(bruno, $"{Meeting.Minutes}-for-Bruno").Replace("\n", " | "),
            "Bruno keeps what he had before he was taken out, and no more.");

        var capture = await anna.PerformAsync("MM-Capture", Uc2.C(at: Meeting.Room, with: PointOfView()));
        var seen = capture.Response.Complements!.With![0]!["ItemValueOrRef"]![0]!["Things"]!.AsArray().OfType<JsonObject>()
                          .Where(t => (string?)t["Kind"] == "Persona").Select(t => (string)t["ID"]!).Order().ToList();
        result["where the Personae are"] = string.Join(", ", new[] { "Anna-Formal", "Anna-Persona", "Bruno-Persona", "Carla-Persona", Meeting.SecretaryPersona }.Select(p => $"{p}@{m.Get(p)?.Location ?? "-"}"));
        result["what Anna sees in the room"] = string.Join(", ", seen);
        result["Activity Data, Process Actions recorded"] = m.ActivityData.Count.ToString();
        return result;
    }
}
