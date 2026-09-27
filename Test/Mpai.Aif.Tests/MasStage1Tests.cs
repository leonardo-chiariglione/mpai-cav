using System.Text.Json;
using System.Text.Json.Nodes;

using Mpai.Cav.Map;
using Mpai.Cav.Recordings;

namespace Mpai.Aif.Tests;

// PHASE 9 (M3237): the Motion Actuation Subsystem, Stage 1.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class MasStage1Tests
{
    private static readonly RoadMap Map = RoadMap.Grid(3);

    // THE COMMANDS OF THE MECHANICAL SUBSYSTEMS, as their schemas give them.
    public static string Brake(string id, double deceleration, long ms, bool abs = true, bool emergency = false, double releaseAt = 0) => new JsonObject
    {
        ["Header"] = "CAV-BRC-V2.0", ["BrakeCommandID"] = id, ["BrakeID"] = "B1",
        ["BrakeCommand"] = new JsonArray(new JsonObject
        {
            ["TargetVelocity"] = releaseAt, ["BrakeCommandTime"] = Time(id + "-T", ms),
            ["DecelerationTarget"] = deceleration, ["ABSAllow"] = abs, ["EmergencyBrakeFlag"] = emergency
        })
    }.ToJsonString();

    public static string Motor(string id, string mode, double target, long ms)
    {
        var command = new JsonObject { ["ControlMode"] = mode, ["MotorCommandTime"] = Time(id + "-T", ms) };
        command[mode switch { "torque" => "TargetTorque", "acceleration" => "TargetAcceleration", _ => "TargetVelocity" }] = target;
        return new JsonObject { ["Header"] = "CAV-MRC-V2.0", ["MotorCommandID"] = id, ["MotorID"] = "M1", ["MotorCommand"] = command }.ToJsonString();
    }

    public static string Wheel(string id, double degrees, long ms) => new JsonObject
    {
        ["Header"] = "CAV-WHC-V2.0", ["WheelCommandID"] = id, ["WheelID"] = "W1", ["WheelCommandTime"] = Time(id + "-T", ms),
        ["WheelCommand"] = new JsonObject { ["Angle"] = degrees, ["SteeringMode"] = "SteerByWire" }
    }.ToJsonString();

    private static JsonObject Time(string id, long ms) => new()
    {
        ["Header"] = "OSD-STM-V1.5", ["SimpleTimeID"] = id,
        ["SimpleTimeData"] = new JsonArray(new JsonObject { ["FlagsByte"] = 3, ["StartTime"] = ms, ["EndTime"] = ms, ["AccuracyMode"] = "single", ["AccuracyPlusMinus"] = 1 })
    };

    // A run: the vehicle on a Route, the same commands at every step, until stopped
    // or for a time. Every command, Response and Spatial Data kept, to be validated.
    private sealed record Run(Simulation Sim, List<(string DataType, string Json)> Objects, double Start, int Steps);

    private static Run Drive(string to, double speed, double seconds, Func<Simulation, long, int, IEnumerable<(string, string)>> commands,
                             double friction = Simulation.DryFriction, bool untilStopped = false)
    {
        var route = Map.FastestRoute("W00", to)!;
        var sim = new Simulation(Map, route, [], seed: 9, egoSpeed: speed)
        {
            Surface = friction == Simulation.DryFriction ? [] : [(0, 10_000, friction)]
        };
        sim.Mechanical(seed: 9);
        var objects = new List<(string, string)>();
        var steps = 0;
        while (steps < seconds / Simulation.Step && !(untilStopped && steps > 0 && sim.EgoSpeed <= 0))
        {
            var ms = (sim.Start + TimeSpan.FromSeconds(sim.Time)).ToUnixTimeMilliseconds();
            var given = commands(sim, ms, steps).ToList();
            objects.AddRange(given);
            sim.Actuate(given);
            objects.AddRange(sim.Sense(camera: false).Messages.Where(m => m.DataType == "CAV-SPD-V2.0"));
            objects.AddRange(sim.Advance());
            steps++;
        }
        return new Run(sim, objects, 0, steps);
    }

    // STEP 1 (M3237 3.1): the mechanical subsystems and the vehicle, open loop.
    [Fact]
    public void Step1MechanicsOpenLoop()
    {
        var result = new Dictionary<string, string>();
        var everything = new List<(string DataType, string Json)>();
        const double g = Vehicle.G;

        // Braking from 20 m/s at more than the road allows: ABS holds the tyres near
        // their peak. With the rolling resistance and the drag, a deceleration
        // a0 + k v^2 stops in ln(1 + k v^2 / a0) / 2k; the brakes' lag adds v tau.
        foreach (var (name, friction, abs) in new[] { ("dry, ABS", Simulation.DryFriction, true), ("icy, ABS", 0.15, true), ("icy, no ABS", 0.15, false) })
        {
            var sawAbs = false; var sawLock = false;
            var run = Drive("W20", 20, 60, (sim, ms, k) =>
            {
                sawAbs |= sim.Mechanics!.AbsActive; sawLock |= sim.Mechanics.WheelLocked;
                return [("CAV-BRC-V2.0", Brake($"BRC{k}", 9, ms, abs))];
            }, friction, untilStopped: true);
            var deceleration = Math.Min(Vehicle.MaxBrakeDeceleration, (abs ? Vehicle.AbsGrip : Vehicle.LockedGrip) * friction * g);
            var a0 = deceleration + Vehicle.RollingResistance * g;
            var k = Vehicle.AirDensity * Vehicle.DragArea / (2 * Vehicle.Mass);
            var expected = Math.Log(1 + k * 20 * 20 / a0) / (2 * k) + 20 * Vehicle.BrakeLag;
            var distance = run.Sim.Mechanics!.Distance;
            everything.AddRange(run.Objects);
            result[$"braking from 20 m/s, {name}"] =
                $"stopped in {Near(distance, expected, 0.05)} of {expected:0} m; {(abs ? (sawAbs ? "ABS active" : "ABS never active") : (sawLock ? "wheels locked" : "wheels never locked"))}; offset {Math.Abs(run.Sim.EgoOffset):0.00} m";
        }

        // Full torque from standstill: never more than the tyres or the motor's power
        // allow, at the speed of the step's start (the speed changes within a step);
        // the speed reached in 10 s.
        {
            double worst = 0, v0 = 0;
            var run = Drive("W20", 0, 10, (sim, ms, k) =>
            {
                var v = sim.Mechanics!.Speed; var a = sim.Mechanics.Acceleration;
                if (k > 0) worst = Math.Max(worst, a - Math.Min(Simulation.DryFriction * g, Vehicle.MaxPower / (Vehicle.Mass * Math.Max(v0, 1))));
                v0 = v;
                return [("CAV-MRC-V2.0", Motor($"MRC{k}", "torque", Vehicle.MaxMotorTorque, ms))];
            });
            everything.AddRange(run.Objects);
            result["full torque from standstill, 10 s"] =
                $"{(worst <= 0.01 ? "within grip and power" : $"beyond them by {worst:0.00} m/s2")}; speed reached {Bucket(run.Sim.Mechanics!.Speed, 2)} m/s";
        }

        // A steady turn: at 10 m/s, the road wheels at 5 degrees, the heading turns at
        // v tan(delta) / L once the steering has reached its angle.
        {
            double h0 = 0;
            var run = Drive("W20", 10, 6, (sim, ms, k) =>
            {
                if (k == 10) h0 = sim.Mechanics!.Heading;
                return [("CAV-MRC-V2.0", Motor($"MRC{k}", "velocity", 10, ms)), ("CAV-WHC-V2.0", Wheel($"WHC{k}", 5, ms))];
            });
            var rate = (run.Sim.Mechanics!.Heading - h0) / ((run.Steps - 10) * Simulation.Step);
            var expected = 10 * Math.Tan(5 * Math.PI / 180) / Vehicle.Wheelbase;
            everything.AddRange(run.Objects);
            result["a steady turn, 10 m/s, 5 degrees, dry"] = $"yaw rate {Near(rate, expected, 0.05)} of {expected:0.000} rad/s";
        }

        // Too sharp a turn on ice: the tyres cannot give it; the vehicle slides, and
        // turns only as far as the grip lets it.
        {
            var skid = false;
            var run = Drive("W20", 15, 3, (sim, ms, k) =>
            {
                skid |= sim.Mechanics!.Skidding;
                return [("CAV-MRC-V2.0", Motor($"MRC{k}", "velocity", 15, ms)), ("CAV-WHC-V2.0", Wheel($"WHC{k}", 20, ms))];
            }, friction: 0.15);
            everything.AddRange(run.Objects);
            result["too sharp a turn on ice, 15 m/s, 20 degrees"] = skid ? "the vehicle slides" : "no slide";
        }

        // The sensors and the Objects.
        {
            // Spatial Data is read before the step moves the vehicle: compare it with
            // the distance travelled at that moment.
            double atReading = 0;
            var run = Drive("W20", 15, 10, (sim, ms, k) =>
            {
                atReading = sim.Mechanics!.Distance;
                return [("CAV-MRC-V2.0", Motor($"MRC{k}", "velocity", 15, ms))];
            });
            var last = JsonNode.Parse(run.Objects.Last(o => o.DataType == "CAV-SPD-V2.0").Json)!["SpatialData"]!;
            var odometer = (double)last["OdometerData"]!;
            everything.AddRange(run.Objects);
            result["the odometer against the distance travelled"] = $"{Bucket((odometer / atReading - 1) * 100, 0.5)} % long";
        }

        var schemas = AIF.Metadata.PublishedSchemas.At(Repository.Schemas);
        var byType = new Dictionary<string, string>
        {
            ["CAV-BRC-V2.0"] = "BrakeCommand", ["CAV-BRR-V2.0"] = "BrakeResponse", ["CAV-MRC-V2.0"] = "MotorCommand", ["CAV-MRP-V2.0"] = "MotorResponse",
            ["CAV-WHC-V2.0"] = "WheelCommand", ["CAV-WHR-V2.0"] = "WheelResponse", ["CAV-SPD-V2.0"] = "SpatialData"
        };
        foreach (var group in everything.GroupBy(o => o.DataType).OrderBy(g => g.Key))
        {
            var schema = schemas[Path.GetFullPath(Path.Combine(Repository.Schemas, "CAV2", "V2.0", "data", byType[group.Key] + ".json"))];
            var valid = group.Count(o => { using var doc = JsonDocument.Parse(o.Json); lock (AIF.Metadata.PublishedSchemas.Lock) return schema.Evaluate(doc.RootElement).IsValid; });
            result[$"{group.Key} against its schema"] = valid == group.Count() ? "every one valid" : $"{valid} of {group.Count()}";
        }
        Expected.Match("mas-stage1-mechanics.json", result);
    }

    private static string Near(double value, double expected, double tolerance) =>
        Math.Abs(value - expected) <= tolerance * expected ? $"within {tolerance * 100:0} %" : $"{value:0.000}, outside {tolerance * 100:0} %";

    private static string Bucket(double value, double size) => (Math.Round(value / size) * size).ToString("0.##");
}
