using System.Diagnostics;

using AIF.Controller;
using AIF.Store;
using Mpai.Aif.Api;

namespace Mpai.Aif.Tests;

// The Controller API of Phase 3 (M3213 3.8), on test Modules made for the purpose
// from test AIMs whose behaviour is exact. Their L3s are in Test/Data/Phase3, not
// in AIMs/AMDs: they are not AIMs anybody would use.
//
// Step 1 records what the Controller does today, failures included; each later
// step changes the record as it changes the Controller.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
[Collection(Timing.Name)]
public class ControllerTests
{
    public const string Text = "TST-TXT-V1.0";

    public static string Amds => Path.Combine(Repository.Root, "Test", "Data", "Phase3");

    private static ControllerApi Api() =>
        new(Amds, Path.Combine(Amds, "no-settings.json"), new TestAims());

    private static string Shown(ControllerApi.Read read) =>
        read.Error + (read.Json is { } v ? $" '{v}'" : "");

    private static string Show(ControllerApi.Result result, params int[] numbers) =>
        $"{result.Error}; " + string.Join("; ", numbers.Select(n => $"#{n} " + (result.ByType(Text, n) is { } v ? $"'{v}'" : "absent")));

    // Each of two same-typed outputs of one AIM is to reach its own destination;
    // a flow into an Input group, every Port of the group and no other.
    [Fact]
    public void Routing()
    {
        using var api = Api();
        var result = api.Advance("1TST-RTE-V1.0-I01", [new ControllerApi.Datum(Text, "x")]);
        var groups = api.Advance("1TST-GRP-V1.0-I01", [new ControllerApi.Datum(Text, "x")]);

        Expected.Match("controller-routing.json", new Dictionary<string, string>
        {
            ["TST-RTE: TST-SPL's first output to TST-UPP (#1), its second to TST-REV (#2)"] = Show(result, 1, 2),
            ["TST-GRP: into TST-GIN's Input 1 - its Ports 1 (TST-UPP, #1) and 2 (TST-REV, #2), not 3 (TST-ECH, #3)"] = Show(groups, 1, 2, 3)
        });
    }

    // What a User Agent is told, writing and reading Port by Port: a Port that
    // produced, one that produced nothing, one whose AIM threw, one that does not
    // exist, a datum of a Data Type the Port does not accept, a Module not started.
    [Fact]
    public void Outcomes()
    {
        using var api = Api();
        const string module = "1TST-DGR-V1.0-I01";
        var result = new Dictionary<string, string>
        {
            ["write, Module not started"]         = api.InputWrite(module, Text, 1, "x").ToString(),
            ["read, Module not started"]          = api.OutputRead(module, Text, 1).Error.ToString()
        };

        api.StartFlow(module);
        result["write, a Port it declares"]                   = api.InputWrite(module, Text, 1, "x").ToString();
        result["write, a Port it does not declare (#2)"]      = api.InputWrite(module, Text, 2, "x").ToString();
        result["write, a Data Type it has no Port for"]       = api.InputWrite(module, "TST-NUM-V1.0", 1, "1").ToString();
        result["write, an Object of a Data Type the Port does not accept"] =
            api.InputWrite(module, Text, 1, "{\"Header\": \"TST-NUM-V1.0\"}").ToString();
        result["read, a Port that produced (#1)"]             = Shown(api.OutputRead(module, Text, 1));
        result["read, a declared Port that produced nothing (#2)"] = Shown(api.OutputRead(module, Text, 2));
        result["read, a Port whose AIM threw (#3)"]           = Shown(api.OutputRead(module, Text, 3));
        result["read, a Port it does not declare (#9)"]       = Shown(api.OutputRead(module, Text, 9));
        result["read, a Data Type it has no Port for"]        = Shown(api.OutputRead(module, "TST-NUM-V1.0", 1));
        result["read again, without writing: that run's"]     = Shown(api.OutputRead(module, Text, 1));
        api.StopFlow(module);

        result["Advance, a Module that is not in the Store"] =
            api.Advance("1TST-XXX-V1.0-I01", [new ControllerApi.Datum(Text, "x")]).Error.ToString();

        Expected.Match("controller-outcomes.json", result);
    }

