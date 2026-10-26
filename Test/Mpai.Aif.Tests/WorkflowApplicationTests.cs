using Mpai.Wdl;

namespace Mpai.Aif.Tests;

// A WORKFLOW OVER A MODULE THAT DRIVES AN APPLICATION (additive, 2026/10/26). The header may name, after the Module it is over, the application Module(s) it drives:
// 'workflow ASM-VCI over 1MMC-VCI-V2.5-I01 application 1CAE-ASM-V1.0-I01'. The steps are the driving Module's, as ever; the Controller starts the application with it and
// connects them by Data Type and Port Number. Judged: the clause is read; a workflow without it reads as it did, with no application; and every workflow of the repository
// still reads.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class WorkflowApplicationTests
{
    [Fact]
    public void TheHeaderNamesTheApplicationTheModuleDrives()
    {
        var w = new WorkflowReader().Read("workflow ASM-VCI over 1MMC-VCI-V2.5-I01 application 1CAE-ASM-V1.0-I01\n\non Start:\n    ask Controller to start\n");
        Assert.Equal(["1MMC-VCI-V2.5-I01"], w.Modules);
        Assert.Equal(["1CAE-ASM-V1.0-I01"], w.Applications);
        var plain = new WorkflowReader().Read("workflow W over A\n\non Start:\n    ask Controller to start\n");
        Assert.Empty(plain.Applications);
    }

    [Fact]
    public void EveryWorkflowOfTheRepositoryStillReadsAndOnlyAsmVciNamesAnApplication()
    {
        var read = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Repository.Root, "Apps"), "*.orch", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
            var w = new WorkflowReader().Read(File.ReadAllText(file));
            if (Path.GetFileName(file) == "ASM-VCI.orch") Assert.Equal(["1CAE-ASM-V1.0-I01"], w.Applications);
            else Assert.Empty(w.Applications);
            read++;
        }
        Assert.True(read >= 15, read + " workflows read");
    }
}
