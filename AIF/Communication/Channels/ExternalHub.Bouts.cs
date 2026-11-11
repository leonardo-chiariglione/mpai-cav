using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIF.Bout;

namespace AIF.Channels;

// BOUTS ON THE EXTERNAL PORTS (MPAI-AIF V3.0, Communication 9.1; Basic API 5.5 to 5.7; MPAI-PTF V1.0, Verification Pipeline 11).
//   Giving:    the Controller gives what an External Output Port of its Module gives in a Bout to the Controllers linked whose AIM Metadata states that
//              their External Input Port of that Data Type and Port Number accepts Bouts (Bout: true); to the others, full instances. The Bout is made from
//              the first Message of the Port, which is given whole; it is given to a Controller before the first Bout Instance it is given, and again to
//              one that asks for it. A Bout Instance travels as the binary body of a frame of the link, not inside JSON text.
//   Receiving: a Bout from the Controller at the other end of the link is accepted (AcceptBout) where the Port accepts Bouts; each Bout Instance is
//              checked and decompressed, and the instances it delivers are given on as any Message of that Controller.
public sealed partial class ExternalHub
{
    private readonly ConcurrentDictionary<string, BoutGiver> boutGivers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> boutGivenTo = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BoutReceiver> boutReceivers = new(StringComparer.Ordinal);
    private long boutInstancesSent, boutInstancesReceived;

    public long BoutInstancesSent => Interlocked.Read(ref boutInstancesSent);
    public long BoutInstancesReceived => Interlocked.Read(ref boutInstancesReceived);

    private static string PortKey(string dataType, int portNumber) => $"{dataType}#{portNumber.ToString(CultureInfo.InvariantCulture)}";

    // Whether the Module whose AIM Metadata this is states, for its Port of that Direction, Data Type and Port Number, that it accepts Bouts.
    public static bool PortAcceptsBout(string? metadata, string direction, string dataType, int portNumber)
    {
        if (string.IsNullOrEmpty(metadata)) return false;
        try
        {
            if (JsonNode.Parse(metadata)?["ExternalPorts"] is not JsonArray ports) return false;
            foreach (var port in ports)
            {
                if ((string?)port?["Direction"] != direction) continue;
                var types = port["DataType"] is JsonArray set ? set.Select(t => (string?)t).ToList() : [(string?)port["DataType"]];
                if (!types.Contains(dataType)) continue;
                if ((BoutSecurity.AsLong(port["PortNumber"]) ?? 1) != portNumber) continue;
                if (port["Bout"] is JsonValue v && v.GetValueKind() == JsonValueKind.True) return true;
            }
        }
        catch (JsonException) { }
        return false;
    }

    private bool PeerAccepts(string peer, string dataType, int portNumber) =>
        PortAcceptsBout(controllerMetadata.GetValueOrDefault(peer), "Input", dataType, portNumber);

    // ---------------------------------------------------------------- MPAI_AIFM_External_Bout_Open (Basic API 5.5)
    // Refused where this Controller has no key to sign with, or where no Controller linked - or the one named - accepts Bouts on the Port: the
    // Messages are then given as full instances.
    public ExternalOutcome OpenBout(string dataType, int portNumber, string signingMode, int chainInterval, out int boutId, int? controllerId = null)
    {
        boutId = 0;
        if (options.BoutKey is null) return ExternalOutcome.Refused;
        if (signingMode is not (BoutSecurity.EachInstance or BoutSecurity.Chain) || chainInterval < 1) return ExternalOutcome.Failed;
        if (controllerId is { } cid)
        {
            if (!Peer(cid, out var name, out _)) return ExternalOutcome.NoSuchController;
            if (!PeerAccepts(name, dataType, portNumber)) return ExternalOutcome.Refused;
        }
        else if (!links.Keys.Any(id => PeerAccepts(id, dataType, portNumber))) return ExternalOutcome.Refused;

        var key = PortKey(dataType, portNumber);
        if (boutGivers.TryRemove(key, out var previous) && previous.Bout is not null) _ = SendCloseAsync(key, previous);
        boutId = 32768 + RandomNumberGenerator.GetInt32(1 << 30);
        boutGivers[key] = new BoutGiver(new BoutGiver.Options
        {
            DataType = dataType, PortNumber = portNumber, BoutId = boutId, SigningMode = signingMode, ChainInterval = chainInterval,
            Key = options.BoutKey, KeyId = options.ControllerId, Issuer = options.ControllerId, SourceAimId = options.ModuleType, Clock = options.Clock
        });
        boutGivenTo[key] = new(StringComparer.Ordinal);
        return ExternalOutcome.Ok;
    }

