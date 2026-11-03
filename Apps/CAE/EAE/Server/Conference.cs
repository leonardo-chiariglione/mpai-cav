using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

using Mpai.Cae.Apps;
using Mpai.Cae.Usc;
using Mpai.Ua;

namespace Mpai.Cae.Eae.Server;

// THE CONFERENCE: the participants (the room, whose microphone array is captured, and the listeners), the capture cut into segments, and the Module of the Enhanced Audioconference Experience that
// the segments go through one at a time. What comes back - the speakers of the room, where each is, and the enhanced audio of each - is sent to the listeners.
public sealed class Conference(ModuleHost module, ArrayGeometry array, double segmentSeconds) : IDisposable
{
    public sealed class Participant
    {
        public string Id = ""; public string Role = ""; public WebSocket Ws = null!;
        public readonly SemaphoreSlim Sending = new(1, 1);
    }

    private readonly List<Participant> _participants = [];
    private readonly object _lock = new();
    private readonly List<List<short>> _pending = Enumerable.Range(0, array.Count).Select(_ => new List<short>()).ToList();
    private readonly Tracker _speakers = new();
    private int _busy, _processed, _dropped;
    private readonly QueueDevice _capture = new("OSD-BAO-V1.5"), _geometry = new("CAE-MAG-V2.5"), _reference = new("CAE-ASR-V1.0");
    private TaskCompletionSource<string?>? _exchange; private string? _scene;
    private readonly CancellationTokenSource _stop = new();
    private Thread? _user;
    public int Channels => array.Count;
    public int Rate => array.Rate;

    public void Dispose() { _stop.Cancel(); }

    // ---- participants ------------------------------------------------------------------------------------------------------------------------------------------------

