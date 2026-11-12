using Mpai.Aif.Api;

namespace Mpai.Aif.Tests;

// THE WORKFLOW OF PICTURE QUESTIONS (Apps/MMC/AMQ/MMC-AMQ.orch) executed by the interpreter of the User Agent, as the standalone AMQ window
// does, with simulated devices and a simulated Controller API: she asks whether the person wants to ask about a picture (yes, typed); she
// asks to select one, which is chosen and shown; the question is typed (the microphone hears nothing); the Module is given the picture, the
// question and the machine's ID and answers with the Portable Avatar, which is presented; she asks about another picture (no), says
// "Maybe another time." and the App ends.
public class AmqWorkflowTests
{
    [Fact]
    public async Task TheAmqWorkflowAsksChoosesTheQuestionPresentsTheAnswerAndEndsWhenThePersonSaysNo()
    {
        var log = new List<string>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var api = new ScriptedController(log, (turn, _) => new[]
        {
            new ControllerApi.Datum("PAF-PAV-V1.6", 1, AppWorkflow.PortableAvatar("answer " + turn))
        });

        var yesNo = new Queue<string>(new[] { "yes", "no" });
        var devices = new Mpai.Ua.DeviceRegistry()
            .RegisterAcquire("OSD-BVO-V1.5", (_, wanted) =>
            {
                lock (log) log.Add("picture " + (wanted ?? "").Contains("JPEG"));
                return Task.FromResult<string?>(Mpai.Core.MpaiJson.ToJson(Mpai.Core.BasicVisualObject.FromFile("red.jpg", File.ReadAllBytes(Path.Combine(Repository.Root, "Test", "Data", "red.jpg")), "Picture")));
            })
            .RegisterAcquire("OSD-BSO-V1.5", (_, _, abandon) => AppWorkflow.NothingUntil(abandon, stop.Token))
            .RegisterAcquire("OSD-BTO-V1.5", (_, wanted, _) =>
            {
                if ((wanted ?? "").Contains("YesNo"))
                {
                    var answer = yesNo.Dequeue();
                    lock (log) log.Add("typed " + answer);
                    return Task.FromResult<string?>(Mpai.Core.MpaiJson.ToJson(Mpai.Core.BasicTextObject.FromText(answer)));
                }
                lock (log) log.Add("typed question");
                return Task.FromResult<string?>(Mpai.Core.MpaiJson.ToJson(Mpai.Core.BasicTextObject.FromText("What is this?")));
            })
            .RegisterPresent("stage", data => { if (data.ContainsKey("OSD-BVO-V1.5")) lock (log) log.Add("stage"); return Task.CompletedTask; })
            .RegisterPresent("screen", data => { if (data.TryGetValue("Prompt", out var p)) lock (log) log.Add("prompt: " + p); return Task.CompletedTask; })
            .RegisterPresent("avatar", data => { if (data.ContainsKey("PAF-PAV-V1.6")) lock (log) log.Add("avatar"); return Task.CompletedTask; });

        var why = await AppWorkflow.RunAsync(AppWorkflow.Load("AMQ"), api, devices, stop.Token);

        Assert.Equal(new[]
        {
            "start 1MMC-AMQ-V2.5-I01",
            "prompt: Do you want to ask a question about a picture?", "typed yes",
            "prompt: Please select a picture.", "picture True", "stage",
            "typed question", "advance 1MMC-AMQ-V2.5-I01", "avatar",
            "prompt: Do you want to ask a question about another picture?", "typed no",
            "prompt: Maybe another time.",
            "stop 1MMC-AMQ-V2.5-I01"
        }, log);

        Assert.Single(api.Turns);
        Assert.Contains(api.Turns[0], d => d.DataType == "OSD-BVO-V1.5");
        Assert.Contains(api.Turns[0], d => d.DataType == "OSD-BTO-V1.5" && d.PortNumber == 1 && d.Json.Contains("What is this?"));
        Assert.Contains(api.Turns[0], d => d.DataType == "string" && d.Json == "\"MPAI as a Service\"");
        Assert.True(why.Length > 0);
    }
}
