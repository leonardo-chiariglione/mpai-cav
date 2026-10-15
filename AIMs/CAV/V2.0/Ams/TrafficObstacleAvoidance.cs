using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Cav.Ess;

namespace Mpai.Cav.Ams;

// TRAFFIC OBSTACLE AVOIDANCE, STAGE 1 (CAV-TOA; M3233 3.7). Each Trajectory refined
// where an Alert came within the last AlertWindow ms, or where the object ahead in the
// CAV's lane - in the latest Full Environment Descriptors - would be reached within
// TimeToCollision seconds: the deceleration that stops the closing before the gap
// falls below StopGap metres, at least the Trajectory's own, at most Emergency m/s2,
// held over the Trajectory. The result is the AMS-MAS Message, the Command Execute,
// with the CAV's Spatial Attitude of the latest Full Environment Descriptors - GNSS
// and the MAS's own estimate combined by the ESS, on the map's frame, the Trajectory's:
// what the MAS follows the Trajectory from (the author: the MAS executes; the best
// knowledge of where the CAV is is the AMS's).
//
// A VEHICLE THAT IS GOING TO CROSS THE CAV'S WAY (the author, 2026/10/14). Where the Full Environment Descriptors give a vehicle's
// PredictedPath - a turn it has said it will make, or that its indicator says - the CAV goes on straight at its speed and sees where the
// two would meet; if they would, it slows, no more than it takes, so as to reach the place after the vehicle has gone from it:
// the least that is enough - and it does not, until the PredictedPath says so, for a vehicle
// that has not said where it is going.
//
// WITH THE ANSWER OF THE MAS (M3237 3.7, Step 6): its Road State. On a road whose
// friction the Road State estimates, no deceleration beyond what it gives, and no
// faster than the CAV can still stop behind the object ahead - if that stops too,
// with all the grip - braking at FrictionMargin of it.
public sealed class TrafficObstacleAvoidance(string instanceId, IReadOnlyDictionary<string, string> settings) : IAimProcessor, IAimRunner
{
    private const double G = 9.81;
    private readonly double ttc = EssJson.Setting(settings, "TimeToCollision", 2), emergency = EssJson.Setting(settings, "Emergency", 6),
                            stopGap = EssJson.Setting(settings, "StopGap", 2), alertWindow = EssJson.Setting(settings, "AlertWindow", 300),
                            frictionMargin = EssJson.Setting(settings, "FrictionMargin", 0.7);
    private JsonNode? fed;
    private long? lastAlert;
    private long count;
    private double? friction;

    public string InstanceId { get; } = instanceId;
    public Task<Message> ProcessAsync(Message message) => throw new NotSupportedException("CAV-TOA runs continuously.");

    public async Task RunAsync(IAimPorts ports, AimContext context)
    {
        while (await ports.SelectAsync(-1, (AmsTypes.Alert, 1), (AmsTypes.Fed, 1), (AmsTypes.Message, 1), (AmsTypes.Trajectory, 1)) is { } port)
        {
            if (await ports.ReadAsync(port.DataType, 1, 0) is not { } m) continue;
            var json = JsonNode.Parse(m.Json)!;
            switch (port.DataType)
            {
                case AmsTypes.Alert: Alerted(json); continue;
                case AmsTypes.Fed: Observe(json); continue;
                case AmsTypes.Message: Answered(json); continue;
            }
            await ports.WriteAsync(AmsTypes.Message, 1, Refine(json).ToJsonString());
        }
    }

    public void Observe(JsonNode fed) => this.fed = fed;

    public void Alerted(JsonNode alert) => lastAlert = Math.Max(lastAlert ?? long.MinValue, AmsTypes.Ms(alert["AlertTime"]));

    // THE ANSWER OF THE MAS: the friction of the road.
    public void Answered(JsonNode answer)
    {
        if ((double?)answer["MASMessage"]?["RoadState"]?["SurfaceCondition"]?["FrictionCoefficientEstimate"] is { } mu) friction = mu;
    }

