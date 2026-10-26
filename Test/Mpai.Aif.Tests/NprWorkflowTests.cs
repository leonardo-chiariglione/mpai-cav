using AIF.Controller;
using Mpai.Aif.Api;

namespace Mpai.Aif.Tests;

// THE WORKFLOW OF NEWSPAPER READING (Apps/MMC/NPR/MMC-NPR.orch) executed by the interpreter of the User Agent, as the standalone
// NPR window does, with simulated devices and a simulated Controller API: the page is chosen (a small picture), shown; she says to
// click on a column; the click (a Point) is given; the Module is offered the page, the Point and the machine's ID, and answers with
// the machine's Portable Avatar, which is presented. Nothing is heard or shown: the presenters record what they are given.
public class NprWorkflowTests
{
    private sealed class FakeController : IAsyncControllerApi
    {
        public readonly List<string> Calls = new();
        public readonly List<ControllerApi.Datum> Offered = new();
        public Action? Answered;

        public Task<AifError> StartFlowAsync(string m) { lock (Calls) Calls.Add("start " + m); return Task.FromResult(AifError.OK); }
        public Task StopFlowAsync(string m) { lock (Calls) Calls.Add("stop " + m); return Task.CompletedTask; }
        public Task<ControllerApi.Result> AdvanceAsync(string m, IEnumerable<ControllerApi.Datum> inputs)
        {
            lock (Calls) { Calls.Add("advance " + m); Offered.AddRange(inputs); }
            Answered?.Invoke();
            return Task.FromResult(new ControllerApi.Result(AifError.OK, new[]
            {
                new ControllerApi.Datum("PAF-PAV-V1.6", 1, "{\"TextObject\":{\"Data\":\"the words of the column\"}}")
            }));
        }
        public Task<AifError> InputWriteAsync(string m, string t, int p, string j, int timeoutMs = -1) => Task.FromResult(AifError.OK);
        public Task<ControllerApi.Read> OutputReadAsync(string m, string t, int p, int timeoutMs = -1) => throw new NotSupportedException();
        public Task<AifError> PauseAsync(string m) => Task.FromResult(AifError.OK);
        public Task<AifError> ResumeAsync(string m) => Task.FromResult(AifError.OK);
        public Task<ControllerApi.ModuleStatus> StatusAsync(string m) => throw new NotSupportedException();
        public Task<AifError> StopAimAsync(string m, string a) => Task.FromResult(AifError.OK);
        public Task<AifError> SharedStorageInitAsync(string m, string l) => Task.FromResult(AifError.OK);
    }

    [Fact]
    public async Task TheNprWorkflowShowsThePageAsksForAColumnAndPresentsThePortableAvatar()
    {
        var api = new FakeController();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var order = new List<string>();
        string? staged = null, avatar = null; var prompts = new List<string>();
        var clicks = 0;

        var devices = new Mpai.Ua.DeviceRegistry()
            .RegisterAcquire("OSD-BVO-V1.5", (_, _) => Task.FromResult<string?>(Mpai.Core.MpaiJson.ToJson(Mpai.Core.BasicVisualObject.FromFile("red.jpg", File.ReadAllBytes(Path.Combine(Repository.Root, "Test", "Data", "red.jpg")), "Picture"))))
            .RegisterAcquire("OSD-PNT-V1.5", async (_, _, abandon) =>
            {
                if (Interlocked.Increment(ref clicks) == 1)
                    return "{\"Header\":\"OSD-PNT-V1.5\",\"Coordinates\":{\"X\":10,\"Y\":20}}";
                try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
                return null;   // no second click: the person stops the App
            })
            .RegisterPresent("stage", data => { if (data.TryGetValue("OSD-BVO-V1.5", out var v)) { staged = v; lock (order) order.Add("page"); } return Task.CompletedTask; })
            .RegisterPresent("screen", data => { if (data.TryGetValue("Prompt", out var p)) { lock (prompts) prompts.Add(p); lock (order) order.Add("prompt"); } return Task.CompletedTask; })
            .RegisterPresent("avatar", data =>
            {
                if (data.TryGetValue("PAF-PAV-V1.6", out var pav)) { avatar = pav; lock (order) order.Add("avatar"); stop.Cancel(); }
                return Task.CompletedTask;
            });

        var said = new List<string>();
        var workflow = new Mpai.Wdl.WorkflowReader().Read(File.ReadAllText(Path.Combine(Repository.Root, "Apps", "MMC", "NPR", "MMC-NPR.orch")));
        var interpreter = new Mpai.Ua.WorkflowInterpreter(api, devices, line => { lock (said) said.Add(line); });
        try { await interpreter.RunAsync(workflow, stop.Token); } catch (OperationCanceledException) { }
        var why = string.Join(" / ", said);

        Assert.True(staged is not null, "the page was never presented: " + why);
        Assert.Equal(new[] { "Click on a column and I will read it." }, prompts);
        Assert.True(avatar is not null, "the Portable Avatar was never presented: " + why);
        Assert.Equal(new[] { "page", "prompt", "avatar" }, order);

        // The Module was started by the workflow, given the page, the Point and the machine's ID, and stopped by On Stop.
        Assert.Equal(new[] { "start 1MMC-NPR-V2.5-I01", "advance 1MMC-NPR-V2.5-I01", "stop 1MMC-NPR-V2.5-I01" }, api.Calls);
        Assert.Contains(api.Offered, d => d.DataType == "OSD-BVO-V1.5");
        Assert.Contains(api.Offered, d => d.DataType == "OSD-PNT-V1.5" && d.Json.Contains("\"X\":10"));
        Assert.Contains(api.Offered, d => d.DataType == "string" && d.Json == "\"Thalia\"");
    }
}