    // ---------------------------------------------------------------- MPAI_AIFM_External_Bout_Get (Basic API 5.6)
    // The Bout this Controller gives on the Port; or, for a controllerID, the Bout that Controller gives - asked of it where it is not held.
    public async Task<JsonObject?> GetBoutAsync(string dataType, int portNumber, int? controllerId = null)
    {
        if (controllerId is not { } cid) return boutGivers.TryGetValue(PortKey(dataType, portNumber), out var giver) ? giver.Bout : null;
        if (!Peer(cid, out var name, out var link)) return null;
        if (boutReceivers.TryGetValue(name + "|" + PortKey(dataType, portNumber), out var receiver) && receiver.Bout is { } held) return held;
        return await AskBoutAsync(name, link, dataType, portNumber) ? boutReceivers[name + "|" + PortKey(dataType, portNumber)].Bout : null;
    }

    // ---------------------------------------------------------------- MPAI_AIFM_External_Bout_Close (Basic API 5.7)
    public async Task<ExternalOutcome> CloseBoutAsync(string dataType, int portNumber)
    {
        var key = PortKey(dataType, portNumber);
        if (!boutGivers.TryRemove(key, out var giver) || giver.Bout is null) return ExternalOutcome.NotProduced;
        await SendCloseAsync(key, giver);
        return ExternalOutcome.Ok;
    }

