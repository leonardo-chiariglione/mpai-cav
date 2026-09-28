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
            : this(id, moduleType, new KeyAdmission(key), discovery, inRange) { }

        public Cav(string id, string moduleType, ILinkAdmission admission, UdpDiscovery discovery, Func<string, bool> inRange,
                   Func<string, string>? sign = null, Func<string, string, string?>? verify = null)
        {
            Hub = new ExternalHub(new ExternalHub.Options
            {
                ControllerId = id, ModuleType = moduleType, Discovery = discovery, Admission = admission, InRange = inRange,
                AnnounceEvery = TimeSpan.FromMilliseconds(100), LeaveAfter = TimeSpan.FromSeconds(1)
            })
            {
                Outputs = [(Era, 1)],
                ReadOutput = async (_, _, timeout) =>
                {
                    using var cancel = new CancellationTokenSource(timeout);
                    try { return await Gives.Reader.ReadAsync(cancel.Token); } catch (OperationCanceledException) { return null; }
                },
                Deliver = m => { Got.Enqueue((m, DateTimeOffset.UtcNow)); return Task.CompletedTask; },
                Sign = sign, Verify = verify
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

    // STEP 3 (M3241 3.1, 3.3): THE TRUST AUTHORITY OF A CITY. Turin admits CAV-A and
    // CAV-B; Milan admits CAV-M; Turin admitted CAV-E yesterday for an hour; CAV-F
    // presents a credential in Turin's name from a key that is not Turin's. Every hub
    // signs what it sends and verifies what it receives; CAV-T alters what it sends
    // after signing it; CAV-I signs in CAV-A's name. Judged: A and B linked, their
    // Messages verified and given on; Milan's CAV, the expired and the forged
    // credentials never linked, each for its reason; the altered Message and the one
    // in another's name refused.
    [Fact]
    public async Task Step3ACityOfTrust()
    {
        var result = new Dictionary<string, string>();
        var port = 42000 + Environment.ProcessId % 1000;
        var turin = new AIF.Trust.TrustAuthority("Turin");
        var milan = new AIF.Trust.TrustAuthority("Milan");
        var impostor = new AIF.Trust.TrustAuthority("Turin");                  // another key, in Turin's name
        var discoveries = new List<UdpDiscovery>();
        UdpDiscovery Medium() { var d = new UdpDiscovery(port); discoveries.Add(d); return d; }
        var cavs = new Dictionary<string, Cav>();
        Cav Make(string id, AIF.Trust.TrustAuthority by, AIF.Trust.TrustAuthority.Admission admission, Func<string, string>? alter = null, string? signAs = null)
        {
            var trust = new AIF.Controller.CityTrust(by, admission);
            Func<string, string> sign = json =>
            {
                var signed = trust.Sign(json);
                if (signAs is not null) signed = signed.Replace($"\"KeyID\":\"{id}\"", $"\"KeyID\":\"{signAs}\"");
                return alter?.Invoke(signed) ?? signed;
            };
            return cavs[id] = new Cav(id, Ams, trust.Admission, Medium(), _ => true, sign, trust.Verify);
        }
        try
        {
            Make("CAV-A", turin, turin.Admit("CAV-A"));
            Make("CAV-B", turin, turin.Admit("CAV-B"));
            Make("CAV-M", milan, milan.Admit("CAV-M"));
            Make("CAV-E", turin, turin.Admit("CAV-E", TimeSpan.FromHours(1), DateTimeOffset.UtcNow.AddDays(-1)));
            Make("CAV-F", turin, impostor.Admit("CAV-F"));
            Make("CAV-T", turin, turin.Admit("CAV-T"), alter: json => json.Replace("\"T1\"", "\"T2\""));
            Make("CAV-I", turin, turin.Admit("CAV-I"), signAs: "CAV-A");

            var (a, b) = (cavs["CAV-A"], cavs["CAV-B"]);
            result["two CAVs admitted by Turin"] = await Until(() => a.Hub.Linked.Contains("CAV-B") && b.Hub.Linked.Contains("CAV-A"), 5000) ? "linked" : "not linked";
            await Until(() => b.Hub.Linked.Contains("CAV-T") && b.Hub.Linked.Contains("CAV-I"), 5000);
            await Task.Delay(1500);
            foreach (var (id, what) in new[] { ("CAV-M", "a CAV admitted by Milan"), ("CAV-E", "a CAV whose credential has expired"), ("CAV-F", "a CAV whose credential is forged in Turin's name") })
            {
                var linked = cavs.Values.Any(c => c != cavs[id] && c.Hub.Linked.Contains(id)) || cavs[id].Hub.Linked.Count > 0;
                var said = cavs.Values.SelectMany(c => { lock (c.Said) return c.Said.ToList(); }).FirstOrDefault(l => l.Contains(id) && l.Contains("not linked"))
                           ?? cavs[id].Said.FirstOrDefault(l => l.Contains("not linked")) ?? "";
                var reason = said.Contains("UnknownIssuer") || said.Contains("not an anchor this end trusts") && !said.Contains("credential is") ? "unknown to Turin"
                           : said.Contains("Expired") ? "expired" : said.Contains("InvalidSignature") ? "its credential's signature invalid" : said;
                result[what] = linked ? "linked" : $"not linked: {reason}";
            }

            await a.Gives.Writer.WriteAsync((Message("A1"), DateTimeOffset.UtcNow));
            var got = await Until(() => b.Got.Any(g => g.Message.Json.Contains("\"A1\"")), 3000);
            var message = got ? b.Got.First(g => g.Message.Json.Contains("\"A1\"")).Message.Json : "";
            result["A's Message at B"] = !got ? "not given on" : message.Contains("\"KeyID\":\"CAV-A\"") && message.Contains("PTF-DEM-V1.0") ? "signed by A, verified, given on" : "given on unsigned";
            await cavs["CAV-T"].Gives.Writer.WriteAsync((Message("T1"), DateTimeOffset.UtcNow));
            await cavs["CAV-I"].Gives.Writer.WriteAsync((Message("I1"), DateTimeOffset.UtcNow));
            await Until(() => b.Hub.Refused >= 2, 3000);
            string Refusal(string from) { lock (b.Said) return b.Said.FirstOrDefault(l => l.StartsWith($"from {from}, refused")) is { } l ? l[(l.IndexOf("refused: ") + 9)..] : "given on"; }
            result["a Message altered after it was signed"] = b.Got.Any(g => g.Message.From == "CAV-T") ? "given on" : $"refused: {Refusal("CAV-T")}";
            result["a Message signed in another CAV's name"] = b.Got.Any(g => g.Message.From == "CAV-I") ? "given on" : $"refused: {Refusal("CAV-I")}";
        }
        finally
        {
            foreach (var c in cavs.Values) await c.DisposeAsync();
            foreach (var d in discoveries) await d.DisposeAsync();
        }
        Expected.Match("external-trust.json", result);
    }

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
