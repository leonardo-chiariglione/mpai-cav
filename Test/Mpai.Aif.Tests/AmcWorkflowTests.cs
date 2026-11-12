using Mpai.Aif.Api;

namespace Mpai.Aif.Tests;

// THE WORKFLOW OF THE AFFECTIVE MULTIMODAL CONVERSATION (Apps/MMC/AMC/MMC-AMC.orch) executed by the interpreter of the User Agent, as the
// standalone AMC window does, with simulated devices and a simulated Controller API: the Avatar Model is taken; each turn the person types,
// the camera gives the face, the Module is offered the words, the face, the Avatar Model, the machine's ID and the memory, and answers
// with the Portable Avatar, which is presented.
public class AmcWorkflowTests
{
    [Fact]
    public async Task TheAmcWorkflowOffersWordsAndFaceEachTurnWithTheMemoryOfThePreviousTurn()
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
                if (n > 2) return await AppWorkflow.NothingUntil(abandon, stop.Token);
                lock (log) log.Add("typed " + n);
                return Mpai.Core.MpaiJson.ToJson(Mpai.Core.BasicTextObject.FromText("words " + n));
            })
            .RegisterAcquire("OSD-BVO-V1.5", (_, wanted) =>
            {
                lock (log) log.Add("face " + (wanted ?? "").Contains("Face"));
                return Task.FromResult<string?>(Mpai.Core.MpaiJson.ToJson(Mpai.Core.BasicVisualObject.FromFile("webcam.jpg", new byte[] { 1, 2, 3 }, "Face")));
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

        var why = await AppWorkflow.RunAsync(AppWorkflow.Load("AMC"), api, devices, stop.Token);

        Assert.Equal(new[]
        {
            "start 1MMC-AMC-V2.5-I01", "model",
            "typed 1", "face True", "advance 1MMC-AMC-V2.5-I01", "avatar",
            "typed 2", "face True", "advance 1MMC-AMC-V2.5-I01", "avatar",
            "stop 1MMC-AMC-V2.5-I01"
        }, log);

        Assert.Contains(api.Turns[0], d => d.DataType == "OSD-BTO-V1.5" && d.PortNumber == 1 && d.Json.Contains("words 1"));
        Assert.Contains(api.Turns[0], d => d.DataType == "OSD-BVO-V1.5");
        Assert.Contains(api.Turns[0], d => d.DataType == "OSD-B3O-V1.5" && d.PortNumber == 1);
        Assert.Contains(api.Turns[0], d => d.DataType == "string" && d.Json == "\"MPAI as a Service\"");
        Assert.DoesNotContain(api.Turns[0], d => d.DataType == "MMC-SUM-V2.5");
        Assert.Contains(api.Turns[1], d => d.DataType == "MMC-SUM-V2.5" && d.Json.Contains("after 1"));
        Assert.True(why.Length > 0);
    }
}
