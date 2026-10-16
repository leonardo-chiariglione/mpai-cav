using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Mpai.Mmm.Client;

// A MEETING, AS SERVICES OF THE M-INSTANCE (MMM Phase 3). ABV's meeting - a room, its seats, the Secretary who takes the minutes - rebuilt in MMM terms:
//  - the room is a Location (MeetingRoom, on MeetingParcel) and its seats are Locations inside it: nothing about a meeting in the room itself;
//  - the AVS Service is a Process. The places of a meeting are those it assigns, where the participating Users place their Personae: a User asks (MM-Send
//    "join"), the AVS Service gives it the right to move its Persona to a free seat (Rights Change - what the meeting code of ABV was) and tells it which
//    (MM-Send); the User moves its Persona there (MM-Move);
//  - the Secretary is a machine Process with a Persona of its own, that takes a seat like any participant and takes the minutes of what it is told
//    (here as text Messages: Phase 4 gives it Speech Objects and an ASR service). The minutes belong to the room: the Secretary hands them to the AVS Service.
// The services act on the M-Instance only through its MMM-API, as any Process does; they are not part of the M-Instance.
public static class Meeting
{
    public const string Parcel = "MeetingParcel", Room = "MeetingRoom", Avs = "AVS", Secretary = "Secretary", Asr = "ASR", SecretaryPersona = "Secretary-Persona", Minutes = "Minutes-" + Room;
    public const double ParcelX = 12, ParcelY = 0;   // where the parcel is, in metres (east, north)

    // The seats around the table: place within the Room (metres: x east, y north) and the yaw of a Persona looking at the table's centre (0 looks north).
    public static readonly (string ID, double X, double Y, double Yaw)[] Seats =
    [
        ($"{Room}-S1", 0, 2.5, 180), ($"{Room}-S2", 0, -2.5, 0), ($"{Room}-S3", -2.5, 0, 270), ($"{Room}-S4", 2.5, 0, 90)
    ];

    // What the M-Instance starts with: the Services, the Room and its Seats. The Parcel, the Room and the Seats are the AVS Service's: not public, so that
    // entering needs a Right, which the AVS Service gives.
    public static void Setup(MInstance m)
    {
        m.AddService(Avs);
        m.AddService(Asr);                                  // the speech recognition: a service of its own, that any Process may ask
        m.AddService(Secretary, SecretaryPersona, "Persona2.glb");
        m.AddLocation(Parcel, owner: Avs, x: ParcelX, y: ParcelY, size: 12);
        m.AddLocation(Room, owner: Avs, x: 0, y: 0, size: 8, at: Parcel);
        foreach (var s in Seats) m.AddLocation(s.ID, owner: Avs, x: s.X, y: s.Y, size: 0.8, perceptible: false, at: Room);
    }

    // Where a seat is in the M-Environment, in metres.
    public static (double X, double Y) WorldOf((string ID, double X, double Y, double Yaw) seat) => (ParcelX + seat.X, ParcelY + seat.Y);

    // The PA of the MM-Move of a Persona to a seat, and the Rights over it: the same Process Action in both, as in Use Case 2. A Persona is its User's own:
    // the User brings, for this meeting, the one it chooses of its Personae; the right is for that Persona and that seat.
    public static string SitID(string persona) => $"PA-Sit-{persona}";
    public static JsonObject SitPA(string persona, string seat) =>
        Uc2.PA("MMM-2MP-V2.2", "MMMove", SitID(persona), ("RQ1", "Nil", "ItemID", persona), ("RQ3", "To", "PointOfView2", Uc2.PointOfView(seat)));

    // The right to place what it says - a Speech Object - at its seat: the seat is where it is heard from.
    public static string SpeakID(string user) => $"PA-Speak-{user}";
    public static JsonObject SpeakPA(string user, string seat) =>
        Uc2.PA("MMM-2DP-V2.2", "MMAdd", SpeakID(user), ("RQ2", "At", "MLocationID", seat));

    // What a User asks: to join, with the Persona it brings (one of its own; if it names none, the one of its name).
    public static string JoinText(string persona) => $"join;persona={persona}";

    // What the AVS Service tells a User: its seat, and the Persona the right is for.
    public static string SeatText((string ID, double X, double Y, double Yaw) seat, string persona)
    {
        var w = WorldOf(seat);
        return string.Create(CultureInfo.InvariantCulture, $"seat={seat.ID};x={w.X};y={w.Y};yaw={seat.Yaw};room={Room};persona={persona}");
    }

