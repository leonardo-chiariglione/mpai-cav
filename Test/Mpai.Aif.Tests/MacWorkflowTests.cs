using Mpai.Aif.Api;

namespace Mpai.Aif.Tests;

// THE WORKFLOW OF MULTIMODAL ACCESS CONTROL (Apps/MMC/MAC/OSD-MAC.orch) executed by the interpreter of the User Agent, as the standalone MAC
// window does, with simulated devices and a simulated Controller API: the avatar welcomes the person and asks to look at the camera (the
// face), asks for a sentence (the speech), and the Module, given the face, the speech, the Avatar Model and the verdict request, answers
// with the Identification (a Boolean) and the Portable Avatar, which is presented. The workflow runs once and ends by itself.
public class MacWorkflowTests
{
    [Fact]
    public async Task TheMacWorkflowWelcomesAsksFaceAndSpeechThenPresentsThePortableAvatarOfTheVerdict()
    {
        var log = new List<string>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var api = new ScriptedController(log, (turn, _) => turn < 3
            ? new[] { new ControllerApi.Datum("PAF-PAV-V1.6", 1, AppWorkflow.PortableAvatar("line " + turn)) }
            : new[]
            {
                new ControllerApi.Datum("boolean", 1, "true"),
                new ControllerApi.Datum("PAF-PAV-V1.6", 1, AppWorkflow.PortableAvatar("line " + turn))
            });

        var devices = new Mpai.Ua.DeviceRegistry()
            .RegisterAcquire("OSD-B3O-V1.5", (_, _) => { lock (log) log.Add("model"); return Task.FromResult<string?>("{\"Model\":\"avatar\"}"); })
            .RegisterAcquire("OSD-BVO-V1.5", (_, wanted) =>
            {
                lock (log) log.Add("face " + (wanted ?? "").Contains("Face"));
                return Task.FromResult<string?>(Mpai.Core.MpaiJson.ToJson(Mpai.Core.BasicVisualObject.FromFile("webcam.jpg", new byte[] { 1, 2, 3 }, "Face")));
            })
            .RegisterAcquire("OSD-BSO-V1.5", (_, _, _) =>
            {
                lock (log) log.Add("speech");
                return Task.FromResult<string?>("{\"Header\":\"OSD-BSO-V1.5\",\"Data\":\"AQIDBA==\"}");
            })
            .RegisterPresent("avatar", data => { if (data.ContainsKey("PAF-PAV-V1.6")) lock (log) log.Add("avatar"); return Task.CompletedTask; });

        var why = await AppWorkflow.RunAsync(AppWorkflow.Load("MAC"), api, devices, stop.Token);

        Assert.Equal(new[]
        {
            "start 1OSD-MAC-V1.5-I01", "model",
            "advance 1OSD-MAC-V1.5-I01", "avatar", "face True",
            "advance 1OSD-MAC-V1.5-I01", "avatar", "speech",
            "advance 1OSD-MAC-V1.5-I01", "avatar",
            "stop 1OSD-MAC-V1.5-I01"
        }, log);

        Assert.Equal(3, api.Turns.Count);
        Assert.Contains(api.Turns[0], d => d.DataType == "OSD-BTO-V1.5" && d.PortNumber == 1 && d.Json.Contains("Welcome to Multimodal Access Control"));
        Assert.Contains(api.Turns[1], d => d.DataType == "OSD-BTO-V1.5" && d.PortNumber == 1 && d.Json.Contains("short sentence"));
        Assert.Contains(api.Turns[2], d => d.DataType == "OSD-BVO-V1.5");
        Assert.Contains(api.Turns[2], d => d.DataType == "OSD-BSO-V1.5");
        Assert.Contains(api.Turns[2], d => d.DataType == "OSD-B3O-V1.5" && d.PortNumber == 1);
        Assert.True(why.Length > 0);
    }
}
