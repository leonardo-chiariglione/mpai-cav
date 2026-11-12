using System.Text.Json.Nodes;

using System.Text;

using AIF.Controller;
using AIF.SharedStorage;

using Mpai.Cav.Ams.Planning;
using Mpai.Cav.Ess;

namespace Mpai.Cav.Ams;

// MOTION SELECTION PLANNING, STAGE 1 (CAV-MSP; M3233 3.6). For each Full Environment
// Descriptors instance, the Trajectory of the next Horizon seconds at Step: along the
// Path, by the Intelligent Driver Model - its desired speed the speed limit of the
// segment the CAV is on, following the object ahead in the CAV's lane at a time gap,
// never nearer than a minimum gap, stopping at the end of the Path as behind a
// vehicle stopped there. The object ahead predicted at its speed. Before a Path -
// the Route not yet executed (M3243 3.1) - the Path is where the CAV stands: it holds.
// Settings: DesiredSpeed (m/s; the limit where absent), TimeGap, MinimumGap,
// Acceleration, Deceleration.
//
// THE OBJECTS BEYOND THE ONE FOLLOWED (M3241): a vehicle reported by a Remote CAV,
// hidden from the CAV by the one it follows, lowers the acceleration too - at each
// point the lowest the model gives against each object ahead in the lane.
//
// AT A CORNER OF THE PATH (M3237, found in Step 4: a CAV that moves as a vehicle does
// cannot turn at the speed limit): where the Path turns by more than 20 degrees the
// desired speed is at most what a turn of CornerRadius metres allows at
// LateralAcceleration m/s2, reached at CornerDeceleration m/s2 before it and held
// through the turn.
//
// THE GENERAL PLANNER (the author, 2026/11/01: the CAV does not collide with what is around it - cars, pedestrians, cyclists - and where it cannot be
// sure it does not, it stops). With the setting Planner = SpaceTime, the Trajectory is the one the general planner computes (Planning/): the objects of the
// Full Environment Descriptors are tracked, each with the places it may go - a vehicle the lines it may follow, a pedestrian or a cyclist the disc it can
// reach - verified against what is perceived, and the Trajectory is the way along the Path that keeps clear of all of them over the next seconds, or,
// where none does, the hardest braking. Without it, the Intelligent Driver Model above. What the planner finds that was not as it predicted - a road user
// faster than it was allowed - is AMS Data: kept in the Shared Storage of the Module, under ams-data/events/. What it needs between two steps and nobody
// else does - the plan it last took and why - is in its Private Storage, under work/.
public sealed class MotionSelectionPlanning(string instanceId, IReadOnlyDictionary<string, string> settings, ISharedStorage? shared = null, ISharedStorage? work = null) : IAimProcessor, IAimRunner
{
    // The setting; where it is not given, the environment variable MPAI_PLANNER (to run the recorded scenarios with one planner or the other).
    private readonly bool spaceTime = string.Equals(settings.TryGetValue("Planner", out var planner) ? planner : Environment.GetEnvironmentVariable("MPAI_PLANNER"), "SpaceTime", StringComparison.OrdinalIgnoreCase);
    private readonly ObjectTracker tracker = new(null, acceleration: 3);
    private readonly SpaceTimePlanner spaceTimePlanner = new(null, new MotionConstraints());
    private MotionConstraints constraints => spaceTimePlanner.Constraints;      // one set of constraints, the strategy's and the check's
    private readonly bool constrained = EssJson.Setting(settings, "Constraints", 0) > 0;
    private int eventsKept;
    private bool storageFailed;

    public const double Step = 0.1, Horizon = 6;
    private readonly double timeGap = EssJson.Setting(settings, "TimeGap", 1.5), minimumGap = EssJson.Setting(settings, "MinimumGap", 4),
                            acceleration = EssJson.Setting(settings, "Acceleration", 1.5), deceleration = EssJson.Setting(settings, "Deceleration", 2);
    private readonly double cornerRadius = EssJson.Setting(settings, "CornerRadius", 10), lateralAcceleration = EssJson.Setting(settings, "LateralAcceleration", 2),
                            cornerDeceleration = EssJson.Setting(settings, "CornerDeceleration", 1);
    private List<(double S, double East, double North, double Heading)>? path;
    private List<double> corners = [];
    private long count;

