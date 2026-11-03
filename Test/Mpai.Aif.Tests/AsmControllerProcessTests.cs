using System.Diagnostics;
using System.Text.Json.Nodes;

using AIF.Controller;
using Mpai.Core;
using Mpai.Mas.Client;
using Xunit;

namespace Mpai.Aif.Tests;

// THE CONTROLLER IS A PROGRAM OF ITS OWN (AsmController) and the User Agent only talks to it through the Controller API: here the User Agent is this test, which holds no Controller, no
// Module and no AIM. It tells the Controller to start the execution of the L3 (StartFlow), writes the typed words to the Port of the Module and reads the response from its Ports.
[Trait("Group", "Models")]
[Trait("Duration", "Long")]
public class AsmControllerProcessTests
{
    private const string Asm = "1CAE-ASM-V1.0-I01";

    [SkippableFact]
    public void TheUserAgentStartsTheModuleThroughTheControllerApiOfAControllerThatIsAProgramOfItsOwn()
    {
        var exe = Path.Combine(Repository.Root, "Apps", "CAE", "ASM", "Controller", "bin", "Debug", "net10.0", "AsmController.exe");
        Skip.IfNot(File.Exists(exe), "AsmController is not built: " + exe);
        var url = "http://127.0.0.1:" + (5100 + Environment.ProcessId % 800);
        var storage = Path.Combine(Path.GetTempPath(), "asm-controller-test-" + Guid.NewGuid().ToString("N")[..8]);
        var info = new ProcessStartInfo(exe, $"--listen {url} --storage \"{storage}\"") { WorkingDirectory = Repository.Root, UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        using var controller = Process.Start(info)!;
        try
        {
            var ready = new ManualResetEventSlim();
            controller.OutputDataReceived += (_, e) => { if (e.Data?.Contains("[MAS] Listening") == true) ready.Set(); };
            controller.BeginOutputReadLine();
            Assert.True(ready.Wait(TimeSpan.FromSeconds(60)), "the Controller did not come up");

            using var api = new RemoteControllerApi(url);
            Assert.Equal(AifError.OK, api.StartFlow(Asm));                           // the User Agent tells the Controller to start; the Controller starts the Module and its AIMs

            var words = MpaiJson.ToJson(BasicTextObject.FromText("place a room 6 by 4 with a ceiling of 3"));
            Assert.Equal(AifError.OK, api.InputWrite(Asm, "OSD-BTO-V1.5", 1, words, 30_000));
            var answer = api.OutputRead(Asm, "OSD-BTO-V1.5", 2, 60_000);              // the response, in text
            Assert.Equal(AifError.OK, answer.Error);
            Assert.False(string.IsNullOrWhiteSpace(MpaiJson.FromJson<BasicTextObject>(answer.Json!)?.GetText()));

            var scene = api.OutputRead(Asm, "OSD-ASD-V1.5", 1, 30_000);              // the scene as edited: a room is in it
            Assert.Equal(AifError.OK, scene.Error);
            Assert.Contains("ClosedSpace", scene.Json);
            api.StopFlow(Asm);
        }
        finally
        {
            try { controller.Kill(entireProcessTree: true); } catch { }
            try { Directory.Delete(storage, true); } catch { }
        }
    }
}
