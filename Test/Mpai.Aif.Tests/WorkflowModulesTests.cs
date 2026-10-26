using Mpai.Wdl;

namespace Mpai.Aif.Tests;

// A WORKFLOW OVER SEVERAL MODULES (additive, 2026/10/26). The header may name several Modules, each with a short name ('over VCI=1MMC-VCI-V2.5-I01, ASM=1CAE-ASM-V1.0-I01');
// the steps that are for one say so - 'to ASM' (offer, stream), 'from VCI' (ask, deliver), 'start ASM' - and a step that names none is for the first Module, so every
// workflow over one Module reads as it did. Judged: the names are read and resolved, a name that is no Module's is refused, and every workflow of the repository still reads,
// with no step naming a Module.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class WorkflowModulesTests
{
    private const string Two = """
        workflow Both over VCI=1MMC-VCI-V2.5-I01, ASM=1CAE-ASM-V1.0-I01

        on Start:
            ask Controller to start VCI
            ask Controller to start ASM
            stream Voice (OSD-BSO-V1.5) from device "Voice" to VCI
            deliver Said (OSD-BSO-V1.5) from VCI to device "Say"
            offer List (MMC-AJI-V2.5:1) = {"Instance":{"UserCommandRQ":{}}} to ASM
            ask Answer (MMC-AJI-V2.5:1) from ASM
            offer Answer (MMC-AJI-V2.5:2) to VCI
            ask Reply (OSD-BTO-V1.5:2), Heard (OSD-BTO-V1.5:1) from VCI
            offer Plain (OSD-BTO-V1.5:1)
            ask Plain2 (OSD-BTO-V1.5:1)
            route Command (MMC-AJI-V2.5:1) from VCI to ASM
            send Listing (MMC-AJI-V2.5:1) = {"Instance":{"UserCommandRQ":{}}} to ASM

        on Stop:
            ask Controller to stop ASM
            ask Controller to stop VCI
        """;

    [Fact]
    public void TheStepsNameTheirModules()
    {
        var w = new WorkflowReader().Read(Two);
        Assert.Equal(["1MMC-VCI-V2.5-I01", "1CAE-ASM-V1.0-I01"], w.Modules);
        var start = w.OnStart;
        Assert.Equal("1MMC-VCI-V2.5-I01", start[0].Module);
        Assert.Equal("1CAE-ASM-V1.0-I01", start[1].Module);
        Assert.Equal("1MMC-VCI-V2.5-I01", start[2].Module);                  // stream ... to VCI
        Assert.Equal("1MMC-VCI-V2.5-I01", start[3].Module);                  // deliver ... from VCI
        Assert.Equal("1CAE-ASM-V1.0-I01", start[4].Module);                  // offer ... to ASM
        Assert.StartsWith("{", start[4].Literal);                           // the literal is the object
        Assert.Equal("1CAE-ASM-V1.0-I01", start[5].Module);                  // ask ... from ASM
        Assert.Equal("1MMC-VCI-V2.5-I01", start[6].Module);
        Assert.Equal("1MMC-VCI-V2.5-I01", start[7].Module);
        Assert.Equal(2, start[7].Ports.Count);
        Assert.Null(start[8].Module);                                       // none named: the first
        Assert.Null(start[9].Module);
        Assert.Equal(StepKind.Route, start[10].Kind);
        Assert.Equal("1MMC-VCI-V2.5-I01", start[10].Module);
        Assert.Equal("1CAE-ASM-V1.0-I01", start[10].ToModule);
        Assert.Equal(StepKind.Send, start[11].Kind);
        Assert.Equal("1CAE-ASM-V1.0-I01", start[11].Module);
        Assert.StartsWith("{", start[11].Literal);
        Assert.Equal("1CAE-ASM-V1.0-I01", w.OnStop[0].Module);
    }

    [Fact]
    public void AModuleThatIsNotTheWorkflowsIsRefused()
    {
        var text = "workflow W over A\n\non Start:\n    ask Controller to start B\n";
        Assert.Throws<WorkflowReader.WorkflowSyntaxError>(() => new WorkflowReader().Read(text));
    }

    [Fact]
    public void EveryWorkflowOfTheRepositoryStillReadsAndNamesNoModule()
    {
        var read = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Repository.Root, "Apps"), "*.orch", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
            var w = new WorkflowReader().Read(File.ReadAllText(file));
            if (w.Modules.Count != 1) continue;                               // a workflow over several Modules names them
            void Walk(IReadOnlyList<Step> steps)
            {
                foreach (var s in steps) { Assert.True(s.Module is null, file + " line " + s.Line); Walk(s.Body); Walk(s.Else); }
            }
            Walk(w.OnStart); Walk(w.OnStop); Walk(w.OnDegraded);
            read++;
        }
        Assert.True(read >= 15, read + " workflows read");
    }
}
