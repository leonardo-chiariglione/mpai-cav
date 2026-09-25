using System.Text.Json.Nodes;

namespace AIF.Channels;

// REMOTE (M3205 3.6.2, M3217 3.2): Channel ends on another machine. The writer and
// the readers on its machine share the Channel as on any transport; each reader
// on another machine is reached through the link to that machine, where the
// Message is put into its reader end - with the behaviour its Input Port
// declares - and the putting acknowledged, so that a Block-ing reader holds the
// writer across the machines as it would on one. Messages travel as JSON, their
// payloads inlined; they are addressed by Module instance, AIM Instance, Data
// Type and Port Number, never by the route of any Service.
public sealed class RemoteTransport : ChannelTransport
{
    private readonly Func<string, string, RemoteLink?> linkFor;

    // linkFor: the link to the machine an AIM Instance of a Module instance is on;
    // null for this one. The boundary (an empty AIM) is the Controller's.
    public RemoteTransport(Func<string, string, RemoteLink?> linkFor, IClock? clock = null) : base(clock) => this.linkFor = linkFor;

    public RemoteTransport(Func<string, RemoteLink?> linkFor, IClock? clock = null) : this((_, aim) => linkFor(aim), clock) { }

    public override string Name => "Remote";

    // What a Message's JSON becomes as it leaves this machine: the payloads it
    // references inlined, by the store that holds them (M3215 3.6).
    public Func<ChannelSpec, string, string>? Leaving { get; set; }

    protected override bool IsHere(ChannelSpec spec, PortEnd reader) => linkFor(spec.Module, reader.Aim) is null;

    protected override RemoteDelivery Remote => DeliverAsync;

    // Both machines know a Channel before a Message crosses it: the Controller
    // sends its ChannelSpec with the AIM it places.
    public void Register(ChannelSpec spec) => Core(spec);

    public override IChannelWriter OpenWriter(ChannelSpec spec) => new Writer(Core(spec));

    private sealed class Writer(ChannelCore core) : IChannelWriter
    {
        public ChannelSpec Spec => core.Spec;
        public long Written => core.Written;

        public ValueTask<bool> WriteAsync(PortMessage message, int timeoutMs = -1, CancellationToken cancel = default)
        {
            core.Stamp(message);
            return core.DeliverAsync(message, timeoutMs, cancel);
        }
    }

    private async ValueTask<bool> DeliverAsync(ChannelSpec spec, PortEnd reader, PortMessage message, int timeoutMs, CancellationToken cancel)
    {
        // A link gone delivers nothing: the AIM at its end is DEGRADED by whoever
        // holds the link (M3217 3.2), not the writer.
        var link = linkFor(spec.Module, reader.Aim);
        if (link is null || link.IsClosed) return false;
        JsonObject reply;
        try
        {
            reply = await link.RequestAsync(new JsonObject
            {
                ["Kind"]       = "Message",
                ["Channel"]    = spec.Id,
                ["Reader"]     = End(reader),
                ["DataType"]   = message.DataType,
                ["PortNumber"] = message.PortNumber,
                ["Json"]       = Leaving?.Invoke(spec, message.Json) ?? message.Json,
                ["Stamp"]      = message.Stamp.ToString("O"),
                ["Sequence"]   = message.Sequence,
                ["Timeout"]    = timeoutMs
            }, cancel);
        }
        catch (Exception) when (link.IsClosed && !cancel.IsCancellationRequested)
        {
            return false;
        }
        return reply["Ok"]?.GetValue<bool>() == true;
    }

    // A Message for a reader on this machine, from a link.
    public async Task<JsonObject> ReceiveAsync(JsonObject frame)
    {
        var core = Core(frame["Channel"]!.GetValue<string>());
        if (core is null) return new JsonObject { ["Ok"] = false, ["Error"] = "unknown Channel" };
        var message = new PortMessage
        {
            DataType   = frame["DataType"]!.GetValue<string>(),
            PortNumber = frame["PortNumber"]!.GetValue<int>(),
            Json       = frame["Json"]!.GetValue<string>()
        };
        message.Stamp = DateTimeOffset.Parse(frame["Stamp"]!.GetValue<string>());
        message.Sequence = frame["Sequence"]!.GetValue<long>();
        var reader = EndOf(frame["Reader"]!);
        var timeout = frame["Timeout"]!.GetValue<int>();

        // For a reader on a third machine - an AIM on one host writing to an AIM
        // on another - the Controller passes it on.
        var ok = core.Readers.ContainsKey(reader) || linkFor(core.Spec.Module, reader.Aim) is null
            ? await core.ArrivedAsync(reader, message, timeout, CancellationToken.None)
            : await DeliverAsync(core.Spec, reader, message, timeout, CancellationToken.None);
        return new JsonObject { ["Ok"] = ok };
    }

    // A Channel as it travels to the machine of an AIM it touches.
    public static JsonObject SpecJson(ChannelSpec spec) => new()
    {
        ["Module"]    = spec.Module,
        ["Writer"]    = End(spec.Writer),
        ["Transport"] = spec.Transport,
        ["Readers"]   = new JsonArray(spec.Readers.Select(r => (JsonNode)new JsonObject
        {
            ["Reader"]   = End(r.Reader),
            ["Depth"]    = r.Behaviour.Depth,
            ["Overflow"] = r.Behaviour.Overflow.ToString(),
            ["MaxAge"]   = r.Behaviour.MaxAge?.TotalMilliseconds
        }).ToArray())
    };

    public static ChannelSpec SpecOf(JsonNode node) => new(
        node["Module"]!.GetValue<string>(),
        EndOf(node["Writer"]!),
        node["Readers"]!.AsArray().Select(r => new ChannelReaderSpec(EndOf(r!["Reader"]!), new PortBehaviour(
            r["Depth"]!.GetValue<int>(),
            Enum.Parse<Overflow>(r["Overflow"]!.GetValue<string>()),
            r["MaxAge"] is { } age ? TimeSpan.FromMilliseconds(age.GetValue<double>()) : null))).ToList(),
        node["Transport"]!.GetValue<string>());

    public static JsonObject End(PortEnd end) =>
        new() { ["Aim"] = end.Aim, ["DataType"] = end.DataType, ["PortNumber"] = end.PortNumber };

    public static PortEnd EndOf(JsonNode node) =>
        new(node["Aim"]!.GetValue<string>(), node["DataType"]!.GetValue<string>(), node["PortNumber"]!.GetValue<int>());
}
