using AIF.Controller;
using Mpai.Aif.Api;

namespace Mpai.Aif.Tests;

// The communication core of Phase 4 (M3215 3.8), on test Modules made for the
// purpose. Their L3s are in Test/Data/Phase4.
//
// Step 1 records what the Controller does with them today; each later step
// changes the record as it changes the Controller.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class ContinuousTests
{
    public const string Text = "TST-TXT-V1.0";

    public static string Amds => Path.Combine(Repository.Root, "Test", "Data", "Phase4");

    private static ControllerApi Api() =>
        new(Amds, Path.Combine(Amds, "no-settings.json"), new ContinuousAims());

    // The test L3s are L3s: every one, of Phase 3 and of Phase 4, validates against
    // the AIM Metadata schema.
    [Fact]
    public void L3sValidate()
    {
        var schema = AIF.Metadata.AimMetadataSchema.Load(Repository.Schemas);
        var invalid = Directory.EnumerateFiles(Amds, "*.json").Concat(Directory.EnumerateFiles(ControllerTests.Amds, "*.json"))
            .Select(f => (File: Path.GetFileName(f), Violations: schema.Violations(File.ReadAllText(f))))
            .Where(v => v.Violations.Count > 0)
            .Select(v => $"{v.File}: {string.Join("; ", v.Violations)}")
            .ToList();
        Assert.True(invalid.Count == 0, string.Join(Environment.NewLine, invalid));
    }

    // A LOOP RUNS (M3215 3.8): TST-LOP, Continuous, TST-ACC's output back to
    // TST-DSC. Fifty inputs give fifty outputs, each counted once, and every
    // Message on every Channel is accounted for.
    [Fact]
    public void Loop()
    {
        var result = new Dictionary<string, string>();
        foreach (var transport in new[] { "Controller", "InProcess" })
        {
            using var api = Api();
            api.Controller.DefaultTransport = transport;
            const string module = "1TST-LOP-V1.0-I01";
            result[$"{transport}: start"] = api.StartFlow(module).ToString();

            var outputs = new List<string>();
            for (var i = 1; i <= 50; i++)
            {
                api.InputWrite(module, Text, 1, $"m{i}", 2000);
                var read = api.OutputRead(module, Text, 1, 2000);
                outputs.Add(read.Ok ? read.Json! : read.Error.ToString());
            }
            result[$"{transport}: first three outputs"] = string.Join(" / ", outputs.Take(3));
            result[$"{transport}: outputs in order"] =
                outputs.Select((o, i) => o.StartsWith($"{i + 1}:m{i + 1}", StringComparison.Ordinal)).All(x => x) ? "50 of 50" : string.Join(" / ", outputs);
            result[$"{transport}: accounts"] = string.Join(" | ", api.ChannelAccounts(module));
            result[$"{transport}: nothing more"] = api.OutputRead(module, Text, 1, 100).Error.ToString();
            api.StopFlow(module);
        }
        Expected.Match("continuous-loop.json", result);
    }

    // What the Channels of TST-PBH carry at each reader: the behaviour its AIM's
    // Metadata declares (M3215 3.2).
    [Fact]
    public void Behaviours()
    {
        var store = new AIF.Store.AmdStore(Amds);
        store.Scan();
        var graph = new AIF.Controller.Controller(store).RegisterAim(store.FindByAimName("1TST-PBH-V1.0-I01")!);
        var executor = new ContinuousExecutor(graph, new AimHost(), new Dictionary<string, AIF.Channels.IChannelTransport>
        {
            ["Controller"] = new AIF.Channels.ControllerTransport()
        }, "TST-PBH");
        var result = executor.Channels.ToDictionary(
            c => c.Writer.ToString(),
            c => $"{c.Transport}: " + string.Join("; ", c.Readers.Select(r =>
                $"{r.Reader} {r.Behaviour.Overflow} Depth {r.Behaviour.Depth}" + (r.Behaviour.MaxAge is { } a ? $" MaxAge {a.TotalMilliseconds} ms" : ""))));
        Expected.Match("continuous-behaviours.json", result);
    }

    // RestartLimit (M3215 3.3): TST-THS throws on its first two runs and has
    // RestartLimit 2; the third input gets through.
    [Fact]
    public void Restart()
    {
        using var api = Api();
        const string module = "1TST-RST-V1.0-I01";
        api.StartFlow(module);
        var result = new Dictionary<string, string>();
        for (var i = 1; i <= 3; i++)
        {
            api.InputWrite(module, Text, 1, $"r{i}", 2000);
            var read = api.OutputRead(module, Text, 1, 500);
            var status = api.Status(module).Aims.Single();
            result[$"input {i}"] = (read.Ok ? $"'{read.Json}'" : read.Error.ToString()) + $"; {status.Status}" + (status.Reason.Length > 0 ? $" ({status.Reason})" : "");
        }
        api.StopFlow(module);
        Expected.Match("continuous-restart.json", result);
    }

    // CONTROLLER AND INPROCESS COMPARED on TST-LOP (M3215 3.8; M3206 Section 2,
    // Continuous): the time from a write at the boundary to the loop's output,
    // the throughput, CPU and memory. Recorded and reported in
    // Test/Reports/continuous-transports.json, not judged.
    [Fact]
    public void Transports()
    {
        var report = new Dictionary<string, object>();
        foreach (var transport in new[] { "Controller", "InProcess" })
        {
            using var api = Api();
            api.Controller.DefaultTransport = transport;
            const string module = "1TST-LOP-V1.0-I01";
            api.StartFlow(module);
            for (var i = 0; i < 20; i++) { api.InputWrite(module, Text, 1, "warm"); api.OutputRead(module, Text, 1, 2000); }

            var process = System.Diagnostics.Process.GetCurrentProcess();
            var cpu0 = process.TotalProcessorTime;
            GC.Collect();
            var memory0 = GC.GetTotalMemory(true);

            // Round trips: one in, one out.
            var latencies = new List<double>();
            for (var i = 0; i < 500; i++)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                api.InputWrite(module, Text, 1, $"t{i}");
                api.OutputRead(module, Text, 1, 2000);
                latencies.Add(clock.Elapsed.TotalMilliseconds);
            }
            latencies.Sort();

            // Throughput: 2000 in as fast as they are taken, then all out.
            var run = System.Diagnostics.Stopwatch.StartNew();
            var reader = Task.Run(() => { var n = 0; while (api.OutputRead(module, Text, 1, 300).Ok) n++; return n; });
            for (var i = 0; i < 2000; i++) api.InputWrite(module, Text, 1, $"s{i}");
            var got = reader.Result;
            run.Stop();

            process.Refresh();

            // What the boundary output kept and what it dropped: it keeps the newest
            // Messages, so that a User Agent that does not read cannot stall the Module.
            // The counts are read when the loop has settled: the last Message may
            // still be on its way when the reader stops waiting.
            System.Text.RegularExpressions.Match Boundary() => System.Text.RegularExpressions.Regex.Match(string.Join(" | ", api.ChannelAccounts(module)),
                @"\(boundary\)\.TST-TXT-V1\.0#1: taken (\d+), dropped (\d+), discarded \d+, pending (\d+)");
            long Sum(System.Text.RegularExpressions.Match m) => long.Parse(m.Groups[1].Value) + long.Parse(m.Groups[2].Value) + long.Parse(m.Groups[3].Value);
            var settle = System.Diagnostics.Stopwatch.StartNew();
            while (Sum(Boundary()) < 2520 && settle.ElapsedMilliseconds < 5000) Thread.Sleep(20);
            var boundary = System.Text.RegularExpressions.Regex.Match(string.Join(" | ", api.ChannelAccounts(module)),
                @"\(boundary\)\.TST-TXT-V1\.0#1: taken (\d+), dropped (\d+), discarded \d+, pending (\d+)");
            var taken = long.Parse(boundary.Groups[1].Value);
            var dropped = long.Parse(boundary.Groups[2].Value);
            var pending = long.Parse(boundary.Groups[3].Value);
            report[transport] = new
            {
                RoundTripMs = new { Median = latencies[latencies.Count / 2], P95 = latencies[(int)(latencies.Count * 0.95)], Max = latencies[^1] },
                Throughput = new { Written = 2000, Read = got, DroppedAtTheBoundary = dropped, PendingAtTheEnd = pending, Seconds = run.Elapsed.TotalSeconds, WrittenPerSecond = 2000 / run.Elapsed.TotalSeconds },
                CpuMs = (process.TotalProcessorTime - cpu0).TotalMilliseconds,
                MemoryKiB = (GC.GetTotalMemory(false) - memory0) / 1024,
                Accounts = api.ChannelAccounts(module)
            };
            api.StopFlow(module);
            Assert.True(20 + 500 + 2000 == taken + dropped + pending,        // every output read, dropped or pending
                        $"{transport}: {taken + dropped + pending} of 2520 accounted for: " + string.Join(" | ", ((dynamic)report[transport]).Accounts));
        }

        var path = Path.Combine(Repository.Root, "Test", "Reports", "continuous-transports.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    // Each test Module: whether it starts, and what one exchange on it returns.
    [Fact]
    public void Today()
    {
        var modules = new[] { "TST-LOP", "TST-PBH", "TST-TRX", "TST-PER", "TST-PEX", "TST-RST", "TST-PAY", "TST-PRV" };
        var result = new Dictionary<string, string>();
        using var api = Api();
        foreach (var name in modules)
        {
            var module = $"1{name}-V1.0-I01";
            string started;
            try { started = api.StartFlow(module).ToString(); }
            catch (Exception failure) { result[name] = "does not start: " + MetadataTests.Short(failure.Message, 140); continue; }
            if (started != "OK") { result[name] = "does not start: " + started; continue; }

            ControllerApi.Result run;
            try { run = api.Advance(module, [new ControllerApi.Datum(Text, "x")]); }
            catch (Exception failure) { result[name] = "starts; the exchange throws: " + MetadataTests.Short(failure.Message, 140); api.StopFlow(module); continue; }
            result[name] = $"starts; an exchange: {run.Error}; " +
                (run.Outputs.Count == 0 ? "no output"
                 : string.Join("; ", run.Outputs.OrderBy(o => o.PortNumber).Select(o => $"#{o.PortNumber} '{MetadataTests.Short(o.Json, 40)}'")));
            api.StopFlow(module);
        }

        Expected.Match("continuous-today.json", result);
    }
}

// The test AIMs of Phase 4, as functions (IAimProcessor). Each reads and writes
// its own Ports by the names its own L3 gives them.
public sealed class ContinuousAims : IAimProvider
{
    private int accumulated, ticks, thrown;

    public bool CanCreate(string aimName) => aimName.StartsWith("1TST-", StringComparison.Ordinal);

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage) =>
        aimName switch
        {
            "1TST-DSC-V1.0-I01" => Aim(aimName, m => Out(("Described", In(m, "Text") + (m.Ports.TryGetValue("Previous", out var p) ? "|" + p : "")))),
            "1TST-ACC-V1.0-I01" => Aim(aimName, m => Out(("Accumulated", $"{Interlocked.Increment(ref accumulated)}:{In(m, "Text")}"))),
            "1TST-RDB-V1.0-I01" or "1TST-RDO-V1.0-I01" or "1TST-RDN-V1.0-I01" or "1TST-RDA-V1.0-I01" =>
                Aim(aimName, async m => { await Task.Delay(50); return Out(("Read", In(m, "Text"))); }),
            "1TST-TXO-V1.0-I01" => Aim(aimName, m => Out(("Written", In(m, "Text")))),
            "1TST-TXC-V1.0-I01" => Aim(aimName, m => Out(("Read", In(m, "Text")))),
            "1TST-TIK-V1.0-I01" => Aim(aimName, m => Out(("Ticks", Interlocked.Increment(ref ticks).ToString()))),
            "1TST-DLN-V1.0-I01" => Aim(aimName, async m => { await Task.Delay(60); return Out(("Late", In(m, "Text"))); }),
            "1TST-THS-V1.0-I01" => Aim(aimName, m => Interlocked.Increment(ref thrown) <= 2
                                                      ? throw new InvalidOperationException("TST-THS throws")
                                                      : Out(("Survived", In(m, "Text")))),
            "1TST-PWR-V1.0-I01" => Aim(aimName, m => Out(("Payload", In(m, "Text")))),
            "1TST-PRD-V1.0-I01" => Aim(aimName, m => Out(("Length", In(m, "Payload").Length.ToString()))),
            "1TST-PVA-V1.0-I01" => Aim(aimName, m => Out(("Private", "A"))),
            "1TST-PVB-V1.0-I01" => Aim(aimName, m => Out(("Private", "B"))),
            _ => throw new InvalidOperationException($"No test AIM {aimName}.")
        };

    private static string In(Message m, string port) => m.Ports.TryGetValue(port, out var text) ? text : "";

    private static Message Out(params (string Port, string Value)[] ports) =>
        new() { Ports = ports.ToDictionary(p => p.Port, p => p.Value) };

    private static IAimProcessor Aim(string id, Func<Message, Message> run) => new Processor(id, m => Task.FromResult(run(m)));

    private static IAimProcessor Aim(string id, Func<Message, Task<Message>> run) => new Processor(id, run);

    private sealed class Processor(string instanceId, Func<Message, Task<Message>> run) : IAimProcessor
    {
        public string InstanceId { get; } = instanceId;
        public Task<Message> ProcessAsync(Message message) => run(message);
    }
}