    // The status of each AIM - ALIVE, DEGRADED, DEAD - and the reports of a run;
    // OnDegraded StopModule, StopAIM and Continue; StopAim from the User Agent and
    // from an AIM (M3213 3.5).
    [Fact]
    public void Degradation()
    {
        var result = new Dictionary<string, string>();
        using var api = Api();

        // Continue: the others run on what they have; the AIM that threw runs again.
        const string cont = "1TST-DGR-V1.0-I01";
        api.StartFlow(cont);
        api.InputWrite(cont, Text, 1, "x");
        result["Continue: outputs"] = string.Join("; ", new[] { 1, 2, 3, 4 }.Select(n => $"#{n} " + Shown(api.OutputRead(cont, Text, n))));
        result["Continue: status after a run"] = StatusOf(api.Status(cont));
        api.InputWrite(cont, Text, 1, "y");
        result["Continue: the next run"] = Shown(api.OutputRead(cont, Text, 1)) + "; " + StatusOf(api.Status(cont), "1TST-THR-V1.0-I01");

        // StopAim from the User Agent.
        result["StopAim 1TST-UPP-V1.0-I01"] = api.StopAim(cont, "1TST-UPP-V1.0-I01").ToString();
        result["StopAim an AIM the Module does not have"] = api.StopAim(cont, "1TST-XXX-V1.0-I01").ToString();
        api.InputWrite(cont, Text, 1, "z");
        result["after StopAim, #1"] = Shown(api.OutputRead(cont, Text, 1));
        result["after StopAim, status"] = StatusOf(api.Status(cont), "1TST-UPP-V1.0-I01");
        api.StopFlow(cont);

        // StopAIM: the AIM that threw is stopped, and skipped from then on.
        const string aim = "1TST-DGA-V1.0-I01";
        api.StartFlow(aim);
        api.InputWrite(aim, Text, 1, "x");
        result["StopAIM: outputs"] = string.Join("; ", new[] { 1, 3 }.Select(n => $"#{n} " + Shown(api.OutputRead(aim, Text, n))));
        result["StopAIM: status"] = StatusOf(api.Status(aim), "1TST-THR-V1.0-I01");
        api.InputWrite(aim, Text, 1, "y");
        result["StopAIM: the next run"] = Shown(api.OutputRead(aim, Text, 1)) + "; " + StatusOf(api.Status(aim), "1TST-THR-V1.0-I01");
        api.StopFlow(aim);

        // StopModule, the default: the run ends, its outputs are not produced, and
        // the Module is stopped.
        const string stop = "1TST-DGS-V1.0-I01";
        api.StartFlow(stop);
        api.InputWrite(stop, Text, 1, "x");
        result["StopModule: #1"] = Shown(api.OutputRead(stop, Text, 1));
        result["StopModule: a write after"] = api.InputWrite(stop, Text, 1, "y").ToString();
        result["StopModule: status"] = StatusOf(api.Status(stop));
        result["StopModule: started again"] = api.StartFlow(stop) + ", " + api.InputWrite(stop, Text, 1, "z");
        api.StopFlow(stop);

        // An AIM stopping another (MPAI_AIFM_AIM_Stop).
        const string kill = "1TST-KLM-V1.0-I01";
        api.StartFlow(kill);
        api.InputWrite(kill, Text, 1, "x");
        result["MPAI_AIFM_AIM_Stop: outputs"] = string.Join("; ", new[] { 1, 2 }.Select(n => $"#{n} " + Shown(api.OutputRead(kill, Text, n))));
        result["MPAI_AIFM_AIM_Stop: status"] = StatusOf(api.Status(kill), "1TST-UPP-V1.0-I01");
        api.StopFlow(kill);

        result["Status, Module not started"] = api.Status(kill).Error.ToString();

        Expected.Match("controller-degradation.json", result);
    }

    // The workflow's 'on Degraded:', acting according to its context (M3213 3.5).
    [Fact]
    public async Task OnDegraded()
    {
        var result = new Dictionary<string, string>();
        foreach (var context in new[] { "public", "medical" })
        {
            using var api = Api();
            var said = new List<string>();
            var devices = new Mpai.Rca.DeviceRegistry()
                .RegisterAcquire(Text, (vad, wanted) => Task.FromResult<string?>("x"));
            var interpreter = new Mpai.Rca.WorkflowInterpreter(api.Async(), devices, said.Add);

            var workflow = new Mpai.Wdl.WorkflowReader().Read($$"""
                workflow TST over 1TST-DGR-V1.0-I01
                on Start:
                    ask Controller to start
                    set Context = "{{context}}"
                    acquire Words (TST-TXT-V1.0)
                    offer Words (TST-TXT-V1.0)
                    ask Upper (TST-TXT-V1.0:1)
                    acquire Again (TST-TXT-V1.0)
                    offer Again (TST-TXT-V1.0)
                    ask UpperAgain (TST-TXT-V1.0:1)
                on Degraded:
                    branch on Status contains "1TST-THR-V1.0-I01 DEGRADED" {
                        ask Controller to stop AIM 1TST-THR-V1.0-I01
                    }
                    branch on Context contains "medical" {
                        ask Controller to stop
                    }
                """);

            try { await interpreter.RunAsync(workflow, CancellationToken.None); }
            catch (Exception failure) { said.Add("ended: " + failure.Message); }

            result[$"context {context}"] = string.Join(" / ", said.Where(s =>
                s.StartsWith("on Degraded") || s.StartsWith("[C] stopped") || s.StartsWith("[C] give") || s.StartsWith("ended")));
        }

        Expected.Match("controller-ondegraded.json", result);
    }

