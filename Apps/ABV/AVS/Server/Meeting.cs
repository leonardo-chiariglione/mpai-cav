using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Paf.Pdx;
using Mpai.Paf.Pmx;
using Mpai.Paf.Vcr;

namespace Mpai.Abv.Server;

// THE MEETING OF THE AVATAR-BASED VIDEOCONFERENCE, as the server of the demo holds it (ABV/PROTOCOL.md). Roles of the standard, by what runs here:
//   VCT  - of each participant: the Portable Avatar it sends (ID, and at the introduction the Avatar Model), made by PMX; its speech is the microphone's PCM;
//   AVS  - PDX gives the ID of a Portable Avatar; PMX puts into it the Audio-Visual Scene (the room) and the Spatial Attitude (the seat); the Portable Avatars go to every
//          participant (the speech goes as audio, relayed, positioned by the client); there is no authentication and no translation today;
//   VCR  - one per participant, holding the state of each avatar by its ID, seating it and adding its speech at the mouth; what it gives out is what the participant sees;
//   VMS  - the minutes: Whisper recognises each utterance and a line is added to the Summary (the secretary's language model is not there today).
public sealed class Meeting
{
    public const int Seats = 5, SecretarySeat = 4;
    public const string SecretaryId = "Secretary", SecretaryAvatar = "Thalia";       // a woman of the same set, not offered to the participants
    private const double TableRadius = 1.05;      // the seats on a circle of 1.05 m: across the table is 2 m away, a neighbour 1.2 m

    public sealed record AvatarInfo(string Id, string File, string Label);

    public sealed class SeatView { public string Id = ""; public string Avatar = ""; public double X, Y, Z, Yaw; public bool Speaking; public int Seat; }

    public sealed class Participant
    {
        public string Id = ""; public string Avatar = ""; public int Seat; public bool Secretary; public string Lang = "auto";
        public WebSocket? Ws;
        public readonly SemaphoreSlim SendLock = new(1, 1);
        public readonly VideoconferenceClientReceiver Vcr = new("1PAF-VCR-V1.6-I01");
        public readonly Dictionary<string, SeatView> View = new(StringComparer.Ordinal);       // what this participant's VCR last gave, by avatar ID
        public JsonObject? Introduced;                                                          // its Portable Avatar as the server sent it on: model, room, seat
        public bool Speaking; public long LastVoiced, LastRelayed; public int SpeechCount; public double Floor = 300;
        public int Frames, Relayed; public double MaxLevel, SumLevel, Smooth;                      // what its microphone has sent since the last line of the log
        public readonly List<byte> Utterance = []; public readonly Queue<byte[]> PreRoll = new(); public long UtteranceStart; public int VoicedFrames; public bool InUtterance;
    }

    private readonly object gate = new();
    private readonly List<Participant> participants = [];
    private readonly List<string> minutes = [];
    private readonly Whisper whisper;
    private readonly PmxAimProcessor pmx;
    private readonly PdxAimProcessor pdx;
    private readonly AimPortReader pmxPorts, pdxPorts;
    public IReadOnlyList<AvatarInfo> Avatars { get; }
    public double Fov { get; set; } = double.TryParse(Environment.GetEnvironmentVariable("ABV_FOV"), out var fov) ? fov : 105;      // at a table of five your neighbours are 54 degrees from where you look: a field of 115 shows them at the edges

    public Meeting(string root, IReadOnlyList<AvatarInfo> avatars, Whisper whisper)
    {
        var store = new AIF.Store.AmdStore(Path.Combine(root, "AIMs", "AMDs")); store.Scan();
        pmxPorts = AimPortReader.Load(store, "1PAF-PMX-V1.6-I01");
        pdxPorts = AimPortReader.Load(store, "1PAF-PDX-V1.6-I01");
        pmx = new PmxAimProcessor("1PAF-PMX-V1.6-I01", pmxPorts);
        pdx = new PdxAimProcessor("1PAF-PDX-V1.6-I01", pdxPorts);
        Avatars = avatars;
        this.whisper = whisper;
        // The secretary is there from the start: a pure avatar, seated by the server.
        var secretary = new Participant { Id = SecretaryId, Avatar = SecretaryAvatar, Seat = SecretarySeat, Secretary = true };
        participants.Add(secretary);
        Introduce(secretary);
        Deliver(secretary.Introduced!);
    }

