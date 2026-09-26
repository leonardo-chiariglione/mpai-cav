using System.Text.Json;
using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Cav.Ess;
using Mpai.Cav.Recordings;

namespace Mpai.Aif.Tests;

// THE ESS IN THE LOOP: the Module 1CAV-ESS-V2.0-I01 under the Controller, fed a
// simulation step by step - each step's sensor data written to its boundary, and
// the step over when the Basic Environment Descriptors of its frame come out. The
// simulation waits for the CAV: nothing is dropped for want of time (M3233).
public sealed class EssInTheLoop : IDisposable
{
    public const string Ess = "1CAV-ESS-V2.0-I01";
    private readonly Mpai.Aif.Api.ControllerApi api;

    public EssInTheLoop()
    {
        api = new Mpai.Aif.Api.ControllerApi(Repository.Amds, Path.Combine(Repository.Root, "AIMs", "aim-settings.json"), new EssProvider(Repository.Root));
        var started = api.StartFlow(Ess);
        if (started != AifError.OK) throw new InvalidOperationException($"{Ess} did not start: {started}");
    }

    public ControllerApiStatus Status => new(api.Status(Ess));

    public (JsonNode? Descriptors, List<JsonNode> Alerts) Step(Simulation.Sensed sensed, int timeoutMs = 60_000)
    {
        foreach (var (dataType, json) in sensed.Messages)
        {
            var written = api.InputWrite(Ess, dataType, 1, json, timeoutMs);
            if (written != AifError.OK) throw new InvalidOperationException($"{dataType} not written: {written}");
        }
        JsonNode? descriptors = null;
        while (descriptors is null)
        {
            var read = api.OutputRead(Ess, "CAV-BED-V2.0", 1, timeoutMs);
            if (read.Error != AifError.OK) throw new InvalidOperationException($"no Basic Environment Descriptors for {sensed.FrameMs}: {read.Error}");
            var json = JsonNode.Parse(read.Json!)!;
            if ((long)(double)json["BasicEnvironmentDescriptorsTime"]!["SimpleTimeData"]![0]!["StartTime"]! == sensed.FrameMs) descriptors = json;
        }
        var alerts = new List<JsonNode>();
        while (api.OutputRead(Ess, "CAV-ALT-V1.1", 1, 0) is { Error: AifError.OK, Json: { } a }) alerts.Add(JsonNode.Parse(a)!);
        return (descriptors, alerts);
    }

    public void Dispose()
    {
        api.StopFlow(Ess);
        api.Dispose();
    }
}

public sealed record ControllerApiStatus(Mpai.Aif.Api.ControllerApi.ModuleStatus Module)
{
    public override string ToString() => string.Join("; ", Module.Aims.OrderBy(a => a.Aim).Select(a => $"{a.Aim} {a.Status}"));
}

// PHASE 8 (M3233): the Autonomous Motion Subsystem, Stage 1, the loop closed by a
// stepped simulation. Run alone: the detector keeps the processor busy (Timing).
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
[Collection(Timing.Name)]
public class AmsStage1Tests
{
    // The scenario of Step 1: a map from a seed; the Route of least time from one
    // corner to a way point past a junction; a vehicle ahead in the ego's lane,
    // faster, and a slower one in the left lane, which the ego overtakes.
    public static Simulation OpenScenario()
    {
        var map = RoadMap.Grid(3);
        var route = map.FastestRoute("W00", "W21")!;
        return new Simulation(map, route,
        [
            new("ahead", 0, 40, [(0, 19.0)], CameraRenderer.Silver),
            new("left", 1, 70, [(0, 8.0)], CameraRenderer.DarkRed)
        ], seed: 7, egoSpeed: 10);
    }

