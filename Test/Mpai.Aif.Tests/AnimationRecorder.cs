using System.Text.Json;
using System.Text.Json.Nodes;

using Mpai.Cav.Ams;

namespace Mpai.Aif.Tests;

// Scratch recorder for the planner animations: runs the closed loop of Step 4 on each scenario and writes a frame per step.
// Runs only when MPAI_RECORD names the output file; the planner is the one MPAI_PLANNER chooses.
public sealed class AnimationRecorder
{
    [Fact]
    public void Record()
    {
        var file = Environment.GetEnvironmentVariable("MPAI_RECORD");
        if (string.IsNullOrEmpty(file)) return;
        var all = new JsonObject();
        foreach (var (name, make) in MasStage1Tests.Scenarios())
        {
            var sim = make();
            var frames = new JsonArray();
            MasStage1Tests.Loop(sim, s =>
            {
                var ego = s.Sensed.Truth["Ego"]!;
                var traj = s.Message["AMSMessage"]!["Trajectory"]!["Trajectory"]!.AsArray();
                var plan = new JsonArray();
                for (var i = 0; i < traj.Count; i += 5) plan.Add(Math.Round(TrafficObstacleAvoidance.Speed(traj[i]!), 2));
                var others = new JsonArray();
                foreach (var o in sim.Around()) others.Add(new JsonArray(o.Id, o.Lane, Math.Round(o.Ahead, 2), Math.Round(o.Speed, 2)));
                frames.Add(new JsonObject
                {
                    ["t"] = Math.Round(sim.Time, 2), ["s"] = Math.Round(sim.EgoS, 2), ["v"] = Math.Round(sim.EgoSpeed, 2), ["a"] = Math.Round(sim.EgoAcceleration, 2),
                    ["off"] = Math.Round(sim.EgoOffset, 2), ["skid"] = sim.Mechanics!.Skidding, ["lim"] = (double)ego["SpeedLimit"]!,
                    ["e"] = (double)ego["East"]!, ["n"] = (double)ego["North"]!, ["h"] = (double)ego["Heading"]!,
                    ["plan"] = plan, ["others"] = others
                });
            }, mas: true);
            all[name] = new JsonObject { ["length"] = Math.Round(sim.Path.Length, 1), ["arrived"] = sim.Arrived, ["collided"] = sim.Collided, ["frames"] = frames };
        }
        File.WriteAllText(file, all.ToJsonString());
    }

    // The same scenarios without the MAS (Step 5's loop: the Trajectory's acceleration moves the ego along its Route).
    [Fact]
    public void RecordDrive()
    {
        var file = Environment.GetEnvironmentVariable("MPAI_RECORD2");
        if (string.IsNullOrEmpty(file)) return;
        var all = new JsonObject();
        foreach (var (name, make) in AmsStage1Tests.Scenarios())
        {
            var sim = make();
            var fed = new FullEnvironmentDescription(AmsProvider.Fed);
            fed.Know(JsonNode.Parse(sim.Map.ToOfflineMapObject(0))!);
            var msp = new MotionSelectionPlanning(AmsProvider.Msp, new Dictionary<string, string>());
            msp.Follow(AmsStage1Tests.PathOf(sim));
            var frames = new JsonArray();
            for (var steps = 0; steps < 900 && !sim.Arrived && !sim.Collided; steps++)
            {
                var sensed = sim.Sense(camera: false);
                var ego = sensed.Truth["Ego"]!;
                var described = fed.Describe(JsonNode.Parse(TruthBed.Of(sim, sensed))!);
                var traj = msp.Plan(described)["Trajectory"]!.AsArray();
                var plan = new JsonArray();
                for (var i = 0; i < traj.Count; i += 5) plan.Add(Math.Round(TrafficObstacleAvoidance.Speed(traj[i]!), 2));
                var others = new JsonArray();
                foreach (var o in sim.Around()) others.Add(new JsonArray(o.Id, o.Lane, Math.Round(o.Ahead, 2), Math.Round(o.Speed, 2)));
                frames.Add(new JsonObject
                {
                    ["t"] = Math.Round(sim.Time, 2), ["s"] = Math.Round(sim.EgoS, 2), ["v"] = Math.Round(sim.EgoSpeed, 2), ["a"] = Math.Round(sim.EgoAcceleration, 2),
                    ["off"] = 0.0, ["skid"] = false, ["lim"] = (double)ego["SpeedLimit"]!,
                    ["e"] = (double)ego["East"]!, ["n"] = (double)ego["North"]!, ["h"] = (double)ego["Heading"]!, ["plan"] = plan, ["others"] = others
                });
                var a = (TrafficObstacleAvoidance.Speed(traj[1]!) - TrafficObstacleAvoidance.Speed(traj[0]!)) / MotionSelectionPlanning.Step;
                sim.Advance(a);
            }
            all[name] = new JsonObject { ["length"] = Math.Round(sim.Path.Length, 1), ["arrived"] = sim.Arrived, ["collided"] = sim.Collided, ["frames"] = frames };
        }
        File.WriteAllText(file, all.ToJsonString());
    }

    // The two-way road: the truck and the traffic coming the other way, the other CAVs telling.
    [Fact]
    public void RecordOncoming()
    {
        var file = Environment.GetEnvironmentVariable("MPAI_RECORD3");
        if (string.IsNullOrEmpty(file)) return;
        var world = OncomingScenarios.TruckAndOncomingTraffic();
        var (trace, _) = HighwayOncomingTests.Drive(world, 100, cooperate: true, roadFriction: 0.9, decisionSeconds: 4);
        var frames = new JsonArray();
        foreach (var t in trace.Where((_, i) => i % 2 == 0)) frames.Add(new JsonArray(Math.Round(t.T, 1), Math.Round(t.X, 2), Math.Round(t.Y, 2), Math.Round(t.Speed, 3), Math.Round(t.TruckX, 1), Math.Round(Math.Min(t.Oncoming, 9999), 0)));
        File.WriteAllText(file, new JsonObject { ["frames"] = frames, ["collided"] = world.EgoCollisions.Any() }.ToJsonString());
    }
}
