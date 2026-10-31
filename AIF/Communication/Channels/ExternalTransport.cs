using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace AIF.Channels;

// THE EXTERNAL TRANSPORT (M3205 3.3, Between Controllers; M3241 3.2). A Controller
// discovers the Controllers in range running the same type of Module and exchanges
// Messages with their External Ports: what an External Output Port of its Module
// gives is sent to every Controller linked, and what they send is written to the
// External Input Port of the same Data Type and Port Number, with the writer's
// controllerID and stamp. A controllerID is valid from the discovery that returned
// it until that Controller leaves range - it is no longer in range, or no longer
// announces itself - and the link is then closed.

// What a Controller says of itself to those in range.
public sealed record Announcement(string ControllerId, string ModuleType, string Host, int Port);

// WHERE ANNOUNCEMENTS TRAVEL: the radio of a vehicle; on one machine, its network.
public interface IDiscoveryMedium : IAsyncDisposable
{
    Task AnnounceAsync(Announcement announcement, CancellationToken cancel);
    event Action<Announcement>? Heard;
}

// ANNOUNCEMENTS BY UDP MULTICAST on the machine's network: every Controller on it
// hears every other; which of them are in range is the hub's to decide.
public sealed class UdpDiscovery : IDiscoveryMedium
{
    public const string DefaultGroup = "239.255.41.10";
    private readonly UdpClient receiver;
    private readonly UdpClient sender;
    private readonly IPEndPoint group;
    private readonly CancellationTokenSource stop = new();
    private readonly Task listening;
    public event Action<Announcement>? Heard;

    public UdpDiscovery(int port, string groupAddress = DefaultGroup)
    {
        group = new IPEndPoint(IPAddress.Parse(groupAddress), port);
        receiver = new UdpClient(AddressFamily.InterNetwork);
        receiver.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        receiver.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        receiver.JoinMulticastGroup(group.Address, IPAddress.Loopback);
        receiver.MulticastLoopback = true;
        sender = new UdpClient(AddressFamily.InterNetwork) { MulticastLoopback = true };
        sender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.Loopback.GetAddressBytes());
        listening = Task.Run(ListenAsync);
    }

    public async Task AnnounceAsync(Announcement a, CancellationToken cancel)
    {
        var bytes = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["Kind"] = "MPAI-AIF-Announcement", ["ControllerId"] = a.ControllerId, ["ModuleType"] = a.ModuleType, ["Host"] = a.Host, ["Port"] = a.Port
        }.ToJsonString());
        await sender.SendAsync(bytes, group, cancel);
    }

    private async Task ListenAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var got = await receiver.ReceiveAsync(stop.Token);
                var o = JsonNode.Parse(Encoding.UTF8.GetString(got.Buffer)) as JsonObject;
                if ((string?)o?["Kind"] != "MPAI-AIF-Announcement") continue;
                Heard?.Invoke(new Announcement((string)o!["ControllerId"]!, (string)o["ModuleType"]!, (string)o["Host"]!, (int)o["Port"]!));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) when (e is SocketException or System.Text.Json.JsonException or InvalidOperationException or FormatException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        receiver.Dispose();
        sender.Dispose();
        try { await listening; } catch { }
    }
}

// A Message received on an External Input Port: its writer and the writer's stamp.
// A payload that travels apart from its Message (Communication 7): the reference the
// writing Controller issued, and the length and the hash its signed Message states.
public sealed record PayloadDescriptor(string Reference, long Length, string Hash);

// payloads: what the Message states of the payloads that did not travel in it; fetch:
// obtains one on the payload channel of the link, checked against its length and hash
// (null where it cannot be obtained or does not match).
public sealed record ExternalMessage(string DataType, int PortNumber, string Json, string From, DateTimeOffset Stamp,
                                     IReadOnlyList<PayloadDescriptor>? Payloads = null, Func<PayloadDescriptor, byte[]?>? Fetch = null);

// THE HUB OF ONE MODULE: its announcement, its links, its External Ports.
public sealed class ExternalHub : IAsyncDisposable
{
    public sealed record Options
    {
        public required string ControllerId { get; init; }
        public required string ModuleType { get; init; }
        public required IDiscoveryMedium Discovery { get; init; }
        public required ILinkAdmission Admission { get; init; }

