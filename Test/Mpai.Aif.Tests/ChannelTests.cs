using AIF.Channels;

namespace Mpai.Aif.Tests;

// Channels, their two transports and Port behaviour, on their own (M3215 3.1,
// 3.2): each behaviour on each transport, recorded side by side.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class ChannelTests
{
    private const string Text = "TST-TXT-V1.0";
    private static readonly PortEnd Writer = new("1TST-W-V1.0-I01", Text);
    private static readonly PortEnd A = new("1TST-A-V1.0-I01", Text);
    private static readonly PortEnd B = new("1TST-B-V1.0-I01", Text);

    private static ChannelSpec Spec(string transport, PortBehaviour behaviour, params PortEnd[] readers) =>
        new("1TST-MOD-V1.0-I01#" + Guid.NewGuid().ToString("N"), Writer,
            readers.Select(r => new ChannelReaderSpec(r, behaviour)).ToList(), transport);

    private static IChannelTransport Transport(string name) =>
        name == "InProcess" ? new InProcessTransport() : new ControllerTransport();

    private static PortMessage M(string json) => new() { DataType = Text, Json = json };

    private static async Task<string> Drain(IChannelReader reader)
    {
        var got = new List<string>();
        while (await reader.ReadAsync(0) is { } m) got.Add(m.Json);
        return string.Join(",", got);
    }

    [Fact]
    public async Task Behaviour()
    {
        var result = new Dictionary<string, string>();
        foreach (var name in new[] { "Controller", "InProcess" })
        {
            var t = Transport(name);

            // Fan-out: every reader receives every Message, in order, the same object.
            {
                var spec = Spec(name, PortBehaviour.Default, A, B);
                var w = t.OpenWriter(spec); var ra = t.OpenReader(spec, A); var rb = t.OpenReader(spec, B);
                var one = M("1");
                await w.WriteAsync(one); await w.WriteAsync(M("2")); await w.WriteAsync(M("3"));
                var firstA = await ra.ReadAsync(0);
                result[$"{name}: two readers"] = $"A {firstA!.Json},{await Drain(ra)}; B {await Drain(rb)}; shared {ReferenceEquals(firstA, one)}";
            }

            // Stamps: sequence, monotonic time, the Controller's time.
            {
                var spec = Spec(name, PortBehaviour.Default, A);
                var w = t.OpenWriter(spec); var r = t.OpenReader(spec, A);
                await w.WriteAsync(M("1")); await w.WriteAsync(M("2"));
                var m1 = await r.ReadAsync(0); var m2 = await r.ReadAsync(0);
                result[$"{name}: stamps"] = $"sequence {m1!.Sequence},{m2!.Sequence}; monotonic {(m2.Written >= m1.Written ? "rising" : "falling")}; " +
                                            $"stamped {(Math.Abs((DateTimeOffset.UtcNow - m1.Stamp).TotalSeconds) < 5 ? "now" : "wrongly")}";
            }

            // Block, Depth 2: a third write waits; with a timeout it gives up; room makes it pass.
            {
                var spec = Spec(name, new PortBehaviour(2, Overflow.Block, null), A);
                var w = t.OpenWriter(spec); var r = t.OpenReader(spec, A);
                await w.WriteAsync(M("1")); await w.WriteAsync(M("2"));
                var timedOut = await w.WriteAsync(M("3"), 50);
                var pending = w.WriteAsync(M("4"), 2000).AsTask();
                await Task.Delay(50);
                var waitedWhileFull = !pending.IsCompleted;
                await r.ReadAsync(0);
                var passed = await pending;
                result[$"{name}: Block, Depth 2"] = $"third write with timeout 50: {(timedOut ? "written" : "timed out")}; " +
                                                    $"a write while full {(waitedWhileFull ? "waits" : "does not wait")}, then {(passed ? "passes" : "fails")} when one is read; " +
                                                    $"read {await Drain(r)}; dropped {r.Dropped}";
            }

            // DropOldest and DropNewest, Depth 2: five written.
            foreach (var overflow in new[] { Overflow.DropOldest, Overflow.DropNewest })
            {
                var spec = Spec(name, new PortBehaviour(2, overflow, null), A);
                var w = t.OpenWriter(spec); var r = t.OpenReader(spec, A);
                for (var i = 1; i <= 5; i++) await w.WriteAsync(M(i.ToString()), 0);
                result[$"{name}: {overflow}, Depth 2, five written"] = $"read {await Drain(r)}; dropped {r.Dropped}";
            }

            // MaxAge 60 ms: an old Message is discarded, a fresh one delivered.
            {
                var spec = Spec(name, new PortBehaviour(16, Overflow.Block, TimeSpan.FromMilliseconds(60)), A);
                var w = t.OpenWriter(spec); var r = t.OpenReader(spec, A);
                await w.WriteAsync(M("old"));
                await Task.Delay(150);
                await w.WriteAsync(M("fresh"));
                result[$"{name}: MaxAge 60 ms"] = $"read {await Drain(r)}; discarded {r.Discarded}";
            }

            // Reads: nothing pending within a timeout; a waiting reader woken by a write.
            {
                var spec = Spec(name, PortBehaviour.Default, A);
                var w = t.OpenWriter(spec); var r = t.OpenReader(spec, A);
                var none = await r.ReadAsync(50);
                var waiting = r.ReadAsync(2000).AsTask();
                await Task.Delay(20);
                await w.WriteAsync(M("late"));
                result[$"{name}: reads"] = $"timeout 50 on nothing: {(none is null ? "nothing" : none.Json)}; a waiting reader gets '{(await waiting)?.Json}'";
            }

            // Only a reader of the Channel is given a reader end.
            {
                var spec = Spec(name, PortBehaviour.Default, A);
                try { t.OpenReader(spec, B); result[$"{name}: a reader end for a Port not on the Channel"] = "given"; }
                catch (InvalidOperationException) { result[$"{name}: a reader end for a Port not on the Channel"] = "refused"; }
            }

            // Closing the Module's Channels releases a waiting reader.
            {
                var spec = Spec(name, PortBehaviour.Default, A);
                var r = t.OpenReader(spec, A);
                var waiting = r.ReadAsync(5000).AsTask();
                ((ChannelTransport)t).Close(spec.Module);
                var ended = await Task.WhenAny(waiting, Task.Delay(1000)) == waiting;
                result[$"{name}: Close"] = ended ? $"a waiting reader returns {(waiting.Result is null ? "nothing" : "a Message")}" : "a waiting reader still waits";
            }
        }

        // The Controller transport relays, and can observe what it relays.
        {
            var t = new ControllerTransport();
            var seen = new List<string>();
            t.Observer = (spec, m) => seen.Add(m.Json);
            var spec = Spec("Controller", PortBehaviour.Default, A);
            var w = t.OpenWriter(spec);
            await w.WriteAsync(M("a")); await w.WriteAsync(M("b"));
            result["Controller: observed"] = $"{string.Join(",", seen)}; relayed {t.Relayed}";
        }

        Expected.Match("channels.json", result);
    }
}