    // ---------------------------------------------------------------- the Portable Avatars

    private static JsonObject Time(string id, long ms) => new()
    {
        ["Header"] = "OSD-STM-V1.5", ["SimpleTimeID"] = id,
        ["SimpleTimeData"] = new JsonArray(new JsonObject { ["FlagsByte"] = 3, ["StartTime"] = ms, ["EndTime"] = ms, ["AccuracyMode"] = "single", ["AccuracyPlusMinus"] = 1 })
    };

    private static (double X, double Y, double Yaw) SeatAt(int seat)
    {
        var a = seat * 2 * Math.PI / Seats;
        return (Math.Round(TableRadius * Math.Cos(a), 3), Math.Round(TableRadius * Math.Sin(a), 3), a + Math.PI);          // facing the centre of the table
    }

    private static JsonObject Attitude(string id, int seat)
    {
        var (x, y, yaw) = SeatAt(seat);
        var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new JsonObject
        {
            ["Header"] = "OSD-OSA-V1.5", ["ObjectSpatialAttitudeID"] = "osa-" + id, ["SpatialAttitudeTime"] = Time("osa-" + id + "-T", ms),
            ["General"] = new JsonObject { ["CoordType"] = "Cartesian" },
            ["Position"] = new JsonObject
            {
                ["Header"] = "OSD-OPS-V1.5", ["PositionID"] = "osa-" + id + "-P", ["General"] = new JsonObject { ["CoordType"] = "Cartesian" },
                ["CartPosition"] = new JsonArray(x, y, 0.0), ["CartPositionAccuracy"] = new JsonArray(0.05, 0.05, 0.05), ["CartVelocity"] = new JsonArray(0.0, 0.0, 0.0)
            },
            ["Orientation"] = new JsonObject { ["Header"] = "OSD-OOR-V1.5", ["OrientationID"] = "osa-" + id + "-O", ["Orientation"] = new JsonArray(0.0, 0.0, Math.Round(yaw * 180 / Math.PI, 3)) }
        };
    }

    // The room: the Audio-Visual Scene Descriptors the participants' Portable Avatars do not have and the server adds.
    private static JsonObject Room() => new()
    {
        ["Header"] = "OSD-BMS-V1.5", ["MInstanceID"] = "", ["BasicAVSceneDescriptorsID"] = "meeting-room",
        ["BAVSDescriptorsSpaceTime"] = new JsonObject { ["Header"] = "OSD-SPT-V1.5", ["MInstanceID"] = "", ["SpaceTimeID"] = "room-st" }
    };

    private Message Call(PmxAimProcessor aim, Dictionary<string, JsonNode> inputs) =>
        aim.ProcessAsync(new Message { MessageId = Guid.NewGuid().ToString(), Ports = inputs.ToDictionary(p => p.Key, p => p.Value.ToJsonString()) }).GetAwaiter().GetResult();

    // VCT of a participant: the Portable Avatar of its introduction - the ID and the Avatar Model (a Basic 3D Model Object that names the file the receivers hold).
    private JsonObject Vct(Participant p, bool introduction, JsonArray? speech = null)
    {
        var inputs = new Dictionary<string, JsonNode> { [pmxPorts.InputOrDefault("string", "")] = JsonValue.Create(p.Id)! };
        if (introduction)
            inputs[pmxPorts.InputOrDefault("OSD-B3O-V1.5", "")] = new JsonObject { ["Header"] = "OSD-B3O-V1.5", ["Basic3DModelObjectID"] = p.Avatar };
        var made = Call(pmx, inputs);
        var pav = JsonNode.Parse(made.Ports[pmxPorts.Output("PAF-PAV-V1.6")])!.AsObject();
        if (speech is not null) pav["SPOOrSPOID"] = speech;
        return pav;
    }

