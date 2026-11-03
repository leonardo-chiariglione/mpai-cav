using System.Text.Json.Nodes;

namespace Mpai.Cav.Vrv;

// THE PROBE OF THE VEHICLE RESPONSE VOCABULARY (CAV-VRV): the CAV on a plane that has no end, no lane and nothing else on it, one command at a time. For each type of surface and each speed
// it commands the brakes, the motor and the steered wheels at several intensities and writes down what the vehicle did: how long it took to answer (dead time), how long it took to
// get to nine tenths of its answer (rise time), what it settled at, and whether the tyres gave what was commanded. What is measured is what a CAV senses of its own motion: its acceleration
// along its heading, the rate at which its heading changes, its speed. No run needs a road, so the whole vocabulary takes seconds to make.
//
// A run: the vehicle is put on the surface at the speed, nothing is commanded for PreRoll seconds (this gives what it does with no command: coasting), then the command is given and the
// vehicle is followed for as long as the run can last (a brake step cannot go on after the vehicle has stopped). Steering is measured at a speed the motor holds.
public sealed class ResponseProbe(IVehiclePlane plane)
{
    public const double Step = 0.02;                  // s between two readings
    public const double PreRoll = 0.5;                // s with no command, before the command

    public static readonly (string Type, double Friction)[] Surfaces = [("dry asphalt", 0.9), ("wet asphalt", 0.6), ("snow", 0.3), ("ice", 0.15)];
    public static readonly double[] Speeds = [3, 6, 10, 15, 20, 25, 30];                       // m/s
    public static readonly double[] BrakeLevels = [0.5, 1, 2, 3, 4, 6, 8];                     // m/s2 of deceleration commanded
    public static readonly double[] MotorLevels = [0.5, 1, 1.5, 2, 3];                         // m/s2 of acceleration commanded
    public static readonly double[] SteeringLevels = [0.25, 0.5, 1, 2, 4, 8, 15, 25];                 // degrees of the road wheels commanded

    // What one run read: at each reading, the time since the command, the acceleration, the rate of turn (degrees/s), the speed, and whether the tyres slipped or the vehicle slid.
    private sealed record Run(double[] T, double[] A, double[] Yaw, double[] V, bool[] Slipping, bool[] Sliding, double Baseline, double Duration);

    // THE VOCABULARY, as the Vehicle Response Vocabulary Data Type (CAV-VRV-V2.0) has it, for the surfaces and speeds given (all of them by default).
    public JsonObject Measure(string cavId, long ms, IReadOnlyList<(string Type, double Friction)>? surfaces = null, IReadOnlyList<double>? speeds = null)
    {
        var surface = new JsonArray();
        foreach (var (type, friction) in surfaces ?? Surfaces)
        {
            var atSpeed = new JsonArray();
            foreach (var v in speeds ?? Speeds)
                atSpeed.Add(new JsonObject { ["Velocity"] = v, ["Brake"] = Brake(v, friction), ["Motor"] = Motor(v, friction), ["Steering"] = Steering(v, friction) });
            surface.Add(new JsonObject { ["SurfaceType"] = type, ["Friction"] = friction, ["Speed"] = atSpeed });
        }
        var id = Guid.NewGuid().ToString();
        return new JsonObject
        {
            ["Header"] = "CAV-VRV-V2.0", ["VRVID"] = id, ["CAVID"] = cavId, ["VRVTime"] = SimpleTime(id + "-T", ms), ["Surface"] = surface,
            ["DescrMetadata"] = "Measured on a plane without a road, by the probe of the Vehicle Response Vocabulary: a command at a time, a surface at a time, a speed at a time."
        };
    }

    // ---- the three devices ------------------------------------------------------------------------------------------------

    private JsonObject Brake(double speed, double friction)
    {
        var levels = new JsonArray(); double max = 0, any = 0;
        foreach (var d in BrakeLevels)
        {
            var seconds = Math.Min(3, 0.7 * speed / d);                         // the vehicle must still be moving when the answer has settled
            if (seconds < 1.0) continue;
            var run = Go(speed, friction, "CAV-BRC-V2.0", new JsonObject { ["BrakeID"] = "B1", ["BrakeCommand"] = new JsonArray(new JsonObject { ["DecelerationTarget"] = d }) }, seconds, hold: false);
            var (dead, rise, settled) = Analyse(run.T, run.A, run.Baseline);
            var slipping = Any(run.Slipping, run.T);
            levels.Add(new JsonObject { ["Command"] = d, ["DeadTime"] = R(dead), ["RiseTime"] = R(rise), ["Settled"] = R(Math.Max(0, -settled)), ["Slipping"] = slipping });
            any = Math.Max(any, -settled); if (!slipping) max = Math.Max(max, -settled);
        }
        if (levels.Count == 0) levels.Add(new JsonObject { ["Command"] = 0.0, ["DeadTime"] = 0.0, ["RiseTime"] = 0.0, ["Settled"] = 0.0 });
        return new JsonObject { ["Level"] = levels, ["MaxDeceleration"] = R(max > 0 ? max : any) };
    }