    public static Dictionary<string, string> ParseSeat(string text) =>
        text.Split(';').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);

    // The text of a Message Item as the M-Instance returns it.
    public static string? TextOf(JsonObject? item) => item?["ItemValueOrRef"]?[0]?["MessageData"]?[0]?["MessageData"]?.GetValue<string>();
}

// The Processes of the meeting, run against the MMM-API at http. Pump them from time to time (the server does, a few times a second).
// recognize: what the ASR Service recognises words with (Whisper); none, it finds no words.
public sealed class MeetingServices(HttpClient http, MInstance m, Recognizer? recognize = null)
{
    private readonly MmmClient asr = new(http, Meeting.Asr, m.TokenOf(Meeting.Asr));
    private readonly Dictionary<string, (string Speaker, long Since, long SpeechMs)> asked = new();   // forwarded Speech Object -> who spoke, when the Secretary got it, how long it lasts
    private readonly List<(string Speaker, long SpeechMs, long TotalMs)> latencies = [];
    private readonly MmmClient avs = new(http, Meeting.Avs, m.TokenOf(Meeting.Avs));
    private readonly MmmClient secretary = new(http, Meeting.Secretary, m.TokenOf(Meeting.Secretary));
    private readonly Dictionary<string, string> seatOf = new();               // User -> seat
    private readonly HashSet<string> handled = new();
    private readonly List<string> minutes = [];
    private readonly SemaphoreSlim one = new(1, 1);

    // Who may be in the meeting is a human decision, the convener's: the first User that convenes it. The AVS Service seats only the Users it has been told to
    // invite (the Secretary, a machine of the room, is always invited). An invitation is a Message from the AVS Service; it replaces the meeting code of ABV.
    private string? convener;
    private readonly HashSet<string> invited = [Meeting.Secretary];
    private readonly Dictionary<string, string> personaOf = new();            // User -> the Persona it brought
    private readonly HashSet<string> participants = new();                   // the Secretary's: the Users that have a seat and receive the minutes
    private int sequence;

    public IReadOnlyList<string> TheMinutes => minutes;

    // For each Speech Object that reached the minutes: who, how long it lasts, and how long from the Secretary getting it to its words being in the minutes.
    public IReadOnlyList<(string Speaker, long SpeechMs, long TotalMs)> Latencies { get { lock (latencies) return latencies.ToList(); } }

    // The Secretary comes to the Metaverse Square, and asks to join the meeting like any participant.
    private bool started;
    public async Task StartAsync()
    {
        if (started) return;          // once: a second MM-Add would put the Secretary's Persona back at the Square
        started = true;
        await secretary.PerformAsync("MM-Add", Uc2.C(Meeting.SecretaryPersona, at: "MVS", with: Uc2.SpatialAttitude("SA-Secretary", 0, 3)));
        await Say(secretary, "Join-Secretary", Meeting.Avs, Meeting.JoinText(Meeting.SecretaryPersona));
    }

    private static Task<MmmClient.Result> Say(MmmClient from, string id, string to, string text) =>
        from.PerformAsync("MM-Send", Uc2.C(id, to: to, with: Uc2.Message(id, text)));

