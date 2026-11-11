using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AIF.Bout;
using AIF.Channels;
using Xunit;

namespace Mpai.Aif.Tests;

// BOUTS BETWEEN CONTROLLERS (MPAI-AIF V3.0, Communication 9.1; Basic API 5.5 to 5.7). Three Controllers linked: A gives on its External Output Port;
// B's Module states that its External Input Port accepts Bouts, C's does not. B is given a Bout and Bout Instances; C full instances.
[Collection(Timing.Name)]
public sealed class ExternalBoutTests
{
    private const string Era = "CAV-ERA-V2.0", Ams = "CAV-AMS-V2.0";

    private static string Metadata(bool acceptsBout) => new JsonObject
    {
        ["Header"] = Ams,
        ["ExternalPorts"] = new JsonArray(
            new JsonObject { ["Direction"] = "Input", ["DataType"] = Era, ["PortNumber"] = 1, ["IsRemote"] = true, ["Bout"] = acceptsBout },
            new JsonObject { ["Direction"] = "Output", ["DataType"] = Era, ["PortNumber"] = 1, ["IsRemote"] = true })
    }.ToJsonString();

    private sealed class Cav : IAsyncDisposable
    {
        public readonly Channel<(string Json, DateTimeOffset Stamp)> Gives = Channel.CreateUnbounded<(string, DateTimeOffset)>();
        public readonly ConcurrentQueue<ExternalMessage> Got = new();
        public readonly ExternalHub Hub;
        public readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public readonly ConcurrentQueue<string> Said = new();

        public Cav(string id, UdpDiscovery discovery, bool acceptsBout, Func<string, ECDsa?> keys)
        {
            Hub = new ExternalHub(new ExternalHub.Options
            {
                ControllerId = id, ModuleType = Ams, Discovery = discovery, Admission = new KeyAdmission("city"),
                AnnounceEvery = TimeSpan.FromMilliseconds(100), LeaveAfter = TimeSpan.FromSeconds(2),
                ModuleMetadata = Metadata(acceptsBout), BoutKey = Key, PeerBoutKey = other => keys(other) is { } k ? (BoutKey)k : null
            })
            {
                Outputs = [(Era, 1)],
                ReadOutput = async (_, _, timeout) =>
                {
                    using var cancel = new CancellationTokenSource(timeout);
                    try { return await Gives.Reader.ReadAsync(cancel.Token); } catch (OperationCanceledException) { return null; }
                },
                Deliver = m => { Got.Enqueue(m); return Task.CompletedTask; }
            };
            Hub.Said += line => Said.Enqueue(line);
            Hub.Start();
        }

        public ValueTask DisposeAsync() => Hub.DisposeAsync();
    }

    private static async Task<bool> Until(Func<bool> condition, int ms)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition()) { if (clock.ElapsedMilliseconds > ms) return false; await Task.Delay(20); }
        return true;
    }

    private static string Message(string id) => new JsonObject { ["Header"] = Era, ["EgoRemoteAMSMessageID"] = id, ["Position"] = new JsonArray(1.5, 2.5, 0.0) }.ToJsonString();
    private static string? IdOf(ExternalMessage m) => (string?)JsonNode.Parse(m.Json)?["EgoRemoteAMSMessageID"];

    [Fact]
    public async Task ABoutIsGivenToTheControllerThatAcceptsIt_AndFullInstancesToTheOther()
    {
        var port = 43000 + Environment.ProcessId % 1000;
        var cavs = new ConcurrentDictionary<string, Cav>();
        ECDsa? KeyOf(string id) => cavs.TryGetValue(id, out var c) ? c.Key : null;
        await using var da = new UdpDiscovery(port);
        await using var db = new UdpDiscovery(port);
        await using var dc = new UdpDiscovery(port);
        await using var a = cavs["CAV-A"] = new Cav("CAV-A", da, acceptsBout: false, KeyOf);
        await using var b = cavs["CAV-B"] = new Cav("CAV-B", db, acceptsBout: true, KeyOf);
        await using var c = cavs["CAV-C"] = new Cav("CAV-C", dc, acceptsBout: false, KeyOf);

        Assert.True(await Until(() => a.Hub.List().Count == 2 && a.Hub.List().All(p => p.Metadata.Length > 0), 8000), "A is not linked to B and C");
        var b1 = a.Hub.List().Single(p => p.ControllerName == "CAV-B").ControllerID;
        var c1 = a.Hub.List().Single(p => p.ControllerName == "CAV-C").ControllerID;

        // C does not accept Bouts: refused for it (NOT_AUTHORISED); B does.
        Assert.Equal(ExternalOutcome.Refused, a.Hub.OpenBout(Era, 1, BoutSecurity.EachInstance, 1, out _, c1));
        Assert.Equal(ExternalOutcome.Ok, a.Hub.OpenBout(Era, 1, BoutSecurity.EachInstance, 1, out var boutId, b1));

        for (var k = 1; k <= 4; k++) await a.Gives.Writer.WriteAsync((Message($"M{k}"), DateTimeOffset.UtcNow));
        Assert.True(await Until(() => b.Got.Count == 4 && c.Got.Count == 4, 8000), $"B got {string.Join(",", b.Got.Select(IdOf))}, C got {c.Got.Count}; A sent {a.Hub.BoutInstancesSent} frames, B received {b.Hub.BoutInstancesReceived}, B refused {b.Hub.Refused}; B said: {string.Join(" | ", b.Said)}; A said: {string.Join(" | ", a.Said)}");

        Assert.Equal(["M1", "M2", "M3", "M4"], b.Got.Select(IdOf).ToArray());
        Assert.Equal(["M1", "M2", "M3", "M4"], c.Got.Select(IdOf).ToArray());
        Assert.Equal(3, a.Hub.BoutInstancesSent);               // the first Message made the Bout and went whole
        Assert.Equal(3, b.Hub.BoutInstancesReceived);
        Assert.Equal(0, c.Hub.BoutInstancesReceived);
        Assert.All(b.Got.Skip(1), m => Assert.EndsWith("/" + boutId, BoutSecurity.BindingOf(JsonNode.Parse(m.Json)!.AsObject())));

        var bout = await b.Hub.GetBoutAsync(Era, 1, b.Hub.List().Single(p => p.ControllerName == "CAV-A").ControllerID);
        Assert.Equal(boutId, (int)bout!["BoutID"]!);
        Assert.Equal(ExternalOutcome.Ok, await a.Hub.CloseBoutAsync(Era, 1));

        // After CloseBout, what A gives goes whole again.
        await a.Gives.Writer.WriteAsync((Message("M5"), DateTimeOffset.UtcNow));
        Assert.True(await Until(() => b.Got.Count == 5, 5000));
        Assert.Equal(3, b.Hub.BoutInstancesReceived);
    }
}
