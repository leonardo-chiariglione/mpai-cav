using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

using AIF.Channels;

namespace Mpai.Aif.Tests;

// PHASE 10, STEP 2 (M3241 3.2): THE EXTERNAL TRANSPORT between two Controllers on
// one machine. Each hub's Module stood in by a queue it reads (its External Output
// Port) and a list it writes (its External Input Port); announcements by UDP
// multicast on the machine's network; range decided by the test, as the simulation
// will. Judged: linked in range, both ways, the writer's controllerID and stamp
// carried; unlinked out of range, linked again when back; never linked to another
// type of Module, nor across admissions; one gone silent left. Reported: the time
// from an Output Port to the other's Input Port.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
[Collection(Timing.Name)]
public class ExternalTransportTests
{
    private const string Era = "CAV-ERA-V2.0", Ams = "CAV-AMS-V2.0";

    private sealed class Cav : IAsyncDisposable
    {
        public readonly Channel<(string Json, DateTimeOffset Stamp)> Gives = Channel.CreateUnbounded<(string, DateTimeOffset)>();
        public readonly ConcurrentQueue<(ExternalMessage Message, DateTimeOffset At)> Got = new();
        public readonly ExternalHub Hub;
        public readonly List<string> Said = [];

        public Cav(string id, string moduleType, string key, UdpDiscovery discovery, Func<string, bool> inRange)
        {
            Hub = new ExternalHub(new ExternalHub.Options
            {
                ControllerId = id, ModuleType = moduleType, Discovery = discovery, Admission = new KeyAdmission(key), InRange = inRange,
                AnnounceEvery = TimeSpan.FromMilliseconds(100), LeaveAfter = TimeSpan.FromSeconds(1)
            })
            {
                Outputs = [(Era, 1)],
                ReadOutput = async (_, _, timeout) =>
                {
                    using var cancel = new CancellationTokenSource(timeout);
                    try { return await Gives.Reader.ReadAsync(cancel.Token); } catch (OperationCanceledException) { return null; }
                },
                Deliver = m => { Got.Enqueue((m, DateTimeOffset.UtcNow)); return Task.CompletedTask; }
            };
            Hub.Said += line => { lock (Said) Said.Add(line); };
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

    private static string Message(string id) => JsonSerializer.Serialize(new { Header = Era, EgoRemoteAMSMessageID = id });

    [Fact]
    public async Task Step2BetweenControllers()
    {
        var result = new Dictionary<string, string>();
        var report = new Dictionary<string, string>();
        var port = 41000 + Environment.ProcessId % 1000;
        var inRange = new ConcurrentDictionary<string, bool>();
        bool Range(string me, string other) => inRange.GetValueOrDefault(me + "|" + other, true) && inRange.GetValueOrDefault(other + "|" + me, true);

        await using var discoveryA = new UdpDiscovery(port);
        await using var discoveryB = new UdpDiscovery(port);
        await using var discoveryC = new UdpDiscovery(port);
        await using var discoveryD = new UdpDiscovery(port);
        await using var a = new Cav("CAV-A", Ams, "city", discoveryA, id => Range("CAV-A", id));
        await using var b = new Cav("CAV-B", Ams, "city", discoveryB, id => Range("CAV-B", id));
        await using var ess = new Cav("CAV-C", "CAV-ESS-V2.0", "city", discoveryC, id => Range("CAV-C", id));
        await using var outsider = new Cav("CAV-0", Ams, "another city", discoveryD, id => Range("CAV-0", id));

        // In range: linked, both ways.
        var linked = await Until(() => a.Hub.Linked.Contains("CAV-B") && b.Hub.Linked.Contains("CAV-A"), 5000);
        result["two CAVs of a city, in range"] = linked ? "linked" : "not linked";

        var stamp = DateTimeOffset.UtcNow;
        var sentAt = System.Diagnostics.Stopwatch.StartNew();
        await a.Gives.Writer.WriteAsync((Message("A1"), stamp));
        var arrived = await Until(() => b.Got.Any(g => g.Message.Json.Contains("A1")), 3000);
        var latency = sentAt.Elapsed.TotalMilliseconds;
        var got = b.Got.FirstOrDefault(g => g.Message.Json.Contains("A1")).Message;
        result["A's Output Port to B's Input Port"] = !arrived ? "not delivered"
            : got.From == "CAV-A" && got.Stamp == stamp && got.DataType == Era && got.PortNumber == 1 ? "delivered, with A's controllerID and stamp" : $"delivered as {got}";
        await b.Gives.Writer.WriteAsync((Message("B1"), DateTimeOffset.UtcNow));
        result["B's Output Port to A's Input Port"] = await Until(() => a.Got.Any(g => g.Message.Json.Contains("B1") && g.Message.From == "CAV-B"), 3000) ? "delivered" : "not delivered";

        // Another type of Module, and another city: never linked.
        await Task.Delay(1500);
        result["a Controller of another type of Module"] = a.Hub.Linked.Contains("CAV-C") || ess.Hub.Linked.Count > 0 ? "linked" : "not linked";
        result["a CAV of another city"] = outsider.Hub.Linked.Count > 0 || a.Hub.Linked.Contains("CAV-0") || b.Hub.Linked.Contains("CAV-0") ? "linked" : "not linked";

        // Out of range: left, and nothing delivered; back in range: linked again.
        inRange["CAV-A|CAV-B"] = false;
        var left = await Until(() => !a.Hub.Linked.Contains("CAV-B") && !b.Hub.Linked.Contains("CAV-A"), 3000);
        await a.Gives.Writer.WriteAsync((Message("A2"), DateTimeOffset.UtcNow));
        await Task.Delay(500);
        result["out of range"] = left && !b.Got.Any(g => g.Message.Json.Contains("A2")) ? "left, nothing delivered" : "still linked";
        inRange["CAV-A|CAV-B"] = true;
        result["back in range"] = await Until(() => a.Hub.Linked.Contains("CAV-B") && b.Hub.Linked.Contains("CAV-A"), 5000) ? "linked again" : "not linked";

        // Gone silent: left.
        await b.DisposeAsync();
        result["a CAV gone silent"] = await Until(() => !a.Hub.Linked.Contains("CAV-B"), 3000) ? "left" : "still linked";

        report["time from A's Output Port to B's Input Port"] = $"{latency:0} ms";
        report["what A's hub said"] = string.Join(" | ", a.Said);
        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "external-transport.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Expected.Match("external-transport.json", result);
    }
}