    private JsonObject Motor(double speed, double friction)
    {
        var levels = new JsonArray(); double max = 0, any = 0;
        foreach (var a in MotorLevels)
        {
            var run = Go(speed, friction, "CAV-MRC-V2.0", new JsonObject { ["MotorID"] = "M1", ["MotorCommand"] = new JsonObject { ["ControlMode"] = "acceleration", ["TargetAcceleration"] = a } }, 3, hold: false);
            var (dead, rise, settled) = Analyse(run.T, run.A, run.Baseline);
            var slipping = Any(run.Slipping, run.T);
            levels.Add(new JsonObject { ["Command"] = a, ["DeadTime"] = R(dead), ["RiseTime"] = R(rise), ["Settled"] = R(settled), ["Slipping"] = slipping });
            any = Math.Max(any, settled); if (!slipping) max = Math.Max(max, settled);
        }
        // What it does with no command: the pre-roll of any run, over its last second.
        var coast = Go(speed, friction, null, null, 1.0, hold: false).Baseline;
        return new JsonObject { ["Level"] = levels, ["Coasting"] = R(coast), ["MaxAcceleration"] = R(max > 0 ? max : any) };
    }

    private JsonObject Steering(double speed, double friction)
    {
        var levels = new JsonArray(); double max = 0;
        foreach (var angle in SteeringLevels)
        {
            var run = Go(speed, friction, "CAV-WHC-V2.0", new JsonObject { ["WheelID"] = "W1", ["WheelCommand"] = new JsonObject { ["Angle"] = angle } }, 3, hold: true);
            var (dead, rise, settled) = Analyse(run.T, run.Yaw, 0);
            var lateral = speed * Math.Abs(settled) * Math.PI / 180;
            var following = !Any(run.Sliding, run.T);
            levels.Add(new JsonObject { ["Command"] = angle, ["DeadTime"] = R(dead), ["RiseTime"] = R(rise), ["Settled"] = R(Math.Abs(settled)), ["LateralAcceleration"] = R(lateral), ["Following"] = following });
            if (following) max = Math.Max(max, lateral);
        }
        return new JsonObject { ["Level"] = levels, ["MaxLateralAcceleration"] = R(max) };
    }

    // ---- a run, and what is read from it ----------------------------------------------------------------------------------

    private Run Go(double speed, double friction, string? dataType, JsonNode? command, double seconds, bool hold)
    {
        plane.Reset(speed, friction);
        // Steering is measured at a speed that is held, so that what the vehicle does is the turn and not the slowing down.
        if (hold) plane.Command("CAV-MRC-V2.0", new JsonObject { ["MotorID"] = "M1", ["MotorCommand"] = new JsonObject { ["ControlMode"] = "velocity", ["TargetVelocity"] = speed } });
        var before = new List<double>();
        for (var t = 0.0; t < PreRoll - 1e-9; t += Step) { plane.Step(Step); before.Add(plane.Acceleration); }
        if (dataType is not null) plane.Command(dataType, command!);
        var n = (int)Math.Round(seconds / Step);
        var (time, a, yaw, v, slip, slide) = (new double[n], new double[n], new double[n], new double[n], new bool[n], new bool[n]);
        var heading = plane.Heading;
        for (var k = 0; k < n; k++)
        {
            plane.Step(Step);
            time[k] = (k + 1) * Step; a[k] = plane.Acceleration; v[k] = plane.Speed;
            var turned = Math.IEEERemainder(plane.Heading - heading, 2 * Math.PI); heading = plane.Heading;
            yaw[k] = turned / Step * 180 / Math.PI;
            slip[k] = plane.Slipping; slide[k] = plane.Sliding;
        }
        // No command: the baseline is what it did over the last second of what was read, which is then all there is.
        var baseline = dataType is null ? a.Skip(Math.Max(0, n - (int)(1.0 / Step))).Average() : before.Skip(Math.Max(0, before.Count - (int)(0.3 / Step))).Average();
        return new Run(time, a, yaw, v, slip, slide, baseline, seconds);
    }

    // Dead time, rise time and where it settled, of a response y(t) that starts from y0 when the command is given at t = 0: the settled value is the mean over the last quarter of the run
    // (at least half a second); the dead time is the first moment it has left y0 by a twentieth of the way to it; the rise time is from there to nine tenths of the way.
    private static (double Dead, double Rise, double Settled) Analyse(double[] t, double[] y, double y0)
    {
        var tail = Math.Min(y.Length, Math.Max((int)(0.5 / Step), y.Length / 4));
        var settled = y.Skip(y.Length - tail).Average();
        var delta = settled - y0;
        if (Math.Abs(delta) < 0.02) return (t[^1], 0, settled);                // it did not answer
        double dead = t[^1], rise = 0; var started = false;
        for (var k = 0; k < y.Length; k++)
        {
            var done = (y[k] - y0) / delta;
            if (!started && done >= 0.05) { dead = Math.Max(0, t[k] - Step); started = true; }
            if (started && done >= 0.9) { rise = t[k] - dead - Step; break; }
        }
        return (dead, Math.Max(0, rise), settled);
    }

    // True if the flag was set at any reading of the last half second of the run: slipping at the end is what counts, not a moment on the way.
    private static bool Any(bool[] flag, double[] t)
    {
        var from = Math.Max(0, flag.Length - (int)(0.5 / Step));
        for (var k = from; k < flag.Length; k++) if (flag[k]) return true;
        return false;
    }

    private static double R(double x) => Math.Round(x, 3);

    private static JsonObject SimpleTime(string id, long ms) => new()
    {
        ["Header"] = "OSD-STM-V1.5", ["SimpleTimeID"] = id,
        ["SimpleTimeData"] = new JsonArray(new JsonObject { ["FlagsByte"] = 3, ["StartTime"] = ms, ["EndTime"] = ms, ["AccuracyMode"] = "single", ["AccuracyPlusMinus"] = 1 })
    };
}