        // Whether a Controller is in range: the radio's; in a simulation, its distance.
        public Func<string, bool> InRange { get; init; } = _ => true;
        public TimeSpan AnnounceEvery { get; init; } = TimeSpan.FromMilliseconds(250);
        public TimeSpan LeaveAfter { get; init; } = TimeSpan.FromSeconds(3);
        public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

        // Where other Controllers reach this one.
        public string Host { get; init; } = "127.0.0.1";

        // Whether a link is given a payload channel beside it (Communication 4).
        public bool PayloadChannel { get; init; } = true;

        // How long a payload offered on the payload channel waits to be fetched.
        public TimeSpan PayloadKeep { get; init; } = TimeSpan.FromSeconds(60);

        // WHERE A SERVICE ANSWERS MANY CLIENTS (a Health Back End; the server of a videoconference):
        // a Message its Module gives goes only to the Controllers it concerns - the one that sent the
        // Message it answers, found by an identifier the two Messages share, and any Controller the
        // Message names by its controllerID. One that concerns none is not sent, and counted. Without
        // it, as in a swarm, an External Output Port gives to every Controller linked.
        public bool DirectedReplies { get; init; }

        // The members of a Message by which an answer is matched to the request it answers (DirectedReplies): the Message's own identifier - RegisterID, AIHDataProcessID - and not
        // those of the subjects it concerns (UserID): a question the Controller's own User Agent asks about a subject is not answered to that subject's Controller.
        public IReadOnlyList<string> CorrelateBy { get; init; } = [];

        // The members of a Message, at any depth, by which it names the Controller it is for - the Federated Learn request names the Front End by its HFEID. Only these name a Controller: a value
        // that happens to equal a controllerID elsewhere in a Message (a user, a licensee) addresses no one.
        public IReadOnlyList<string> AddressedBy { get; init; } = [];

        // A client of a service has one Controller to speak to: what its Module gives waits, in the Module, until that Controller is linked - not sent to nobody.
        public bool HoldUntilLinked { get; init; }
    }

    // What the hub gives its Module (writes to an External Input Port), and the
    // External Output Ports it reads: (Data Type, Port Number) pairs, and a reader
    // that waits up to the timeout for the next Message of one of them.
    public required Func<ExternalMessage, Task> Deliver { get; init; }
    public IReadOnlyList<(string DataType, int PortNumber)> Outputs { get; init; } = [];

    // THE CONTROLLER'S TRUST (M3241 3.1, 3.3): what its Module sends signed; what it
    // receives verified against the Controller the link admitted - null where it is
    // accepted, else why not; refused, it is said and given to no one.
    public Func<string, string>? Sign { get; init; }
    public Func<string, string, string?>? Verify { get; init; }
    public long Refused => Interlocked.Read(ref refused);
    private long refused;
    public Func<string, int, int, Task<(string Json, DateTimeOffset Stamp)?>>? ReadOutput { get; init; }

    // THE PAYLOAD CHANNEL (Communication 4, 7): ReadOutputApart reads what an External
    // Output Port gives with the references of its payloads kept, and PayloadSource gives
    // their data; SignPayloads signs the Message with the length and hash of each, so
    // that the receiving Controller can check what it fetches. Where any is missing, or
    // a link has no payload channel, payloads travel in the Message.
    public Func<string, int, int, Task<(string Json, DateTimeOffset Stamp)?>>? ReadOutputApart { get; init; }
    public IPayloadSource? PayloadSource { get; init; }
    public Func<string, IReadOnlyList<PayloadDescriptor>, string>? SignPayloads { get; init; }
    // WHAT THE CONTROLLER CANNOT ROUTE (DirectedReplies): a Message its Module gave that concerns no Controller linked - an answer to a client that is not a Controller of the service. Where the
    // Controller says what to do with it, it is given to the User Agent that serves that client; otherwise it is not sent, and counted.
    public Func<string, int, string, Task>? Unrouted { get; init; }

    // Messages not sent because they concern no Controller linked (DirectedReplies).
    public long Undirected => Interlocked.Read(ref undirected);
    private long undirected;
    public long Fetched => Interlocked.Read(ref fetched);
    public long Offered => Interlocked.Read(ref offeredCount);
    public IReadOnlyCollection<string> PayloadLinked => payloadLinks.Keys.ToList();
    private long fetched, offeredCount;
    private readonly ConcurrentDictionary<string, RemoteLink> payloadLinks = new();

