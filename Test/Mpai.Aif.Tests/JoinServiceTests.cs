using Mpai.Ua;
using Mpai.Wdl;

namespace Mpai.Aif.Tests;

// 'ask Controller to join "<service>" [as "<name>"] [role <Role>]': a step of the trust protocol (Basic API 3.9, MPAI_AIFU_External_Join), not a datum. The User Agent knows the Trust
// Authorities its User decided to trust - the anchor of each and where it is reached - by the name of their service; the workflow names the service and nothing of how to trust it.
public class JoinServiceTests
{
    [Fact]
    public void TheWorkflowJoinsAServiceByName()
    {
        var workflow = new WorkflowReader().Read("""
            workflow Test over 1AIH-HFE-V1.1-I01

            on Start:
                ask Controller to start
                ask Controller to join "AIH-HBE" as "hfe-1" role FrontEnd

            on Stop:
                ask Controller to stop
            """);
        var join = workflow.OnStart.Single(s => s.Kind == StepKind.JoinService);
        Assert.Equal("AIH-HBE", join.Text);
        Assert.Equal("hfe-1", join.Name);
        Assert.Equal("FrontEnd", join.Role);
        Assert.True(join.IsControllerRequest);
    }

    [Fact]
    public void TheUserAgentKnowsTheServicesItsUserTrusts()
    {
        var registry = new DeviceRegistry().RegisterService("AIH-HBE", "{\"AnchorID\":\"hbe\"}", "127.0.0.1:1");
        Assert.Equal(("{\"AnchorID\":\"hbe\"}", "127.0.0.1:1", (string?)null), registry.Service("AIH-HBE"));
        Assert.Null(registry.Service("another"));
    }

    // 'ask Controller to admit by "<policy>"' (MPAI_AIFU_External_Authority) and 'ask Controller to leave' (MPAI_AIFU_External_Leave): the policy is read whole, quotes and all.
    [Fact]
    public void TheWorkflowMakesTheControllerTheAuthorityAndLeaves()
    {
        var workflow = new WorkflowReader().Read("""
            workflow Test over 1AIH-HBE-V1.1-I01

            on Start:
                ask Controller to start
                ask Controller to admit by "{ "Roles": ["BackEnd", "FrontEnd"], "AuthorityRole": "BackEnd" }"

            on Stop:
                ask Controller to leave
                ask Controller to stop
            """);
        var admit = workflow.OnStart.Single(s => s.Kind == StepKind.AdmitService);
        Assert.Equal("""{ "Roles": ["BackEnd", "FrontEnd"], "AuthorityRole": "BackEnd" }""", admit.Text);
        Assert.True(admit.IsControllerRequest);
        var leave = workflow.OnStop.First();
        Assert.Equal(StepKind.LeaveService, leave.Kind);
        Assert.True(leave.IsControllerRequest);
    }

    [Fact]
    public void AnAdmitThatGivesNoPolicyIsRefused() =>
        Assert.Throws<WorkflowReader.WorkflowSyntaxError>(() => new WorkflowReader().Read("""
            workflow Test over 1AIH-HBE-V1.1-I01

            on Start:
                ask Controller to admit by
            """));

    [Fact]
    public void AJoinThatNamesNoServiceIsRefused() =>
        Assert.Throws<WorkflowReader.WorkflowSyntaxError>(() => new WorkflowReader().Read("""
            workflow Test over 1AIH-HFE-V1.1-I01

            on Start:
                ask Controller to join
            """));
}
