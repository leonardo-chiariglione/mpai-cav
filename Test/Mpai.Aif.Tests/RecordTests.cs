using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

using AIF.Controller;
using AIF.SharedStorage;
using AIF.Store;
using Mpai.Aif.Api;

namespace Mpai.Aif.Tests;

// THE RECORD OF THE BOUNDARY (M3219 3.4): what the Controller saw cross a
// Module's boundary, with its stamps, in the Private Storage of the Module, for
// the User Agent to read - at its request, or always, where the Metadata says so.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class RecordTests
{
    private const string Text = StorageTests.Text;

    private static ControllerApi Api(string? amds = null) =>
        new(amds ?? StorageTests.Amds, Path.Combine(amds ?? StorageTests.Amds, "no-settings.json"),
            amds is null ? new StorageAims() : new RemoteAims());

    private static string Location() => Path.Combine(Path.GetTempPath(), "mpai-phase6-" + Guid.NewGuid().ToString("N"));

    // The records of a record, in their order: header and payloads apart.
    public static List<JsonObject> Records(IRuledStorage storage, string id) =>
        storage.MPAI_AIFM_RuledStorage_List(BoundaryRecord.Category, id + "/")
            .Where(k => !k.EndsWith("/header") && !k.Contains("/payload/"))
            .Select(k => storage.MPAI_AIFM_RuledStorage_Get(k, out var d) == StorageOutcome.OK ? JsonNode.Parse(d)!.AsObject() : new JsonObject())
            .ToList();

    public static JsonObject? Header(IRuledStorage storage, string id) =>
        storage.MPAI_AIFM_RuledStorage_Get(id + "/header", out var d) == StorageOutcome.OK ? JsonNode.Parse(d)!.AsObject() : null;

    private static string Totals(IReadOnlyDictionary<BoundaryRecord.PortKey, (long Recorded, long NotRecorded)>? totals) =>
        totals is null ? "none" : string.Join("; ", totals.OrderBy(t => t.Key.Direction).ThenBy(t => t.Key.PortNumber)
            .Select(t => $"{t.Key.Direction} #{t.Key.PortNumber} {t.Value.Recorded}" + (t.Value.NotRecorded > 0 ? $" (not {t.Value.NotRecorded})" : "")));

    private static string Payload(string reference, int length) =>
        new JsonObject { ["Entries"] = new JsonArray(new JsonObject { ["DataLength"] = length, ["DataURI"] = reference }) }.ToJsonString();

    // A CONTINUOUS MODULE RECORDED: three inputs at their own rates, a payload
    // among them, and their echoes.
    [Fact]
    public void Continuous()
    {
        var result = new Dictionary<string, string>();
        using var api = Api();
        const string rec = "1TST-REC-V1.0-I01";
        api.StartFlow(rec);
        result["Record_Start before the Module's scope is initialised"] = api.RecordStart(rec, out _).ToString();
        api.SharedStorageInit(rec, Location());
        result["Record_Start"] = api.RecordStart(rec, out var id).ToString();
        result["Record_Start again"] = api.RecordStart(rec, out _).ToString();

        var bytes = Enumerable.Range(0, 100_000).Select(i => (byte)(i % 251)).ToArray();
        for (var i = 0; i < 20; i++)
        {
            api.InputWrite(rec, Text, 1, $"a{i}", 2000);
            api.OutputRead(rec, Text, 1, 2000);
            if (i % 4 == 0) { api.InputWrite(rec, Text, 2, $"b{i / 4}", 2000); api.OutputRead(rec, Text, 2, 2000); }
            if (i % 10 == 0)
            {
                var (_, reference) = api.PayloadPut(rec, Text, 3, bytes);
                api.InputWrite(rec, Text, 3, Payload(reference!, bytes.Length), 2000);
                api.OutputRead(rec, Text, 3, 2000);
            }
            Thread.Sleep(5);
        }
        result["the Module's status while recorded"] = Totals(api.Status(rec).Record);
        result["Record_Stop"] = api.RecordStop(rec, out var totals) + ": " + Totals(totals);
        result["Record_Stop again"] = api.RecordStop(rec, out _).ToString();

        var storage = api.ModuleStorage(rec)!;
        var records = Records(storage, id!);
        result["records read by the User Agent"] = records.Count.ToString();
        result["their sequence"] = records.Select(r => (long)r["Sequence"]!).SequenceEqual(Enumerable.Range(1, records.Count).Select(n => (long)n)) ? "1 to " + records.Count : "not in order";
        result["their stamps, in the order of the sequence"] = records.Select(r => DateTimeOffset.Parse((string)r["Stamp"]!)).Zip(records.Skip(1).Select(r => DateTimeOffset.Parse((string)r["Stamp"]!))).All(p => p.First <= p.Second) ? "never going back" : "going back";
        result["each echo recorded after its input"] = records.Where(r => (string)r["Direction"]! == "Out" && (int)r["PortNumber"]! == 1)
            .All(o => records.Any(i => (string)i["Direction"]! == "In" && (string)i["Json"]! == (string)o["Json"]! && (long)i["Sequence"]! < (long)o["Sequence"]!)) ? "yes" : "no";
        var payloadRecords = records.Where(r => (int)r["PortNumber"]! == 3).ToList();
        var kept = payloadRecords.Select(r => (string)JsonNode.Parse((string)r["Json"]!)!["Entries"]![0]!["DataURI"]!).ToList();
        result["the payloads, in the records"] = $"{kept.Count}, each a datum of its own: " + (kept.All(k => k.StartsWith("record:payload/")) ? "record:payload/<n>" : string.Join(", ", kept));
        result["the payloads, byte for byte"] = kept.All(uri => storage.MPAI_AIFM_RuledStorage_Get(id + "/payload/" + uri["record:payload/".Length..], out var d) == StorageOutcome.OK && d.SequenceEqual(bytes))
            ? $"{kept.Count} of {kept.Count}" : "not all";
        var header = Header(storage, id!);
        result["the header"] = header is null ? "none" : $"{header["Module"]}, always {header["Always"]}, ports: " +
            string.Join("; ", header["Ports"]!.AsArray().Select(p => $"{p!["Direction"]} #{p["PortNumber"]} {p["Recorded"]}/{p["NotRecorded"]}"));
        storage.MPAI_AIFM_RuledStorage_Trace(id + "/header", out var trace);
        result["the header's Trace"] = trace is null ? "none" : $"{trace.Writer}, {trace.Category}, readers {string.Join(",", trace.Readers ?? [])}";
        result["the User Agent writes into it"] = storage.MPAI_AIFM_RuledStorage_Put(id + "/header", [1], BoundaryRecord.Category).ToString();
        api.StopFlow(rec);

        Expected.Match("record-continuous.json", result);
    }

    // ALWAYS ON, AN EXCHANGE, AN AIM THAT MAY NOT READ IT.
    [Fact]
    public void AlwaysAndExchange()
    {
        var result = new Dictionary<string, string>();

        // TST-RCA: its Metadata declares Record Always.
        {
            using var api = Api();
            const string rca = "1TST-RCA-V1.0-I01";
            var location = Location();
            api.StartFlow(rca);
            api.SharedStorageInit(rca, location);
            var id = api.RecordId(rca);
            result["TST-RCA recorded without being asked"] = id is null ? "no" : "yes";
            for (var i = 0; i < 3; i++) { api.InputWrite(rca, Text, 1, $"x{i}", 2000); api.OutputRead(rca, Text, 1, 2000); }
            result["TST-RCA: Record_Stop"] = api.RecordStop(rca, out _).ToString();
            api.StopFlow(rca);
            var storage = api.ModuleStorageAt(rca, location);
            var records = Records(storage, id!);
            result["TST-RCA, after its Stop: what is recorded"] = string.Join(" ", records.Select(r => $"{r["Direction"]}:{r["Json"]}"));
            result["TST-RCA, its header"] = Header(storage, id!) is { } h ? $"always {h["Always"]}" : "none";
        }

        // TST-REX: an exchange, exchange by exchange.
        {
            using var api = Api();
            const string rex = "1TST-REX-V1.0-I01";
            api.StartFlow(rex);
            api.SharedStorageInit(rex, Location());
            api.RecordStart(rex, out var id);
            api.Advance(rex, [new(Text, 1, "hello")]);
            api.Advance(rex, [new(Text, 1, "world")]);
            api.RecordStop(rex, out var totals);
            result["TST-REX: exchange by exchange"] = string.Join(" ", Records(api.ModuleStorage(rex)!, id!).Select(r => $"{r["Direction"]}:{r["Json"]}"));
            result["TST-REX: Record_Stop"] = Totals(totals);
            api.StopFlow(rex);
        }

        // TST-MPS: its reader AIM may not read the record; the User Agent may.
        {
            using var api = Api();
            const string mps = "1TST-MPS-V1.0-I01";
            api.StartFlow(mps);
            api.SharedStorageInit(mps, Location());
            api.RecordStart(mps, out var id);
            api.Advance(mps, [new(Text, 2, "nothing")]);
            api.RecordStop(mps, out _);
            result["TST-MPS: its AIM reads the record's header"] = Outputs(api.Advance(mps, [new(Text, 2, id + "/header")]));
            result["TST-MPS: the User Agent reads it"] = Header(api.ModuleStorage(mps)!, id!) is null ? "no" : "yes";
            api.StopFlow(mps);
        }

        Expected.Match("record-always.json", result);
    }

    private static string Outputs(ControllerApi.Result run) =>
        $"{run.Error}; " + string.Join("; ", run.Outputs.OrderBy(o => o.PortNumber).Select(o => $"#{o.PortNumber} '{o.Json}'"));

    // A RECORD THAT CANNOT KEEP UP: the Module is not held; the Messages not
    // recorded are counted, and the User Agent told.
    [Fact]
    public void CannotKeepUp()
    {
        var result = new Dictionary<string, string>();
        using var api = Api();
        api.Controller.RecordQueueDepth = 2;
        api.Controller.RecordWriteDelay = TimeSpan.FromMilliseconds(30);
        const string rec = "1TST-REC-V1.0-I01";
        api.StartFlow(rec);
        api.SharedStorageInit(rec, Location());
        api.RecordStart(rec, out _);
        var clock = Stopwatch.StartNew();
        var echoed = 0;
        for (var i = 0; i < 40; i++)
        {
            api.InputWrite(rec, Text, 1, $"a{i}", 2000);
            if (api.OutputRead(rec, Text, 1, 2000).Ok) echoed++;
        }
        var took = clock.Elapsed;
        var status = api.Status(rec).Record;
        api.RecordStop(rec, out var totals);
        result["40 inputs, their echoes"] = $"{echoed} of 40";
        result["the Module held by its record"] = took < TimeSpan.FromSeconds(40 * 2 * 0.03) ? "no" : $"yes: {took.TotalSeconds:0.0} s";
        result["the status told Messages not recorded"] = status is not null && status.Values.Any(v => v.NotRecorded > 0) ? "yes" : "no";
        result["Record_Stop: recorded and not recorded, all 80 accounted for"] =
            totals!.Values.Sum(v => v.Recorded + v.NotRecorded) == 80 && totals.Values.Any(v => v.NotRecorded > 0) ? "yes" : Totals(totals);
        api.StopFlow(rec);
        Expected.Match("record-cannot-keep-up.json", result);
    }

    private sealed class TestClock(DateTimeOffset epoch) : AIF.Channels.IClock
    {
        private readonly long start = Stopwatch.GetTimestamp();
        public DateTimeOffset Now => epoch + Stopwatch.GetElapsedTime(start);
        public long Monotonic => Stopwatch.GetTimestamp();
    }

    // THE STAMPS ARE THE CONTROLLER'S, on the clock plugged in; and a Module with
    // an AIM on a host is recorded as the same Module all local.
    [Fact]
    public async Task StampsAndHost()
    {
        var result = new Dictionary<string, string>();

        {
            var store = new AmdStore(StorageTests.Amds);
            store.Scan();
            var ua = new UserAgent(store, null, new TestClock(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)));
            ua.MPAI_AIFU_Controller_Initialize();
            ua.MPAI_AIFU_MODULE_Start("1TST-REC-V1.0-I01", new StorageAims(), AimSettings.Empty, out var id);
            var location = Location();
            ua.MPAI_AIFU_SharedStorage_Init(id, location);
            ua.MPAI_AIFU_Record_Start(id, out var recordId);
            await ua.ContinuousWriteAsync(id, Text, 1, "t", 2000);
            await ua.ContinuousReadAsync(id, Text, 1, 2000);
            ua.MPAI_AIFU_Record_Stop(id, out _);
            var records = Records(ua.ModuleStorage(id)!, recordId!);
            result["on a test clock of 2030: the stamps"] = string.Join(", ", records.Select(r => DateTimeOffset.Parse((string)r["Stamp"]!).Year).Distinct());
            ua.MPAI_AIFU_MODULE_Stop(id);
        }

        // TST-RXC with three of its AIMs on a host, and TST-RXL all local: the same
        // exchanges recorded alike. (A loop is not compared: when its feedback
        // arrives depends on the time the hop to the host takes, and so does what
        // it gives.)
        using var hostProcess = new HostProcess();
        var seen = new Dictionary<string, string>();
        foreach (var (module, hosted) in new[] { ("1TST-RXL-V1.0-I01", false), ("1TST-RXC-V1.0-I01", true) })
        {
            using var api = Api(RemoteTests.Amds);
            if (hosted)
            {
                api.Controller.AimHostKey = HostProcess.Key;
                foreach (var aim in new[] { "1TST-UPP-V1.0-I01", "1TST-SLP-V1.0-I01", "1TST-RPT-V1.0-I01" }) api.Controller.AimHosts[aim] = hostProcess.Address;
            }
            api.StartFlow(module);
            api.SharedStorageInit(module, Location());
            api.RecordStart(module, out var recordId);
            foreach (var x in new[] { "x", "y" }) api.Advance(module, [new(Text, 1, x)]);
            api.RecordStop(module, out _);
            // The outputs of one exchange reach the boundary in no fixed order.
            seen[module] = string.Join(" ", Records(api.ModuleStorage(module)!, recordId!)
                .Select(r => $"{r["Direction"]}#{r["PortNumber"]}:{r["Json"]}").Order(StringComparer.Ordinal));
            api.StopFlow(module);
        }
        result["TST-RXL, all local"] = seen["1TST-RXL-V1.0-I01"];
        result["TST-RXC, three AIMs on a host: the same"] = seen["1TST-RXC-V1.0-I01"] == seen["1TST-RXL-V1.0-I01"] ? "yes" : seen["1TST-RXC-V1.0-I01"];

        Expected.Match("record-stamps-host.json", result);
    }
}