    public string InstanceId { get; } = instanceId;
    public Task<Message> ProcessAsync(Message message) => throw new NotSupportedException("CAV-MSP runs continuously.");

    public async Task RunAsync(IAimPorts ports, AimContext context)
    {
        while (await ports.SelectAsync(-1, (AmsTypes.Interaction, 1), (AmsTypes.Fed, 1), (AmsTypes.RoadState, 1)) is { } port)
        {
            if (await ports.ReadAsync(port.DataType, 1, 0) is not { } m) continue;
            var json = JsonNode.Parse(m.Json)!;
            if (port.DataType == AmsTypes.RoadState)
            {
                RoadState(json);                                  // the Road State on the Bus, whenever someone writes it
                continue;
            }
            if (port.DataType == AmsTypes.Interaction)
            {
                if (json["PathResponse"] is { } p) Follow(p);
                continue;
            }
            await ports.WriteAsync(AmsTypes.Trajectory, 1, Plan(json).ToJsonString());
        }
    }

    // The objects perceived, in the map's frame, from the Full Environment Descriptors: where they are, the velocity they have, their size and class.
    private static List<Observation> Observe(JsonNode fed, double time, double east, double north, double heading, double speed)
    {
        var (cos, sin) = (Math.Cos(heading), Math.Sin(heading));
        var seen = new List<Observation>();
        foreach (var o in fed["FullEnvironmentObjects"]?.AsArray() ?? [])
        {
            var b = o?["BasicEnvironmentObject"];
            if (b is null || (double?)b["ExistenceConfidence"] < 0.5) continue;
            if (EssJson.Vector(b["SpatialAttitude"]?["Position"]?["CartPosition"]) is not { } p) continue;
            var v = EssJson.Vector(b["SpatialAttitude"]?["Position"]?["CartVelocity"]) ?? (0, 0, 0);
            var cls = (string?)b["InstanceIdentifier"]?["InstanceIdentifier"] ?? "car";
            var (length, width) = cls switch { "person" => (0.5, 0.5), "bicycle" => (1.8, 0.6), "motorcycle" => (2.1, 0.8), "truck" or "bus" => (12.0, 2.5), _ => (4.5, 1.8) };
            // The place and the velocity are the ego's frame's - ahead, left - the velocity relative to the CAV's.
            seen.Add(new Observation((string?)b["BasicEnvironmentObjectID"] ?? "object", time, east + p.X * cos - p.Y * sin, north + p.X * sin + p.Y * cos,
                                     speed * cos + v.X * cos - v.Y * sin, speed * sin + v.X * sin + v.Y * cos, length, width, cls));
        }
        // What a Remote CAV reports of an object the CAV perceives itself is the same object, seen some time ago: the CAV's own perception is the one it keeps.
        seen.RemoveAll(r => r.Id.Contains('/') && seen.Any(m => !m.Id.Contains('/') && m.Class == r.Class && Math.Abs(m.X - r.X) < 30 && Math.Abs(m.Y - r.Y) < 3));
        return seen;
    }

    // The Trajectory of the general planner. The objects, in the map's frame, from the Full Environment Descriptors; the planner's Trajectory along the Path.
    private JsonObject PlanSpaceTime(JsonNode fed, long ms, double east, double north, double heading, double speed, double desired)
    {
        var time = ms / 1000.0;
        var seen = Observe(fed, time, east, north, heading, speed);
        tracker.Update(time, seen);
        var plan = spaceTimePlanner.Next(new EgoState(time, east, north, heading, speed), new PolyPath(path!.Select(q => (q.East, q.North))), desired, tracker.Objects, [0]);
        Keep(time, plan);

        return TrajectoryOf(ms, plan.Points.Take((int)Math.Round(Horizon / Step) + 1).Select(q => new TrajectoryPoint(q.T, q.Pose, q.Speed, q.Acceleration)).ToList());
    }