    public JsonObject Refine(JsonNode trajectory)
    {
        var ms = AmsTypes.Ms(trajectory["TrajectoryTime"]);
        var refined = trajectory.DeepClone();
        var points = refined["Trajectory"]!.AsArray();
        var speed = Speed(points[0]!);
        var own = points.Count > 1 ? (Speed(points[1]!) - speed) / MotionSelectionPlanning.Step : 0;

        // The object ahead in the CAV's lane, and how soon it would be reached.
        double? gap = null, closing = null;
        foreach (var o in fed?["FullEnvironmentObjects"]?.AsArray() ?? [])
        {
            if ((int?)o?["Placement"]?["Lane"] != 0) continue;
            var p = EssJson.Vector(o!["BasicEnvironmentObject"]!["SpatialAttitude"]?["Position"]?["CartPosition"]);
            var v = EssJson.Vector(o["BasicEnvironmentObject"]!["SpatialAttitude"]?["Position"]?["CartVelocity"]);
            if (p is null || p.Value.X <= 0 || (gap is not null && p.Value.X >= gap)) continue;
            if (speed + (v?.X ?? 0) < -1) continue;       // coming the other way: the Path Selection Planning's to meet, or not to pull out
            (gap, closing) = (p.Value.X, -(v?.X ?? 0));
        }
        var grip = friction is { } mu ? mu * G : double.PositiveInfinity;
        var brake = 0.0;
        var yielding = Yield(speed, own);
        if (yielding > 0) brake = Math.Min(Math.Min(emergency, grip), yielding);

        // The deceleration that ends the closing before the gap falls below StopGap.
        var alerted = lastAlert is { } last && ms - last <= alertWindow;
        var danger = gap is { } g && closing is { } c && c > 0.1 && g / c < ttc;
        if (alerted || danger)
        {
            var needed = gap is { } g2 && closing is { } c2 && c2 > 0 ? c2 * c2 / (2 * Math.Max(0.5, g2 - stopGap)) : 0;
            brake = Math.Max(brake, Math.Min(Math.Min(emergency, grip), Math.Max(needed, -own)));
        }

        // On a road that grips less: no faster than stopping behind the object ahead
        // allows - it may brake with all the grip, the CAV plans on FrictionMargin of it.
        if (friction is not null && gap is { } g3 && closing is { } c3)
        {
            var available = grip * frictionMargin;
            var lead = Math.Max(0, speed - c3);
            var safe = Math.Sqrt(Math.Max(0, 2 * available * (g3 - stopGap) + lead * lead * frictionMargin));
            if (speed > safe) brake = Math.Max(brake, available);
        }

        // Held over the Trajectory: each point's speed from the braking, its position along the way.
        if (brake > 0 && brake > -own + 0.01)
        {
            double v0 = speed, travelled = 0;
            var (e0, n0, h0) = Position(points[0]!);
            for (var k = 0; k < points.Count; k++)
            {
                var sa = points[k]!["ExpectedSpaceTime"]!["SpatialAttitude1"]!["Position"]!;
                var h = Heading(points[k]!, h0);
                if (k > 0)
                {
                    var next = Math.Max(0, v0 - brake * MotionSelectionPlanning.Step);
                    travelled += (v0 + next) / 2 * MotionSelectionPlanning.Step;
                    v0 = next;
                }
                sa["CartPosition"] = EssJson.Triple((e0 + travelled * Math.Cos(h0), n0 + travelled * Math.Sin(h0), 0));
                sa["CartVelocity"] = EssJson.Triple((v0 * Math.Cos(h), v0 * Math.Sin(h), 0));
                sa["CartAccel"] = EssJson.Triple((-brake * Math.Cos(h), -brake * Math.Sin(h), 0));
            }
        }
        return Message(refined, ms);
    }

    // The deceleration the CAV is to hold, 0 if none: the least that keeps it out of the way of every vehicle whose predicted path
    // crosses its own. It goes on straight, at the speed it has, braking at a: where it is at t is s(t) = v t - a t^2 / 2 along its
    // heading; a vehicle is in the way where, in the CAV's frame, it is within 2.4 m across (the two bodies and what is left between them)
    // and 5.7 m along, and 1.5 s of the CAV's speed more where it is ahead of it - the CAV does not arrive on the heels of a vehicle that
    // is crossing - or 2 s more where it is behind: a vehicle that comes into the way two seconds after the CAV has gone by is not in it.
    private double Yield(double speed, double own)
    {
        if (fed?["EgoSpatialAttitude"] is not { } attitude || speed < 1) return 0;
        var (east, north, heading, _) = AmsTypes.Ego(attitude);
        var paths = new List<List<(double T, double East, double North)>>();
        foreach (var o in fed["FullEnvironmentObjects"]?.AsArray() ?? [])
            if (o?["PredictedPath"] is JsonArray path && !FollowedAhead(path, east, north, heading))
                paths.Add(path.Select(p => ((double)p!["T"]!, (double)p["East"]!, (double)p["North"]!)).ToList());
        if (paths.Count == 0) return 0;

        // a: the acceleration the CAV holds (negative: braking).
        bool Clear(double a, double extra = 0)
        {
            foreach (var path in paths)
                for (double t = 0; t <= YieldHorizon; t += 0.1)
                {
                    var (qe, qn) = At(path, t);
                    var s = a >= 0 ? speed * t + a * t * t / 2 : Math.Min(speed * t + a * t * t / 2, speed * speed / (2 * -a));
                    var (dx, dy) = (qe - (east + s * Math.Cos(heading)), qn - (north + s * Math.Sin(heading)));
                    var (along, across) = (dx * Math.Cos(heading) + dy * Math.Sin(heading), -dx * Math.Sin(heading) + dy * Math.Cos(heading));
                    var now = Math.Max(0, speed + a * t);                           // its speed then: a CAV that has stopped needs no room to stop in
                    if (Math.Abs(across) < 2.4 && along > -(5.7 + 2.0 * now) && along < 5.7 + (1.5 + extra) * now) return false;
                }
            return true;
        }

        // As the Trajectory goes - accelerating, if it does - the CAV is in no one's way: nothing to do. Else it brakes as little as it takes
        // to have half a second more of room than that: where it is only just in the way, braking so that it is only just not would be at the
        // mercy of a metre.
        if (Clear(own)) return 0;
        for (var a = 0.05; a <= emergency; a += a < 0.1 ? 0.45 : 0.5)
            if (Clear(-a, 0.5)) return a;
        return emergency;
    }