    public async Task PumpAsync()
    {
        await one.WaitAsync();
        try
        {
            foreach (var (id, from) in await avs.InboxAsync())
            {
                if (!handled.Add($"{Meeting.Avs}|{id}")) continue;
                var text = Meeting.TextOf((await avs.ReadAsync(id)).Item);
                if (text is null) continue;
                if (text == "convene")
                {
                    if (convener is null) { convener = from; invited.Add(from); await Say(avs, $"Convened-{from}", from, "convened"); }
                    else await Say(avs, $"Convened-{from}", from, $"already convened by {convener}");
                }
                else if (text.StartsWith("invite;user=", StringComparison.Ordinal) || text.StartsWith("revoke;user=", StringComparison.Ordinal))
                {
                    var who = text[(text.IndexOf('=') + 1)..];
                    if (from != convener) { await Say(avs, $"Refused-{id}", from, "only the convener decides who is in the meeting"); continue; }
                    if (text.StartsWith("invite", StringComparison.Ordinal)) await Invite(who); else await Revoke(who);
                }
                else if (text.StartsWith("join", StringComparison.Ordinal))
                {
                    if (!invited.Contains(from)) { await Say(avs, $"Seat-{id}", from, "not invited"); continue; }   // the answer is to this request: a User asks again after it is invited
                    var persona = Meeting.ParseSeat(text.Replace("join;", "")).GetValueOrDefault("persona") ?? $"{from}-Persona";
                    await Seat(from, persona, $"Seat-{id}");
                    personaOf[from] = persona;
                    if (from != Meeting.Secretary) await Say(avs, $"Participant-{from}-{++sequence}", Meeting.Secretary, $"participant;user={from}");
                }
            }
            foreach (var (id, from) in await secretary.InboxAsync())
            {
                if (!handled.Add($"{Meeting.Secretary}|{id}")) continue;
                var item = (await secretary.ReadAsync(id)).Item;
                if (Speech.Is(item)) { await Forward(id, from, item!); continue; }   // sent to the Secretary directly
                var text = Meeting.TextOf(item);
                if (text is null) continue;
                if (from == Meeting.Avs && text.StartsWith("participant;user=", StringComparison.Ordinal))
                {
                    var user = text[(text.IndexOf('=') + 1)..];
                    participants.Add(user);
                    if (minutes.Count > 0) await Publish(user);          // a participant that comes late is given the minutes so far
                }
                else if (from == Meeting.Avs && text.StartsWith("left;user=", StringComparison.Ordinal)) participants.Remove(text[(text.IndexOf('=') + 1)..]);
                else if (from == Meeting.Asr && id.StartsWith("Words-", StringComparison.Ordinal) && asked.Remove(id["Words-".Length..], out var who))
                {
                    if (text != NoWords) { minutes.Add($"{who.Speaker}: {text}"); await Publish(); }
                    lock (latencies) latencies.Add((who.Speaker, who.SpeechMs, (long)Stopwatch.GetElapsedTime(who.Since).TotalMilliseconds));
                }
                else if (text.StartsWith("seat=", StringComparison.Ordinal)) await Sit(Meeting.ParseSeat(text));
                else if (text.StartsWith("say:", StringComparison.Ordinal))
                {
                    minutes.Add($"{from}: {text[4..].Trim()}");
                    await Publish();
                }
            }
            await Hear();
        }
        finally { one.Release(); }
    }

    // What a participant said: the Secretary has it recognised by the ASR Service, which is not its own.
    private async Task Forward(string id, string speaker, JsonObject item)
    {
        var forward = $"Forward-{id}";
        asked[forward] = (speaker, Stopwatch.GetTimestamp(), Speech.DurationMs(item));
        await secretary.PerformAsync("MM-Send", Uc2.C(forward, to: Meeting.Asr, with: (JsonObject)item["ItemValueOrRef"]![0]!.DeepClone()));
    }

    // The Secretary hears the room like any participant: it captures it, and the Speech Objects placed at the seats are what was said. Not yet seated, it hears nothing.
    private async Task Hear()
    {
        var seen = await secretary.PerformAsync("MM-Capture", Uc2.C(at: Meeting.Room, with: new JsonObject
        {
            ["Header"] = "OSD-OPV-V1.5", ["PointOfViewID"] = "POV-Secretary", ["CartPosition"] = new JsonArray(0.0, 0.0, 1.6), ["Orientation"] = new JsonArray(0.0, 0.0, 0.0)
        }));
        if (seen.Http != 200) return;
        foreach (var thing in (seen.Response.Complements?.With?[0]?["ItemValueOrRef"]?[0]?["Things"]?.AsArray() ?? []).OfType<JsonObject>().Where(t => (string?)t["Kind"] == "Speech" && (bool?)t["Streaming"] != true))   // a stream still going on is taken when it is over
        {
            var id = (string)thing["ID"]!;
            if (!handled.Add($"{Meeting.Secretary}|heard|{id}")) continue;
            var item = (await secretary.ReadAsync(id)).Item;
            if (Speech.Is(item)) await Forward(id, (string?)thing["Owner"] ?? "?", item!);
        }
    }

    private const string NoWords = "[no words]";
    private readonly SemaphoreSlim asrOne = new(1, 1);