    // A Trajectory of the AMS-MAS Message from points of the map's frame.
    private JsonObject TrajectoryOf(long ms, IReadOnlyList<TrajectoryPoint> way)
    {
        var id = $"TRJ{++count:D6}";
        var points = new JsonArray();
        for (var k = 0; k < way.Count; k++)
        {
            var (t, pose, v0, a) = way[k];
            var point = EssJson.Attitude($"{id}-{k}", ms + (long)Math.Round(t * 1000), (pose.X, pose.Y, 0), (0.5, 0.5, 0.1), (v0 * Math.Cos(pose.Heading), v0 * Math.Sin(pose.Heading), 0),
                qualifier: EssJson.EnuQualifier("the origin of the Offline Map of the CAV"));
            point["Position"]!["CartAccel"] = EssJson.Triple((a * Math.Cos(pose.Heading), a * Math.Sin(pose.Heading), 0));
            points.Add(new JsonObject { ["ExpectedSpaceTime"] = EssJson.SpaceTime($"{id}-{k}-ST", ms + (long)Math.Round(t * 1000), point) });
        }
        return new JsonObject { ["Header"] = AmsTypes.Trajectory, ["TrajectoryID"] = id, ["TrajectoryTime"] = EssJson.SimpleTime(id + "-T", ms), ["Trajectory"] = points };
    }

    // What is kept of a step of the general planner: what was not as predicted, AMS Data of the Module's Shared Storage; and its work area.
    private void Keep(double time, Plan plan)
    {
        if (storageFailed) return;
        try
        {
            for (; eventsKept < tracker.Unpredicted.Count; eventsKept++)
            {
                var (objectId, at, faster, allowed) = tracker.Unpredicted[eventsKept];
                var record = new JsonObject { ["Event"] = "Unpredicted", ["ObjectID"] = objectId, ["Time"] = at, ["Speed"] = faster, ["Allowed"] = allowed };
                shared?.MPAI_AIFM_SharedStorage_Put($"{AmsData.Prefix}events/{eventsKept:D6}-{objectId}", Encoding.UTF8.GetBytes(record.ToJsonString()));
            }
            var last = new JsonObject { ["Time"] = time, ["Clear"] = plan.Clear, ["Acceleration"] = plan.Acceleration, ["Candidates"] = plan.Candidates, ["Feasible"] = plan.Feasible,
                                        ["Detail"] = plan.Detail, ["Keep"] = plan.Keep, ["Why"] = plan.Why };
            work?.MPAI_AIFM_SharedStorage_Put("work/last-plan", Encoding.UTF8.GetBytes(last.ToJsonString()));
        }
        catch (InvalidOperationException) { storageFailed = true; }       // no storage initialised for the Module: nothing is kept, the Trajectory is the same
    }

    // The Path to follow, from a Path Response.
    public void Follow(JsonNode path)
    {
        this.path = Line(path);
        corners = [];
        for (var i = 0; i + 1 < this.path.Count; i++)
            if (Math.Abs(Math.IEEERemainder(this.path[i + 1].Heading - this.path[i].Heading, 2 * Math.PI)) > 20 * Math.PI / 180)
                corners.Add(this.path[i + 1].S);
    }

    // The desired speed at s: no more than the corners ahead, and the one being
    // turned, allow.
    private double CornerLimit(double s, double desired)
    {
        var turning = Math.Sqrt(lateralAcceleration * cornerRadius);
        foreach (var c in corners)
        {
            if (s > c + cornerRadius) continue;
            desired = Math.Min(desired, s >= c ? turning : Math.Sqrt(turning * turning + 2 * cornerDeceleration * (c - s)));
        }
        return desired;
    }

