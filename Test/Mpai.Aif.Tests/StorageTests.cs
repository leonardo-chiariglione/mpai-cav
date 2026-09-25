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
            result["TST-SHA writes"] = Outputs(a.Advance("1TST-SHA-V1.0-I01", [new(Text, 1, "shared: s=shared; readers=1TST-SHA-V1.0-I01")]));
            result["TST-SHB reads it"] = Outputs(b.Advance("1TST-SHB-V1.0-I01", [new(Text, 1, "shared: s")]));
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
    private const string Swr = "1TST-SWR-V1.0-I01", Srd = "1TST-SRD-V1.0-I01", Ua = AIF.SharedStorage.RuledStore.UserAgent;

    private static string Ua_Get(ControllerApi api, string module, string key)
    {
        var storage = api.ModuleStorage(module)!;
        var outcome = storage.MPAI_AIFM_RuledStorage_Get(key, out var data);
        return outcome == AIF.SharedStorage.StorageOutcome.OK ? Encoding.UTF8.GetString(data) : outcome.ToString();
    }

    // THE PRIVATE STORAGE OF THE MODULE, NO CENTRAL CONTROL (M3219 3.1, 3.2): the
    // writer sets the rules of each datum - its readers and its time.
    [Fact]
    public async Task WritersRules()
    {
        var result = new Dictionary<string, string>();
        using var api = Api();
        const string mps = "1TST-MPS-V1.0-I01";
        var location = Location();
        api.StartFlow(mps);
        api.SharedStorageInit(mps, location);
        string Write(string text) => Outputs(api.Advance(mps, [new(Text, 1, text)]));
        string Read(string key) => Outputs(api.Advance(mps, [new(Text, 2, key)]));

        result["a, no readers named: written"] = Write("a=1");
        result["a: read by TST-SRD"] = Read("a");
        result["a: read by the User Agent"] = Ua_Get(api, mps, "a");

        result["b, readers TST-SRD: written"] = Write($"b=2; readers={Srd}");
        result["b: read by TST-SRD, by the User Agent"] = Read("b") + " | " + Ua_Get(api, mps, "b");

        result["c, readers the User Agent: read by it"] = Write($"c=3; readers={Ua}") + " | " + Ua_Get(api, mps, "c");

        Write($"d=4; readers={Srd}; time=150ms");
        var fresh = Read("d");
        await Task.Delay(400);
        result["d, time 150 ms: read at once, then after 400 ms"] = fresh + " | " + Read("d");

        // The User Agent writes too, and names its readers.
        var ua = api.ModuleStorage(mps)!;
        result["u, written by the User Agent for TST-SRD"] = ua.MPAI_AIFM_RuledStorage_Put("u", Encoding.UTF8.GetBytes("from the UA"), "Data", [Srd]) + " | " + Read("u");
        result["a, overwritten by the User Agent"] = ua.MPAI_AIFM_RuledStorage_Put("a", Encoding.UTF8.GetBytes("x"), "Data").ToString();

        // The Trace, to one who may read.
        result["c: its Trace, to the User Agent"] = ua.MPAI_AIFM_RuledStorage_Trace("c", out var trace) +
            (trace is null ? "" : $": {trace.Writer}, {trace.Category}, readers {string.Join(",", trace.Readers ?? [])}, {trace.Time}, {trace.Rule}");
        result["b: its Trace, to the User Agent"] = ua.MPAI_AIFM_RuledStorage_Trace("b", out _).ToString();
        result["what the User Agent may list"] = string.Join(",", ua.MPAI_AIFM_RuledStorage_List());

        // Kept until the Module stops, or as long as the scope: a new instance at
        // the same location.
        Write($"e=5; readers={Ua}; time=Module");
        Write($"f=6; readers={Ua}");
        api.StopFlow(mps);
        api.StartFlow(mps);
        api.SharedStorageInit(mps, location);
        result["after a new instance: e (Module), f (Scope)"] = Ua_Get(api, mps, "e") + " | " + Ua_Get(api, mps, "f");
        api.StopFlow(mps);

        Expected.Match("storage-writers.json", result);
    }

    // A CENTRAL CONTROL (M3219 3.2): TST-SGV, named by StorageControl, sets the
    // general rules of each category; a writer restricts them, never extends
    // them; a narrowing applies at once; the central control reads everything.
    [Fact]
    public void CentralControl()
    {
        var result = new Dictionary<string, string>();
        using var api = Api();
        const string mpc = "1TST-MPC-V1.0-I01";
        api.StartFlow(mpc);
        api.SharedStorageInit(mpc, Location());
        string Write(string text) => Outputs(api.Advance(mpc, [new(Text, 1, text)]));
        string Read(string key) => Outputs(api.Advance(mpc, [new(Text, 2, key)]));
        string Rule(string text) => Outputs(api.Advance(mpc, [new(Text, 3, text)]));

        result["Notes written before any rule"] = Write("n=1; category=Notes");
        result["the rule of Notes"] = Rule($"category Notes: writers {Swr}; readers {Srd},{Ua}; time Session");
        result["n written, read by TST-SRD and by the User Agent"] = Write("n=1; category=Notes") + " | " + Read("n") + " | " + Ua_Get(api, mpc, "n");
        result["m, readers narrowed to TST-SRD: TST-SRD, the User Agent"] = Write($"m=2; category=Notes; readers={Srd}") + " | " + Read("m") + " | " + Ua_Get(api, mpc, "m");
        result["x, a reader beyond the rule"] = Write("x=3; category=Notes; readers=1TST-UPP-V1.0-I01");
        result["y, a time beyond the rule (Scope)"] = Write("y=4; category=Notes; time=Scope");
        result["z, a time within it (Module)"] = Write("z=5; category=Notes; time=Module");
        result["o, a category without a rule"] = Write("o=6; category=Other");
        result["a rule set by the User Agent"] = api.ModuleStorage(mpc)!.MPAI_AIFM_RuledStorage_SetRule("Notes",
            new AIF.SharedStorage.StorageRule([Ua], [Ua], AIF.SharedStorage.StorageTime.Scope)).ToString();

        result["the rule narrowed: readers the User Agent only"] = Rule($"category Notes: writers {Swr}; readers {Ua}; time Session");
        result["n, after the narrowing: TST-SRD, the User Agent"] = Read("n") + " | " + Ua_Get(api, mpc, "n");
        result["the central control reads m, whose readers leave it out"] = Rule("read m");
        result["the central control lists"] = Rule("list");
        api.StopFlow(mpc);

        // A StorageControl that names an AIM not of the Module.
        try { api.StartFlow("1TST-MPX-V1.0-I01"); result["TST-MPX"] = "started"; api.StopFlow("1TST-MPX-V1.0-I01"); }
        catch (InvalidOperationException refused) { result["TST-MPX"] = "refused: " + refused.Message; }

        Expected.Match("storage-central.json", result);
    }
    // SHARED STORAGE OF SEVERAL MODULES (M3219 3.3): the Modules given one
    // location share its Shared Storage, each datum under its rules; without
    // readers named, every Module reads it, as Shared Storage always was read.
    [Fact]
    public void SharedAcrossModules()
    {
        var result = new Dictionary<string, string>();
        using var api = Api();
        const string sha = "1TST-SHA-V1.0-I01", shb = "1TST-SHB-V1.0-I01", mpc = "1TST-MPC-V1.0-I01", mps = "1TST-MPS-V1.0-I01";
        var location = Location();
        foreach (var m in new[] { sha, shb }) { api.StartFlow(m); api.SharedStorageInit(m, location); }
        string A(string text) => Outputs(api.Advance(sha, [new(Text, 1, "shared: " + text)]));
        string B(string key) => Outputs(api.Advance(shb, [new(Text, 1, "shared: " + key)]));
        string Ua(string key)
        {
            var outcome = api.SharedStorage(sha)!.MPAI_AIFM_RuledStorage_Get(key, out var data);
            return outcome == AIF.SharedStorage.StorageOutcome.OK ? Encoding.UTF8.GetString(data) : outcome.ToString();
        }

        result["s1, the plain call: TST-SHB, the User Agent"] = A("s1=v1") + " | " + B("s1") + " | " + Ua("s1");
        result["s2, readers TST-SHB: TST-SHB, the User Agent"] = A($"s2=v2; readers={shb}") + " | " + B("s2") + " | " + Ua("s2");
        result["s3, readers its own Module: TST-SHB"] = A($"s3=v3; readers={sha}") + " | " + B("s3");

        // A Module given another location reaches none of it. Under a User Agent
        // of its own: one User Agent retains an AIM across its Modules, and the
        // AIM keeps the storage of the first (M3205 Section 8).
        using (var other = Api())
        {
            other.StartFlow(mps); other.SharedStorageInit(mps, Location());
            result["TST-MPS, elsewhere, reads s1"] = Outputs(other.Advance(mps, [new(Text, 2, "shared: s1")]));
            other.StopFlow(mps);
        }

        // A central control: the StorageControl of TST-MPC, named by the User Agent.
        api.StartFlow(mpc);
        result["TST-SHA named as central control (it has no StorageControl)"] = api.SharedStorageInit(sha, location, governs: true).ToString();
        result["TST-MPC named as central control"] = api.SharedStorageInit(mpc, location, governs: true).ToString();
        string Rule(string text) => Outputs(api.Advance(mpc, [new(Text, 3, "shared: " + text)]));
        result["s4, the plain call before any rule"] = A("s4=v4");
        result["the rule of Data"] = Rule($"category Data: writers {sha}; readers {shb},{sha}");
        result["s4 written, read by TST-SHB"] = A("s4=v4") + " | " + B("s4");
        result["s5, readers narrowed to TST-SHA: TST-SHB"] = A($"s5=v5; readers={sha}") + " | " + B("s5");
        result["s6, a reader beyond the rule (TST-MPS)"] = A($"s6=v6; readers={mps}");
        result["the central control reads s5"] = Rule("read s5");
        foreach (var m in new[] { sha, shb, mpc }) api.StopFlow(m);

        Expected.Match("storage-shared.json", result);
    }
}

