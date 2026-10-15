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
    public const string Parcel = "MeetingParcel", Room = "MeetingRoom", Avs = "AVS", Secretary = "Secretary", SecretaryPersona = "Secretary-Persona", Minutes = "Minutes-" + Room;
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
public sealed class MeetingServices(HttpClient http, MInstance m)
{
    private readonly MmmClient avs = new(http, Meeting.Avs, m.TokenOf(Meeting.Avs));
    private readonly MmmClient secretary = new(http, Meeting.Secretary, m.TokenOf(Meeting.Secretary));
    private readonly Dictionary<string, string> seatOf = new();               // User -> seat
    private readonly HashSet<string> handled = new();
    private readonly List<string> minutes = [];
    private readonly SemaphoreSlim one = new(1, 1);

    public IReadOnlyList<string> TheMinutes => minutes;

    // The Secretary comes to the Metaverse Square, and asks to join the meeting like any participant.
    public async Task StartAsync()
    {
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
                if (text is not null && text.StartsWith("join", StringComparison.Ordinal))
                    await Seat(from, Meeting.ParseSeat(text.Replace("join;", "")).GetValueOrDefault("persona") ?? $"{from}-Persona");
            }
            foreach (var (id, from) in await secretary.InboxAsync())
            {
                if (!handled.Add($"{Meeting.Secretary}|{id}")) continue;
                var text = Meeting.TextOf((await secretary.ReadAsync(id)).Item);
                if (text is null) continue;
                if (text.StartsWith("seat=", StringComparison.Ordinal)) await Sit(Meeting.ParseSeat(text));
                else if (text.StartsWith("say:", StringComparison.Ordinal))
                {
                    minutes.Add($"{from}: {text[4..].Trim()}");
                    await Say(secretary, Meeting.Minutes, Meeting.Avs, string.Join("\n", minutes));   // the minutes belong to the room: the Secretary hands them to its service
                }
            }
        }
        finally { one.Release(); }
    }

    // The AVS Service gives a User the right to move its Persona to a free seat, and tells it which. A User that has a seat is told it again.
    private async Task Seat(string user, string persona)
    {
        if (!seatOf.TryGetValue(user, out var seatId))
        {
            var free = Meeting.Seats.FirstOrDefault(s => !seatOf.ContainsValue(s.ID));
            if (free.ID is null) { await Say(avs, $"Seat-{user}", user, "full"); return; }
            seatOf[user] = seatId = free.ID;
        }
        var pa = Meeting.SitPA(persona, seatId);   // the Persona the User brings: the right is for it (a User moves only Personae it owns, whatever it is told)
        await avs.PerformAsync("Rights Change", Uc2.C(user, with: [pa, Uc2.Rights($"Seat-Right-{persona}", "May", "Granted", Meeting.SitID(persona))]));
        await Say(avs, $"Seat-{user}", user, Meeting.SeatText(Meeting.Seats.First(s => s.ID == seatId), persona));
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