    // The Path as a line: each point with its distance along the Path.
    private static List<(double S, double East, double North, double Heading)> Line(JsonNode path)
    {
        var line = new List<(double, double, double, double)>();
        double s = 0; (double E, double N)? last = null;
        foreach (var p in path["Path"]!.AsArray())
        {
            var c = p!["PointOfView"]!["CartPosition"]!.AsArray();
            var (e, n) = ((double)c[0]!, (double)c[1]!);
            if (last is { } l) s += Math.Sqrt(Math.Pow(e - l.E, 2) + Math.Pow(n - l.N, 2));
            line.Add((s, e, n, (double)p["PointOfView"]!["Orientation"]![0]! * Math.PI / 180));
            last = (e, n);
        }
        return line;
    }

    private (double East, double North, double Heading) At(double s)
    {
        var p = path!;
        if (s >= p[^1].S) return (p[^1].East, p[^1].North, p[^1].Heading);
        var i = Math.Max(0, p.FindLastIndex(x => x.S <= s));
        var (a, b) = (p[i], p[Math.Min(i + 1, p.Count - 1)]);
        var f = b.S > a.S ? (s - a.S) / (b.S - a.S) : 0;
        return (a.East + f * (b.East - a.East), a.North + f * (b.North - a.North), b.Heading);
    }

    // Where the CAV is along the Path: its nearest point.
    private double Along(double east, double north)
    {
        var best = double.MaxValue; var at = 0.0;
        for (var i = 0; i + 1 < path!.Count; i++)
        {
            var (a, b) = (path[i], path[i + 1]);
            var len = b.S - a.S;
            if (len <= 0) continue;
            var f = Math.Clamp(((east - a.East) * (b.East - a.East) + (north - a.North) * (b.North - a.North)) / (len * len), 0, 1);
            var (x, y) = (a.East + f * (b.East - a.East), a.North + f * (b.North - a.North));
            var d = Math.Pow(east - x, 2) + Math.Pow(north - y, 2);
            if (d < best) { best = d; at = a.S + f * len; }
        }
        return at;
    }

    // The acceleration of the Intelligent Driver Model, behind something gap metres
    // ahead approached at closing m/s (none: gap infinite).
    public double Idm(double speed, double desired, double gap, double closing)
    {
        var free = 1 - Math.Pow(speed / Math.Max(0.1, desired), 4);
        var wanted = minimumGap + Math.Max(0, speed * timeGap + speed * closing / (2 * Math.Sqrt(acceleration * deceleration)));
        return acceleration * (free - Math.Pow(wanted / Math.Max(0.1, gap), 2));
    }

    // THE TRAJECTORY the AMS gives: the strategy's (the Intelligent Driver Model, or the space-time planner), and - with the setting Constraints = 1 - only if it has
    // passed the check of the Motion Constraints (M3287, S-3): else the fall-back, which stops the CAV, in its lane, as gently as the situation lets it.
    public JsonObject Plan(JsonNode fed)
    {
        var strategy = PlanStrategy(fed);
        return constrained ? Constrain(fed, strategy) : strategy;
    }

    // THE ROAD STATE the MAS answers (the friction the Ice Condition Analysis estimates, against the dry road's 0.9): the grip of the road for the constraints. Until it is
    // heard the grip is not known, and the least is taken (M3287, C4.3).
    public void RoadState(JsonNode answer)
    {
        var mu = (double?)answer["MASMessage"]?["RoadState"]?["SurfaceCondition"]?["FrictionCoefficientEstimate"] ?? (double?)answer["SurfaceCondition"]?["FrictionCoefficientEstimate"];
        if (mu is { } friction) constraints.Grip = friction / 0.9;
    }

    // What the last check found on the strategy's trajectory, and the level of the fall-back where it was used (null: the strategy's trajectory was given).
    public IReadOnlyList<Violation> LastViolations { get; private set; } = [];
    public string? LastFallBack { get; private set; }
    public int FallBacks { get; private set; }
    public double Grip => constraints.Grip;

