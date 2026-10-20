using System.Diagnostics;

namespace Mpai.Aif.Tests;

// THE ASM APP: its own self-test (--selftest) run as a program. The App starts the Controller of the Module and the AIM host of its AIMs itself (nothing is deployed beforehand); the
// report goes to Downloads\ASM\self-test. Judged: every check of the self-test is ok.
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
[Trait("Duration", "Long")]
public class AsmStudioSelfTests
{
    [SkippableFact]
    public void TheAsmAppPassesItsSelfTestWithItsOwnControllerAndModule()
    {
        var exe = Path.Combine(Repository.Root, "Apps", "CAE", "ASM", "App", "bin", "Debug", "net10.0-windows10.0.19041.0", "AsmApp.exe");
        Skip.IfNot(File.Exists(exe), "AsmApp is not built: " + exe);
        var report = Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? "", "Downloads", "ASM", "self-test", "selftest.txt");
        if (File.Exists(report)) File.Delete(report);

        var info = new ProcessStartInfo(exe, "--selftest") { WorkingDirectory = Repository.Root, UseShellExecute = false };
        using var program = Process.Start(info)!;
        var finished = program.WaitForExit(TimeSpan.FromMinutes(10));
        if (!finished) { try { program.Kill(entireProcessTree: true); } catch { } }
        Assert.True(finished, "the self-test did not end in 10 minutes.");
        Assert.True(File.Exists(report), "the self-test wrote no report: " + report);
        var lines = File.ReadAllLines(report);
        var failed = lines.Where(l => l.StartsWith("FAIL")).ToList();
        Assert.True(failed.Count == 0, "the self-test failed:\n" + string.Join("\n", failed));
        Assert.Contains("103 ok", lines[^1]);
    }
}