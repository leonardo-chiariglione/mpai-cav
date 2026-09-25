using System.Text;

using AIF.Controller;
using Mpai.Aif.Api;

namespace Mpai.Aif.Tests;

// Private Storage of the Module and recorded data (M3219): a Module's own storage
// under rules its writers set, or its central control; Shared Storage reachable
// by several Modules; the record of what crosses a Module's boundary, and its
// playback. The test Modules are in Test/Data/Phase6.
//
// Step 1 records what happens today: every AIM of a Module reads what any other
// wrote, and so does every Module given the same location; the Metadata fields of
// M3219 are not in the schema and are not acted upon; nothing records a
// boundary; the WDL has no stream.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class StorageTests
{
    public const string Text = "TST-TXT-V1.0";

    public static string Amds => Path.Combine(Repository.Root, "Test", "Data", "Phase6");

    private static ControllerApi Api() => new(Amds, Path.Combine(Amds, "no-settings.json"), new StorageAims());

    private static string Location() => Path.Combine(Path.GetTempPath(), "mpai-phase6-" + Guid.NewGuid().ToString("N"));

    private static string Outputs(ControllerApi.Result run) =>
        $"{run.Error}; " + string.Join("; ", run.Outputs.OrderBy(o => o.PortNumber).Select(o => $"#{o.PortNumber} '{o.Json}'"));

    [Fact]
    public void Today()
    {
        var result = new Dictionary<string, string>();

        // THE METADATA FIELDS: StorageControl and Record are not in the schema.
        var schema = AIF.Metadata.AimMetadataSchema.Load(Repository.Schemas);
        foreach (var module in new[] { "TST-MPC", "TST-RCA" })
        {
            var violations = schema.Violations(File.ReadAllText(Path.Combine(Amds, $"1{module}-V1.0-I01.json")));
            result[$"{module} against the schema"] = violations.Count == 0 ? "valid" : string.Join("; ", violations);
        }

        // ONE MODULE: its reader reads what its writer wrote, whoever the writer
        // meant it for; a central control named in the Metadata is not one.
        foreach (var module in new[] { "TST-MPS", "TST-MPC" })
        {
            using var api = Api();
            var name = $"1{module}-V1.0-I01";
            api.StartFlow(name);
            api.SharedStorageInit(name, Location());
            var inputs = new List<ControllerApi.Datum> { new(Text, 1, "k=v; readers=1TST-SWR-V1.0-I01") };
            if (module == "TST-MPC") inputs.Add(new(Text, 3, "category Notes: writers 1TST-SWR-V1.0-I01, readers none"));
            result[$"{module}: rules given, then a write"] = Outputs(api.Advance(name, inputs));
            result[$"{module}: the other AIM reads it"] = Outputs(api.Advance(name, [new(Text, 2, "k")]));
            api.StopFlow(name);
        }

        // TWO MODULES, ONE LOCATION: what one writes the other reads, with no rules.
        {
            var location = Location();
            using var a = Api();
            using var b = Api();
            a.StartFlow("1TST-SHA-V1.0-I01"); a.SharedStorageInit("1TST-SHA-V1.0-I01", location);
            b.StartFlow("1TST-SHB-V1.0-I01"); b.SharedStorageInit("1TST-SHB-V1.0-I01", location);
            result["TST-SHA writes"] = Outputs(a.Advance("1TST-SHA-V1.0-I01", [new(Text, 1, "s=shared; readers=1TST-SHA-V1.0-I01")]));
            result["TST-SHB reads it"] = Outputs(b.Advance("1TST-SHB-V1.0-I01", [new(Text, 1, "s")]));
            a.StopFlow("1TST-SHA-V1.0-I01"); b.StopFlow("1TST-SHB-V1.0-I01");
        }

        // THE RECORD: none, whether asked for or declared always on.
        foreach (var module in new[] { "TST-REC", "TST-RCA" })
        {
            using var api = Api();
            var name = $"1{module}-V1.0-I01";
            var location = Location();
            api.StartFlow(name);
            api.SharedStorageInit(name, location);
            api.InputWrite(name, Text, 1, "a1", 2000);
            var echoed = api.OutputRead(name, Text, 1, 2000);
            api.StopFlow(name);
            var files = Directory.Exists(location) ? Directory.EnumerateFiles(location, "*", SearchOption.AllDirectories).Count() : 0;
            result[$"{module}: an input, then its echo"] = $"{echoed.Error} '{echoed.Json}'; files at the Module's location: {files}";
        }
        result["the Controller API's calls for a record"] =
            typeof(ControllerApi).GetMethods().Any(m => m.Name.Contains("Record")) ? "present" : "none";

        // THE BOUNDARY OF ESS STAGE 1: its inputs arrive and are counted.
        {
            using var api = Api();
            const string esb = "1TST-ESB-V1.0-I01";
            api.StartFlow(esb);
            api.InputWrite(esb, "CAV-GNO-V1.1", 1, """{"Header":"CAV-GNO-V1.1","GNSSObjectID":"g1","GNSSData":[{"Data":"JEdQR0dB"}]}""", 2000);
            var counts = api.OutputRead(esb, Text, 1, 2000);
            api.StopFlow(esb);
            result["TST-ESB: a GNSS Object written"] = $"{counts.Error} '{counts.Json}'";
        }

        // STREAM: the WDL reader does not know it.
        const string workflow = """
            workflow TST over 1TST-REC-V1.0-I01
            on Start:
                ask Controller to start
                stream A (TST-TXT-V1.0:1) from record "R"
            """;
        // A line that does not begin with a step the reader knows is taken for the
        // continuation of the line before it: the stream line is lost, silently.
        try
        {
            var read = new Mpai.Wdl.WorkflowReader().Read(workflow);
            result["stream in a workflow"] = "read as: " + string.Join("; ", read.OnStart.Select(s => s.Kind.ToString()));
        }
        catch (Exception refused) { result["stream in a workflow"] = "not read: " + refused.Message; }

        Expected.Match("storage-today.json", result);
    }
}

