using System.Text;
using System.Text.Json.Nodes;

using AIF.SharedStorage;

using Mpai.Cav.Ams;
using Mpai.Cav.Ess;

namespace Mpai.Aif.Tests;

// THE GENERAL PLANNER IN THE MOTION SELECTION PLANNING AIM (the author, 2026/11/01): with the setting Planner = SpaceTime the Trajectory keeps clear of every
// object of the Full Environment Descriptors - a pedestrian at the kerb included - or brakes as hard as it can; what was not as predicted is AMS Data in the
// Shared Storage of the Module, the planner's last plan is in its Private Storage.
[Trait("Group", "Fast")]
public class MspSpaceTimeTests
{
    private static JsonObject PathResponse() => new()
    {
        ["PathResponse"] = new JsonObject
        {
            ["Path"] = new JsonArray(Enumerable.Range(0, 101).Select(i => (JsonNode)new JsonObject
            {
                ["PointOfView"] = new JsonObject { ["CartPosition"] = new JsonArray(i * 5.0, 0.0, 0.0), ["Orientation"] = new JsonArray(0.0, 0.0, 0.0) }
            }).ToArray())
        }
    };

    // A Full Environment Descriptors at t seconds: the CAV at x, going east at 14 m/s; the objects given as (id, class, ahead, left, speed ahead relative to the CAV's).
    private static JsonNode Fed(double t, double x, params (string Id, string Class, double Ahead, double Left, double Rel)[] objects)
    {
        var ms = 1_767_254_400_000 + (long)(t * 1000);
        var list = new JsonArray();
        foreach (var o in objects)
            list.Add(new JsonObject
            {
                ["BasicEnvironmentObject"] = new JsonObject
                {
                    ["BasicEnvironmentObjectID"] = o.Id, ["InstanceIdentifier"] = EssJson.Identifier(o.Class, 1),
                    ["SpatialAttitude"] = EssJson.Attitude($"{o.Id}-{t}", ms, (o.Ahead, o.Left, 0), (0.1, 0.1, 0.1), (o.Rel, 0, 0)), ["ExistenceConfidence"] = 1.0
                },
                ["Source"] = "Ego"
            });
        return new JsonObject
        {
            ["Header"] = "CAV-FED-V2.0", ["FullEnvironmentDescriptorsID"] = $"FED{t}", ["FullEnvironmentDescriptorsTime"] = EssJson.SimpleTime($"FED{t}-T", ms),
            ["EgoSpatialAttitude"] = EssJson.Attitude($"EGO{t}", ms, (x, 0, 0), (0.1, 0.1, 0.1), (14, 0, 0)),
            ["RoadAhead"] = new JsonArray(new JsonObject { ["SpeedLimit"] = 14.0 }), ["FullEnvironmentObjectCount"] = list.Count, ["FullEnvironmentObjects"] = list
        };
    }

    private static double SpeedAt(JsonObject trajectory, int k) =>
        EssJson.Vector(trajectory["Trajectory"]![k]!["ExpectedSpaceTime"]!["SpatialAttitude1"]!["Position"]!["CartVelocity"])!.Value.X;

    [Fact]
    public void ThePlannerSlowsForAPedestrianAtTheKerbAndNotForNoOne()
    {
        var settings = new Dictionary<string, string> { ["Planner"] = "SpaceTime" };
        var clear = new MotionSelectionPlanning("MSP", settings);
        clear.Follow(PathResponse()["PathResponse"]!);
        var empty = clear.Plan(Fed(0, 0));
        var withPedestrian = new MotionSelectionPlanning("MSP", settings);
        withPedestrian.Follow(PathResponse()["PathResponse"]!);
        var careful = withPedestrian.Plan(Fed(0, 0, ("P", "person", 25, -3, -14)));          // standing at the kerb 25 m ahead: its velocity is the CAV's reversed

        Assert.True(SpeedAt(empty, 30) > 13, $"no one: {SpeedAt(empty, 30):F1} m/s after 3 s");
        Assert.True(SpeedAt(careful, 30) < 8, $"a pedestrian 25 m ahead at the kerb: {SpeedAt(careful, 30):F1} m/s after 3 s");
    }

    [Fact]
    public void WhatWasNotAsPredictedIsAmsDataAndThePlanIsInThePrivateStorage()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpai-msp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var shared = new FileSharedStorage(Path.Combine(root, "shared"), "Module/MSP", "test");
            var work = new FileSharedStorage(Path.Combine(root, "private"), "Module/MSP", "test");
            var msp = new MotionSelectionPlanning("MSP", new Dictionary<string, string> { ["Planner"] = "SpaceTime" }, shared, work);
            msp.Follow(PathResponse()["PathResponse"]!);
            msp.Plan(Fed(0, 0, ("P", "person", 60, -3, -14)));
            msp.Plan(Fed(0.1, 1.4, ("P", "person", 60 - 1.4 - 0.5, -3 + 0.5, -14 + 4)));    // it went at 5 m/s: more than a pedestrian is allowed

            var events = shared.MPAI_AIFM_SharedStorage_List(AmsData.Prefix + "events/");
            Assert.Single(events);
            Assert.Contains("\"Event\":\"Unpredicted\"", Encoding.UTF8.GetString(shared.MPAI_AIFM_SharedStorage_Get(events[0])));
            Assert.True(work.MPAI_AIFM_SharedStorage_Exists("work/last-plan"));
            Assert.Empty(shared.MPAI_AIFM_SharedStorage_List("work/"));          // the work area is not where the AMS Data is
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
