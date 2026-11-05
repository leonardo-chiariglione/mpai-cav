using System.Net.WebSockets;
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
    public static double ParcelX { get; set; } = 12;
    public static double ParcelY { get; set; } = 0;   // where the parcel is, in metres (east, north); the world sets them
    // In the world of the MMM (World.Setup) the doors of the building are Rights: the AVS Service gives them to the Users it invites. Doors: the Locations behind them.
    public static bool DoorsNeedRights { get; set; }
    public static string[] Doors { get; set; } = [];
    // Every place of the world that needs a Right (the wings and the rooms): an MPAI Officer's Account is given the Rights to all of them.
    public static string[] AllPlaces { get; set; } = [];
    // The lady at the counter of the atrium: a machine Process of the M-Instance with a Persona of its own, which stands where the world says and answers visitors (the Multimodal Conversation
    // Avatar). Null: there is none.
    public static (string Persona, string Model, string At, double X, double Y, double Yaw)? Reception { get; set; }
    public const string ReceptionService = "Reception";

    // The seats around the table: place within the Room (metres: x east, y north) and the yaw of a Persona looking at the table's centre (0 looks north).
    private static readonly (string ID, double X, double Y, double Yaw)[] DefaultSeats =
    [
        ($"{Room}-S1", 0, 2.5, 180), ($"{Room}-S2", 0, -2.5, 0), ($"{Room}-S3", -2.5, 0, 270), ($"{Room}-S4", 2.5, 0, 90)
    ];
    public static (string ID, double X, double Y, double Yaw)[] Seats { get; set; } = DefaultSeats;

    // THE ROOM HAS THE SEATS THAT WERE PLANNED, no more: the people invited, and the Secretary. null: all the seats the world has (the tests, the demonstrations).
    public static int? PlannedSeats { get; set; }

    // The seats are given in the order the world lists them (the Secretary, who takes its seat first, is at the side; the first two people sit across the table and see each other). Nobody is in front of a
    // shared screen in the view of whoever looks at it: the viewer draws aside the Personae between the viewer and the wall it faces (MMM/Viewer/index.html, compactSeat).
    public static IEnumerable<(string ID, double X, double Y, double Yaw)> InOrderFor(string user, IEnumerable<(string ID, double X, double Y, double Yaw)> seats) => seats;

    // The seats kept when only n are planned: the first n of the world's, in its order.
    public static (string ID, double X, double Y, double Yaw)[] Plan((string ID, double X, double Y, double Yaw)[] all, int n) => n >= all.Length ? all : all.Take(Math.Max(n, 1)).ToArray();

    // What the world (World.Setup) changes - where the building is, the seats, the doors - is put back as it was: the meeting of the demonstrations and of the tests is the one above.
    public static void Reset() { ParcelX = 12; ParcelY = 0; Seats = DefaultSeats; DoorsNeedRights = false; Doors = []; AllPlaces = []; Reception = null; }

    // What the M-Instance starts with: the Services, the Room and its Seats. The Parcel, the Room and the Seats are the AVS Service's: not public, so that
    // entering needs a Right, which the AVS Service gives.
    public static void Setup(MInstance m)
    {
        m.AddService(Avs);
        m.AddService(Asr);                                  // the speech recognition: a service of its own, that any Process may ask
        m.AddService(Secretary, SecretaryPersona, "catalogue/Female_Adult_05.glb");
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

    // The right to share a screen in the room - to place a Visual Object that is a stream there: the convener's only, for now.
    public static string ShareID(string user) => $"PA-Share-{user}";
    public static JsonObject SharePA(string user) => Uc2.PA("MMM-2DP-V2.2", "MMAdd", ShareID(user), ("RQ2", "At", "MLocationID", Room));

    // What a User asks: to join, with the Persona it brings (one of its own; if it names none, the one of its name).
    public static string JoinText(string persona) => $"join;persona={persona}";

    // What the AVS Service tells a User: its seat, and the Persona the right is for.
    public static string SeatText((string ID, double X, double Y, double Yaw) seat, string persona, bool convener = false)
    {
        var w = WorldOf(seat);
        return string.Create(CultureInfo.InvariantCulture, $"seat={seat.ID};x={w.X};y={w.Y};yaw={seat.Yaw};room={Room};persona={persona}") + (convener ? ";convener=1" : "");
    }

    // THE RIGHTS OF A MEETING last as long as the meeting does: they carry its period (RightsTime, a Simple Time from its start to its end) and the AVS Service withdraws them when it ends.
    public static JsonObject ForTheMeeting(JsonObject rights, DateTimeOffset start, DateTimeOffset? end)
    {
        if (end is null) return rights;
        rights["RightsTime"] = new JsonObject
        {
            ["Header"] = "OSD-STM-V1.5", ["SimpleTimeID"] = rights["RightsID"] + "-T",
            ["SimpleTimeData"] = new JsonArray(new JsonObject { ["FlagsByte"] = 3, ["StartTime"] = start.ToUnixTimeMilliseconds(), ["EndTime"] = end.Value.ToUnixTimeMilliseconds(), ["AccuracyMode"] = "single", ["AccuracyPlusMinus"] = 1 })
        };
        return rights;
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
    private readonly Dictionary<string, (string Speaker, long Since, long SpeechMs, long ActualMs)> asked = new();   // forwarded Speech Object -> who spoke, when the Secretary got it, how long it lasts, how much of it there is
    private readonly List<(string Speaker, long SpeechMs, long TotalMs)> latencies = [];
    private readonly MmmClient avs = new(http, Meeting.Avs, m.TokenOf(Meeting.Avs));
    private readonly MmmClient secretary = new(http, Meeting.Secretary, m.TokenOf(Meeting.Secretary));
    private readonly MmmClient? reception = Meeting.Reception is null ? null : new(http, Meeting.ReceptionService, m.TokenOf(Meeting.ReceptionService));
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

    // THE HANDS RAISED. A participant raises a hand to ask for the floor: the AVS Service keeps who has one up and tells every participant (Hands-for-<User>), and the Secretary
    // notes it in the minutes. A hand comes down when its owner lowers it, when the convener lowers it, or by itself once its owner has spoken for at least HandLowersAfterMs (the
    // Secretary tells the AVS Service how long each recognised utterance was).
    public const long HandLowersAfterMs = 5000;
    private readonly List<string> hands = [];
    private readonly HashSet<string> sharers = [];      // the participants the convener has let show a screen
    private readonly Dictionary<string, long> handSpoken = new();
    public IReadOnlyList<string> HandsUp { get { lock (hands) return hands.ToList(); } }

    // THE SUMMARY IS FOR THE PARTICIPANTS: when one of them asks (summary;lang=el;mode=spoken), the Secretary makes it once and every participant is sent it, in the language each chose (pref;lang=fr tells the Secretary
    // the language of a participant; English if it has not) - written for those that did not ask, spoken or written as the one that asked wanted (an Item of its own for each: Summary-Latest-<User>).
    // The Secretary makes it, in English, from the minutes - in pieces if they are long, then the pieces - and translates it into the language the User chose; if the User wants it
    // spoken, it has it spoken in that language. What condenses, translates and speaks is not the Secretary's: the Processes it asks are given here, and with none there is no summary.
    public Summariser? Summarise { get; set; }
    public Translator? Translate { get; set; }
    public Synthesiser? Synthesise { get; set; }
    public const int PieceChars = 6000;
    private readonly Queue<(string User, string ID, string Language, string Mode)> summaryAsked = new();
    private readonly SemaphoreSlim summaryOne = new(1, 1);
    private readonly Dictionary<string, string> summaryLang = new();            // User -> the language of the summaries it is sent

    // THE DIALOGUE WITH THE SECRETARY. The Secretary is a User: a participant that wants it, and speaks to it, sends its speech to the Secretary (MM-Send of its Speech Object, a stream) instead of placing it
    // in the room, so that nobody else hears it. The Secretary has what it hears recognised (a longer pause ends a piece than in the room: a request has pauses in it), has the Voice Command Interface
    // (here: Interpret) turn the words into a request, carries it out - for the one that asked, and nobody else - and answers in a few spoken words (Message Secretary-<User>-<n>: what it says,
    // whether it is also written, and what the client of the User is to do - show or hide the summary, switch its microphone, go back to the meeting).
    public delegate Task<(JsonObject? Command, string? Hint)> Interpreter(string user, string words);
    public Interpreter? Interpret { get; set; }
    private readonly Queue<(string User, string Words)> dialogueAsked = new();
    private readonly HashSet<string> dialogueIds = new();                      // the pieces sent to the ASR Service that are a dialogue with the Secretary, not the meeting
    private readonly SemaphoreSlim dialogueOne = new(1, 1);
    private sealed class Preference { public string Language = "en", Form = "written"; public bool Voice; public int Sequence; }      // text is the default; the voice is an option
    private readonly Dictionary<string, Preference> preferences = new();
    private readonly HashSet<string> closing = new();                          // who was asked to be sure that the meeting is to be closed
    public const int DialoguePauseMs = 1200;

    public IReadOnlyList<string> TheMinutes => minutes;

    // When the meeting ends (null: when the convener ends it): the rights it gave last until then. Ended: it is over, and nobody is given a seat or a door any more.
    public DateTimeOffset? EndsAt { get; set; }
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;     // the time the meeting goes by (a test moves it)
    private DateTimeOffset? began;
    private bool ended;
    public bool Ended => ended;
    private JsonObject Meant(JsonObject rights) => Meeting.ForTheMeeting(rights, began ??= Clock(), EndsAt);

    // THE END OF THE MEETING: the AVS Service withdraws the rights of every participant - the doors, the seat, the speech - and tells the Secretary. What the participants had of the
    // meeting (the minutes) they keep; they get no more, and cannot come in again.
    public async Task EndAsync()
    {
        if (ended) return;
        ended = true;
        m.StopScreens();                                       // a screen shared in the room is over with the meeting
        foreach (var user in seatOf.Keys.Concat(invited).Where(u => u != Meeting.Secretary).Distinct().ToList()) await Revoke(user);
        await Say(avs, $"Ended-{++sequence}", Meeting.Secretary, "ended");
    }

    // The Users invited before anybody has registered (--invite A,B,C): the first is the convener. Each is given its seat, and the right to the doors, when it asks to join.
    public void Preinvite(IEnumerable<string> users)
    {
        foreach (var u in users) { convener ??= u; invited.Add(u); }
    }

    // For each Speech Object that reached the minutes: who, how long it lasts, and how long from the Secretary getting it to its words being in the minutes.
    public IReadOnlyList<(string Speaker, long SpeechMs, long TotalMs)> Latencies { get { lock (latencies) return latencies.ToList(); } }

    // The Secretary comes to the Metaverse Square, and asks to join the meeting like any participant.
    private bool started;
    public async Task StartAsync()
    {
        if (started) return;          // once: a second MM-Add would put the Secretary's Persona back at the Square
        started = true;
        await secretary.PerformAsync("MM-Add", Uc2.C(Meeting.SecretaryPersona, at: "MVS", with: Uc2.SpatialAttitude("SA-Secretary", 0, 3)));
        if (reception is not null && Meeting.Reception is { } r)             // the lady at the counter, in the atrium, facing the door
            await reception.PerformAsync("MM-Add", Uc2.C(r.Persona, at: r.At, with: SeatedAt("SA-Reception", r.Yaw, r.X, r.Y)));
        await Say(secretary, "Join-Secretary", Meeting.Avs, Meeting.JoinText(Meeting.SecretaryPersona));
    }

    private static Task<MmmClient.Result> Say(MmmClient from, string id, string to, string text) =>
        from.PerformAsync("MM-Send", Uc2.C(id, to: to, with: Uc2.Message(id, text)));

    public async Task PumpAsync()
    {
        await one.WaitAsync();
        try
        {
            if (EndsAt is { } until && !ended && Clock() >= until) await EndAsync();
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
                else if (text == "end")
                {
                    if (from == convener) await EndAsync(); else await Say(avs, $"Refused-{id}", from, "only the convener ends the meeting");
                }
                else if (text.StartsWith("hand;", StringComparison.Ordinal))
                {
                    var part = text.Split(';');
                    var who = part.Length > 2 && part[2].StartsWith("user=", StringComparison.Ordinal) && from == convener ? part[2][5..] : from;     // only the convener lowers another's hand
                    if (seatOf.ContainsKey(who) && who != Meeting.Secretary) await SetHand(who, part.Length > 1 && part[1] == "up", who == from ? "" : " (by the convener)");
                }
                else if (text.StartsWith("share;", StringComparison.Ordinal))
                {
                    // Showing a screen is the convener's to give, one participant at a time (and to take back).
                    var part = text.Split(';'); var who = part.Length > 2 && part[2].StartsWith("user=", StringComparison.Ordinal) ? part[2][5..] : "";
                    if (from != convener) { await Say(avs, $"Refused-{id}", from, "only the convener lets a participant share a screen"); continue; }
                    if (seatOf.ContainsKey(who) && who != Meeting.Secretary)
                    {
                        var grant = part.Length > 1 && part[1] == "grant";
                        lock (sharers) { if (grant) sharers.Add(who); else sharers.Remove(who); }
                        await Say(avs, $"Sharing-for-{who}", who, grant ? "granted" : "revoked");
                    }
                }
                else if (from == Meeting.Secretary && text.StartsWith("spoke;", StringComparison.Ordinal))
                {
                    var part = Meeting.ParseSeat(text["spoke;".Length..]);
                    if (part.TryGetValue("user", out var talker) && part.TryGetValue("ms", out var ms) && long.TryParse(ms, out var spent))
                    {
                        bool up; lock (hands) up = hands.Contains(talker);
                        if (up && (handSpoken[talker] = handSpoken.GetValueOrDefault(talker) + spent) >= HandLowersAfterMs) await SetHand(talker, false, " (after speaking)");
                    }
                }
                else if (text.StartsWith("join", StringComparison.Ordinal))
                {
                    var level = m.AccountLevelOf(from);
                    var officer = level == "Officer";
                    if (officer) await Officer(from);                  // an MPAI Officer's Account holds the Rights to every place, whatever the meeting
                    if (ended) { await Say(avs, $"Seat-{id}", from, officer ? "officer" : "the meeting is over"); continue; }
                    if (!invited.Contains(from)) { await Say(avs, $"Seat-{id}", from, officer ? "officer" : "not invited"); continue; }   // the answer is to this request: a User asks again after it is invited
                    if (level == "Visitor") { await Say(avs, $"Seat-{id}", from, "visitor account"); continue; }                         // the meeting rooms are for a Participant (or an Officer) Account
                    var persona = Meeting.ParseSeat(text.Replace("join;", "")).GetValueOrDefault("persona") ?? $"{from}-Persona";
                    await Seat(from, persona, $"Seat-{id}");
                    personaOf[from] = persona;
                    bool mayShare; lock (sharers) mayShare = sharers.Contains(from);
                    if (mayShare) await Say(avs, $"Sharing-for-{from}", from, "granted");
                    if (from != Meeting.Secretary && HandsUp.Count > 0) await ShowHands(from);                 // a newcomer is told who has a hand up - when somebody has one; with none there is nothing to tell
                    if (from != Meeting.Secretary) await Say(avs, $"Participant-{from}-{++sequence}", Meeting.Secretary, $"participant;user={from}");
                }
            }
            foreach (var (id, from) in await secretary.InboxAsync())
            {
                if (!handled.Add($"{Meeting.Secretary}|{id}")) continue;
                var item = (await secretary.ReadAsync(id)).Item;
                if (Speech.Is(item))                                                  // sent to the Secretary directly: a stream is a dialogue with it, listened to as it goes on
                {
                    if (Speech.DurationMs(item) == 0 && Speech.PcmOf(item).Length == 0) { if (handled.Add($"{Meeting.Secretary}|tap|{id}")) _ = TapAsync(id, from, true); }
                    else await Forward(id, from, item!);
                    continue;
                }
                var text = Meeting.TextOf(item);
                if (text is null) continue;
                if (from == Meeting.Avs && text.StartsWith("participant;user=", StringComparison.Ordinal))
                {
                    var user = text[(text.IndexOf('=') + 1)..];
                    participants.Add(user);
                    if (minutes.Count > 0) await Publish(user);          // a participant that comes late is given the minutes so far
                }
                else if (from == Meeting.Avs && text.StartsWith("left;user=", StringComparison.Ordinal)) participants.Remove(text[(text.IndexOf('=') + 1)..]);
                else if (from == Meeting.Asr && id.StartsWith("Words-", StringComparison.Ordinal) && dialogueIds.Remove(id["Words-".Length..]) && asked.Remove(id["Words-".Length..], out var talker))
                {
                    if (text != NoWords) lock (dialogueAsked) dialogueAsked.Enqueue((talker.Speaker, text));            // what was said to the Secretary is not the meeting: not in the minutes
                }
                else if (from == Meeting.Asr && id.StartsWith("Words-", StringComparison.Ordinal) && asked.Remove(id["Words-".Length..], out var who))
                {
                    if (text != NoWords)
                    {
                        minutes.Add($"{who.Speaker}: {text}"); await Publish();
                        if (HandsUp.Contains(who.Speaker)) await Say(secretary, $"Spoke-{id}", Meeting.Avs, $"spoke;user={who.Speaker};ms={who.ActualMs}");      // only a hand up has to be told how long its owner spoke
                    }
                    lock (latencies) latencies.Add((who.Speaker, who.SpeechMs, (long)Stopwatch.GetElapsedTime(who.Since).TotalMilliseconds));
                }
                else if (text.StartsWith("pref;", StringComparison.Ordinal))
                    lock (summaryLang) summaryLang[from] = (Meeting.ParseSeat(text["pref;".Length..]).GetValueOrDefault("lang") ?? "en").ToLowerInvariant();
                else if (text.StartsWith("summary;", StringComparison.Ordinal))
                {
                    var asking = Meeting.ParseSeat(text["summary;".Length..]);
                    lock (summaryAsked) summaryAsked.Enqueue((from, id, asking.GetValueOrDefault("lang") ?? "en", asking.GetValueOrDefault("mode") ?? "written"));
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
    private async Task Forward(string id, string speaker, JsonObject item, bool dialogue = false)
    {
        var forward = $"Forward-{id}";
        if (dialogue) dialogueIds.Add(forward);
        var pcm = Speech.PcmOf(item);
        asked[forward] = (speaker, Stopwatch.GetTimestamp(), Speech.DurationMs(item), pcm.Length > 0 ? pcm.Length / 32 : Speech.DurationMs(item));      // 16 kHz, 16 bits: 32 bytes a millisecond
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
        foreach (var thing in (seen.Response.Complements?.With?[0]?["ItemValueOrRef"]?[0]?["Things"]?.AsArray() ?? []).OfType<JsonObject>().Where(t => (string?)t["Kind"] == "Speech"))   // a stream still going on is taken when it is over
        {
            var id = (string)thing["ID"]!;
            if ((bool?)thing["Streaming"] == true)           // a stream is not waited for: the Secretary listens to it as it goes on
            {
                if (handled.Add($"{Meeting.Secretary}|tap|{id}")) _ = TapAsync(id, (string?)thing["Owner"] ?? "?");
                continue;
            }
            if (handled.Contains($"{Meeting.Secretary}|tap|{id}")) continue;      // it was listened to, in pieces, while it went on
            if (!handled.Add($"{Meeting.Secretary}|heard|{id}")) continue;
            var item = (await secretary.ReadAsync(id)).Item;
            if (Speech.Is(item)) await Forward(id, (string?)thing["Owner"] ?? "?", item!);
        }
    }

    // A SPEECH THAT IS A STREAM HAS NO DURATION FIXED BEFOREHAND, so the Secretary does not wait for it to end. It listens to the stream as any participant does (the address of its data)
    // and has what it hears recognised in pieces: at a pause in the speech, or when a piece has become long. The words come while the person is still speaking.
    public const int PauseMs = 600, MaxPieceMs = 12_000, MinPieceMs = 300;
    private const double Loud = 600;                           // RMS of 16-bit samples above which a piece of the stream is speech, not silence

    private async Task TapAsync(string id, string speaker, bool dialogue = false)
    {
        var pause = dialogue ? DialoguePauseMs : PauseMs;
        try
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri($"ws://{secretary.Server!.Authority}/ws/v2.2/speech/{id}/listen?token={Uri.EscapeDataString(secretary.Token!)}"), CancellationToken.None);
            var piece = new List<byte>(); int quietMs = 0, n = 0; var spoken = false; var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            async Task Cut()
            {
                if (spoken && piece.Count / 32 >= MinPieceMs)
                {
                    var pieceId = $"{id}-p{++n}";
                    await Forward(pieceId, speaker, new JsonObject { ["ItemValueOrRef"] = new JsonArray(Speech.Make(pieceId, piece.ToArray(), start)) }, dialogue);
                }
                piece.Clear(); quietMs = 0; spoken = false; start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            var buffer = new byte[16384];
            while (ws.State == WebSocketState.Open)
            {
                var got = new List<byte>(); WebSocketReceiveResult r;
                do { r = await ws.ReceiveAsync(buffer, CancellationToken.None); got.AddRange(buffer.AsSpan(0, r.Count).ToArray()); } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Close) break;
                if (got.Count < 2) continue;
                double sum = 0; var bytes = got.ToArray(); var samples = bytes.Length / 2;
                for (var i = 0; i < samples; i++) { double v = BitConverter.ToInt16(bytes, i * 2); sum += v * v; }
                var rms = Math.Sqrt(sum / samples);
                if (rms > Loud) { spoken = true; quietMs = 0; } else quietMs += got.Count / 32;
                piece.AddRange(got);
                if ((spoken && quietMs >= pause) || piece.Count / 32 >= MaxPieceMs) await Cut();
            }
            await Cut();
        }
        catch (Exception e) when (e is WebSocketException or HttpRequestException or OperationCanceledException or IOException) { }
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

    private async Task SetHand(string user, bool up, string how)
    {
        lock (hands) { if (up == hands.Contains(user)) return; if (up) hands.Add(user); else hands.Remove(user); handSpoken.Remove(user); }
        await ShowHands();
        await Say(avs, $"Hand-{user}-{++sequence}", Meeting.Secretary, $"say:{user} {(up ? "raised a hand" : "lowered the hand")}{how}");
    }

    // Every participant is told who has a hand up: a Message of its own, replaced at each change.
    private async Task ShowHands(string? only = null)
    {
        string list; lock (hands) list = string.Join(",", hands);
        foreach (var u in only is null ? seatOf.Keys.Where(u => u != Meeting.Secretary).ToList() : [only]) await Say(avs, $"Hands-for-{u}", u, "hands:" + list);
    }

    // What was said to the Secretary is answered. Slow (the language model, the voice), so it has its own pump.
    public async Task PumpDialogueAsync()
    {
        await dialogueOne.WaitAsync();
        try
        {
            while (true)
            {
                (string User, string Words) r;
                lock (dialogueAsked) { if (dialogueAsked.Count == 0) break; r = dialogueAsked.Dequeue(); }
                try { await Dialogue(r.User, r.Words); }
                catch (Exception e) { await Reply(r.User, "Sorry, something went wrong with that.", null); Console.Error.WriteLine("secretary: " + e.Message); }
            }
        }
        finally { dialogueOne.Release(); }
    }

    private static readonly string[] Yes = ["yes", "yeah", "yep", "sure", "confirm", "do it", "go ahead", "please do", "ok", "okay"], No = ["no", "nope", "don't", "do not", "cancel", "stop", "never mind"];
    private static bool Says(string words, string[] any) => any.Any(a => System.Text.RegularExpressions.Regex.IsMatch(words, @"\b" + System.Text.RegularExpressions.Regex.Escape(a) + @"\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase));

    private async Task Dialogue(string user, string words)
    {
        if (!participants.Contains(user)) return;                              // the Secretary speaks with the participants of the meeting
        var pref = preferences.TryGetValue(user, out var p0) ? p0 : preferences[user] = new Preference();
        if (closing.Remove(user))                                              // it asked to be sure: a plain yes or no is the answer
        {
            if (Says(words, Yes) && !Says(words, No)) { await Reply(user, "Closing the meeting.", null, pref); await EndAsync(); return; }
            if (Says(words, No)) { await Reply(user, "The meeting stays open.", null, pref); return; }
        }
        if (Interpret is null) { await Reply(user, "I cannot understand speech in this meeting.", null, pref); return; }
        var (command, _) = await Interpret(user, words);
        if (command?["UserCommandRQ"] is not JsonObject rq)
        {
            await Reply(user, "Sorry, I did not catch a request. I can summarise the meeting, raise or lower your hand, switch your microphone, or close the meeting.", null, pref);
            return;
        }
        var say = new List<string>(); var ui = new JsonObject();
        if (rq["Answers"] is JsonValue a) { pref.Voice = a.GetValue<string>() == "Spoken"; say.Add(pref.Voice ? "I will answer by voice as well as in writing." : "I will answer in writing only."); }
        if (rq["Summary"] is JsonObject s)
        {
            var language = ((string?)s["Language"] ?? pref.Language).ToLowerInvariant(); var form = ((string?)s["Form"] ?? pref.Form).ToLowerInvariant();
            if (language is "ja" or "zh" && form == "spoken") { form = "written"; say.Add("No voice speaks that language, so I will write it."); }
            pref.Language = language; pref.Form = form;
            ui["Summary"] = new JsonObject { ["Language"] = language, ["Form"] = form };
            say.Add("One moment, I am preparing your summary.");
        }
        if (rq["Display"] is JsonValue d) { ui["Display"] = d.GetValue<string>(); say.Add("Done."); }
        if (rq["Hand"] is JsonValue h) { var up = h.GetValue<string>() == "Raise"; await SetHand(user, up, ""); say.Add(up ? "Your hand is up." : "Your hand is down."); }
        if (rq["Microphone"] is JsonValue mic) { ui["Microphone"] = mic.GetValue<string>(); say.Add(mic.GetValue<string>() == "Off" ? "Your microphone is off." : "Your microphone is on."); }
        if (rq["Screen"] is JsonValue sc)
        {
            if (sc.GetValue<string>() == "Stop") { ui["Screen"] = "Stop"; say.Add("Screen sharing stopped."); }
            else say.Add("I cannot start the sharing for you: the browser lets only you start it. Press Share screen.");
        }
        if (rq["CloseMeeting"] is JsonValue cm && cm.GetValue<bool>())
        {
            if (user != convener) say.Add("Only the convener can close the meeting.");
            else { closing.Add(user); say.Add("Do you want to close the meeting for everybody? Say yes or no."); }
        }
        if (rq["Confirm"] is not null && !say.Any()) say.Add("There is nothing to confirm.");
        if (rq["ReturnToMeeting"] is JsonValue back && back.GetValue<bool>()) { ui["Mode"] = "meeting"; say.Add("Back to the meeting."); }
        if (say.Count == 0) say.Add("Done.");
        await Reply(user, string.Join(" ", say), ui, pref);
    }

    // The answer of the Secretary to one participant: a Message of its own, in a few words, spoken (and written too if the participant asked for it).
    private async Task Reply(string user, string text, JsonObject? ui, Preference? pref = null)
    {
        pref ??= preferences.TryGetValue(user, out var p0) ? p0 : preferences[user] = new Preference();
        var reply = new JsonObject { ["Say"] = text, ["Written"] = true, ["Do"] = ui ?? new JsonObject() };
        if (pref.Voice && Synthesise is not null) { try { reply["Speech"] = Convert.ToBase64String(await Synthesise(text, "en")); } catch (Exception) { } }          // 16 kHz, 16 bits, mono
        await Say(secretary, $"Secretary-{user}-{++pref.Sequence}", user, reply.ToJsonString());
    }

    // The Secretary makes the summaries that were asked for. Slow (the language model, the voice), so it has its own pump: the seats and the minutes do not wait for it.
    public async Task PumpSummaryAsync()
    {
        await summaryOne.WaitAsync();
        try
        {
            while (true)
            {
                (string User, string ID, string Language, string Mode) r;
                lock (summaryAsked) { if (summaryAsked.Count == 0) break; r = summaryAsked.Dequeue(); }
                var answer = $"Summary-for-{r.ID}";
                if (!participants.Contains(r.User)) { await Say(secretary, answer, r.User, "refused: the summary is for the participants of the meeting"); continue; }
                var said = minutes.Where(line => !line.StartsWith("AVS: ", StringComparison.Ordinal)).ToList();         // the hands are not what was said
                if (Summarise is null) { await Say(secretary, answer, r.User, "refused: this meeting has no summary service"); continue; }
                if (said.Count == 0) { await Say(secretary, answer, r.User, "nothing has been said yet"); continue; }
                try
                {
                    var english = await Condensed(said);
                    var language = r.Language.ToLowerInvariant();
                    lock (summaryLang) summaryLang[r.User] = language;
                    var done = new Dictionary<string, string> { ["en"] = english };
                    async Task<string> In(string lang) { if (done.TryGetValue(lang, out var had)) return had; var t = Translate is null ? english : await Translate(english, lang); done[lang] = t; return t; }
                    var text = await In(language);
                    var reply = new JsonObject { ["Language"] = text == english ? "en" : language, ["Mode"] = r.Mode, ["Summary"] = SummaryOf($"{r.ID}-en", english) };
                    if (text != english) reply["TranslatedSummary"] = SummaryOf($"{r.ID}-{language}", text);
                    if (r.Mode == "spoken" && Synthesise is not null) reply["Speech"] = Convert.ToBase64String(await Synthesise(text, language));          // 16 kHz, 16 bits, mono
                    await Say(secretary, answer, r.User, reply.ToJsonString());
                    // and the others: the participants are all sent it, written, in the language of each
                    foreach (var other in participants.Where(u => u != r.User && u != Meeting.Secretary).ToList())
                    {
                        string lang; lock (summaryLang) lang = summaryLang.GetValueOrDefault(other, "en");
                        var t = await In(lang);
                        var push = new JsonObject { ["Version"] = r.ID, ["AskedBy"] = r.User, ["Language"] = t == english ? "en" : lang, ["Mode"] = "written", ["Summary"] = SummaryOf($"{r.ID}-en", english) };
                        if (t != english) push["TranslatedSummary"] = SummaryOf($"{r.ID}-{lang}", t);
                        await Say(secretary, $"Summary-Latest-{other}", other, push.ToJsonString());
                    }
                }
                catch (Exception e) { await Say(secretary, answer, r.User, "failed: " + e.Message); }
            }
        }
        finally { summaryOne.Release(); }
    }

    // A Summary (MMC-SUM-V2.5): its text in SummaryData.
    private static JsonObject SummaryOf(string id, string text) => new()
    {
        ["Header"] = "MMC-SUM-V2.5", ["MInstanceID"] = "MI1", ["SummaryID"] = id,
        ["SummaryData"] = new JsonArray(new JsonObject { ["Data"] = text })
    };

    // The minutes in pieces of a size the language model takes, each summarised, then the pieces summarised, until one is left.
    private async Task<string> Condensed(List<string> lines)
    {
        var pieces = Pieces(lines);
        while (true)
        {
            var summaries = new List<string>();
            foreach (var piece in pieces) summaries.Add((await Summarise!(piece)).Trim());
            if (summaries.Count == 1) return summaries[0];
            var next = Pieces(summaries);
            if (next.Count >= summaries.Count) { var all = string.Join("\n", summaries); next = [all[..Math.Min(PieceChars, all.Length)] + "\n"]; }      // it does not get shorter: what fits is taken
            pieces = next;
        }
    }

    private static List<string> Pieces(List<string> lines)
    {
        var pieces = new List<string>(); var current = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            if (current.Length > 0 && current.Length + line.Length > PieceChars) { pieces.Add(current.ToString()); current.Clear(); }
            current.Append(line).Append('\n');
        }
        if (current.Length > 0) pieces.Add(current.ToString());
        return pieces;
    }

    // The minutes belong to the room: the Secretary hands them to the AVS Service, and gives a copy to each participant - an Item of its own for each, so that a
    // participant who is taken out of the meeting keeps what it had and is given no more.
    private async Task Publish(string? only = null)
    {
        var text = string.Join("\n", minutes);
        if (only is null) await Say(secretary, Meeting.Minutes, Meeting.Avs, text);
        foreach (var u in only is null ? participants.ToList() : [only]) await Say(secretary, $"{Meeting.Minutes}-for-{u}", u, text);
    }

    // The doors of the building are Rights the AVS Service gives a User when it invites it, and takes back when it takes the User out (the world of the MMM).
    private async Task Doors(string user, bool grant)
    {
        if (!Meeting.DoorsNeedRights) return;
        var with = new List<JsonNode>();
        foreach (var door in Meeting.Doors)
        {
            with.Add(World.DoorPA(user, door));
            var right = Uc2.Rights($"Door-{(grant ? "Right" : "Revoke")}-{user}-{door}", grant ? "May" : "May Not", "Granted", World.DoorID(user, door));
            with.Add(grant ? Meant(right) : right);
        }
        await avs.PerformAsync("Rights Change", Uc2.C(user, with: with.ToArray()));
    }

    // THE RIGHTS OF AN MPAI OFFICER: the Right to move its Persona to every place that needs one. They do not end with a meeting; they are the Account's.
    private readonly HashSet<string> officers = new();
    private async Task Officer(string user)
    {
        if (!Meeting.DoorsNeedRights || !officers.Add(user)) return;
        var with = new List<JsonNode>();
        foreach (var place in Meeting.AllPlaces)
        {
            with.Add(World.DoorPA(user, place));
            with.Add(Uc2.Rights($"Officer-Right-{user}-{place}", "May", "Granted", World.DoorID(user, place)));
        }
        await avs.PerformAsync("Rights Change", Uc2.C(user, with: with.ToArray()));
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
        await Doors(user, false);
        if (user == convener) await avs.PerformAsync("Rights Change", Uc2.C(user, with: [Meeting.SharePA(user), Uc2.Rights($"Share-Revoke-{user}", "May Not", "Granted", Meeting.ShareID(user))]));
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
            var free = Meeting.InOrderFor(user, Meeting.Seats.Where(s => !seatOf.ContainsValue(s.ID))).FirstOrDefault();
            if (free.ID is null) { await Say(avs, replyId, user, "full"); return; }
            seatOf[user] = seatId = free.ID;
        }
        var pa = Meeting.SitPA(persona, seatId);   // the Persona the User brings: the right is for it (a User moves only Personae it owns, whatever it is told)
        // ...and the right to place what it says at that seat.
        await avs.PerformAsync("Rights Change", Uc2.C(user, with: [pa, Meant(Uc2.Rights($"Seat-Right-{persona}", "May", "Granted", Meeting.SitID(persona))),
                                                                   Meeting.SpeakPA(user, seatId), Meant(Uc2.Rights($"Speak-Right-{user}", "May", "Granted", Meeting.SpeakID(user)))]));
        if (user == convener)            // the convener's: to share a screen in the room (the others see it)
            await avs.PerformAsync("Rights Change", Uc2.C(user, with: [Meeting.SharePA(user), Meant(Uc2.Rights($"Share-Right-{user}", "May", "Granted", Meeting.ShareID(user)))]));
        await Doors(user, true);         // in the world of the MMM: and the right to go through the doors of the building to it
        await Say(avs, replyId, user, Meeting.SeatText(Meeting.Seats.First(s => s.ID == seatId), persona, user == convener));
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
    public static JsonObject SeatedAt(string id, double yaw, double x = 0, double y = 0)
    {
        var sa = Uc2.SpatialAttitude(id, x, y);
        sa["Orientation"]!["Orientation"] = new JsonArray(yaw, 0.0, 0.0);
        return sa;
    }
}