// The test AIMs of Phase 6. TST-SWR and TST-SRD write and read the storage they
// are given; TST-SGV sets rules; TST-TRE echoes; TST-SNK counts.
public sealed class StorageAims : IAimProvider
{
    private readonly TestAims phase3 = new();

    public bool CanCreate(string aimName) => aimName.StartsWith("1TST-", StringComparison.Ordinal);

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage) =>
        Create(aimName, settings, storage, null, null);

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage,
                                AIF.SharedStorage.ISharedStorage? privateStorage, AIF.SharedStorage.IRuledStorage? moduleStorage) =>
        aimName switch
        {
            "1TST-SWR-V1.0-I01" => new Aim(aimName, m => ("Written", Write(storage, moduleStorage, In(m)))),
            "1TST-SRD-V1.0-I01" => new Aim(aimName, m => ("Read", Read(storage, moduleStorage, In(m)))),
            "1TST-SGV-V1.0-I01" => new Aim(aimName, m => ("Ruled", In(m).StartsWith("shared:")
                                                              ? Rule(storage as AIF.SharedStorage.IRuledStorage, In(m)[7..].Trim())
                                                              : Rule(moduleStorage, In(m)))),
            "1TST-TRE-V1.0-I01" => new Echo(aimName),
            "1TST-SNK-V1.0-I01" => new Sink(aimName),
            _ => phase3.Create(aimName, settings, storage)
        };

    private static string In(Message m) => m.Ports.Values.FirstOrDefault() ?? "";

    // "key=value; category=C; readers=A,B; time=Module 200ms" into the Module's
    // Private Storage; "shared: key=value" into the Shared Storage.
    private static string Write(AIF.SharedStorage.ISharedStorage? shared, AIF.SharedStorage.IRuledStorage? mine, string text)
    {
        var toShared = text.StartsWith("shared:");
        var parts = text[(toShared ? 7 : 0)..].Split(';', StringSplitOptions.TrimEntries);
        var kv = parts[0].Split('=', 2);
        var options = parts.Skip(1).Select(o => o.Split('=', 2)).ToDictionary(o => o[0], o => o[1]);
        if (toShared)
        {
            if (shared is null) return "no Shared Storage";
            if (shared is not AIF.SharedStorage.IRuledStorage ruled)
            {
                shared.MPAI_AIFM_SharedStorage_Put(kv[0], Encoding.UTF8.GetBytes(kv[1]));
                return $"{kv[0]} written" + (options.Count > 0 ? $"; not given: {string.Join(", ", options.Keys)}" : "");
            }
            if (options.Count == 0)
            {
                // The plain call, as every AIM makes it.
                try { shared.MPAI_AIFM_SharedStorage_Put(kv[0], Encoding.UTF8.GetBytes(kv[1])); return $"{kv[0]}: OK"; }
                catch (UnauthorizedAccessException) { return $"{kv[0]}: NotAuthorised"; }
            }
            mine = ruled;
        }
        if (mine is null) return "no Private Storage of the Module";
        var outcome = mine.MPAI_AIFM_RuledStorage_Put(kv[0], Encoding.UTF8.GetBytes(kv[1]),
            options.GetValueOrDefault("category", "Data"),
            options.TryGetValue("readers", out var r) ? r.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : null,
            options.TryGetValue("time", out var t) ? AIF.SharedStorage.StorageTime.Parse(t) : null);
        return $"{kv[0]}: {outcome}";
    }

    // "key" from the Module's Private Storage; "shared: key" from the Shared Storage.
    private static string Read(AIF.SharedStorage.ISharedStorage? shared, AIF.SharedStorage.IRuledStorage? mine, string text)
    {
        if (text.StartsWith("shared:"))
        {
            if (shared is null) return "no Shared Storage";
            if (shared is AIF.SharedStorage.IRuledStorage ruled) return Read(null, ruled, text[7..].Trim());
            try { return Encoding.UTF8.GetString(shared.MPAI_AIFM_SharedStorage_Get(text[7..].Trim())); }
            catch (Exception refused) { return $"{refused.GetType().Name}: {refused.Message}"; }
        }
        if (mine is null) return "no Private Storage of the Module";
        var outcome = mine.MPAI_AIFM_RuledStorage_Get(text, out var data);
        return outcome == AIF.SharedStorage.StorageOutcome.OK ? Encoding.UTF8.GetString(data) : outcome.ToString();
    }

    // "category C: writers A,B; readers C,D; time Scope", "read key", "list".
    private static string Rule(AIF.SharedStorage.IRuledStorage? mine, string text)
    {
        if (mine is null) return "no Private Storage of the Module";
        if (text.StartsWith("read ")) return Read(null, mine, text[5..]);
        if (text == "list") return string.Join(",", mine.MPAI_AIFM_RuledStorage_List());
        var colon = text.IndexOf(':');
        var category = text[9..colon].Trim();
        var parts = text[(colon + 1)..].Split(';', StringSplitOptions.TrimEntries).ToDictionary(p => p.Split(' ', 2)[0], p => p.Split(' ', 2)[1]);
        string[] Names(string key) => parts.TryGetValue(key, out var v) && v != "none" ? v.Split(',', StringSplitOptions.TrimEntries) : [];
        var rule = new AIF.SharedStorage.StorageRule(Names("writers"), Names("readers"),
                                                     parts.TryGetValue("time", out var t) ? AIF.SharedStorage.StorageTime.Parse(t) : AIF.SharedStorage.StorageTime.Scope);
        return $"{category}: {mine.MPAI_AIFM_RuledStorage_SetRule(category, rule)}";
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