    // Step 1: the map and the simulation, open - the ego keeping the speed limit,
    // whatever it meets - with the ESS in the loop for 20 s: every frame described;
    // what the ESS says against the truth; the same seed, the same world.
    [SkippableFact]
    public void Step1SimulationWithTheEss()
    {
        Skip.IfNot(File.Exists(Path.Combine(Repository.Root, "Models", "yolox_s.onnx")), "Models/yolox_s.onnx is absent: the model files are obtained separately.");
        var result = new Dictionary<string, string>();
        var report = new Dictionary<string, string>();

        // The same seed, the same world: two runs of the simulation alone, the same policy.
        string Run()
        {
            var sim = OpenScenario();
            var truths = new List<string>();
            for (var i = 0; i < 200; i++) { var sensed = sim.Sense(); truths.Add(sensed.Truth.ToJsonString()); sim.Advance(KeepTheLimit(sim)); }
            return string.Concat(truths);
        }
        result["the same seed, the same world"] = Run() == Run() ? "yes" : "no";

        var map = RoadMap.Grid(3);
        var route = map.FastestRoute("W00", "W21")!;
        result["the Route of the scenario"] = $"{string.Join(" ", route.Select(s => s.Id))}, {map.TimeOf(route):0.0} s at the limits";

        var s = OpenScenario();
        using var ess = new EssInTheLoop();
        int steps = 0, described = 0, aheadFrames = 0, aheadFound = 0, leftFrames = 0, leftFound = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string? statusAtEnd = null;
        for (var i = 0; i < 200 && !s.Arrived; i++)
        {
            var sensed = s.Sense();
            var (bed, _) = ess.Step(sensed);
            steps++;
            if (bed is not null) described++;
            var objects = bed!["BasicEnvironmentObjects"]!.AsArray()
                .Select(o => o!["SpatialAttitude"]!["Position"]!["CartPosition"]!.AsArray()).Select(p => (X: (double)p[0]!, Y: (double)p[1]!)).ToList();
            foreach (var v in sensed.Truth["Vehicles"]!.AsArray())
            {
                var d = (double)v!["Distance"]!;
                if (d < 12 || d > 50) continue;
                var lane = (int)v["Lane"]!;
                var found = objects.Any(o => Math.Abs(o.X - d) < 0.25 * d + 2 && (lane == 0 ? Math.Abs(o.Y) < 1.75 : o.Y > 1.75 && o.Y < 5.25));
                if (lane == 0) { aheadFrames++; if (found) aheadFound++; } else { leftFrames++; if (found) leftFound++; }
            }
            s.Advance(KeepTheLimit(s));
        }
        statusAtEnd = ess.Status.ToString();

        result["steps, each frame described"] = steps == described ? $"{steps}, all described" : $"{steps}, {described} described";
        result["the vehicle ahead, 12 to 50 m, in the descriptors"] = aheadFrames == 0 ? "never in view" : aheadFound >= aheadFrames * 0.9 ? "90% of frames or more" : "fewer than 90% of frames";
        result["the vehicle in the left lane, 12 to 50 m, in the descriptors"] = leftFrames == 0 ? "never in view" : leftFound >= leftFrames * 0.9 ? "90% of frames or more" : "fewer than 90% of frames";
        result["the ESS's AIMs at the end"] = statusAtEnd;
        report["the vehicle ahead, 12 to 50 m, in the descriptors"] = $"{aheadFound} of {aheadFrames}";
        report["the vehicle in the left lane, 12 to 50 m, in the descriptors"] = $"{leftFound} of {leftFrames}";
        report["time a step takes"] = $"{clock.Elapsed.TotalMilliseconds / Math.Max(1, steps):0} ms";
        report["ego at the end"] = $"{s.EgoS:0} m along the Route of {s.Path.Length:0} m, at {s.EgoSpeed:0.0} m/s";

        foreach (var (k, v) in result) report.TryAdd(k, v);
        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "ams-stage1-simulation.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Expected.Match("ams-stage1-simulation.json", result);
    }

    // Open: the speed limit of the segment, reached at 1.5 m/s2, whatever is ahead.
    public static double KeepTheLimit(Simulation s)
    {
        var limit = s.Path.At(s.EgoS).Segment.SpeedLimit;
        return Math.Clamp((limit - s.EgoSpeed) / 1.0, -2, 1.5);
    }
}
