using System.Text.Json.Nodes;

using AIF.Channels;

namespace Mpai.Aif.Tests;

// The Remote transport on its own (M3217 3.2): two sides - a Controller's and a
// host's - joined by a real TLS link over the loopback, each with its own
// transport; Channels crossing it both ways; Port behaviour kept at the reader,
// wherever it is; the key; a link lost.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
[Collection(Timing.Name)]
public class RemoteChannelTests
{
    private const string Text = "TST-TXT-V1.0";
    private static readonly PortEnd Here  = new("1TST-HERE-V1.0-I01", Text);    // on the Controller's machine
    private static readonly PortEnd There = new("1TST-THERE-V1.0-I01", Text);   // on the host

    private sealed class Pair : IAsyncDisposable
    {
        public required RemoteTransport Controller { get; init; }
        public required RemoteTransport Host { get; init; }
        public required RemoteLink ControllerLink { get; init; }
        public required RemoteLink HostLink { get; init; }
        public required RemoteLink.Listener Listener { get; init; }

        public async ValueTask DisposeAsync()
        {
            await ControllerLink.DisposeAsync();
            await HostLink.DisposeAsync();
            await Listener.DisposeAsync();
        }
    }

    private static async Task<Pair> Connect(string key = "the key")
    {
        var admitted = new TaskCompletionSource<RemoteLink>();
        var listener = new RemoteLink.Listener(0, "the key", link => admitted.TrySetResult(link));
        var controllerLink = await RemoteLink.ConnectAsync("localhost", listener.Port, key);
        var hostLink = await admitted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var controller = new RemoteTransport(aim => aim == There.Aim ? controllerLink : null);
        var host = new RemoteTransport(aim => aim == There.Aim ? null : hostLink);
        controllerLink.OnRequest = f => controller.ReceiveAsync(f)!;
        hostLink.OnRequest = f => host.ReceiveAsync(f)!;
        return new Pair { Controller = controller, Host = host, ControllerLink = controllerLink, HostLink = hostLink, Listener = listener };
    }

    private static ChannelSpec Spec(PortEnd writer, PortBehaviour behaviour, params PortEnd[] readers) =>
        new("TST#" + Guid.NewGuid().ToString("N"), writer, readers.Select(r => new ChannelReaderSpec(r, behaviour)).ToList(), "Remote");

    private static PortMessage M(string json) => new() { DataType = Text, Json = json };

    private static async Task<string> Drain(IChannelReader reader)
    {
        var got = new List<string>();
        while (await reader.ReadAsync(0) is { } m) got.Add(m.Json);
        return string.Join(",", got);
    }

