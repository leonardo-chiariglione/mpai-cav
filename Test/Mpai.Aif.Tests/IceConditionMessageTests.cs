using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Cav.Ams;
using Mpai.Cav.Mas;
using Mpai.Cav.Recordings;

namespace Mpai.Aif.Tests;

// THE ICE CONDITION ANALYSIS SENDS THE ROAD STATE TO THE AMS AT ANY TIME (the author, 2026/11/01: a two-way channel of AMS-MAS Messages between it and the AMS): on the
// second AMS-MAS Message output of the MAS, when the Road State is first known and whenever it changes - not in answer to a Message of the AMS.
public sealed class IceConditionMessageTests
{
    [Fact]
    public void TheRoadStateIsSentOnTheSecondAmsMasMessageOutput()
    {
        var sim = AmsStage1Tests.Scenarios()["a free road to the Destination"]();
        sim.Mechanical(seed: 9);
        using var mas = new MasInTheLoop(sim.Path.At(0).Heading * 180 / Math.PI);
        var sensed = sim.Sense(camera: false);
        mas.Sense(sensed, []);

        var read = mas.Api.OutputRead(mas.ModuleId, MasTypes.Message, 2, 5000);
        Assert.Equal(AifError.OK, read.Error);
        var message = JsonNode.Parse(read.Json!)!;
        Assert.Equal(MasTypes.Message, (string?)message["Header"]);
        Assert.Null(message["AMSMessage"]);                                              // from the MAS: no trajectory asked
        var road = message["MASMessage"]!["RoadState"]!;
        Assert.Equal(MasTypes.RoadState, (string?)road["Header"]);
        Assert.InRange((double)road["SurfaceCondition"]!["FrictionCoefficientEstimate"]!, 0.05, 1);

        // nothing changes while the road stays as it is: no second message for the next attitudes
        sim.Advance();
        mas.Sense(sim.Sense(camera: false), []);
        Assert.NotEqual(AifError.OK, mas.Api.OutputRead(mas.ModuleId, MasTypes.Message, 2, 300).Error);
    }
}
