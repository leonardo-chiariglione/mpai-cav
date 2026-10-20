using System.Text.Json.Nodes;
using AIF.Controller;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Mas.Client;
using Mpai.SpatialAudio;

namespace Mpai.Aif.Tests;

// AUDIO SCENE MANAGEMENT AS THREE PROCESSES that talk through APIs: the User Agent (here, this test), the Controller (AsmServer) and the Module's AIMs (an AIM
// host, AIF.AimHost). The User Agent starts the Module and writes and reads its Ports by Data Type and Port Number through the Controller API; it holds no
// Controller and no AIM, and the Controller holds no AIM. The Module runs continuously: what is written is written at once and what is read is read within
// the time the reader allows.
[Collection("AsmDeployment")]       // one at a time: they share the Controller's port
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
public class AsmServerTests
{
    private static string Wav(double hz) => Convert.ToBase64String(SceneAudio.Wav(
        Enumerable.Range(0, 48000).Select(i => (float)(0.3 * Math.Sin(2 * Math.PI * hz * i / 48000.0))).ToArray(), 1, 48000));

    [SkippableFact]
    public void TheUserAgentManagesAudioThroughTheControllerAndTheModuleRunsApart()
    {
        Skip.If(AsmDeployment.SkipReason() is { } why, AsmDeployment.SkipReason());
        using var asm = new AsmDeployment();
        using var api = new RemoteControllerApi(AsmDeployment.Url, timeout: TimeSpan.FromMinutes(3));

        var started = api.StartFlow(AsmDeployment.Module);
        Assert.True(started == AifError.OK, $"StartFlow gave {started}:\n{asm.Said}");

        // A sound the User Agent captured: it comes back as the open Object.
        var bao = new BasicAudioObject { BasicAudioObjectID = "low", BasicAudioObjectData = [new InlineAudioData(Wav(440))], DescrMetadata = "low" };
        Assert.Equal(AifError.OK, api.InputWrite(AsmDeployment.Module, "OSD-BAO-V1.5", 1, MpaiJson.ToJson(bao)));
        var read = api.OutputRead(AsmDeployment.Module, "OSD-BAO-V1.5", 1, 60_000);
        Assert.True(read.Error == AifError.OK, $"the sound did not come back ({read.Error}):\n{asm.Said}");
        Assert.Equal("OSD-BAO-V1.5", JsonNode.Parse(read.Json!)!["Header"]!.ToString());

        // Nothing else was asked for: a read of a Port that gives nothing answers within the time it was given, not before and not long after.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var none = api.OutputRead(AsmDeployment.Module, "OSD-BSS-V1.5", 1, 700);
        Assert.Equal(AifError.NotProduced, none.Error);
        Assert.InRange(clock.ElapsedMilliseconds, 500, 4000);
        api.StopFlow(AsmDeployment.Module);
    }
}