    // AVS: PDX gives the ID the Portable Avatar carries (authentication is not run today); PMX adds the room and the seat, starting from the Portable Avatar.
    private JsonObject Avs(JsonObject fromParticipant)
    {
        var parts = pdx.ProcessAsync(new Message { MessageId = Guid.NewGuid().ToString(), Ports = new() { [pdxPorts.Input("PAF-PAV-V1.6")] = fromParticipant.ToJsonString() } }).GetAwaiter().GetResult();
        var id = JsonNode.Parse(parts.Ports[pdxPorts.OutputOrDefault("string", "")])!.GetValue<string>();
        var who = participants.First(p => p.Id == id);
        var made = Call(pmx, new Dictionary<string, JsonNode>
        {
            [pmxPorts.InputOrDefault("PAF-PAV-V1.6", "")] = fromParticipant,
            [pmxPorts.InputOrDefault("string", "")] = JsonValue.Create(id)!,
            [pmxPorts.InputOrDefault("OSD-BMS-V1.5", "")] = Room(),
            [pmxPorts.InputOrDefault("OSD-OSA-V1.5", "")] = Attitude(id, who.Seat)
        });
        return JsonNode.Parse(made.Ports[pmxPorts.Output("PAF-PAV-V1.6")])!.AsObject();
    }

    private void Introduce(Participant p) => p.Introduced = Avs(Vct(p, introduction: true));

    // Every participant's VCR takes the Portable Avatar, and what it gives out is kept as that participant's view.
    private void Deliver(JsonObject pav)
    {
        foreach (var c in participants.Where(x => !x.Secretary)) Take(c, pav);
    }