    // The identifiers of the Messages received, and the Controller each came from, kept for the
    // replies (DirectedReplies); the oldest are forgotten past a bound.
    private const int RoutesKept = 20000;
    private readonly ConcurrentDictionary<string, string> routes = new();
    private readonly ConcurrentQueue<string> routeOrder = new();

    // The values of the members named, at the top of a Message: the identifiers by which an answer is matched to its request.
    private IEnumerable<string> CorrelatorsIn(string json)
    {
        if (options.CorrelateBy.Count == 0) return [];
        try
        {
            return JsonNode.Parse(json) is JsonObject o
                ? options.CorrelateBy.Select(name => o[name] is JsonValue v && v.TryGetValue<string>(out var id) && id.Length > 0 ? id : null).OfType<string>().ToList()
                : [];
        }
        catch (System.Text.Json.JsonException) { return []; }
    }

    // The values of the members that name a Controller, at any depth, outside the signature.
    private IEnumerable<string> AddresseesIn(string json)
    {
        var found = new List<string>();
        if (options.AddressedBy.Count == 0) return found;
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var (name, child) in o)
                    {
                        if (name == "DataXMData") continue;
                        if (options.AddressedBy.Contains(name) && child is JsonValue v && v.TryGetValue<string>(out var text)) found.Add(text);
                        Walk(child);
                    }
                    break;
                case JsonArray a:
                    foreach (var child in a) Walk(child);
                    break;
            }
        }
        try { Walk(JsonNode.Parse(json)); } catch (System.Text.Json.JsonException) { }
        return found;
    }

    private void Remember(string json, string from)
    {
        foreach (var id in CorrelatorsIn(json))
            if (routes.TryAdd(id, from)) { routeOrder.Enqueue(id); if (routeOrder.Count > RoutesKept && routeOrder.TryDequeue(out var old)) routes.TryRemove(old, out _); }
            else routes[id] = from;
    }

    // The Controllers a Message concerns; every Controller where replies are not directed.
    private IReadOnlyCollection<string> Concerned(string json, string? dataType = null, int portNumber = 1)
    {
        if (!options.DirectedReplies) return links.Keys.ToList();
        var to = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in AddresseesIn(json)) if (links.ContainsKey(text)) to.Add(text);                 // it names a Controller
        foreach (var id in CorrelatorsIn(json)) if (routes.TryGetValue(id, out var from) && links.ContainsKey(from)) to.Add(from);   // it answers a Message of one
        if (to.Count == 0)
        {
            if (Unrouted is not null && dataType is not null) _ = Unrouted(dataType, portNumber, json);
            else { Interlocked.Increment(ref undirected); Said?.Invoke("a Message concerns no Controller linked: not sent"); }
        }
        return to;
    }
    private readonly ConcurrentDictionary<string, (byte[] Data, DateTimeOffset Until)> offered = new();

    private readonly Options options;
    private readonly RemoteLink.Listener listener;
    private readonly ConcurrentDictionary<string, RemoteLink> links = new();
    private readonly ConcurrentDictionary<string, (Announcement Said, DateTimeOffset Heard)> heard = new();
    private readonly ConcurrentDictionary<string, byte> opening = new();
    private readonly CancellationTokenSource stop = new();
    private readonly List<Task> running = new();

    // What happened, for whoever watches: a Controller linked, left, refused.
    public event Action<string>? Said;
    public IReadOnlyCollection<string> Linked => links.Keys.ToList();
    public long Sent => Interlocked.Read(ref sent);
    public long Received => Interlocked.Read(ref received);
    private long sent, received;

    public ExternalHub(Options options)
    {
        this.options = options;
        listener = new RemoteLink.Listener(0, options.Admission, Accepted);
        listener.Failed += why => Said?.Invoke($"a Controller not linked: {why}");
        options.Discovery.Heard += Heard;
    }

    public void Start()
    {
        running.Add(Task.Run(AnnounceAsync));
        running.Add(Task.Run(WatchAsync));
        foreach (var (dataType, portNumber) in Outputs) running.Add(Task.Run(() => PumpAsync(dataType, portNumber)));
    }

    private async Task AnnounceAsync()
    {
        var me = new Announcement(options.ControllerId, options.ModuleType, options.Host, listener.Port);
        while (!stop.IsCancellationRequested)
        {
            try { await options.Discovery.AnnounceAsync(me, stop.Token); } catch (Exception) when (!stop.IsCancellationRequested) { }
            try { await Task.Delay(options.AnnounceEvery, stop.Token); } catch (OperationCanceledException) { }
        }
    }

    private void Heard(Announcement a)
    {
        if (a.ControllerId == options.ControllerId || a.ModuleType != options.ModuleType) return;
        heard[a.ControllerId] = (a, options.Clock());
    }

    // WHO IS IN RANGE, NOW: a Controller heard lately and in range is linked - by the
    // one of the two whose controllerID is lower, so that there is one link; one no
    // longer heard, or no longer in range, is left.
    private async Task WatchAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            var now = options.Clock();
            foreach (var (id, (said, at)) in heard)
            {
                var present = now - at <= options.LeaveAfter && options.InRange(id);
                if (!present && links.TryRemove(id, out var gone))
                {
                    Said?.Invoke($"{id} left");
                    await gone.DisposeAsync();
                }
                else if (present && !links.ContainsKey(id) && string.CompareOrdinal(options.ControllerId, id) < 0 && opening.TryAdd(id, 0))
                    _ = ConnectAsync(said);
            }
            try { await Task.Delay(100, stop.Token); } catch (OperationCanceledException) { }
        }
    }

    private async Task ConnectAsync(Announcement to)
    {
        try
        {
            var link = await RemoteLink.ConnectAsync(to.Host, to.Port, options.Admission, stop.Token);
            // The link is ours, with its handlers, before the other end hears from us: what it sends the moment it has heard must find them set.
            Join(to.ControllerId, link);
            await link.NoticeAsync(new JsonObject { ["Kind"] = "Hello", ["ControllerId"] = options.ControllerId });
            if (options.PayloadChannel && ReadOutputApart is not null) await OpenPayloadChannelAsync(to, link);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or SocketException)
        {
            Said?.Invoke($"{to.ControllerId} not linked: {e.Message}");
        }
        catch (OperationCanceledException) { }
        finally { opening.TryRemove(to.ControllerId, out _); }
    }

    // THE PAYLOAD CHANNEL OF A LINK: a second TLS connection, opened by the same Controller
    // that opened the link, admitted by the same credentials. It carries requests for a
    // payload and the payloads, raw; no Message.
    private async Task OpenPayloadChannelAsync(Announcement to, RemoteLink main)
    {
        try
        {
            var channel = await RemoteLink.ConnectAsync(to.Host, to.Port, options.Admission, stop.Token);
            AddPayloadLink(to.ControllerId, channel, main);
            await channel.NoticeAsync(new JsonObject { ["Kind"] = "PayloadChannel", ["ControllerId"] = options.ControllerId });
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or SocketException)
        {
            Said?.Invoke($"{to.ControllerId}: no payload channel ({e.Message})");
        }
    }

    private void AddPayloadLink(string id, RemoteLink channel, RemoteLink main)
    {
        if (!payloadLinks.TryAdd(id, channel)) { _ = channel.DisposeAsync().AsTask(); return; }
        channel.OnRequest = ServePayload;
        channel.Lost += why => { payloadLinks.TryRemove(new KeyValuePair<string, RemoteLink>(id, channel)); };
        main.Lost += why => { if (payloadLinks.TryRemove(new KeyValuePair<string, RemoteLink>(id, channel))) _ = channel.DisposeAsync().AsTask(); };
        Said?.Invoke($"{id} payload channel");
    }

    private Task<JsonObject?> ServePayload(JsonObject request)
    {
        var reference = (string?)request["Reference"];
        if ((string?)request["Kind"] != "PayloadGet" || reference is null || !offered.TryGetValue(reference, out var held))
            return Task.FromResult<JsonObject?>(new JsonObject { ["Ok"] = false });
        Interlocked.Increment(ref fetched);
        return Task.FromResult<JsonObject?>(RemoteLink.WithBody(new JsonObject { ["Ok"] = true }, held.Data));
    }

    // The payloads a signed Message states travel apart (DataXMData, Security, Integrity).
    private static IReadOnlyList<PayloadDescriptor> PayloadsStated(string json) =>
        (JsonNode.Parse(json)?["DataXMData"]?["Security"]?["Integrity"]?["Payloads"] as JsonArray)?
            .Select(p => new PayloadDescriptor((string)p!["Reference"]!, (long)p["Length"]!, (string)p["Hash"]!)).ToList() ?? [];

    // One payload of a Message, fetched on the payload channel of the link it came on,
    // and checked against the length and the hash the signed Message states.
    private byte[]? Fetch(string from, PayloadDescriptor wanted)
    {
        if (!payloadLinks.TryGetValue(from, out var channel)) return null;
        byte[]? data;
        try
        {
            var reply = channel.RequestAsync(new JsonObject { ["Kind"] = "PayloadGet", ["Reference"] = wanted.Reference }).GetAwaiter().GetResult();
            data = reply["Ok"]?.GetValue<bool>() == true ? RemoteLink.BodyOf(reply) : null;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { data = null; }
        if (data is null) { Said?.Invoke($"from {from}, payload {wanted.Reference} not obtained"); return null; }
        if (data.LongLength != wanted.Length || !string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)), wanted.Hash, StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref refused);
            Said?.Invoke($"from {from}, payload {wanted.Reference} refused: it is not the one the Message states");
            return null;
        }
        return data;
    }

    // A link another Controller opened: it names itself first.
    private void Accepted(RemoteLink link)
    {
        link.OnNotice = frame =>
        {
            if ((string?)frame["Kind"] == "PayloadChannel" && (string?)frame["ControllerId"] is { } payloadFrom)
            {
                // Only the Controller the link admitted may open the payload channel in its name.
                if ((link.Peer is null || link.Peer == payloadFrom) && links.TryGetValue(payloadFrom, out var main)) AddPayloadLink(payloadFrom, link, main);
                else { Said?.Invoke($"{payloadFrom} payload channel refused"); _ = link.DisposeAsync().AsTask(); }
            }
            else if ((string?)frame["Kind"] == "Hello" && (string?)frame["ControllerId"] is { } id)
            {
                if (options.InRange(id)) Join(id, link);
                else { Said?.Invoke($"{id} refused: not in range"); _ = link.DisposeAsync().AsTask(); }
            }
            return Task.CompletedTask;
        };
    }

    private void Join(string id, RemoteLink link)
    {
        if (!links.TryAdd(id, link)) { _ = link.DisposeAsync().AsTask(); return; }
        link.OnNotice = Receive(id);
        link.Lost += _ => { if (links.TryRemove(new KeyValuePair<string, RemoteLink>(id, link))) Said?.Invoke($"{id} lost"); };
        Said?.Invoke($"{id} linked");
    }

    private Func<JsonObject, Task> Receive(string from) => async frame =>
    {
        if ((string?)frame["Kind"] != "External") return;
        Interlocked.Increment(ref received);
        if (Verify?.Invoke(from, (string)frame["Json"]!) is { } why)
        {
            Interlocked.Increment(ref refused);
            Said?.Invoke($"from {from}, refused: {why}");
            return;
        }
        var json = (string)frame["Json"]!;
        if (options.DirectedReplies) Remember(json, from);
        // What the Message states of its payloads: from the signed Message where it is
        // verified, else from the frame.
        IReadOnlyList<PayloadDescriptor> apart = Verify is not null
            ? PayloadsStated(json)
            : (frame["Payloads"] as JsonArray)?.Select(p => new PayloadDescriptor((string)p!["Reference"]!, (long)p["Length"]!, (string)p["Hash"]!)).ToList() ?? [];
        await Deliver(new ExternalMessage((string)frame["DataType"]!, (int)frame["PortNumber"]!, json, from,
                                          DateTimeOffset.Parse((string)frame["Stamp"]!, System.Globalization.CultureInfo.InvariantCulture),
                                          apart.Count > 0 ? apart : null, d => Fetch(from, d)));
    };

    // WHAT AN EXTERNAL OUTPUT PORT GIVES, to every Controller linked.
    private async Task PumpAsync(string dataType, int portNumber)
    {
        while (!stop.IsCancellationRequested)
        {
            (string Json, DateTimeOffset Stamp)? next;
            if (options.HoldUntilLinked && links.IsEmpty) { try { await Task.Delay(50, stop.Token); } catch (OperationCanceledException) { } continue; }
            var apart = ReadOutputApart is not null && PayloadSource is not null && options.PayloadChannel;
            try { next = await (apart ? ReadOutputApart! : ReadOutput!)(dataType, portNumber, 200); }
            catch (Exception) when (stop.IsCancellationRequested) { break; }
            if (next is not { } m) continue;
            if (apart) { await SendWithPayloadsAsync(dataType, portNumber, m.Json, m.Stamp); continue; }
            await SendAsync(dataType, portNumber, Sign?.Invoke(m.Json) ?? m.Json, m.Stamp);
        }
    }

    // A MESSAGE WHOSE PAYLOADS WERE WRITTEN BY REFERENCE (Communication 7): to a Controller
    // with a payload channel it goes with the references, signed with the length and the
    // hash of each payload, which wait to be fetched; to any other, with the data inlined.
    private async Task SendWithPayloadsAsync(string dataType, int portNumber, string json, DateTimeOffset stamp)
    {
        var source = PayloadSource!;
        var references = source.ReferencesOf(json);
        var descriptors = new List<PayloadDescriptor>();
        var until = options.Clock() + options.PayloadKeep;
        foreach (var r in references)
            if (source.Get(r) is { } data)
            {
                offered[r] = (data, until);
                descriptors.Add(new PayloadDescriptor(r, data.LongLength, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data))));
                Interlocked.Increment(ref offeredCount);
            }
        foreach (var gone in offered.Where(o => o.Value.Until < options.Clock()).Select(o => o.Key).ToList()) offered.TryRemove(gone, out _);

        var apartJson = descriptors.Count == 0 ? json : SignPayloads is not null ? SignPayloads(json, descriptors) : Sign?.Invoke(json) ?? json;
        var inlined = descriptors.Count == 0 || links.Keys.Any(id => !payloadLinks.ContainsKey(id)) ? source.Inline(json) : null;
        // Released for this reader once: by Inline where it ran, else here.
        if (inlined is null) foreach (var r in references) source.Release(r);
        var inlineJson = inlined is null ? null : Sign?.Invoke(inlined) ?? inlined;

        var concerned = Concerned(json, dataType, portNumber);
        foreach (var (id, link) in links)
        {
            if (!concerned.Contains(id)) continue;
            var viaChannel = descriptors.Count > 0 && payloadLinks.ContainsKey(id);
            var frame = new JsonObject
            {
                ["Kind"] = "External", ["DataType"] = dataType, ["PortNumber"] = portNumber, ["Json"] = viaChannel ? apartJson : inlineJson,
                ["Stamp"] = stamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            };
            if (viaChannel)
                frame["Payloads"] = new JsonArray(descriptors.Select(d => (JsonNode)new JsonObject { ["Reference"] = d.Reference, ["Length"] = d.Length, ["Hash"] = d.Hash }).ToArray());
            try { await link.NoticeAsync(frame); Interlocked.Increment(ref sent); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { Said?.Invoke($"{id}: not sent ({e.Message})"); }
        }
    }

    public async Task SendAsync(string dataType, int portNumber, string json, DateTimeOffset stamp)
    {
        var frame = new JsonObject
        {
            ["Kind"] = "External", ["DataType"] = dataType, ["PortNumber"] = portNumber, ["Json"] = json,
            ["Stamp"] = stamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        };
        var concerned = Concerned(json, dataType, portNumber);
        foreach (var (id, link) in links)
        {
            if (!concerned.Contains(id)) continue;
            try { await link.NoticeAsync(frame.DeepClone().AsObject()); Interlocked.Increment(ref sent); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { Said?.Invoke($"{id}: not sent ({e.Message})"); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        options.Discovery.Heard -= Heard;
        try { await Task.WhenAll(running); } catch { }
        foreach (var link in links.Values) await link.DisposeAsync();
        links.Clear();
        foreach (var channel in payloadLinks.Values) await channel.DisposeAsync();
        payloadLinks.Clear();
        await listener.DisposeAsync();
    }
}