    // Pause, Resume and Stop, on an AIM that takes 300 ms and honours both, at
    // the top of its Module and one composite down.
    [Fact]
    public async Task Lifecycle()
    {
        var result = new Dictionary<string, string>();

        foreach (var module in new[] { "1TST-SLW-V1.0-I01", "1TST-NST-V1.0-I01" })
        {
            // Paused during a run.
            var (ua, id) = Start(module);
            var run = ua.RunAsync(id, Boundary("x"));
            await Task.Delay(100);
            ua.MPAI_AIFU_MODULE_Pause(id);
            result[$"{module}: Pause during a run"] = await Within(run, 1000) ? "the run completed: not held" : "held";
            ua.MPAI_AIFU_MODULE_Resume(id);
            result[$"{module}: Resume"] = await Within(run, 2000) ? "the run completed" : "still held";

            // Paused, then a run started.
            ua.MPAI_AIFU_MODULE_Pause(id);
            var next = ua.RunAsync(id, Boundary("y"));
            result[$"{module}: a run started while paused"] = await Within(next, 1000) ? "completed: not held" : "held";
            ua.MPAI_AIFU_MODULE_Resume(id);
            await Within(next, 2000);

            // Stopped during a run.
            var stopped = ua.RunAsync(id, Boundary("z"));
            await Task.Delay(100);
            ua.MPAI_AIFU_MODULE_Stop(id);
            if (await Within(stopped, 2000))
            {
                var (_, outcome) = await stopped;
                var message = outcome?.Completed;
                result[$"{module}: Stop during a run"] =
                    message is null ? "no outcome"
                    : message.IsCancelled ? "the AIM was stopped"
                    : message.Ports.Count > 0 ? "the AIM was not signalled: the run completed with its output"
                    : "the run completed without output";
            }
            else result[$"{module}: Stop during a run"] = "the run did not end";
        }

        // Through the Controller API, paused before a write: the read observes its
        // timeout; after Resume the run completes.
        using (var api = Api())
        {
            const string module = "1TST-NST-V1.0-I01";
            api.StartFlow(module);
            result["Controller API: Pause"] = api.Pause(module).ToString();
            api.InputWrite(module, Text, 1, "p");
            result["Controller API: a read with timeout 600 while paused"] = Shown(api.OutputRead(module, Text, 1, 600));
            result["Controller API: Resume"] = api.Resume(module).ToString();
            result["Controller API: a read after Resume"] = Shown(api.OutputRead(module, Text, 1, 3000));
            api.StopFlow(module);
            result["Controller API: Pause, Module not started"] = api.Pause(module).ToString();
        }

        Expected.Match("controller-lifecycle.json", result);
    }

    // Whether a call can be bounded in time: a read that does not wait, one whose
    // time runs out, one that waits without limit; the run continuing after a
    // TIMEOUT, and a later read returning its result.
    [Fact]
    public void Time()
    {
        using var api = Api();
        const string module = "1TST-SLW-V1.0-I01";
        api.StartFlow(module);
        var result = new Dictionary<string, string>();

        api.InputWrite(module, Text, 1, "a");
        result["read with timeout 0, on an AIM that takes 300 ms"]   = Shown(api.OutputRead(module, Text, 1, 0));
        result["then a read with timeout 100"]                        = Shown(api.OutputRead(module, Text, 1, 100));
        result["then a read without limit: the run continued"]        = Shown(api.OutputRead(module, Text, 1, -1));

        api.InputWrite(module, Text, 1, "b");
        var clock = Stopwatch.StartNew();
        var waited = api.OutputRead(module, Text, 1, 2000);
        clock.Stop();
        result["a read with timeout 2000"] = Shown(waited) + (clock.ElapsedMilliseconds >= 280 ? ", after the AIM's 300 ms" : ", before the AIM's 300 ms");
        api.StopFlow(module);

        Expected.Match("controller-time.json", result);
    }

