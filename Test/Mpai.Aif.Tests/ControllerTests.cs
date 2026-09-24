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

    // The status of each AIM after a run in which one worked, one produced
    // nothing and one threw.
    [Fact]
    public async Task Status()
    {
        var (ua, id) = Start("1TST-DGR-V1.0-I01");
        await ua.RunAsync(id, Boundary("x"));

        var result = new Dictionary<string, string>();
        foreach (var aim in new[] { "1TST-UPP-V1.0-I01", "1TST-NOP-V1.0-I01", "1TST-THR-V1.0-I01" })
        {
            ua.MPAI_AIFU_AIM_GetStatus(id, aim, out var status);
            result[$"after a run: {aim}"] = status.ToString();
        }
        ua.MPAI_AIFU_MODULE_Stop(id);
        ua.MPAI_AIFU_AIM_GetStatus(id, "1TST-UPP-V1.0-I01", out var afterStop);
        result["after Stop: 1TST-UPP-V1.0-I01"] = afterStop.ToString();

        Expected.Match("controller-status.json", result);
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

    // ---------------------------------------------------------------------

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
    public bool CanCreate(string aimName) => aimName.StartsWith("1TST-", StringComparison.Ordinal);

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage) =>
        aimName switch
        {
            "1TST-SPL-V1.0-I01" => new TestAim(aimName, m => Out(("First", "A:" + In(m)), ("Second", "B:" + In(m)))),
            "1TST-UPP-V1.0-I01" => new TestAim(aimName, m => Out(("Upper", In(m).ToUpperInvariant()))),
            "1TST-REV-V1.0-I01" => new TestAim(aimName, m => Out(("Reversed", new string(In(m).Reverse().ToArray())))),
            "1TST-ECH-V1.0-I01" => new TestAim(aimName, m => Out(("Echo", In(m)))),
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