// The test AIMs of Phase 6. TST-SWR and TST-SRD write and read the storage they
// are given; TST-SGV sets rules; TST-TRE echoes; TST-SNK counts.
public sealed class StorageAims : IAimProvider
{
    private readonly TestAims phase3 = new();

    public bool CanCreate(string aimName) => aimName.StartsWith("1TST-", StringComparison.Ordinal);

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage) =>
        aimName switch
        {
            "1TST-SWR-V1.0-I01" => new Aim(aimName, m => ("Written", Write(storage, In(m)))),
            "1TST-SRD-V1.0-I01" => new Aim(aimName, m => ("Read", Read(storage, In(m)))),
            "1TST-SGV-V1.0-I01" => new Aim(aimName, m => ("Ruled", "no rules can be set: " + In(m))),
            "1TST-TRE-V1.0-I01" => new Echo(aimName),
            "1TST-SNK-V1.0-I01" => new Sink(aimName),
            _ => phase3.Create(aimName, settings, storage)
        };

    private static string In(Message m) => m.Ports.Values.FirstOrDefault() ?? "";

    // "key=value; readers=...; time=..." - today only key and value can be given.
    private static string Write(AIF.SharedStorage.ISharedStorage? storage, string text)
    {
        if (storage is null) return "no storage";
        var parts = text.Split(';', StringSplitOptions.TrimEntries);
        var kv = parts[0].Split('=', 2);
        storage.MPAI_AIFM_SharedStorage_Put(kv[0], Encoding.UTF8.GetBytes(kv[1]));
        var ignored = parts.Skip(1).ToList();
        return $"{kv[0]} written" + (ignored.Count > 0 ? $"; not given: {string.Join(", ", ignored)}" : "");
    }

    private static string Read(AIF.SharedStorage.ISharedStorage? storage, string key)
    {
        if (storage is null) return "no storage";
        try { return Encoding.UTF8.GetString(storage.MPAI_AIFM_SharedStorage_Get(key)); }
        catch (Exception refused) { return $"{refused.GetType().Name}: {refused.Message}"; }
    }

    private sealed class Aim(string id, Func<Message, (string Port, string Value)> run) : IAimProcessor
    {
        public string InstanceId { get; } = id;

        public Task<Message> ProcessAsync(Message message)
        {
            var (port, value) = run(message);
            return Task.FromResult(new Message { Ports = new() { [port] = value } });
        }
    }

    // TST-TRE: each Input Port given back on the Output Port of its number.
    private sealed class Echo(string id) : IAimProcessor, IAimRunner
    {
        public string InstanceId { get; } = id;
        public Task<Message> ProcessAsync(Message message) => throw new NotSupportedException("TST-TRE runs continuously.");

        public async Task RunAsync(IAimPorts ports, AimContext context)
        {
            while (await ports.SelectAsync(-1, (StorageTests.Text, 1), (StorageTests.Text, 2), (StorageTests.Text, 3)) is { } port)
                if (await ports.ReadAsync(port.DataType, port.PortNumber, 0) is { } m)
                    await ports.WriteAsync(StorageTests.Text, port.PortNumber, m.Json);
        }
    }

    // TST-SNK: what arrived on each input of the boundary of ESS Stage 1, counted.
    private sealed class Sink(string id) : IAimProcessor, IAimRunner
    {
        private static readonly string[] Inputs = ["OSD-BVO-V1.5", "OSD-OSA-V1.5", "CAV-GNO-V1.1", "CAV-WDT-V1.1", "CAV-FED-V1.1"];
        public string InstanceId { get; } = id;
        public Task<Message> ProcessAsync(Message message) => throw new NotSupportedException("TST-SNK runs continuously.");

        public async Task RunAsync(IAimPorts ports, AimContext context)
        {
            var counts = Inputs.ToDictionary(t => t, _ => 0);
            while (await ports.SelectAsync(-1, Inputs.Select(t => (t, 1)).ToArray()) is { } port)
            {
                if (await ports.ReadAsync(port.DataType, port.PortNumber, 0) is null) continue;
                counts[port.DataType]++;
                await ports.WriteAsync(StorageTests.Text, 1, string.Join(" ", counts.Select(c => $"{c.Key}:{c.Value}")));
            }
        }
    }
}