    private const double YieldHorizon = 14;

    // A vehicle on the CAV's own road, going the same way - ahead of it in its lane, or beside or behind it where it passes - is FOLLOWED (the
    // Motion Selection Planning keeps the gap) or left behind, not yielded to: its path lies along the CAV's own, and it is no crossing. If it
    // turns off, its path leaves the road; where the CAV passes it, it is the Path Selection Planning that takes the CAV out of the lane.
    private static bool FollowedAhead(JsonArray path, double east, double north, double heading)
    {
        if (path.Count < 2) return false;
        double e0 = (double)path[0]!["East"]!, n0 = (double)path[0]!["North"]!, e1 = (double)path[1]!["East"]!, n1 = (double)path[1]!["North"]!;
        var (dx, dy) = (e0 - east, n0 - north);
        var (along, across) = (dx * Math.Cos(heading) + dy * Math.Sin(heading), -dx * Math.Sin(heading) + dy * Math.Cos(heading));
        var turn = Math.Atan2(n1 - n0, e1 - e0) - heading;
        turn = Math.Atan2(Math.Sin(turn), Math.Cos(turn));
        return Math.Abs(across) < 4.0 && Math.Abs(turn) < 0.5;          // ahead or behind, in the lane or beside it (the CAV may be passing it)
    }

    // Where a predicted path has the vehicle at t: between its points; where it ends, at its last.
    private static (double East, double North) At(List<(double T, double East, double North)> path, double t)
    {
        if (t <= path[0].T) return (path[0].East, path[0].North);
        for (var i = 1; i < path.Count; i++)
            if (t <= path[i].T)
            {
                var f = (t - path[i - 1].T) / Math.Max(1e-9, path[i].T - path[i - 1].T);
                return (path[i - 1].East + (path[i].East - path[i - 1].East) * f, path[i - 1].North + (path[i].North - path[i - 1].North) * f);
            }
        return (path[^1].East, path[^1].North);
    }

    private JsonObject Message(JsonNode trajectory, long ms)
    {
        var ams = new JsonObject { ["Trajectory"] = trajectory };
        if (fed?["EgoSpatialAttitude"] is { } ego) ams["SpatialAttitude"] = ego.DeepClone();
        ams["Command"] = "Execute";
        return new()
        {
            ["Header"] = AmsTypes.Message, ["AMSMASMessageID"] = $"AMM{++count:D6}",
            ["AMSMASMessageTime"] = EssJson.SimpleTime($"AMM{count:D6}-T", ms),
            ["AMSMessage"] = ams
        };
    }

    public static double Speed(JsonNode point)
    {
        var v = EssJson.Vector(point["ExpectedSpaceTime"]?["SpatialAttitude1"]?["Position"]?["CartVelocity"]) ?? (0, 0, 0);
        return Math.Sqrt(v.X * v.X + v.Y * v.Y);
    }

    private static (double East, double North, double Heading) Position(JsonNode point)
    {
        var p = EssJson.Vector(point["ExpectedSpaceTime"]?["SpatialAttitude1"]?["Position"]?["CartPosition"]) ?? (0, 0, 0);
        return (p.X, p.Y, Heading(point, 0));
    }

    private static double Heading(JsonNode point, double fallback)
    {
        var v = EssJson.Vector(point["ExpectedSpaceTime"]?["SpatialAttitude1"]?["Position"]?["CartVelocity"]);
        return v is { } x && (Math.Abs(x.X) > 1e-6 || Math.Abs(x.Y) > 1e-6) ? Math.Atan2(x.Y, x.X) : fallback;
    }
}
