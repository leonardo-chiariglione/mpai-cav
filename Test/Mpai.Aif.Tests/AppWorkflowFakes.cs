using AIF.Controller;
using Mpai.Aif.Api;

namespace Mpai.Aif.Tests;

// THE SHARED SIMULATIONS OF THE APP WORKFLOW TESTS. The standalone windows run the .orch of their App through the interpreter of the User
// Agent, supplying only devices. These tests run the real .orch the same way, with a Controller API that answers from a script and devices
// that record what they were given, and judge the ORDER of what happened: one log holds the calls on the Module and what the devices were
// asked to acquire and present.
internal sealed class ScriptedController : IAsyncControllerApi
{
    public readonly List<string> Log;
    public readonly List<IReadOnlyList<ControllerApi.Datum>> Turns = new();
    private readonly Func<int, IReadOnlyList<ControllerApi.Datum>, IEnumerable<ControllerApi.Datum>> _answer;

    public ScriptedController(List<string> log, Func<int, IReadOnlyList<ControllerApi.Datum>, IEnumerable<ControllerApi.Datum>> answer)
    { Log = log; _answer = answer; }

    // The MODULE_IDs this Controller handed out, and the Module each one names (Basic API 2.2): the log shows the Module a call reached,
    // and a call with an id it did not hand out shows as "#id", which no expected order contains.
    private readonly Dictionary<int, string> _started = new();
    private int _nextId = 1;
    private string Of(int id) { lock (Log) return _started.TryGetValue(id, out var m) ? m : "#" + id; }

    public Task<(AifError Error, int ModuleId)> StartFlowAsync(string m)
    { int id; lock (Log) { id = _nextId++; _started[id] = m; Log.Add("start " + m); } return Task.FromResult((AifError.OK, id)); }
    public Task StopFlowAsync(int id) { lock (Log) { Log.Add("stop " + Of(id)); _started.Remove(id); } return Task.CompletedTask; }
    public Task<ControllerApi.Result> AdvanceAsync(int id, IEnumerable<ControllerApi.Datum> inputs)
    {
        var offered = inputs.ToList();
        int turn;
        lock (Log) { Turns.Add(offered); turn = Turns.Count; Log.Add("advance " + Of(id)); }
        return Task.FromResult(new ControllerApi.Result(AifError.OK, _answer(turn, offered).ToList()));
    }
    public Task<AifError> InputWriteAsync(int id, string t, int p, string j, int timeoutMs = -1) => Task.FromResult(AifError.OK);
    public Task<ControllerApi.Read> OutputReadAsync(int id, string t, int p, int timeoutMs = -1) => throw new NotSupportedException();
    public Task<AifError> PauseAsync(int id) => Task.FromResult(AifError.OK);
    public Task<AifError> ResumeAsync(int id) => Task.FromResult(AifError.OK);
    public Task<ControllerApi.ModuleStatus> StatusAsync(int id) => throw new NotSupportedException();
    public Task<AifError> StopAimAsync(int id, string a) => Task.FromResult(AifError.OK);
    public Task<AifError> SharedStorageInitAsync(int id, string l) => Task.FromResult(AifError.OK);
}

internal static class AppWorkflow
{
    public static Mpai.Wdl.Workflow Load(string app) =>
        new Mpai.Wdl.WorkflowReader().Read(File.ReadAllText(Path.Combine(Repository.Root, "Apps", "MMC", app, $"{(app is "ACR" or "MAC" ? "OSD" : "MMC")}-{app}.orch")));      // ACR and MAC are OSD V1.5 AIMs; their Apps stay in this folder

    // Runs the workflow until it ends or the token is cancelled (the person's Stop); returns what the interpreter said, for the failure message.
    public static async Task<string> RunAsync(Mpai.Wdl.Workflow workflow, IAsyncControllerApi api, Mpai.Ua.DeviceRegistry devices, CancellationToken stop)
    {
        var said = new List<string>();
        var interpreter = new Mpai.Ua.WorkflowInterpreter(api, devices, line => { lock (said) said.Add(line); });
        try { await interpreter.RunAsync(workflow, stop); } catch (OperationCanceledException) { }
        lock (said) return string.Join(" / ", said);
    }

    public static string PortableAvatar(string words) => "{\"TextObject\":{\"Data\":\"" + words + "\"}}";

    // A device that waits until it is abandoned or the App is stopped, and gives nothing: the microphone of a person who types.
    public static async Task<string?> NothingUntil(CancellationToken abandon, CancellationToken stop)
    {
        using var either = CancellationTokenSource.CreateLinkedTokenSource(abandon, stop);
        try { await Task.Delay(Timeout.Infinite, either.Token); } catch (OperationCanceledException) { }
        return null;
    }
}