    private static void Take(Participant c, JsonObject pav)
    {
        foreach (var (type, _, json) in c.Vcr.Take("PAF-PAV-V1.6", pav.DeepClone()))
        {
            if (type != "PAF-PAV-V1.6") continue;
            var avatar = JsonNode.Parse(json)!;
            var list = avatar["AvatarOrAvatarID"]!.AsArray();
            var id = list.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).First(s => !string.IsNullOrEmpty(s))!;
            var model = list.OfType<JsonObject>().FirstOrDefault()?["Basic3DModelObjectID"]?.GetValue<string>() ?? "";
            var at = avatar["AvatarSpatialAttitude"];
            if (at?["Position"]?["CartPosition"] is not JsonArray { Count: 3 } position) continue;                 // not seated: not seen
            var yaw = (double?)at["Orientation"]?["Orientation"]?[2] ?? 0;
            if (!c.View.TryGetValue(id, out var view)) c.View[id] = view = new SeatView { Id = id };
            view.Avatar = model; view.X = (double)position[0]!; view.Y = (double)position[1]!; view.Z = (double)position[2]!; view.Yaw = yaw * Math.PI / 180;
            view.Speaking = avatar["SPOOrSPOID"] is JsonArray { Count: > 0 };
        }
    }

    // ---------------------------------------------------------------- joining, leaving

    public (Participant? Who, string? Error) Join(string name, string avatar, WebSocket ws, string? lang = null)
    {
        name = (name ?? "").Trim();
        if (name.Length is 0 or > 20 || name.Any(ch => !(char.IsLetterOrDigit(ch) || ch is ' ' or '_' or '-'))) return (null, "the name is 1-20 letters, digits, space, - or _");
        if (name.Equals(SecretaryId, StringComparison.OrdinalIgnoreCase)) return (null, "that name is the secretary's");
        if (Avatars.All(a => a.Id != avatar)) return (null, "unknown avatar");
        lock (gate)
        {
            if (participants.Any(p => p.Id.Equals(name, StringComparison.OrdinalIgnoreCase))) return (null, "name taken");
            // The seats in the order they are given: at a table of five, the two across the table are at 18 degrees from where you look, your neighbours at 54 - outside a field of
            // view of 90. So the second to come sits across the first, the third beside them: everybody has two others in front of them.
            var free = new[] { 0, 2, 3, 1 }.Where(s => participants.All(p => p.Seat != s)).ToList();
            if (free.Count == 0) return (null, "the meeting is full");
            var who = new Participant { Id = name, Avatar = avatar, Seat = free[0], Ws = ws, Lang = new[] { "it", "en", "fr", "pt", "es", "de" }.Contains(lang ?? "") ? lang! : "auto" };
            participants.Add(who);
            Introduce(who);
            foreach (var q in participants.Where(p => p != who)) Take(who, q.Introduced!);      // the newcomer's VCR learns who is there
            Deliver(who.Introduced!);                                                           // and everybody's, who has come
            return (who, null);
        }
    }

    public void Leave(Participant who)
    {
        lock (gate)
        {
            participants.Remove(who);
            foreach (var c in participants) c.View.Remove(who.Id);
        }
    }

    public IEnumerable<Participant> Humans() { lock (gate) return participants.Where(p => !p.Secretary && p.Ws is { State: WebSocketState.Open }).ToList(); }

    public string MinutesText() { lock (gate) return string.Join("\n", minutes); }

    // ---------------------------------------------------------------- what is sent

    public JsonObject SceneFor(Participant c)
    {
        lock (gate)
        {
            var seats = c.View.Values.OrderBy(v => participants.FirstOrDefault(p => p.Id == v.Id)?.Seat ?? 9)
                .Select(v => new JsonObject { ["id"] = v.Id, ["avatar"] = v.Avatar, ["x"] = v.X, ["y"] = v.Y, ["z"] = v.Z, ["yaw"] = Math.Round(v.Yaw, 4), ["speaking"] = v.Speaking });
            return new JsonObject { ["t"] = "scene", ["you"] = c.Id, ["seats"] = new JsonArray(seats.ToArray<JsonNode?>()) };
        }
    }

    public JsonObject Welcome(Participant c) => new()
    {
        ["t"] = "welcome", ["id"] = c.Id, ["seat"] = c.Seat, ["fov"] = Fov, ["minutes"] = MinutesText(),
        ["avatars"] = new JsonArray(Avatars.Select(a => (JsonNode?)new JsonObject { ["id"] = a.Id, ["file"] = a.File, ["label"] = a.Label }).ToArray())
    };

    public static async Task SendText(Participant c, JsonNode message)
    {
        if (c.Ws is not { State: WebSocketState.Open } ws) return;
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await c.SendLock.WaitAsync();
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException) { }
        finally { c.SendLock.Release(); }
    }

    // Audio goes to a participant if it can be taken without waiting: a slow one loses sound, it does not hold the others back.
    private static async Task SendAudio(Participant c, byte[] frame)
    {
        if (c.Ws is not { State: WebSocketState.Open } ws) return;
        if (!await c.SendLock.WaitAsync(0)) return;
        try { await ws.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None); }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException) { }
        finally { c.SendLock.Release(); }
    }

    public async Task BroadcastScenes() { foreach (var c in Humans()) await SendText(c, SceneFor(c)); }

    public async Task Broadcast(JsonNode message) { foreach (var c in Humans()) await SendText(c, message); }

    // ---------------------------------------------------------------- what comes in

    // A frame of the microphone: Int16 PCM at 16 kHz. Voiced frames (and a hangover) are relayed; the utterances are cut for the minutes.
    public async Task Audio(Participant who, byte[] pcm)
    {
        var now = Environment.TickCount64;
        var level = Rms(pcm);
        who.Floor = level < who.Floor ? level * 0.5 + who.Floor * 0.5 : who.Floor * 0.995 + level * 0.005;      // the noise the microphone makes by itself: falls at once, rises slowly
        var voiced = level > Math.Max(500, who.Floor * 3);
        // What the others hear is more generous than what the minutes take: a quiet microphone must still be heard.
        var heard = level > Math.Max(220, who.Floor * 2.5);
        // TWO MICROPHONES IN ONE ROOM: the voice of one reaches the other's microphone through the air and the loudspeakers, fainter. The louder microphone is the speaker's: what
        // reaches the quieter one, while the other is clearly louder (3 times), is not its owner's speech, and is neither relayed nor minuted under its name.
        who.Smooth = who.Smooth * 0.7 + level * 0.3;
        if ((voiced || heard) && Humans().Any(p => p != who && now - p.LastRelayed < 300 && p.Smooth > who.Smooth * 3)) { voiced = false; heard = false; }
        if (voiced) who.LastVoiced = now;
        if (heard) who.LastRelayed = now;
        who.Frames++; who.SumLevel += level; who.MaxLevel = Math.Max(who.MaxLevel, level);
        var relay = now - who.LastRelayed < 600;

        if (relay)
        {
            var id = Encoding.UTF8.GetBytes(who.Id);
            var frame = new byte[2 + id.Length + pcm.Length];
            frame[0] = 2; frame[1] = (byte)id.Length; id.CopyTo(frame, 2); pcm.CopyTo(frame, 2 + id.Length);
            foreach (var c in Humans().Where(c => c != who)) _ = SendAudio(c, frame);
        }

        // The utterances for the minutes.
        if (!who.InUtterance)
        {
            who.PreRoll.Enqueue(pcm); while (who.PreRoll.Count > 4) who.PreRoll.Dequeue();
            if (voiced) { who.InUtterance = true; who.UtteranceStart = now; who.VoicedFrames = 0; foreach (var f in who.PreRoll) who.Utterance.AddRange(f); who.PreRoll.Clear(); }
        }
        else
        {
            who.Utterance.AddRange(pcm);
            if (voiced) who.VoicedFrames++;
            var silent = now - who.LastVoiced > 800;
            if (silent || now - who.UtteranceStart > 15000)
            {
                var data = who.Utterance.ToArray(); var voicedFrames = who.VoicedFrames;
                who.Utterance.Clear(); who.InUtterance = false; who.VoicedFrames = 0;
                if (voicedFrames >= 6) _ = Minute(who, data);                      // 0.6 s of voice at the least: a cough or a click is not minuted
            }
        }
        await Task.CompletedTask;
    }

    private static double Rms(byte[] pcm)
    {
        if (pcm.Length < 2) return 0;
        double sum = 0; int n = pcm.Length / 2;
        for (var i = 0; i < n; i++) { var s = BitConverter.ToInt16(pcm, 2 * i); sum += (double)s * s; }
        return Math.Sqrt(sum / n);
    }

    private async Task Minute(Participant who, byte[] pcm)
    {
        var text = await whisper.TranscribeAsync(pcm, who.Lang);
        if (text.Length < 2) return;
        AddMinute($"{who.Id}: {text}");
        await Broadcast(new JsonObject { ["t"] = "minutes", ["text"] = MinutesText() });
    }

    public void AddMinute(string line) { lock (gate) minutes.Add(line); }

    // Every 150 ms: who speaks. A change is a Portable Avatar update - the ID and the speech, or the speech gone - which every participant's VCR takes.
    private long lastLog;

    public async Task Tick()
    {
        var now = Environment.TickCount64;
        if (now - lastLog > 5000)
        {
            lastLog = now;
            lock (gate)
                foreach (var p in participants.Where(p => !p.Secretary && p.Frames > 0))
                {
                    Console.WriteLine($"[ABV] {p.Id}: {p.Frames} frames, level avg {p.SumLevel / p.Frames:F0} max {p.MaxLevel:F0}, noise floor {p.Floor:F0}, heard by others {(now - p.LastRelayed < 5000 ? "yes" : "no")}");
                    p.Frames = 0; p.SumLevel = 0; p.MaxLevel = 0;
                }
        }
        var changed = false;
        List<(Participant, bool)> changes = [];
        lock (gate)
        {
            foreach (var p in participants.Where(p => !p.Secretary))
            {
                var speaking = now - p.LastRelayed < 400 && p.LastRelayed > 0;
                if (speaking == p.Speaking) continue;
                p.Speaking = speaking; changes.Add((p, speaking));
            }
            foreach (var (p, speaking) in changes)
            {
                var speech = speaking
                    ? new JsonArray(new JsonObject { ["Header"] = "OSD-BSO-V1.5", ["BasicSpeechObjectID"] = $"speech-{p.Id}-{++p.SpeechCount}" })
                    : new JsonArray();
                Deliver(Avs(Vct(p, introduction: false, speech)));
                changed = true;
            }
        }
        if (changed) await BroadcastScenes();
    }

    public async Task Chat(Participant who, string text)
    {
        text = (text ?? "").Trim(); if (text.Length == 0) return; if (text.Length > 400) text = text[..400];
        AddMinute($"{who.Id}: {text}");
        await Broadcast(new JsonObject { ["t"] = "chat", ["from"] = who.Id, ["text"] = text });
        await Broadcast(new JsonObject { ["t"] = "minutes", ["text"] = MinutesText() });
    }
}