    // The ASR Service recognises the words of the Speech Objects it is sent, and answers the Process that sent them. Slow (seconds), so it has its own pump: the
    // seats and the minutes do not wait for it.
    public async Task PumpAsrAsync()
    {
        await asrOne.WaitAsync();
        try
        {
            foreach (var (id, from) in await asr.InboxAsync())
            {
                if (!handled.Add($"{Meeting.Asr}|{id}")) continue;
                var item = (await asr.ReadAsync(id)).Item;
                if (!Speech.Is(item)) continue;
                var words = recognize is null ? "" : await recognize(Speech.PcmOf(item));
                await Say(asr, $"Words-{id}", from, string.IsNullOrWhiteSpace(words) ? NoWords : words.Trim());
            }
        }
        finally { asrOne.Release(); }
    }

    // The minutes belong to the room: the Secretary hands them to the AVS Service, and gives a copy to each participant - an Item of its own for each, so that a
    // participant who is taken out of the meeting keeps what it had and is given no more.
    private async Task Publish(string? only = null)
    {
        var text = string.Join("\n", minutes);
        if (only is null) await Say(secretary, Meeting.Minutes, Meeting.Avs, text);
        foreach (var u in only is null ? participants.ToList() : [only]) await Say(secretary, $"{Meeting.Minutes}-for-{u}", u, text);
    }

    private async Task Invite(string user)
    {
        invited.Add(user);
        await Say(avs, $"Invitation-{user}", user, $"invitation;room={Meeting.Room};from={convener}");
    }

    // The convener takes a User out of the meeting: the AVS Service withdraws its right to a seat (the Rights of Use Case 2: May Not prevails) and tells the Secretary.
    private async Task Revoke(string user)
    {
        invited.Remove(user);
        if (personaOf.TryGetValue(user, out var persona) && seatOf.TryGetValue(user, out var seatId))
            await avs.PerformAsync("Rights Change", Uc2.C(user, with: [Meeting.SitPA(persona, seatId), Uc2.Rights($"Seat-Revoke-{persona}", "May Not", "Granted", Meeting.SitID(persona)),
                                                                       Meeting.SpeakPA(user, seatId), Uc2.Rights($"Speak-Revoke-{user}", "May Not", "Granted", Meeting.SpeakID(user))]));
        await Say(avs, $"Left-{user}-{++sequence}", Meeting.Secretary, $"left;user={user}");
    }

    // The AVS Service gives a User the right to move its Persona to a free seat, and tells it which. A User that has a seat is told it again.
    private async Task Seat(string user, string persona, string replyId)
    {
        if (!seatOf.TryGetValue(user, out var seatId))
        {
            var free = Meeting.Seats.FirstOrDefault(s => !seatOf.ContainsValue(s.ID));
            if (free.ID is null) { await Say(avs, replyId, user, "full"); return; }
            seatOf[user] = seatId = free.ID;
        }
        var pa = Meeting.SitPA(persona, seatId);   // the Persona the User brings: the right is for it (a User moves only Personae it owns, whatever it is told)
        // ...and the right to place what it says at that seat.
        await avs.PerformAsync("Rights Change", Uc2.C(user, with: [pa, Uc2.Rights($"Seat-Right-{persona}", "May", "Granted", Meeting.SitID(persona)),
                                                                   Meeting.SpeakPA(user, seatId), Uc2.Rights($"Speak-Right-{user}", "May", "Granted", Meeting.SpeakID(user))]));
        await Say(avs, replyId, user, Meeting.SeatText(Meeting.Seats.First(s => s.ID == seatId), persona));
    }

    // The Secretary moves its Persona to the seat it was given.
    private async Task Sit(Dictionary<string, string> seat)
    {
        var yaw = double.Parse(seat["yaw"], CultureInfo.InvariantCulture);
        var pa = Meeting.SitPA(Meeting.SecretaryPersona, seat["seat"]);
        await secretary.PerformAsync("MM-Move", Uc2.C($"{Meeting.Secretary}-Persona", from: "MVS", to: seat["seat"],
            with: [SeatedAt($"SA-Secretary-Seat", yaw), pa, Uc2.Rights($"Seat-Move-{Meeting.SecretaryPersona}", "May", "Acquired", Meeting.SitID(Meeting.SecretaryPersona))]));
    }

    // The Spatial Attitude of a Persona at its seat: at the seat's centre, looking at the table's.
    public static JsonObject SeatedAt(string id, double yaw)
    {
        var sa = Uc2.SpatialAttitude(id);
        sa["Orientation"]!["Orientation"] = new JsonArray(yaw, 0.0, 0.0);
        return sa;
    }
}
