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
