using System.Text.Json;
using System.Text.Json.Nodes;

using Mpai.Cav.Map;
using Mpai.Cav.Recordings;

namespace Mpai.Aif.Tests;

// PHASE 10 (M3241): the Remote CAVs, Stage 1.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class RemoteStage1Tests
{
    // THE HIDDEN VEHICLE (M3241 3.5): on the first straight of the Route of Phase 8, a
    // vehicle stopped in the lane at 200 m; CAV A 25 m ahead of CAV B at 12 m/s, in the
    // same lane, hiding it; A changes lane 8 m before it (scripted: Stage 1's Paths
    // keep the lane). B starts at 12 m/s.
    public const double StoppedAt = 200, AStart = 30, Speed = 12, CutOut = 8;

    public static Simulation HiddenVehicle()
    {
        var map = RoadMap.Grid(3);
        var route = map.FastestRoute("W00", "W21")!;
        var cutAt = (StoppedAt - CutOut - AStart) / Speed;
        return new Simulation(map, route,
        [
            new ScenarioVehicle("A", 0, AStart, [(0, Speed)], CameraRenderer.Silver, LaneChange: (cutAt, 1)),
            new ScenarioVehicle("stopped", 0, StoppedAt, [(0, 0.0)], CameraRenderer.DarkRed)
        ], seed: 7, egoSpeed: Speed);
    }

    // B driven on perfect perception as its camera would allow it - what a nearer
    // vehicle in its lane hides, and what is beyond 90 m, unseen - or with the stopped
    // vehicle known, as A would report it. Judged: no collision; with it known, less
    // hard braking than without. Reported: when B first knows the stopped vehicle,
    // its speed when A pulls out, the hardest braking, the lowest time to collision,
    // the gap it stops at.
    [Fact]
    public void Step1WithoutTheExchange()
    {
        var result = new Dictionary<string, string>();
        var report = new Dictionary<string, string>();
        var hardest = new Dictionary<string, double>();
        foreach (var (name, known) in new[] { ("the stopped vehicle unknown until seen", false), ("the stopped vehicle known, as A would report it", true) })
        {
            var sim = HiddenVehicle();
            double brake = 0, lowestTtc = double.PositiveInfinity, speedAtCut = double.NaN;
            double? knownAt = null;
            var cutAt = (StoppedAt - CutOut - AStart) / Speed;
            MasStage1Tests.Loop(sim, s =>
            {
                brake = Math.Max(brake, -sim.EgoAcceleration);
                if (double.IsNaN(speedAtCut) && sim.Time >= cutAt) speedAtCut = sim.EgoSpeed;
                var stopped = sim.Around().First(a => a.Id == "stopped");
                if (sim.Around().All(a => a.Id == "stopped" || a.Lane != 0 || a.Ahead > stopped.Ahead) && stopped.Ahead > 0 && sim.EgoSpeed > 0.1)
                    lowestTtc = Math.Min(lowestTtc, stopped.Ahead / sim.EgoSpeed);
                // Known: from the start when reported; else once within the camera's 90 m
                // with nothing nearer in the lane - as TruthBed sees it.
                var visible = stopped.Ahead < 90 && sim.Around().All(a => a.Id == "stopped" || a.Lane != 0 || a.Ahead > stopped.Ahead);
                if (knownAt is null && (known || visible)) knownAt = known ? 0 : sim.Time;
            }, bedOf: (m, sensed) => TruthBed.Of(m, sensed, occlude: true, known: known ? ["stopped"] : null));
            var gap = sim.Around().First(a => a.Id == "stopped").Ahead;
            hardest[name] = brake;
            result[name] = $"{(sim.Collided ? "collision" : "no collision")}";
            report[name] = result[name] + $"; the stopped vehicle known at {knownAt:0.0} s (A pulls out at {cutAt:0.0} s); speed when A pulls out {speedAtCut:0.0} m/s; " +
                           $"hardest braking {brake:0.0} m/s2; lowest time to collision {(double.IsPositiveInfinity(lowestTtc) ? "-" : lowestTtc.ToString("0.0"))} s; stopped {gap:0.0} m behind it";
        }
        result["known, against unknown"] = hardest.Values.Last() < hardest.Values.First() - 0.5 ? "less hard braking" : "no less hard braking";
        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "remote-stage1-without.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Expected.Match("remote-stage1-without.json", result);
    }
}
