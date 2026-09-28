using System.Text.Json.Nodes;

using Mpai.Cav.Ess;
using Mpai.Cav.Recordings;

namespace Mpai.Aif.Tests;

// PERFECT PERCEPTION: the Basic Environment Descriptors V2.0 of a step of the
// simulation made from its ground truth - the CAV where it is, on the map's frame,
// each vehicle where it is, relative to the CAV. For testing the AMS apart from what
// the ESS gets wrong.
public static class TruthBed
{
    // occlude: a vehicle behind a nearer one in its lane is not seen (the camera's
    // view); known: vehicles given all the same, wherever they are - as a Remote CAV
    // would report them (M3241).
    public static string Of(Simulation sim, Simulation.Sensed sensed, bool occlude = false, IReadOnlyCollection<string>? known = null)
    {
        var truth = sensed.Truth;
        var ego = truth["Ego"]!;
        var heading = (double)ego["Heading"]! * Math.PI / 180;
        var speed = (double)ego["Speed"]!;
        var ms = sensed.FrameMs;
        var attitude = EssJson.Attitude($"EGO{sim.StepNumber}", ms, ((double)ego["East"]!, (double)ego["North"]!, 0), (0.1, 0.1, 0.1),
            (speed * Math.Cos(heading), speed * Math.Sin(heading), 0),
            new JsonObject { ["Header"] = "OSD-OOR-V1.5", ["OrientationID"] = $"EGO{sim.StepNumber}-O", ["Orientation"] = new JsonArray(0.0, 0.0, heading * 180 / Math.PI) });
        var objects = new JsonArray();
        var seen = truth["Vehicles"]!.AsArray().Select(v => (Id: (string)v!["Id"]!, Lane: (int)v["Lane"]!, Distance: (double)v["Distance"]!, Speed: (double)v["Speed"]!)).ToList();
        if (occlude) seen = seen.Where(v => !seen.Any(o => o.Lane == v.Lane && o.Distance < v.Distance)).ToList();
        foreach (var k in sim.Around().Where(a => known?.Contains(a.Id) == true && a.Ahead > 0 && seen.All(v => v.Id != a.Id)))
            seen.Add((k.Id, k.Lane, k.Ahead, k.Speed));
        foreach (var v in seen.Select(s => new JsonObject { ["Id"] = s.Id, ["Lane"] = s.Lane, ["Distance"] = s.Distance, ["Speed"] = s.Speed }))
        {
            var id = (string)v!["Id"]!;
            objects.Add(new JsonObject
            {
                ["BasicEnvironmentObjectID"] = id,
                ["InstanceIdentifier"] = EssJson.Identifier("car", 1),
                ["SpatialAttitude"] = EssJson.Attitude($"{id}-{sim.StepNumber}", ms, ((double)v["Distance"]!, (int)v["Lane"]! * 3.5, 0), (0.1, 0.1, 0.1),
                    ((double)v["Speed"]! - speed, 0, 0)),
                ["ExistenceConfidence"] = 1.0, ["Motion"] = "Dynamic",
                ["Contributions"] = new JsonArray(new JsonObject { ["Technology"] = "Visual" })
            });
        }
        return new JsonObject
        {
            ["Header"] = "CAV-BED-V2.0", ["BasicEnvironmentDescriptorsID"] = $"TRUTH{sim.StepNumber:D6}",
            ["BasicEnvironmentDescriptorsTime"] = EssJson.SimpleTime($"TRUTH{sim.StepNumber:D6}-T", ms),
            ["EgoSpatialAttitude"] = attitude, ["BasicEnvironmentObjectCount"] = objects.Count, ["BasicEnvironmentObjects"] = objects
        }.ToJsonString();
    }
}