    private JsonObject Constrain(JsonNode fed, JsonObject strategy)
    {
        var ms = AmsTypes.Ms(fed["FullEnvironmentDescriptorsTime"]);
        var (east, north, heading, speed) = AmsTypes.Ego(fed["EgoSpatialAttitude"]!);
        (LastViolations, LastFallBack) = ([], null);
        if (path is null || path.Count < 2) return strategy;
        var limit = EssJson.Setting(settings, "DesiredSpeed", (double?)fed["RoadAhead"]?[0]?["SpeedLimit"] ?? 13.9);
        var road = new RoadContext(new PolyPath(path.Select(q => (q.East, q.North))), [0], limit);
        var seen = Observe(fed, ms / 1000.0, east, north, heading, speed);
        var points = strategy["Trajectory"]!.AsArray().Select((point, k) =>
        {
            var attitude = point!["ExpectedSpaceTime"]!["SpatialAttitude1"]!;
            var position = EssJson.Vector(attitude["Position"]?["CartPosition"])!.Value;
            var velocity = EssJson.Vector(attitude["Position"]?["CartVelocity"]) ?? (0, 0, 0);
            var acceleration = EssJson.Vector(attitude["Position"]?["CartAccel"]) ?? (0, 0, 0);
            var v = Math.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y);
            var h = v > 0.3 ? Math.Atan2(velocity.Y, velocity.X) : road.Route.At(road.Route.Project(position.X, position.Y).S).Heading;
            var along = Math.Sqrt(acceleration.X * acceleration.X + acceleration.Y * acceleration.Y) * Math.Sign(acceleration.X * Math.Cos(h) + acceleration.Y * Math.Sin(h));
            return new TrajectoryPoint(k * Step, new Pose(position.X, position.Y, h), v, along);
        }).ToList();
        LastViolations = constraints.Check(points, seen, road);
        if (!LastViolations.Any(v => v.Hard)) return strategy;

