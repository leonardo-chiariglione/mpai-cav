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
    private readonly Func<string, RemoteLink?> linkFor;

    // linkFor: the link to the machine an AIM Instance is on; null for this one.
    // The boundary (an empty AIM) is always the Controller's.
    public RemoteTransport(Func<string, RemoteLink?> linkFor, IClock? clock = null) : base(clock) => this.linkFor = linkFor;

    public override string Name => "Remote";

    protected override bool IsHere(PortEnd reader) => linkFor(reader.Aim) is null;

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
        var link = linkFor(reader.Aim) ?? throw new InvalidOperationException($"No link to the machine of {reader}.");
        var reply = await link.RequestAsync(new JsonObject
        {
            ["Kind"]       = "Message",
            ["Channel"]    = spec.Id,
            ["Reader"]     = End(reader),
            ["DataType"]   = message.DataType,
            ["PortNumber"] = message.PortNumber,
            ["Json"]       = message.Json,
            ["Stamp"]      = message.Stamp.ToString("O"),
            ["Sequence"]   = message.Sequence,
            ["Timeout"]    = timeoutMs
        }, cancel);
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
        var ok = await core.ArrivedAsync(EndOf(frame["Reader"]!), message, frame["Timeout"]!.GetValue<int>(), CancellationToken.None);
        return new JsonObject { ["Ok"] = ok };
    }

    public static JsonObject End(PortEnd end) =>
        new() { ["Aim"] = end.Aim, ["DataType"] = end.DataType, ["PortNumber"] = end.PortNumber };

    public static PortEnd EndOf(JsonNode node) =>
        new(node["Aim"]!.GetValue<string>(), node["DataType"]!.GetValue<string>(), node["PortNumber"]!.GetValue<int>());
}
