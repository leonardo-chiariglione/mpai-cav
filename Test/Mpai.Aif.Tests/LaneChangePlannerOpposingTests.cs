using System.Text.Json.Nodes;

using Mpai.Cav.Ams;
using Mpai.Cav.Ess;

namespace Mpai.Aif.Tests;

// THE LANE CHANGE PLANNER ON A TWO-WAY ROAD, on its own: Full Environment Descriptors made by hand, the decisions read off. The long
// runs of HighwayOncomingTests show that the CAV passes; here, why it does not, and what it does when what it knew proves wrong.
public class LaneChangePlannerOpposingTests
{
    private sealed record Obj(string Id, string Source, double X, double Y, double RelativeSpeed, string Class = "car");

    private static JsonObject Fed(long ms, double egoSpeed, params Obj[] objects)
    {
        var array = new JsonArray();
        foreach (var o in objects)
            array.Add(new JsonObject
            {
                ["Source"] = o.Source,
                ["BasicEnvironmentObject"] = new JsonObject
                {
                    ["BasicEnvironmentObjectID"] = o.Id, ["InstanceIdentifier"] = EssJson.Identifier(o.Class, 1),
                    ["SpatialAttitude"] = EssJson.Attitude($"{o.Id}-{ms}", ms, (o.X, o.Y, 0), (0.1, 0.1, 0.1), (o.RelativeSpeed, 0, 0)),
                    ["ExistenceConfidence"] = 1.0
                }
            });
        return new JsonObject
        {
            ["FullEnvironmentDescriptorsTime"] = EssJson.SimpleTime($"F{ms}", ms),
            ["EgoSpatialAttitude"] = EssJson.Attitude($"E{ms}", ms, (0, 0, 0), (0.1, 0.1, 0.1), (egoSpeed, 0, 0)),
            ["FullEnvironmentObjects"] = array
        };
    }

    private static LaneChangePlanner Planner() => new(new Dictionary<string, string> { ["LaneChange"] = "1", ["OpposingLane"] = "1" });

    // A truck 45 m ahead, 2 m/s slower than the CAV; nothing else that the CAV sees - and, if said, a CAV coming the other way,
    // 1.4 km ahead, that tells where it is and what it sees.
    private static (LaneChangePlanner Planner, LaneChange? Decision, int Step) Follow(bool remoteCav)
    {
        var planner = Planner();
        for (var i = 0; i < 40; i++)
        {
            var objects = new List<Obj> { new("Truck", "Ego", 45 - 0.2 * i, 0, -2, "truck") };
            if (remoteCav) objects.Add(new("CAV-9/CAV-9", "Remote", 1400 - 4.6 * i, 3.5, -46));
            var decision = planner.Update(Fed(i * 100L, 22, objects.ToArray()), i * 2.2, 30, 2);
            if (decision is not null) return (planner, decision, i);
        }
        return (planner, null, -1);
    }

    [Fact]
    public void WithoutAReportItDoesNotPullOutBehindATruck()
    {
        var (planner, decision, _) = Follow(remoteCav: false);
        Assert.Null(decision);
        Assert.Contains("oncoming traffic cannot be ruled out", planner.Status);
    }

    [Fact]
    public void ToldThatTheLaneIsFreeItPullsOutToTheOtherLane()
    {
        var (planner, decision, step) = Follow(remoteCav: true);
        Assert.NotNull(decision);
        Assert.Equal(3.5, decision!.ToOffset, 3);
        Assert.Equal(LaneChangePlanner.Phase.PullingOut, planner.Now);
        Assert.True(step < 25, $"it decided after {step} looks");
    }

    [Fact]
    public void ToldThatAVehicleComesItWaits()
    {
        var planner = Planner();
        LaneChange? decision = null;
        for (var i = 0; i < 40 && decision is null; i++)
            decision = planner.Update(Fed(i * 100L, 22, new Obj("Truck", "Ego", 45 - 0.2 * i, 0, -2, "truck"),
                                                        new Obj("CAV-9/CAV-9", "Remote", 1400 - 4.6 * i, 3.5, -46),
                                                        new Obj("CAV-9/Car", "Remote", 600 - 4.6 * i, 3.5, -46)), i * 2.2, 30, 2);
        Assert.Null(decision);
        Assert.Contains("CAV-9/Car", planner.Status);
    }

    // Out, and not yet level with the truck: a vehicle that was not there is seen coming. It goes back behind the truck.
    [Fact]
    public void ItGoesBackBehindTheTruckIfAVehicleAppearsBeforeItIsLevel()
    {
        var (planner, _, step) = Follow(remoteCav: true);
        var i = step + 1;
        var decision = planner.Update(Fed(i * 100L, 23, new Obj("Truck", "Ego", 35, 0, -3, "truck"), new Obj("CAV-9/CAV-9", "Remote", 1200, 3.5, -47),
                                                          new Obj("Surprise", "Ego", 260, 3.5, -47)), i * 2.2, 30, 2);
        Assert.NotNull(decision);
        Assert.Equal(0, decision!.ToOffset, 3);
        Assert.Equal(LaneChangePlanner.Phase.Returning, planner.Now);
        Assert.Contains("abandons", planner.Status);
    }

    // Level with the truck already: it cannot go back behind it; it gets past as soon as it can.
    [Fact]
    public void ItHurriesPastIfAVehicleAppearsWhenItIsLevel()
    {
        var (planner, _, step) = Follow(remoteCav: true);
        var i = step + 1;
        var decision = planner.Update(Fed(i * 100L, 27, new Obj("Truck", "Ego", -3, -3.5, -7, "truck"), new Obj("CAV-9/CAV-9", "Remote", 1200, 0, -51),
                                                          new Obj("Surprise", "Ego", 260, 0, -51)), i * 2.2, 30, 2);
        Assert.Null(decision);
        Assert.Contains("hurrying", planner.Status);
        Assert.Equal(LaneChangePlanner.Phase.PullingOut, planner.Now);
    }
}