    // Shared Storage: offsets (M3203 4.10.1, 4.10.2) and the scope of one Module
    // initialised where the User Agent says (3.4.1) - after its AIMs hold their
    // handles.
    [Fact]
    public void SharedStorage()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpai-phase3-" + Guid.NewGuid().ToString("N"));
        var result = new Dictionary<string, string>();
        try
        {
            AIF.SharedStorage.ISharedStorage s = new AIF.SharedStorage.FileSharedStorage(Path.Combine(root, "plain"), "test", "local");
            string Text(byte[] b) => string.Concat(b.Select(x => x == 0 ? "0" : ((char)x).ToString()));
            string Value(string k) => Text(s.MPAI_AIFM_SharedStorage_Get(k));
            byte[] B(string v) => System.Text.Encoding.ASCII.GetBytes(v);

            s.MPAI_AIFM_SharedStorage_Put("k", B("abc"), 0);
            result["Put 'abc' at 0"] = Value("k");
            s.MPAI_AIFM_SharedStorage_Put("k", B("XY"), 5);
            result["then 'XY' at 5, beyond the end"] = Value("k");
            s.MPAI_AIFM_SharedStorage_Put("k", B("Z"), 1);
            result["then 'Z' at 1, within"] = Value("k");
            s.MPAI_AIFM_SharedStorage_Put("k", B("hi"), 0);
            result["then 'hi' at 0: the value replaced"] = Value("k");
            s.MPAI_AIFM_SharedStorage_Put("new", B("ab"), 2);
            result["Put 'ab' at 2 to a new key"] = Value("new");
            result["Get 'new' from 1, 2 bytes"] = Text(s.MPAI_AIFM_SharedStorage_Get("new", 1, 2));
            result["Get 'new' from 3, 10 bytes"] = Text(s.MPAI_AIFM_SharedStorage_Get("new", 3, 10));
            result["Get 'new' from 4, the end"] = "'" + Text(s.MPAI_AIFM_SharedStorage_Get("new", 4, 10)) + "'";
            try { s.MPAI_AIFM_SharedStorage_Get("new", 5, 1); result["Get 'new' from 5, beyond the end"] = "returned"; }
            catch (ArgumentOutOfRangeException) { result["Get 'new' from 5, beyond the end"] = "an error"; }
            result["GetKeyInfo 'k' Length"] = s.MPAI_AIFM_SharedStorage_GetKeyInfo("k").Length.ToString();

            // Two Modules; the second initialised elsewhere after it started.
            var store = new AmdStore(Amds);
            store.Scan();
            var ua = new UserAgent(store, Path.Combine(root, "default"));
            ua.MPAI_AIFU_Controller_Initialize();
            var aimsA = new TestAims();
            var aimsB = new TestAims();
            ua.MPAI_AIFU_MODULE_Start("1TST-RTE-V1.0-I01", aimsA, AimSettings.Empty, out var a);
            ua.MPAI_AIFU_MODULE_Start("1TST-GRP-V1.0-I01", aimsB, AimSettings.Empty, out var b);
            result["SharedStorage_Init of the second Module"] =
                ua.MPAI_AIFU_SharedStorage_Init(b, Path.Combine(root, "elsewhere")).ToString();
            result["SharedStorage_Init of a Module not started"] =
                ua.MPAI_AIFU_SharedStorage_Init(999, Path.Combine(root, "nowhere")).ToString();

            aimsA.Storage["1TST-UPP-V1.0-I01"]!.MPAI_AIFM_SharedStorage_Put("from", B("RTE"));
            aimsB.Storage["1TST-ECH-V1.0-I01"]!.MPAI_AIFM_SharedStorage_Put("from", B("GRP"));
            string Held(string folder) =>
                Directory.Exists(Path.Combine(root, folder))
                    ? Value2(new AIF.SharedStorage.FileSharedStorage(Path.Combine(root, folder), "test", "local"))
                    : "no scope";
            string Value2(AIF.SharedStorage.ISharedStorage st) =>
                st.MPAI_AIFM_SharedStorage_Exists("from")
                    ? Text(st.MPAI_AIFM_SharedStorage_Get("from")) + " by " + st.MPAI_AIFM_SharedStorage_GetKeyInfo("from").StoredBy
                    : "nothing";
            result["the default location holds"] = Held("default");
            result["the second Module's location holds"] = Held("elsewhere");
            ua.MPAI_AIFU_MODULE_Stop(a);
            ua.MPAI_AIFU_MODULE_Stop(b);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        Expected.Match("controller-sharedstorage.json", result);
    }

