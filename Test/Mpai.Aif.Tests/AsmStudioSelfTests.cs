using System.Diagnostics;

namespace Mpai.Aif.Tests;

// THE ASM USER AGENT (ASM Studio) against the deployed Module: its own self-test (--selftest) is run with the Controller of AsmDeployment as its Controller.
// It holds no Controller and no AIM; the Module runs on an AIM host. Judged: every check of the self-test is ok.
[Collection("AsmDeployment")]       // one at a time: they share the Controller's port
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
[Trait("Duration", "Long")]
public class AsmStudioSelfTests
{
    [SkippableFact]
    public void TheAsmUserAgentPassesItsSelfTestAgainstTheDeployedModule()
    {
        Skip.If(AsmDeployment.SkipReason() is not null, AsmDeployment.SkipReason());
        var exe = Path.Combine(Repository.Root, "AIF", "UserAgent", "Clients", "AsmStudio", "bin", "Debug", "net10.0-windows10.0.19041.0", "AsmStudio.exe");
        Skip.IfNot(File.Exists(exe), "AsmStudio is not built: " + exe);
        var report = Path.Combine(Repository.Root, "Test", "Reports", "AsmStudio", "selftest.txt");
        if (File.Exists(report)) File.Delete(report);

        using var asm = new AsmDeployment();
        var info = new ProcessStartInfo(exe, "--selftest") { WorkingDirectory = Repository.Root, UseShellExecute = false };
        info.Environment["MPAI_MAS_SERVER"] = AsmDeployment.Url;
        using var program = Process.Start(info)!;
        var finished = program.WaitForExit(TimeSpan.FromMinutes(10));
        if (!finished) { try { program.Kill(entireProcessTree: true); } catch { } }
        Assert.True(finished, "the self-test did not end in 10 minutes.\n" + asm.Said);
        Assert.True(File.Exists(report), "the self-test wrote no report.\n" + asm.Said);
        var lines = File.ReadAllLines(report);
        var failed = lines.Where(l => l.StartsWith("FAIL")).ToList();
        Assert.True(failed.Count == 0, "the self-test failed:\n" + string.Join("\n", failed) + "\n" + asm.Said);
    }
}