        var strategyBrake = points.Where(q => q.T <= 3.0).Select(q => -q.Acceleration).DefaultIfEmpty(0).Max();
        var (way, level, left) = constraints.FallBack(east, north, speed, seen, road, Horizon, Step, strategyBrake);
        (FallBacks, LastFallBack) = (FallBacks + 1, level);
        KeepViolation(ms / 1000.0, LastViolations.First(v => v.Hard), level, left.Any(v => v.Hard));
        return TrajectoryOf(ms, way);
    }

    // The substitution is AMS Data: which constraint the strategy's trajectory violated, when, and the level of the fall-back.
    private int violationsKept;
    private void KeepViolation(double time, Violation v, string level, bool stillViolated)
    {
        if (storageFailed) return;
        try
        {
            var record = new JsonObject { ["Event"] = "ConstraintViolated", ["Time"] = time, ["Constraint"] = v.Constraint, ["At"] = Math.Round(v.At, 2), ["Depth"] = Math.Round(v.Depth, 2),
                                          ["What"] = v.What, ["FallBack"] = level, ["StillViolated"] = stillViolated, ["Parameters"] = constraints.P.Version };
            shared?.MPAI_AIFM_SharedStorage_Put($"{AmsData.Prefix}events/constraint-{violationsKept++:D6}", Encoding.UTF8.GetBytes(record.ToJsonString()));
        }
        catch (InvalidOperationException) { storageFailed = true; }
    }

    private JsonObject PlanStrategy(JsonNode fed)
    {
        var ms = AmsTypes.Ms(fed["FullEnvironmentDescriptorsTime"]);
        var (east, north, heading, speed) = AmsTypes.Ego(fed["EgoSpatialAttitude"]!);
        if (path is null) (path, corners) = ([(0, east, north, heading)], []);   // no Route yet: hold where it stands
        var desired = EssJson.Setting(settings, "DesiredSpeed", (double?)fed["RoadAhead"]?[0]?["SpeedLimit"] ?? 13.9);
        if (spaceTime && path!.Count >= 2) return PlanSpaceTime(fed, ms, east, north, heading, speed, desired);
        var s = Along(east, north);

        // The objects ahead in the CAV's lane: their gaps, and their speeds; the nearest
        // is the one followed.
        var ahead = new List<(double Gap, double Speed)>();
        foreach (var o in fed["FullEnvironmentObjects"]?.AsArray() ?? [])
        {
            if ((int?)o?["Placement"]?["Lane"] != 0) continue;
            var b = o!["BasicEnvironmentObject"]!;
            if ((double?)b["ExistenceConfidence"] < 0.5) continue;
            var p = EssJson.Vector(b["SpatialAttitude"]?["Position"]?["CartPosition"]);
            var v = EssJson.Vector(b["SpatialAttitude"]?["Position"]?["CartVelocity"]);
            if (p is null || p.Value.X <= 0) continue;
            if (speed + (v?.X ?? 0) < -1) continue;       // coming the other way: not followed - the Path Selection Planning meets it, or does not pull out
            ahead.Add((p.Value.X, Math.Max(0, speed + (v?.X ?? 0))));
        }
        ahead.Sort((x, y) => x.Gap.CompareTo(y.Gap));
        double? leadGap = ahead.Count > 0 ? ahead[0].Gap : null, leadSpeed = ahead.Count > 0 ? ahead[0].Speed : null;
        // Those beyond it (M3241: a vehicle a Remote CAV reports, hidden by the one
        // followed): each may only lower the acceleration - multi-anticipation.
        var beyond = ahead.Skip(1).Select(x => (Position: s + x.Gap, x.Speed)).ToList();
        var end = path![^1].S;

        var id = $"TRJ{++count:D6}";
        var points = new JsonArray();
        double t = 0, v0 = speed, pos = s, lead = leadGap is { } g ? s + g : double.MaxValue;
        for (var k = 0; k <= Horizon / Step; k++, t += Step)
        {
            // Behind the nearer of the object ahead and the end of the Path.
            var (gap, closing) = (end + minimumGap - pos, v0);
            if (lead - pos < gap) (gap, closing) = (lead - pos, v0 - (leadSpeed ?? 0));
            var a = Idm(v0, CornerLimit(pos, desired), gap, closing);
            foreach (var (position, lspeed) in beyond) a = Math.Min(a, Idm(v0, CornerLimit(pos, desired), position - pos, v0 - lspeed));
            a = Math.Clamp(a, -9, acceleration);
            var (e, n, h) = At(pos);
            var point = EssJson.Attitude($"{id}-{k}", ms + (long)Math.Round(t * 1000), (e, n, 0), (0.5, 0.5, 0.1), (v0 * Math.Cos(h), v0 * Math.Sin(h), 0),
                qualifier: EssJson.EnuQualifier("the origin of the Offline Map of the CAV"));
            point["Position"]!["CartAccel"] = EssJson.Triple((a * Math.Cos(h), a * Math.Sin(h), 0));
            points.Add(new JsonObject { ["ExpectedSpaceTime"] = EssJson.SpaceTime($"{id}-{k}-ST", ms + (long)Math.Round(t * 1000), point) });
            var next = Math.Max(0, v0 + a * Step);
            pos += (v0 + next) / 2 * Step;
            v0 = next;
            if (leadSpeed is { } ls) lead += ls * Step;
            for (var j = 0; j < beyond.Count; j++) beyond[j] = (beyond[j].Position + beyond[j].Speed * Step, beyond[j].Speed);
        }
        return new JsonObject
        {
            ["Header"] = AmsTypes.Trajectory, ["TrajectoryID"] = id, ["TrajectoryTime"] = EssJson.SimpleTime(id + "-T", ms), ["Trajectory"] = points
        };
    }
}
