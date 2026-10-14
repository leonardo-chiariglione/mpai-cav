using Mpai.Wdl;

namespace Mpai.Aif.Tests;

// 'ask Controller to keep storage in "<where>"': the User says where the Module's Shared Storage is, through the User Agent (Basic API 3.5, MPAI_AIFU_SharedStorage_Init).
public class KeepStorageTests
{
    [Fact]
    public void TheWorkflowSaysWhereTheStorageIs()
    {
        var workflow = new WorkflowReader().Read("""
            workflow Test over 1CAE-ASM-V1.0-I01

            on Start:
                type Where (OSD-BTO-V1.5)
                ask Controller to start
                ask Controller to keep storage in "{Where}"

            on Stop:
                ask Controller to stop
            """);
        var step = workflow.OnStart.Single(s => s.Kind == StepKind.KeepStorage);
        Assert.Equal("{Where}", step.Text);
        Assert.True(step.IsControllerRequest);
    }

    [Fact]
    public void ALineTheNotationDoesNotKnowIsRefused()
    {
        Assert.Throws<WorkflowReader.WorkflowSyntaxError>(() => new WorkflowReader().Read("""
            workflow Test over 1CAE-ASM-V1.0-I01

            on Start:
                ask Controller to keep the storage somewhere
            """));
    }
}
