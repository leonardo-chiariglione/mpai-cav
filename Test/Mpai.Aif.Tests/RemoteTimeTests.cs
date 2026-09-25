using System.Diagnostics;
using System.Text.RegularExpressions;

using AIF.Controller;
using AIF.Store;
using Mpai.Aif.Api;

namespace Mpai.Aif.Tests;

// Time and cost across machines (M3217 3.2, 3.6): a Message written on an AIM
// host is stamped on the Controller's time base; and the Remote transport
// compared with Controller and InProcess on the loop - reported, not judged.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
[Collection(Timing.Name)]
public class RemoteTimeTests
{
    private const string Text = RemoteTests.Text;

    private sealed class TestClock(DateTimeOffset epoch) : AIF.Channels.IClock
    {
        private readonly long start = Stopwatch.GetTimestamp();
        public DateTimeOffset Now => epoch + Stopwatch.GetElapsedTime(start);
        public long Monotonic => Stopwatch.GetTimestamp();
    }

    // THE CONTROLLER ON A TEST CLOCK OF 2030, TST-ACC ON THE HOST, whose own clock
    // says today: what TST-ACC writes arrives stamped in 2030.
    [Fact]
    public async Task Stamps()
    {
        var result = new Dictionary<string, string>();
        using var hostProcess = new HostProcess();

        var store = new AmdStore(RemoteTests.Amds);
        store.Scan();
        var clock = new TestClock(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var ua = new UserAgent(store, null, clock) { AimHostKey = HostProcess.Key };
        ua.AimHosts["1TST-ACC-V1.0-I01"] = hostProcess.Address;
        ua.MPAI_AIFU_Controller_Initialize();

        var arrived = new List<(string Writer, DateTimeOffset Stamp, DateTimeOffset Here)>();
        ua.RemoteTransport.Arrived = (spec, message) => { lock (arrived) arrived.Add((spec.Writer.Aim, message.Stamp, clock.Now)); };

        Assert.Equal(AifError.OK, ua.MPAI_AIFU_MODULE_Start("1TST-RLP-V1.0-I01", new RemoteAims(), AimSettings.Empty, out var id));
        for (var i = 1; i <= 3; i++)
        {
            await ua.ContinuousWriteAsync(id, Text, 1, $"x{i}", 2000);
            await ua.ContinuousReadAsync(id, Text, 1, 2000);
        }
        ua.MPAI_AIFU_MODULE_Stop(id);

        lock (arrived)
        {
            result["from TST-ACC on the host"] = arrived.Count == 0 ? "nothing arrived"
                : arrived.All(a => a.Writer == "1TST-ACC-V1.0-I01") ? "every Message that arrived" : "Messages from others too";
            result["their stamps"] = arrived.All(a => a.Stamp.Year == 2030)
                ? "on the Controller's time base: 2030"
                : "not on the Controller's time base: " + string.Join(", ", arrived.Select(a => a.Stamp.Year).Distinct());
            var gap = arrived.Count == 0 ? TimeSpan.MaxValue : arrived.Max(a => (a.Here - a.Stamp).Duration());
            result["each stamp against the Controller's clock at arrival"] = gap < TimeSpan.FromMilliseconds(100)
                ? "within 100 ms" : $"{gap.TotalMilliseconds:0} ms apart";
        }

        Expected.Match("remote-stamps.json", result);
    }

    // THE TRANSPORTS COMPARED on the loop - TST-DSC and TST-ACC feeding each other:
    // TST-RLL all here on Controller, then on InProcess; TST-RLP with TST-ACC on
    // the host, its Channels on Remote. The measures of ContinuousTests.Transports;
    // every Message accounted for; reported in Test/Reports, not judged.
    [Fact]
    public void Transports()
    {
        var report = new Dictionary<string, object>();
        using var hostProcess = new HostProcess();

        foreach (var (label, module, transport, hosted) in new[]
        {
            ("Controller", "1TST-RLL-V1.0-I01", "Controller", false),
            ("InProcess",  "1TST-RLL-V1.0-I01", "InProcess",  false),
            ("Remote",     "1TST-RLP-V1.0-I01", "Controller", true)
        })
        {
            using var api = new ControllerApi(RemoteTests.Amds, Path.Combine(RemoteTests.Amds, "no-settings.json"), new RemoteAims());
            api.Controller.DefaultTransport = transport;
            if (hosted)
            {
                api.Controller.AimHostKey = HostProcess.Key;
                api.Controller.AimHosts["1TST-ACC-V1.0-I01"] = hostProcess.Address;
            }
            api.StartFlow(module);
            for (var i = 0; i < 20; i++) { api.InputWrite(module, Text, 1, "warm"); api.OutputRead(module, Text, 1, 2000); }

            var process = Process.GetCurrentProcess();
            var cpu0 = process.TotalProcessorTime;
            var hostCpu0 = hostProcess.ProcessorTime;
            GC.Collect();
            var memory0 = GC.GetTotalMemory(true);

            // Round trips: one in, one out.
            var latencies = new List<double>();
            for (var i = 0; i < 500; i++)
            {
                var clock = Stopwatch.StartNew();
                api.InputWrite(module, Text, 1, $"t{i}");
                api.OutputRead(module, Text, 1, 2000);
                latencies.Add(clock.Elapsed.TotalMilliseconds);
            }
            latencies.Sort();

            // Throughput: 2000 in as fast as they are taken, then all out.
            var run = Stopwatch.StartNew();
            var reader = Task.Run(() => { var n = 0; while (api.OutputRead(module, Text, 1, 300).Ok) n++; return n; });
            for (var i = 0; i < 2000; i++) api.InputWrite(module, Text, 1, $"s{i}");
            var got = reader.Result;
            run.Stop();
            process.Refresh();

            Match Boundary() => Regex.Match(string.Join(" | ", api.ChannelAccounts(module)),
                @"\(boundary\)\.TST-TXT-V1\.0#1: taken (\d+), dropped (\d+), discarded \d+, pending (\d+)");
            long Sum(Match m) => long.Parse(m.Groups[1].Value) + long.Parse(m.Groups[2].Value) + long.Parse(m.Groups[3].Value);
            var settle = Stopwatch.StartNew();
            while (Sum(Boundary()) < 2520 && settle.ElapsedMilliseconds < 5000) Thread.Sleep(20);
            var boundary = Boundary();
            var (taken, dropped, pending) = (long.Parse(boundary.Groups[1].Value), long.Parse(boundary.Groups[2].Value), long.Parse(boundary.Groups[3].Value));

            report[label] = new
            {
                Module = module,
                RoundTripMs = new { Median = latencies[latencies.Count / 2], P95 = latencies[(int)(latencies.Count * 0.95)], Max = latencies[^1] },
                Throughput = new { Written = 2000, Read = got, DroppedAtTheBoundary = dropped, PendingAtTheEnd = pending, Seconds = run.Elapsed.TotalSeconds, WrittenPerSecond = 2000 / run.Elapsed.TotalSeconds },
                CpuMs = (process.TotalProcessorTime - cpu0).TotalMilliseconds,
                HostCpuMs = hosted ? (hostProcess.ProcessorTime - hostCpu0).TotalMilliseconds : 0,
                MemoryKiB = (GC.GetTotalMemory(false) - memory0) / 1024,
                Accounts = api.ChannelAccounts(module)
            };
            api.StopFlow(module);
            Assert.True(20 + 500 + 2000 == taken + dropped + pending,        // every output read, dropped or pending
                        $"{label}: {taken + dropped + pending} of 2520 accounted for: " + string.Join(" | ", api.ChannelAccounts(module)));
        }

        var path = Path.Combine(Repository.Root, "Test", "Reports", "remote-transports.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}
