using Mpai.Aif.Api;

namespace Mpai.Aif.Tests;

// THE WORKFLOW OF THE MULTIMODAL CONVERSATION AVATAR (Apps/MMC/MCA/MMC-MCA.orch) executed by the interpreter of the User Agent, as the
// standalone MCA window does, with simulated devices and a simulated Controller API: the Avatar Model is taken; each turn the person types
// (the microphone hears nothing), the Module is offered the words, the Avatar Model, the machine's ID and the memory of the conversation,
// and answers with the Portable Avatar, which is presented. The memory goes back with the next turn and not with the first.
public class McaWorkflowTests
{
    [Fact]
    public async Task TheMcaWorkflowTakesTheModelThenEachTurnOffersWordsModelIdAndMemoryAndPresentsThePortableAvatar()
    {
        var log = new List<string>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var api = new ScriptedController(log, (turn, _) => new[]
        {
            new ControllerApi.Datum("PAF-PAV-V1.6", 1, AppWorkflow.PortableAvatar("reply " + turn)),
            new ControllerApi.Datum("MMC-SUM-V2.5", 1, "{\"Summary\":\"after " + turn + "\"}")
        });

        var typed = 0;
        var devices = new Mpai.Ua.DeviceRegistry()
            .RegisterAcquire("OSD-B3O-V1.5", (_, _) => { lock (log) log.Add("model"); return Task.FromResult<string?>("{\"Model\":\"avatar\"}"); })
            .RegisterAcquire("OSD-BSO-V1.5", (_, _, abandon) => AppWorkflow.NothingUntil(abandon, stop.Token))
            .RegisterAcquire("OSD-BTO-V1.5", async (_, _, abandon) =>
            {
                var n = Interlocked.Increment(ref typed);
                if (n > 2) return await AppWorkflow.NothingUntil(abandon, stop.Token);   // no third turn: the person stops the App
                lock (log) log.Add("typed " + n);
                return Mpai.Core.MpaiJson.ToJson(Mpai.Core.BasicTextObject.FromText("words " + n));
            })
            .RegisterPresent("avatar", data =>
            {
                if (data.TryGetValue("PAF-PAV-V1.6", out var pav))
                {
                    lock (log) log.Add("avatar " + pav.Substring(pav.IndexOf("reply"), 7));
                    if (log.Count(l => l.StartsWith("avatar ")) == 2) stop.Cancel();
                }
                return Task.CompletedTask;
            });

        var why = await AppWorkflow.RunAsync(AppWorkflow.Load("MCA"), api, devices, stop.Token);

        Assert.True(log.Contains("avatar reply 2"), "the second reply was never presented: " + why);
        Assert.Equal(new[]
        {
            "start 1MMC-MCA-V2.5-I01", "model",
            "typed 1", "advance 1MMC-MCA-V2.5-I01", "avatar reply 1",
            "typed 2", "advance 1MMC-MCA-V2.5-I01", "avatar reply 2",
            "stop 1MMC-MCA-V2.5-I01"
        }, log);

        // Turn 1: the words, the model and the machine's ID, and no memory yet. Turn 2: the memory the Module gave in turn 1.
        Assert.Contains(api.Turns[0], d => d.DataType == "OSD-BTO-V1.5" && d.PortNumber == 1 && d.Json.Contains("words 1"));
        Assert.Contains(api.Turns[0], d => d.DataType == "OSD-B3O-V1.5" && d.PortNumber == 1);
        Assert.Contains(api.Turns[0], d => d.DataType == "string" && d.Json == "\"Thalia\"");
        Assert.DoesNotContain(api.Turns[0], d => d.DataType == "MMC-SUM-V2.5");
        Assert.Contains(api.Turns[1], d => d.DataType == "OSD-BTO-V1.5" && d.Json.Contains("words 2"));
        Assert.Contains(api.Turns[1], d => d.DataType == "MMC-SUM-V2.5" && d.Json.Contains("after 1"));
    }
}
