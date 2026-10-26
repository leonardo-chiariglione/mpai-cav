using Mpai.Aif.Api;

namespace Mpai.Aif.Tests;

// THE WORKFLOW OF ACCESS CONTROL REGISTRATION (Apps/MMC/ACR/MMC-ACR.orch) executed by the interpreter of the User Agent, as the standalone
// ACR window does, with simulated devices and a simulated Controller API: the avatar welcomes the person and asks for the name (typed),
// asks to look at the camera (the face), asks for a sentence (the speech), and confirms the registration; the Module is given the face,
// the speech and the name under OSD-BTO #2. The workflow runs once and ends by itself.
public class AcrWorkflowTests
{
    [Fact]
    public async Task TheAcrWorkflowWelcomesAsksTheNameTheFaceAndTheSpeechThenConfirmsTheRegistration()
    {
        var log = new List<string>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var api = new ScriptedController(log, (turn, _) => new[]
        {
            new ControllerApi.Datum("PAF-PAV-V1.6", 1, AppWorkflow.PortableAvatar("line " + turn))
        });

        var devices = new Mpai.Ua.DeviceRegistry()
            .RegisterAcquire("OSD-B3O-V1.5", (_, _) => { lock (log) log.Add("model"); return Task.FromResult<string?>("{\"Model\":\"avatar\"}"); })
            .RegisterAcquire("OSD-BTO-V1.5", (_, _, _) =>
            {
                lock (log) log.Add("name");
                return Task.FromResult<string?>(Mpai.Core.MpaiJson.ToJson(Mpai.Core.BasicTextObject.FromText("Ada")));
            })
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

        var why = await AppWorkflow.RunAsync(AppWorkflow.Load("ACR"), api, devices, stop.Token);

        Assert.Equal(new[]
        {
            "start 1MMC-ACR-V2.5-I01", "model",
            "advance 1MMC-ACR-V2.5-I01", "avatar", "name",
            "advance 1MMC-ACR-V2.5-I01", "avatar", "face True",
            "advance 1MMC-ACR-V2.5-I01", "avatar", "speech",
            "advance 1MMC-ACR-V2.5-I01", "avatar",
            "stop 1MMC-ACR-V2.5-I01"
        }, log);

        Assert.Equal(4, api.Turns.Count);
        Assert.Contains(api.Turns[0], d => d.DataType == "OSD-BTO-V1.5" && d.PortNumber == 1 && d.Json.Contains("Welcome to Access Control Registration"));
        Assert.Contains(api.Turns[1], d => d.DataType == "OSD-BTO-V1.5" && d.PortNumber == 1 && d.Json.Contains("Thank you, Ada"));
        Assert.Contains(api.Turns[2], d => d.DataType == "OSD-BTO-V1.5" && d.PortNumber == 1 && d.Json.Contains("short sentence"));
        // The last exchange gives the Module the face, the speech and the name (#2), and the words of the confirmation (#1).
        Assert.Contains(api.Turns[3], d => d.DataType == "OSD-BVO-V1.5");
        Assert.Contains(api.Turns[3], d => d.DataType == "OSD-BSO-V1.5");
        Assert.Contains(api.Turns[3], d => d.DataType == "OSD-BTO-V1.5" && d.PortNumber == 2 && d.Json.Contains("Ada"));
        Assert.Contains(api.Turns[3], d => d.DataType == "OSD-BTO-V1.5" && d.PortNumber == 1 && d.Json.Contains("Ada, you are registered"));
        Assert.True(why.Length > 0);
    }
}
