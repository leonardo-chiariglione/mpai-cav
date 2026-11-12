using Mpai.Aif.Api;

namespace Mpai.Aif.Tests;

// THE WORKFLOW OF THE MULTIMODAL TRANSLATION SERVICE (Apps/MMC/MTS/MMC-MTS.orch) executed by the interpreter of the User Agent, as the
// standalone MTS window does, with simulated devices and a simulated Controller API: the Avatar Model and the Language Selector are
// taken once; each turn the person types, the Module is offered the Selector, the words, the Avatar Model and the machine's ID, and
// answers with the translation (shown on the screen) and the Portable Avatar (presented), in that order.
public class MtsWorkflowTests
{
    [Fact]
    public async Task TheMtsWorkflowTakesModelAndLanguagesOnceThenTranslatesShowingTheTextBeforeThePortableAvatar()
    {
        var log = new List<string>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var api = new ScriptedController(log, (turn, _) => new[]
        {
            new ControllerApi.Datum("OSD-BTO-V1.5", 1, Mpai.Core.MpaiJson.ToJson(Mpai.Core.BasicTextObject.FromText("translation " + turn))),
            new ControllerApi.Datum("PAF-PAV-V1.6", 1, AppWorkflow.PortableAvatar("translation " + turn))
        });

        var typed = 0;
        var devices = new Mpai.Ua.DeviceRegistry()
            .RegisterAcquire("OSD-B3O-V1.5", (_, _) => { lock (log) log.Add("model"); return Task.FromResult<string?>("{\"Model\":\"avatar\"}"); })
            .RegisterAcquire("OSD-SEL-V1.5", (_, _) =>
            {
                lock (log) log.Add("languages");
                return Task.FromResult<string?>(Mpai.Core.MpaiJson.ToJson(Mpai.Core.BasicSelectorObject.Languages("en", "it")));
            })
            .RegisterAcquire("OSD-BSO-V1.5", (_, _, abandon) => AppWorkflow.NothingUntil(abandon, stop.Token))
            .RegisterAcquire("OSD-BTO-V1.5", async (_, _, abandon) =>
            {
                var n = Interlocked.Increment(ref typed);
                if (n > 2) return await AppWorkflow.NothingUntil(abandon, stop.Token);
                lock (log) log.Add("typed " + n);
                return Mpai.Core.MpaiJson.ToJson(Mpai.Core.BasicTextObject.FromText("sentence " + n));
            })
            .RegisterPresent("screen", data =>
            {
                foreach (var kv in data) if (kv.Key.StartsWith("Display:OSD-BTO")) lock (log) log.Add("shown " + Mpai.Core.MpaiJson.FromJson<Mpai.Core.BasicTextObject>(kv.Value)!.GetText());
                return Task.CompletedTask;
            })
            .RegisterPresent("avatar", data =>
            {
                if (data.ContainsKey("PAF-PAV-V1.6"))
                {
                    lock (log) log.Add("avatar");
                    if (log.Count(l => l == "avatar") == 2) stop.Cancel();
                }
                return Task.CompletedTask;
            });

        var why = await AppWorkflow.RunAsync(AppWorkflow.Load("MTS"), api, devices, stop.Token);

        Assert.Equal(new[]
        {
            "start 1MMC-MTS-V2.5-I01", "model", "languages",
            "typed 1", "advance 1MMC-MTS-V2.5-I01", "shown translation 1", "avatar",
            "typed 2", "advance 1MMC-MTS-V2.5-I01", "shown translation 2", "avatar",
            "stop 1MMC-MTS-V2.5-I01"
        }, log);

        foreach (var turn in api.Turns)
        {
            Assert.Contains(turn, d => d.DataType == "OSD-SEL-V1.5");
            Assert.Contains(turn, d => d.DataType == "OSD-B3O-V1.5" && d.PortNumber == 1);
            Assert.Contains(turn, d => d.DataType == "string" && d.Json == "\"MPAI as a Service\"");
        }
        Assert.Contains(api.Turns[0], d => d.DataType == "OSD-BTO-V1.5" && d.PortNumber == 1 && d.Json.Contains("sentence 1"));
        Assert.Contains(api.Turns[1], d => d.DataType == "OSD-BTO-V1.5" && d.PortNumber == 1 && d.Json.Contains("sentence 2"));
        Assert.True(why.Length > 0);
    }
}