    public (Participant? Who, string? Error) Join(string name, string role, WebSocket ws)
    {
        name = new string(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ').ToArray()).Trim();
        if (name.Length is 0 or > 24) return (null, "choose a name of up to 24 letters or digits");
        if (role is not ("room" or "listener")) return (null, "the role is room or listener");
        lock (_lock)
        {
            if (_participants.Any(p => p.Id.Equals(name, StringComparison.OrdinalIgnoreCase))) return (null, "name taken");
            if (role == "room" && _participants.Any(p => p.Role == "room")) return (null, "the room is already in the conference");
            var who = new Participant { Id = name, Role = role, Ws = ws };
            _participants.Add(who); return (who, null);
        }
    }

    public void Leave(Participant who)
    {
        lock (_lock)
        {
            _participants.Remove(who);
            if (who.Role == "room") foreach (var p in _pending) p.Clear();
        }
    }

    public JsonObject Welcome(Participant who) => new()
    {
        ["t"] = "welcome", ["id"] = who.Id, ["role"] = who.Role, ["channels"] = array.Count, ["rate"] = array.Rate, ["segment"] = segmentSeconds,
        ["status"] = Status()
    };

    public string Status()
    {
        lock (_lock) return $"{_participants.Count(p => p.Role == "listener")} listener(s); {_processed} segment(s) processed" + (_dropped > 0 ? $", {_dropped} dropped (the Module is slower than the room)" : "");
    }

    // ---- the room's audio --------------------------------------------------------------------------------------------------------------------------------------------

    // Interleaved 16-bit samples of all the channels, 16 kHz (what a room client sends). Whole segments go to the Module.
    public void Audio(byte[] interleaved)
    {
        var frames = interleaved.Length / 2 / array.Count; if (frames == 0) return;
        List<short[]>? segment = null;
        lock (_lock)
        {
            for (var i = 0; i < frames; i++)
                for (var c = 0; c < array.Count; c++) _pending[c].Add(BitConverter.ToInt16(interleaved, (i * array.Count + c) * 2));
            if (_pending[0].Count >= (int)(segmentSeconds * array.Rate))
            {
                segment = _pending.Select(p => p.ToArray()).ToList();
                foreach (var p in _pending) p.Clear();
            }
        }
        if (segment is not null) Process(new MultiAudio(segment.ToArray(), array.Rate));
    }

    // The segment goes through the Module if it is free; if it is not, the segment is dropped: a conference does not wait for the past.
    public void Process(MultiAudio segment)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) { Interlocked.Increment(ref _dropped); if (Environment.GetEnvironmentVariable("EAE_TRACE") == "1") Console.WriteLine("[EAE] segment dropped: the Module is busy"); return; }
        _ = Task.Run(async () =>
        {
            try { await Run(segment); }
            catch (Exception e) { Console.WriteLine("[EAE] a segment failed: " + e.Message); }
            finally { Interlocked.Exchange(ref _busy, 0); }
        });
    }

    // The User Agent runs the conference workflow once, on a thread of its own (the Controller API blocks while the Module runs): the Module is started once and each exchange takes the next
    // segment from the devices; what it presents is kept until the exchange says it is over.
    public void Start()
    {
        var devices = new DeviceRegistry()
            .RegisterDevice("Capture", _capture).RegisterDevice("Array", _geometry).RegisterDevice("Reference", _reference)
            .RegisterPresent("scene", d =>
            {
                if (d.TryGetValue("OSD-BAS-V1.5", out var s)) _scene = s;
                if (d.ContainsKey("Prompt")) { var scene = _scene; _scene = null; _exchange?.TrySetResult(scene); }
                return Task.CompletedTask;
            });
        _user = new Thread(() =>
        {
            try { new WorkflowRun(module.Api, Path.Combine(ModuleHost.Root, "Apps", "CAE", "EAE", "EAE-Conference.orch"), devices, Environment.GetEnvironmentVariable("EAE_TRACE") == "1" ? s => Console.WriteLine("[EAE]   " + s) : null).Run(_stop.Token); }
            catch (Exception e) { Console.WriteLine("[EAE] the workflow ended: " + e.Message); _exchange?.TrySetResult(null); }
        }) { IsBackground = true, Name = "EAE User Agent" };
        _user.Start();
    }

    private static readonly string Reference = new JsonObject { ["Header"] = "CAE-ASR-V1.0", ["PolynomialVariable"] = "AngleSine", ["Polynomial"] = new JsonArray(new JsonObject { ["Coefficient"] = 1.0, ["Exponent"] = 0 }) }.ToJsonString();

    private async Task Run(MultiAudio segment)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously); _exchange = done;
        _capture.Give(AudioObject.MakeMultichannel("capture", segment, array)); _geometry.Give(array.ToJson().ToJsonString()); _reference.Give(Reference);
        var finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(60)));
        if (finished != done.Task) throw new TimeoutException("the Module gave nothing in 60 s");
        var scene = await done.Task;
        if (scene is null) { Interlocked.Increment(ref _processed); Console.WriteLine($"[EAE] segment: quiet ({clock.ElapsedMilliseconds} ms)"); return; }
        var speakers = new JsonArray(); var audio = new List<(string Id, short[] Samples)>();
        foreach (var member in JsonNode.Parse(scene)!["BasicAudioSceneDescriptorsData"]!.AsArray())
        {
            var item = AudioObject.Read(member!["AObjectIDOrAObject"]![0]!.ToJsonString()).Single();
            var source = SceneDescriptors.Read(new JsonObject { ["Header"] = SceneDescriptors.Header, ["BasicAudioSceneDescriptorsData"] = new JsonArray(member.DeepClone()) }.ToJsonString()).Single();
            var id = _speakers.Identify(source.Azimuth, source.Elevation);
            speakers.Add(new JsonObject { ["id"] = id, ["azimuth"] = Math.Round(source.Azimuth, 1), ["elevation"] = Math.Round(source.Elevation, 1), ["speaking"] = source.Active.Length > 0 });
            if (source.Active.Length > 0) audio.Add((id, item.Audio.At(array.Rate).Samples));
        }
        Interlocked.Increment(ref _processed);
        await BroadcastText(new JsonObject { ["t"] = "scene", ["speakers"] = speakers, ["seconds"] = segmentSeconds, ["processing"] = (int)clock.ElapsedMilliseconds });
        foreach (var (id, samples) in audio) await BroadcastAudio(id, samples);
        Console.WriteLine($"[EAE] segment: {speakers.Count} speaker(s) in {clock.ElapsedMilliseconds} ms");
    }

    // ---- to the listeners --------------------------------------------------------------------------------------------------------------------------------------------

    private List<Participant> Listeners() { lock (_lock) return _participants.Where(p => p.Role == "listener").ToList(); }

    public static async Task SendText(Participant to, JsonObject message) => await Send(to, Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text);

    private async Task BroadcastText(JsonObject message) { foreach (var p in Listeners()) await SendText(p, message); }

    // byte 0x02, the length of the id, the id, then 16-bit PCM mono at 16 kHz in chunks of 100 ms.
    private async Task BroadcastAudio(string id, short[] samples)
    {
        var name = Encoding.UTF8.GetBytes(id);
        for (var at = 0; at < samples.Length; at += 1600)
        {
            var n = Math.Min(1600, samples.Length - at); var frame = new byte[2 + name.Length + n * 2];
            frame[0] = 2; frame[1] = (byte)name.Length; name.CopyTo(frame, 2); Buffer.BlockCopy(samples, at * 2, frame, 2 + name.Length, n * 2);
            foreach (var p in Listeners()) await Send(p, frame, WebSocketMessageType.Binary);
        }
    }

    private static async Task Send(Participant to, byte[] bytes, WebSocketMessageType type)
    {
        await to.Sending.WaitAsync();
        try { if (to.Ws.State == WebSocketState.Open) await to.Ws.SendAsync(bytes, type, true, CancellationToken.None); }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or IOException) { }
        finally { to.Sending.Release(); }
    }

    // The same speaker keeps the same letter from one segment to the next: a direction within 25 degrees of one already known is that speaker's.
    private sealed class Tracker
    {
        private readonly List<(string Id, double Az, double El)> _known = [];

        public string Identify(double az, double el)
        {
            lock (_known)
            {
                for (var i = 0; i < _known.Count; i++)
                    if (Angle(_known[i].Az, _known[i].El, az, el) < 25) { _known[i] = (_known[i].Id, az, el); return _known[i].Id; }
                var id = ((char)('A' + _known.Count % 26)).ToString() + (_known.Count >= 26 ? (_known.Count / 26).ToString() : "");
                _known.Add((id, az, el)); return id;
            }
        }

        private static double Angle(double a1, double e1, double a2, double e2)
        {
            double[] U(double a, double e) => [Math.Cos(e * Math.PI / 180) * Math.Cos(a * Math.PI / 180), Math.Cos(e * Math.PI / 180) * Math.Sin(a * Math.PI / 180), Math.Sin(e * Math.PI / 180)];
            var u = U(a1, e1); var v = U(a2, e2);
            return Math.Acos(Math.Clamp(u[0] * v[0] + u[1] * v[1] + u[2] * v[2], -1, 1)) * 180 / Math.PI;
        }
    }
}