    // ---------------------------------------------------------------------

    // Each AIM's status, why, and what it reported; or one AIM's.
    private static string StatusOf(ControllerApi.ModuleStatus status, string? aim = null) =>
        !status.Ok ? status.Error.ToString()
        : string.Join("; ", status.Aims.Where(a => aim is null || a.Aim == aim).Select(a =>
            $"{a.Aim} {a.Status}" + (a.Reason.Length > 0 ? $" ({a.Reason})" : "") +
            (a.Reports.Count > 0 ? $" reported '{string.Join("', '", a.Reports)}'" : "")));

    private static (UserAgent, int) Start(string module)
    {
        var store = new AmdStore(Amds);
        store.Scan();
        var ua = new UserAgent(store);
        ua.MPAI_AIFU_Controller_Initialize();
        var error = ua.MPAI_AIFU_MODULE_Start(module, new TestAims(), AimSettings.Empty, out var id);
        Assert.Equal(AifError.OK, error);
        return (ua, id);
    }

    private static Dictionary<string, string> Boundary(string text) => new() { [Text + "#1"] = text };

    private static async Task<bool> Within(Task task, int milliseconds) =>
        await Task.WhenAny(task, Task.Delay(milliseconds)) == task;
}

// The test AIMs. Each reads and writes its own Ports, by the names its own L3
// gives them, as any implementation of an L3 does.
public sealed class TestAims : IAimProvider
{
    // The Shared Storage handle the Controller gave each AIM.
    public Dictionary<string, AIF.SharedStorage.ISharedStorage?> Storage { get; } = new();

    public bool CanCreate(string aimName) => aimName.StartsWith("1TST-", StringComparison.Ordinal);

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage)
    {
        Storage[aimName] = storage;
        return Make(aimName);
    }

    private static IAimProcessor Make(string aimName) =>
        aimName switch
        {
            "1TST-SPL-V1.0-I01" => new TestAim(aimName, m => Out(("First", "A:" + In(m)), ("Second", "B:" + In(m)))),
            "1TST-UPP-V1.0-I01" => new TestAim(aimName, m => Out(("Upper", In(m).ToUpperInvariant()))),
            "1TST-REV-V1.0-I01" => new TestAim(aimName, m => Out(("Reversed", new string(In(m).Reverse().ToArray())))),
            "1TST-ECH-V1.0-I01" => new TestAim(aimName, m => Out(("Echo", In(m)))),
            "1TST-RPT-V1.0-I01" => new TestAim(aimName, m => { m.Context.Report("fell back to a simpler path"); return Out(("Reported", In(m))); }),
            "1TST-KIL-V1.0-I01" => new TestAim(aimName, m => { m.Context.StopAim("1TST-UPP-V1.0-I01"); return Out(("Killed", In(m))); }),
            "1TST-NOP-V1.0-I01" => new TestAim(aimName, m => Out()),
            "1TST-THR-V1.0-I01" => new TestAim(aimName, (Func<Message, Message>)(m => throw new InvalidOperationException("TST-THR throws"))),
            "1TST-SLP-V1.0-I01" => new TestAim(aimName, async m =>
            {
                for (var i = 0; i < 30; i++)
                {
                    await m.Context.CheckAsync();
                    await Task.Delay(10);
                }
                await m.Context.CheckAsync();
                return Out(("Slept", In(m)));
            }),
            _ => throw new InvalidOperationException($"No test AIM {aimName}.")
        };

    private static string In(Message m) => m.Ports.TryGetValue("Text", out var text) ? text : "";

    private static Message Out(params (string Port, string Value)[] ports) =>
        new() { Ports = ports.ToDictionary(p => p.Port, p => p.Value) };

    private sealed class TestAim : IAimProcessor
    {
        private readonly Func<Message, Task<Message>> run;

        public TestAim(string instanceId, Func<Message, Message> run) : this(instanceId, m => Task.FromResult(run(m))) { }

        public TestAim(string instanceId, Func<Message, Task<Message>> run)
        {
            InstanceId = instanceId;
            this.run = run;
        }

        public string InstanceId { get; }

        public Task<Message> ProcessAsync(Message message) => run(message);
    }
}