    private async Task SendCloseAsync(string key, BoutGiver giver)
    {
        JsonObject closing;
        lock (giver) closing = giver.Close();
        if (!boutGivenTo.TryRemove(key, out var given)) return;
        foreach (var id in given.Keys)
            if (links.TryGetValue(id, out var link))
                try { await link.NoticeAsync(new JsonObject { ["Kind"] = "CloseBout", ["DataType"] = giver.Bout!["DataType"]!.DeepClone(), ["PortNumber"] = giver.Bout["PortNumber"]!.DeepClone(), ["Message"] = closing.DeepClone() }); }
                catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    // ---------------------------------------------------------------- giving
    // What an External Output Port gives, where a Bout is opened on it. true where it was sent here; false: not a Port with a Bout.
    private async Task<bool> SendInBoutAsync(string dataType, int portNumber, string json, DateTimeOffset stamp)
    {
        var key = PortKey(dataType, portNumber);
        if (!boutGivers.TryGetValue(key, out var giver) || JsonNode.Parse(json) is not JsonObject instance) return false;
        JsonObject? bout; byte[]? frame = null;
        lock (giver)
        {
            if (giver.Bout is null) bout = giver.Open(instance);
            else { bout = giver.Bout; frame = giver.Instance(instance); }
        }
        var given = boutGivenTo.GetOrAdd(key, _ => new(StringComparer.Ordinal));
        var concerned = Concerned(json, dataType, portNumber);
        var whole = new List<string>();
        foreach (var (id, link) in links)
        {
            if (!concerned.Contains(id)) continue;
            if (frame is null || !PeerAccepts(id, dataType, portNumber)) { whole.Add(id); continue; }
            try
            {
                if (given.TryAdd(id, 0)) await link.NoticeAsync(new JsonObject { ["Kind"] = "Bout", ["Bout"] = bout.DeepClone() });
                await link.NoticeAsync(RemoteLink.WithBody(new JsonObject { ["Kind"] = "BoutInstance", ["DataType"] = dataType, ["PortNumber"] = portNumber }, frame));
                Interlocked.Increment(ref sent);
                Interlocked.Increment(ref boutInstancesSent);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { Said?.Invoke($"{id}: not sent ({e.Message})"); }
        }
        // The first Message, from which the Bout is made, and every Message to a Controller that does not accept Bouts: whole, signed.
        if (whole.Count > 0)
        {
            var signed = Sign?.Invoke(json, BindingOf(dataType, portNumber, StampText(stamp))) ?? json;
            await SendToAsync(whole, dataType, portNumber, signed, stamp);
        }
        return true;
    }

    private async Task SendToAsync(IEnumerable<string> to, string dataType, int portNumber, string json, DateTimeOffset stamp)
    {
        var frame = new JsonObject
        {
            ["Kind"] = "External", ["DataType"] = dataType, ["PortNumber"] = portNumber, ["Json"] = json,
            ["Stamp"] = stamp.ToString("O", CultureInfo.InvariantCulture)
        };
        foreach (var id in to)
            if (links.TryGetValue(id, out var link))
                try { await link.NoticeAsync(frame.DeepClone().AsObject()); Interlocked.Increment(ref sent); }
                catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { Said?.Invoke($"{id}: not sent ({e.Message})"); }
    }

    // The Bout this Controller gives on a Port, for a Controller that asks for it.
    private JsonObject ServeBout(string from, JsonObject frame)
    {
        var dataType = (string?)frame["DataType"] ?? "";
        var portNumber = (int?)frame["PortNumber"] ?? 1;
        var key = PortKey(dataType, portNumber);
        if (!boutGivers.TryGetValue(key, out var giver) || giver.Bout is not { } bout || !PeerAccepts(from, dataType, portNumber))
            return Replied(ExternalOutcome.NotProduced);
        boutGivenTo.GetOrAdd(key, _ => new(StringComparer.Ordinal)).TryAdd(from, 0);
        var reply = Replied(ExternalOutcome.Ok);
        reply["Bout"] = bout.DeepClone();
        return reply;
    }

    // ---------------------------------------------------------------- receiving
    private BoutReceiver ReceiverFor(string from, string dataType, int portNumber) =>
        boutReceivers.GetOrAdd(from + "|" + PortKey(dataType, portNumber), _ => new BoutReceiver(new BoutReceiver.Options
        {
            DataType = dataType, PortNumber = portNumber,
            // Check 2 and 3: signed by the Controller at the other end of this link, with the key of its anchor.
            KeyFor = id => id == from ? options.PeerBoutKey?.Invoke(from) : null,
            MayGive = bout => BoutSecurity.KeyIdOf(bout) == from ? null : "the Bout is not signed by the Controller at the other end of the link",
            // Check 8: this Module accepts Bouts on the Port.
            Policy = _ => PortAcceptsBout(options.ModuleMetadata, "Input", dataType, portNumber) ? null : "the Port of this Module does not accept Bouts",
            Clock = options.Clock, Freshness = options.Freshness,
            Record = (operation, targetType, target, failure) => options.Decided?.Invoke(operation, targetType, target, failure)
        }));

    // A frame of the link about Bouts: true where it was one.
    private async Task<bool> ReceiveBoutFrameAsync(string from, JsonObject frame)
    {
        if ((string?)frame["Kind"] is not ("Bout" or "BoutInstance" or "CloseBout")) return false;
        // The frames of one link are taken one at a time: a Bout Instance is checked against the Sequence of the one before it.
        var gate = boutGates.GetOrAdd(from, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try { return await ReceiveBoutFrameInOrderAsync(from, frame); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Interlocked.Increment(ref refused);
            Said?.Invoke($"from {from}, Bout frame not taken: {e.GetType().Name}: {e.Message}");
            return true;
        }
        finally { gate.Release(); }
    }

    private readonly ConcurrentDictionary<string, SemaphoreSlim> boutGates = new(StringComparer.Ordinal);

    private async Task<bool> ReceiveBoutFrameInOrderAsync(string from, JsonObject frame)
    {
        switch ((string?)frame["Kind"])
        {
            case "Bout" when frame["Bout"] is JsonObject bout:
            {
                var dataType = (string?)bout["DataType"] ?? "";
                var portNumber = (int)(BoutSecurity.AsLong(bout["PortNumber"]) ?? 1);
                var accepted = ReceiverFor(from, dataType, portNumber).Accept(bout);
                if (!accepted.Ok) Said?.Invoke($"from {from}, Bout refused: {accepted.ErrorCode} {accepted.Reason}");
                return true;
            }
            case "BoutInstance":
            {
                var dataType = (string?)frame["DataType"] ?? "";
                var portNumber = (int?)frame["PortNumber"] ?? 1;
                if (RemoteLink.BodyOf(frame) is not { } body) return true;
                var receiver = ReceiverFor(from, dataType, portNumber);
                // A receiver that does not hold the Bout asks the Controller that gives the flow for it (Communication 9.1, item 5).
                if (receiver.Bout is null && links.TryGetValue(from, out var link) && !await AskBoutAsync(from, link, dataType, portNumber))
                {
                    Interlocked.Increment(ref refused);
                    return true;
                }
                var got = receiver.Receive(body);
                if (!got.Ok)
                {
                    Interlocked.Increment(ref refused);
                    Said?.Invoke($"from {from}, Bout Instance refused: {got.ErrorCode} {got.Reason}");
                    return true;
                }
                await DeliverAll(from, dataType, portNumber, got.Delivered);
                return true;
            }
            case "CloseBout" when frame["Message"] is JsonObject message:
            {
                var dataType = (string?)frame["DataType"] ?? "";
                var portNumber = (int?)frame["PortNumber"] ?? 1;
                if (boutReceivers.TryRemove(from + "|" + PortKey(dataType, portNumber), out var receiver))
                {
                    var ended = receiver.Close(message);
                    await DeliverAll(from, dataType, portNumber, ended.Delivered);
                    if (!ended.Ok) Said?.Invoke($"from {from}, CloseBout: {ended.Reason}");
                }
                return true;
            }
        }
        return false;
    }

    private async Task<bool> AskBoutAsync(string from, RemoteLink link, string dataType, int portNumber)
    {
        var reply = await AskAsync(link, new JsonObject { ["Kind"] = "BoutGet", ["DataType"] = dataType, ["PortNumber"] = portNumber }, 5000);
        return reply?["Bout"] is JsonObject bout && ReceiverFor(from, dataType, portNumber).Accept(bout).Ok;
    }

    // Each instance a Bout delivered, given on as a Message of the Controller it came from, with the stamp its Binding states.
    private async Task DeliverAll(string from, string dataType, int portNumber, IReadOnlyList<JsonObject> delivered)
    {
        foreach (var instance in delivered)
        {
            Interlocked.Increment(ref received);
            Interlocked.Increment(ref boutInstancesReceived);
            var stamp = BoutSecurity.ParseBinding(BoutSecurity.BindingOf(instance))?.Stamp ?? options.Clock();
            await Deliver(new ExternalMessage(dataType, portNumber, instance.ToJsonString(), from, stamp, null, null));
        }
    }
}
