using System.Diagnostics;

using AIF.Controller;
using Mpai.Mas.Client;
using Xunit;

namespace Mpai.Aif.Tests;

// THE STAND-ALONE APPS OF MPAI-MMC ARE USER AGENTS AND THEIR CONTROLLER IS A PROGRAM OF ITS OWN (MmcController): the User Agent here is this test, which holds no Controller, no Module and no
// AIM and only speaks the Controller API. It tells the Controller to start the Module of an App; the Controller starts it and its AIMs as the L3 says.
[Trait("Group", "Models")]
[Trait("Duration", "Long")]
public class MmcControllerProcessTests
{
    [SkippableTheory]
    [InlineData("1MMC-NPR-V2.5-I01")]
    [InlineData("1MMC-MTS-V2.5-I01")]
    [InlineData("1MMC-AMQ-V2.5-I01")]
    public void TheUserAgentStartsTheModuleOfAnAppThroughTheControllerApi(string module)
    {
        var exe = Path.Combine(Repository.Root, "Apps", "MMC", "Controller", "bin", "Debug", "net10.0", "MmcController.exe");
        Skip.IfNot(File.Exists(exe), "MmcController is not built: " + exe);
        var url = "http://127.0.0.1:" + (5900 + Environment.ProcessId % 90);
        var info = new ProcessStartInfo(exe, $"--listen {url}") { WorkingDirectory = Repository.Root, UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        using var controller = Process.Start(info)!;
        try
        {
            var ready = new ManualResetEventSlim();
            controller.OutputDataReceived += (_, e) => { if (e.Data?.Contains("[MAS] Listening") == true) ready.Set(); };
            controller.BeginOutputReadLine();
            Assert.True(ready.Wait(TimeSpan.FromSeconds(60)), "the Controller did not come up");
            using var api = new RemoteControllerApi(url);
            Assert.Equal(AifError.OK, api.StartFlow(module, out var id));
            api.StopFlow(id);
        }
        finally { try { controller.Kill(entireProcessTree: true); } catch { } }
    }
}