    [Fact]
    public async Task AcrossTheLink()
    {
        var result = new Dictionary<string, string>();
        await using var pair = await Connect();

        // A writer on the Controller's side, a reader on each side.
        {
            var spec = Spec(new PortEnd("1TST-W-V1.0-I01", Text), PortBehaviour.Default, Here, There);
            pair.Controller.Register(spec); pair.Host.Register(spec);
            var w = pair.Controller.OpenWriter(spec);
            var here = pair.Controller.OpenReader(spec, Here);
            var there = pair.Host.OpenReader(spec, There);
            await w.WriteAsync(M("1")); await w.WriteAsync(M("2")); await w.WriteAsync(M("3"));
            var first = await there.ReadAsync(0);
            result["writer here, readers here and there"] =
                $"here {await Drain(here)}; there {first!.Json},{await Drain(there)}; stamped there {(Math.Abs((DateTimeOffset.UtcNow - first.Stamp).TotalSeconds) < 5 ? "now" : "wrongly")}, sequence {first.Sequence}";
        }

        // A writer on the host, a reader on the Controller's side.
        {
            var spec = Spec(new PortEnd(There.Aim, Text, 2), PortBehaviour.Default, Here);
            pair.Controller.Register(spec); pair.Host.Register(spec);
            var w = pair.Host.OpenWriter(spec);
            var here = pair.Controller.OpenReader(spec, Here);
            await w.WriteAsync(M("from the host"));
            result["writer there, reader here"] = $"{(await here.ReadAsync(2000))?.Json}";
        }

        // Block, Depth 2, the reader there: the writer here is held.
        {
            var spec = Spec(new PortEnd("1TST-W-V1.0-I01", Text, 3), new PortBehaviour(2, Overflow.Block, null), There);
            pair.Controller.Register(spec); pair.Host.Register(spec);
            var w = pair.Controller.OpenWriter(spec);
            var there = pair.Host.OpenReader(spec, There);
            await w.WriteAsync(M("1")); await w.WriteAsync(M("2"));
            var third = await w.WriteAsync(M("3"), 100);
            var fourth = w.WriteAsync(M("4"), 3000).AsTask();
            await Task.Delay(100);
            var held = !fourth.IsCompleted;
            await there.ReadAsync(0);
            result["Block, Depth 2, the reader there"] =
                $"third write with timeout 100: {(third ? "written" : "timed out")}; a write while full {(held ? "waits" : "does not wait")}, then {(await fourth ? "passes" : "fails")}; there {await Drain(there)}";
        }

        // DropOldest and MaxAge, at the reader there.
        {
            var spec = Spec(new PortEnd("1TST-W-V1.0-I01", Text, 4), new PortBehaviour(2, Overflow.DropOldest, null), There);
            pair.Controller.Register(spec); pair.Host.Register(spec);
            var w = pair.Controller.OpenWriter(spec);
            var there = pair.Host.OpenReader(spec, There);
            for (var i = 1; i <= 5; i++) await w.WriteAsync(M(i.ToString()), 0);
            result["DropOldest, Depth 2, the reader there, five written"] = $"read {await Drain(there)}; dropped {there.Dropped}";

            var aged = Spec(new PortEnd("1TST-W-V1.0-I01", Text, 5), new PortBehaviour(16, Overflow.Block, TimeSpan.FromMilliseconds(60)), There);
            pair.Controller.Register(aged); pair.Host.Register(aged);
            var wa = pair.Controller.OpenWriter(aged);
            var ta = pair.Host.OpenReader(aged, There);
            await wa.WriteAsync(M("old")); await Task.Delay(150); await wa.WriteAsync(M("fresh"));
            result["MaxAge 60 ms, the reader there"] = $"read {await Drain(ta)}; discarded {ta.Discarded}";
        }

        Expected.Match("remote-channels.json", result);
    }

    [Fact]
    public async Task KeyAndLoss()
    {
        var result = new Dictionary<string, string>();

        try { await using var wrong = await Connect("not the key"); result["a Controller with the wrong key"] = "admitted"; }
        catch (UnauthorizedAccessException) { result["a Controller with the wrong key"] = "not admitted"; }

        await using var pair = await Connect();
        var spec = Spec(new PortEnd("1TST-W-V1.0-I01", Text), PortBehaviour.Default, There);
        pair.Controller.Register(spec); pair.Host.Register(spec);
        var w = pair.Controller.OpenWriter(spec);
        await w.WriteAsync(M("before"));
        var lost = new TaskCompletionSource<string>();
        pair.ControllerLink.Lost += reason => lost.TrySetResult(reason);
        await pair.HostLink.DisposeAsync();                          // the host gone
        var noticed = await Task.WhenAny(lost.Task, Task.Delay(5000)) == lost.Task;
        // Not delivered, and no error to the writer: the AIM at the other end is
        // DEGRADED by whoever holds the link (M3217 3.2).
        try { result["a write after the host went"] = await w.WriteAsync(M("after"), 2000) ? "delivered" : "not delivered; the writer goes on"; }
        catch (IOException) { result["a write after the host went"] = "an error on the Channel"; }
        result["the Controller told the link was lost"] = noticed ? "yes" : "no";

        Expected.Match("remote-link.json", result);
    }
}
