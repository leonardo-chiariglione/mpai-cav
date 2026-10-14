using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Cav.Ess;
using Mpai.Cav.Map;

namespace Mpai.Cav.Ams;

// FULL ENVIRONMENT DESCRIPTION, STAGE 1 (CAV-FED; M3233 3.3). The Basic Environment
// Objects of the ESS placed on the Offline Map: the CAV by map matching - its
// position and heading on the map's frame, the segment it is on, how far along it -
// and each object where it is, on a segment and in a lane, 0 the CAV's lane, +1 the
// lane to its left. The Full Environment Descriptors V2.0, at the instant of the
// Basic Environment Descriptors they were made from, one for each of them.
//
// THE REMOTE CAVs (M3241 3.3). A CAV given its identity (the setting CAVID) sends its
// Full Environment Descriptors - what it perceives itself - to the CAVs in range, as
// an Ego-Remote AMS Message Response, at most one each SendEvery ms. What another CAV
// sends is placed on the map as this CAV's own are: each object where the other CAV
// says it is - that CAV's Spatial Attitude and the object's place relative to it,
// on the map's frame both share - predicted by its velocity to this instant; marked
// Remote; left out where this CAV perceives it itself, or where it is this CAV; not
// used when older than MaxAge ms. The Controller verified who sent it.
//
// WHAT AN INDICATOR SAYS (the author, 2026/10/14). A vehicle the ESS sees with its indicator lit - Left or Right: TurnIndicator of its Basic
// Environment Object - is going to turn at the next junction of the Offline Map it is heading for, within JunctionReach m. The CAV
// predicts the turn as drivers make it: the vehicle goes on, slows to TurnSpeed m/s (at DecelerationToTurn m/s2), follows an arc of
// TurnRadius m to the road it turns into and leaves it. An indicator blinks, so it is taken for lit for IndicatorMemory ms after it was
// seen so. The roads are two-way, the vehicles keep to the right: half the road's lane width (RoadLaneWidth) to the right of the axis.
//
// WHAT A REMOTE CAV INTENDS (the author, 2026/10/14: the connected attribute). A Sender Info message (M3241: "the Trajectory the Ego
// CAV intends to adopt") carries the Trajectory the Remote CAV is to follow - where it will be in the next seconds, which is what a turn
// at a junction is: the CAV that hears it knows, before the vehicle has turned, where it goes. The entry of the Remote CAV itself then
// carries its PredictedPath: its places (East, North) at times (T seconds after the instant of the Full Environment Descriptors).
public sealed class FullEnvironmentDescription(string instanceId, IReadOnlyDictionary<string, string>? settings = null) : IAimProcessor, IAimRunner
{
    public const string Remote = "CAV-ERA-V2.0";
    private readonly string? cav = settings?.TryGetValue("CAVID", out var id) == true ? id : null;
    private readonly double sendEvery = EssJson.Setting(settings ?? new Dictionary<string, string>(), "SendEvery", 200),
                            maxAge = EssJson.Setting(settings ?? new Dictionary<string, string>(), "MaxAge", 1000);
    private readonly double junctionReach = EssJson.Setting(settings ?? new Dictionary<string, string>(), "JunctionReach", 300),
                            turnRadius = EssJson.Setting(settings ?? new Dictionary<string, string>(), "TurnRadius", 10),
                            turnSpeed = EssJson.Setting(settings ?? new Dictionary<string, string>(), "TurnSpeed", 7),
                            decelerationToTurn = EssJson.Setting(settings ?? new Dictionary<string, string>(), "DecelerationToTurn", 2),
                            indicatorMemory = EssJson.Setting(settings ?? new Dictionary<string, string>(), "IndicatorMemory", 1000),
                            roadLaneWidth = EssJson.Setting(settings ?? new Dictionary<string, string>(), "RoadLaneWidth", RoadMap.LaneWidth);
    private readonly Dictionary<string, (string Turn, long Seen)> signalling = new(StringComparer.Ordinal);    // each vehicle that shows an indicator, and when it last did
    private RoadMap? map;
    private long count, sent;
    private long? lastSent;
    private readonly Dictionary<string, JsonNode> reported = new(StringComparer.Ordinal);    // the latest of each Remote CAV
    private readonly Dictionary<string, JsonNode> intended = new(StringComparer.Ordinal);    // the Trajectory each Remote CAV last said it will follow

    public string InstanceId { get; } = instanceId;
    public Task<Message> ProcessAsync(Message message) => throw new NotSupportedException("CAV-FED runs continuously.");

    public async Task RunAsync(IAimPorts ports, AimContext context)
    {
        while (await ports.SelectAsync(-1, (AmsTypes.Map, 1), (Remote, 1), (AmsTypes.Bed, 1)) is { } port)
        {
            if (await ports.ReadAsync(port.DataType, 1, 0) is not { } m) continue;
            var json = JsonNode.Parse(m.Json)!;
            if (port.DataType == AmsTypes.Map) { map = RoadMap.FromOfflineMapObject(json); continue; }
            if (port.DataType == Remote) { if (Heard(json) is { } why) context.Report(why); continue; }
            if (map is null) { context.Report("Basic Environment Descriptors before the Offline Map: not placed."); continue; }
            var fed = Describe(json);
            await ports.WriteAsync(AmsTypes.Fed, 1, fed.ToJsonString());
            if (ToSend(fed) is { } message) await ports.WriteAsync(Remote, 1, message.ToJsonString());
        }
    }

    // The Offline Map, as a Basic Offline Map Object.
    public void Know(JsonNode offlineMapObject) => map = RoadMap.FromOfflineMapObject(offlineMapObject);

    // AN EGO-REMOTE AMS MESSAGE: a Response carrying another CAV's Full Environment
    // Descriptors is kept, the latest of each CAV. Null where it is; else why not.
    public string? Heard(JsonNode message)
    {
        if (message["Message"]?["Trajectory"] is { } said && (string?)message["Message"]?["EgoCAVID"] is { } sender)        // Sender Info: where it goes
        {
            if (sender != cav) intended[sender] = said.DeepClone();
            return null;
        }
        if (message["Message"]?["RemoteFED"] is not JsonObject fed || (string?)message["Message"]?["RemoteCAVID"] is not { } from)
            return $"{(string?)message["EgoRemoteAMSMessageID"]}: not a Response with Full Environment Descriptors - not used in Stage 1.";
        if (from == cav) return null;
        if (!reported.TryGetValue(from, out var known) || AmsTypes.Ms(known["FullEnvironmentDescriptorsTime"]) <= AmsTypes.Ms(fed["FullEnvironmentDescriptorsTime"]))
        {
            reported[from] = fed;
        }
        return null;
    }

    // What this CAV sends, where it has an identity and it is time: its own perception.
    public JsonObject? ToSend(JsonObject fed)
    {
        var ms = AmsTypes.Ms(fed["FullEnvironmentDescriptorsTime"]);
        if (cav is null || ms - lastSent < sendEvery) return null;          // lastSent null: not yet
        lastSent = ms;
        var own = fed.DeepClone().AsObject();
        var objects = new JsonArray(own["FullEnvironmentObjects"]!.AsArray().Where(o => (string?)o?["Source"] == "Ego").Select(o => o!.DeepClone()).ToArray());
        own["FullEnvironmentObjects"] = objects;
        own["FullEnvironmentObjectCount"] = objects.Count;
        var id = $"ERA{++sent:D6}";
        return new JsonObject
        {
            ["Header"] = Remote, ["EgoRemoteAMSMessageID"] = $"{cav}-{id}",
            ["EgoRemoteAMSMessageTime"] = EssJson.SimpleTime($"{cav}-{id}-T", ms), ["CAVIdentifier"] = cav,
            ["Message"] = new JsonObject { ["RemoteCAVID"] = cav, ["RemoteFED"] = own }
        };
    }

    public JsonObject Describe(JsonNode bed)
    {
        var id = $"FED{++count:D6}";
        var attitude = bed["EgoSpatialAttitude"]!;
        var (east, north, heading, _) = AmsTypes.Ego(attitude);
        var ego = AmsTypes.Match(map!, east, north, heading);
        var ms = AmsTypes.Ms(bed["BasicEnvironmentDescriptorsTime"]);

        var objects = new JsonArray();
        var seen = new List<(double X, double Y, JsonObject Entry)>();
        foreach (var o in bed["BasicEnvironmentObjects"]?.AsArray() ?? [])
        {
            var p = EssJson.Vector(o?["SpatialAttitude"]?["Position"]?["CartPosition"]);
            if (p is null) continue;
            // Ahead X and left Y of the CAV, turned to the map's frame.
            var (x, y) = (east + p.Value.X * Math.Cos(heading) - p.Value.Y * Math.Sin(heading), north + p.Value.X * Math.Sin(heading) + p.Value.Y * Math.Cos(heading));
            var placed = AmsTypes.Match(map!, x, y, heading);
            var entry = new JsonObject { ["BasicEnvironmentObject"] = o!.DeepClone(), ["Source"] = "Ego" };
            if (placed is { } q) entry["Placement"] = AmsTypes.Placement(q.Segment, q.Along, AmsTypes.LaneOf(q.Left - (ego?.Left ?? 0)));
            if (SignalledPath(o, (x, y), attitude, heading, ms) is { } predicted) entry["PredictedPath"] = predicted;
            seen.Add((p.Value.X, p.Value.Y, entry));
            objects.Add(entry);
        }
        foreach (var entry in FromRemote(ms, east, north, heading, attitude, ego?.Left ?? 0, seen)) objects.Add(entry);

        var fed = new JsonObject
        {
            ["Header"] = AmsTypes.Fed, ["FullEnvironmentDescriptorsID"] = id,
            ["FullEnvironmentDescriptorsTime"] = bed["BasicEnvironmentDescriptorsTime"]!.DeepClone(),
            ["OfflineMapID"] = map!.Id,
            ["EgoSpatialAttitude"] = attitude.DeepClone()
        };
        if (ego is { } e)
        {
            fed["EgoPlacement"] = AmsTypes.Placement(e.Segment, e.Along, 0);
            fed["RoadAhead"] = new JsonArray(new JsonObject
            {
                ["SegmentID"] = e.Segment.Id, ["Length"] = Math.Round(map.Length(e.Segment), 3), ["SpeedLimit"] = e.Segment.SpeedLimit, ["Lanes"] = e.Segment.Lanes
            });
        }
        fed["FullEnvironmentObjectCount"] = objects.Count;
        fed["FullEnvironmentObjects"] = objects;
        if (bed["WeatherData"] is { } w) fed["WeatherData"] = w.DeepClone();
        return fed;
    }

    // Where a vehicle that signals is going to be, if its indicator was seen lit lately: null where it was not, or no junction is ahead of it.
    private JsonArray? SignalledPath(JsonNode? o, (double East, double North) at, JsonNode egoAttitude, double egoHeading, long ms)
    {
        var id = (string?)o?["BasicEnvironmentObjectID"];
        if (id is null || map is null) return null;
        if ((string?)o?["TurnIndicator"] is { } turn) signalling[id] = (turn, ms);
        if (!signalling.TryGetValue(id, out var said) || ms - said.Seen > indicatorMemory) return null;
        var own = EssJson.Vector(egoAttitude["Position"]?["CartVelocity"]) ?? (0, 0, 0);
        var rel = EssJson.Vector(o?["SpatialAttitude"]?["Position"]?["CartVelocity"]) ?? (0, 0, 0);
        var (vx, vy) = (own.X + rel.X * Math.Cos(egoHeading) - rel.Y * Math.Sin(egoHeading), own.Y + rel.X * Math.Sin(egoHeading) + rel.Y * Math.Cos(egoHeading));
        var speed = Math.Sqrt(vx * vx + vy * vy);
        if (speed < 1) return null;
        return TurnPath(at, Math.Atan2(vy, vx), speed, said.Turn == "Left");
    }

    // The turn of a vehicle at (East, North) heading as given, at its speed, at the next junction ahead of it: its places each 0.5 s for 14 s.
    public JsonArray? TurnPath((double East, double North) at, double heading, double speed, bool left)
    {
        var (ux, uy) = (Math.Cos(heading), Math.Sin(heading));
        var (nx, ny) = (-uy, ux);                                                  // to the left of the vehicle
        var half = roadLaneWidth / 2;
        // The junction ahead: a way point where three or more segments meet, within reach, in front, near the road the vehicle is on. Its
        // centre is where the axis of that road - half a lane to the vehicle's left - meets the road across.
        (double East, double North)? centre = null; var best = double.MaxValue;
        foreach (var w in map!.WayPoints)
        {
            if (map.Segments.Count(s => s.From == w.Id || s.To == w.Id) < 3) continue;
            var (dx, dy) = (w.East - at.East, w.North - at.North);
            var (along, across) = (dx * ux + dy * uy, dx * nx + dy * ny);
            if (along <= 0 || along > junctionReach || Math.Abs(across) > 2 * roadLaneWidth + 2 || along >= best) continue;
            best = along;
            centre = (w.East + nx * (half - across), w.North + ny * (half - across));
        }
        if (centre is not { } c) return null;
        // Where the arc begins, half a lane to the right of the axis: a left turn begins the radius less half a lane before the centre, a right
        // turn the radius more.
        var before = left ? turnRadius - half : turnRadius + half;
        var begin = (East: c.East - before * ux - half * nx, North: c.North - before * uy - half * ny);
        var toArc = (begin.East - at.East) * ux + (begin.North - at.North) * uy;
        var arc = Math.PI / 2 * turnRadius;
        var side = left ? 1 : -1;
        var (cx, cy) = (begin.East + side * turnRadius * nx, begin.North + side * turnRadius * ny);

        (double East, double North) At(double along)
        {
            if (along <= toArc) return (at.East + along * ux, at.North + along * uy);
            var turned = Math.Min(along - toArc, arc) / turnRadius * side;
            var (px, py) = (begin.East - cx, begin.North - cy);
            var (e, n) = (cx + px * Math.Cos(turned) - py * Math.Sin(turned), cy + px * Math.Sin(turned) + py * Math.Cos(turned));
            var leaving = Math.Max(0, along - toArc - arc);
            return (e + leaving * (ux * Math.Cos(turned) - uy * Math.Sin(turned)), n + leaving * (ux * Math.Sin(turned) + uy * Math.Cos(turned)));
        }

        var points = new JsonArray();
        double s = 0, v = speed, nextAt = 0;
        for (double time = 0; time <= 14 + 1e-9; time += 0.1)
        {
            if (time >= nextAt - 1e-9)
            {
                var (e, n) = At(s);
                points.Add(new JsonObject { ["T"] = Math.Round(time, 2), ["East"] = Math.Round(e, 3), ["North"] = Math.Round(n, 3) });
                nextAt += 0.5;
            }
            // Its speed: down to the speed of the turn by the time it gets there, the turn at that speed, then away.
            var remaining = toArc - s;
            if (remaining > 0 && v > turnSpeed && remaining < (v * v - turnSpeed * turnSpeed) / (2 * decelerationToTurn)) v = Math.Max(turnSpeed, v - decelerationToTurn * 0.1);
            else if (s > toArc + arc) v = Math.Min(speed, v + 1.5 * 0.1);
            s += v * 0.1;
        }
        return points;
    }

    // The places a Trajectory says a vehicle will be at, at 0.5 s from one another, from the instant given on.
    public static JsonArray? PredictedPath(JsonNode trajectory, long ms)
    {
        var points = trajectory["Trajectory"]?.AsArray();
        if (points is null || points.Count == 0) return null;
        var path = new JsonArray(); double next = 0;
        foreach (var point in points)
        {
            var st = point?["ExpectedSpaceTime"];
            var p = EssJson.Vector(st?["SpatialAttitude1"]?["Position"]?["CartPosition"]);
            if (p is null) continue;
            var time = (AmsTypes.Ms(st?["Time"]) - ms) / 1000.0;
            if (time < next) continue;
            path.Add(new JsonObject { ["T"] = Math.Round(time, 2), ["East"] = Math.Round(p.Value.X, 3), ["North"] = Math.Round(p.Value.Y, 3) });
            next = time + 0.5;
        }
        return path.Count > 0 ? path : null;
    }

    // WHAT OTHER CAVs PERCEIVE, placed as this CAV's own: the objects each reports -
    // and itself - where this CAV does not perceive something already, nor is.
    private IEnumerable<JsonObject> FromRemote(long ms, double east, double north, double heading, JsonNode attitude, double egoLeft, List<(double X, double Y, JsonObject Entry)> seen)
    {
        var own = EssJson.Vector(attitude["Position"]?["CartVelocity"]) ?? (0, 0, 0);
        foreach (var (from, fed) in reported)
        {
            var age = ms - AmsTypes.Ms(fed["FullEnvironmentDescriptorsTime"]);
            if (age > maxAge || age < -maxAge) continue;
            var dt = age / 1000.0;
            var remote = fed["EgoSpatialAttitude"]!;
            var (re, rn, rh, _) = AmsTypes.Ego(remote);
            var rv = EssJson.Vector(remote["Position"]?["CartVelocity"]) ?? (0, 0, 0);
            var reports = new List<(string Id, JsonNode Object, (double X, double Y) World, (double X, double Y) Velocity)>
            {
                (from, remote, (re, rn), (rv.X, rv.Y))                                          // the Remote CAV itself
            };
            foreach (var o in fed["FullEnvironmentObjects"]?.AsArray() ?? [])
            {
                var b = o?["BasicEnvironmentObject"];
                var p = EssJson.Vector(b?["SpatialAttitude"]?["Position"]?["CartPosition"]);
                if (b is null || p is null || (string?)o!["Source"] != "Ego") continue;
                var v = EssJson.Vector(b["SpatialAttitude"]?["Position"]?["CartVelocity"]) ?? (0, 0, 0);
                reports.Add(((string?)b["BasicEnvironmentObjectID"] ?? "object", b,
                    (re + p.Value.X * Math.Cos(rh) - p.Value.Y * Math.Sin(rh), rn + p.Value.X * Math.Sin(rh) + p.Value.Y * Math.Cos(rh)),
                    (rv.X + v.X * Math.Cos(rh) - v.Y * Math.Sin(rh), rv.Y + v.X * Math.Sin(rh) + v.Y * Math.Cos(rh))));
            }
            foreach (var (objectId, source, world, velocity) in reports)
            {
                // Predicted to this instant; relative to this CAV.
                var (wx, wy) = (world.X + velocity.X * dt, world.Y + velocity.Y * dt);
                var (dx, dy) = (wx - east, wy - north);
                var (x, y) = (dx * Math.Cos(heading) + dy * Math.Sin(heading), -dx * Math.Sin(heading) + dy * Math.Cos(heading));
                if (Math.Abs(x) < 3 && Math.Abs(y) < 2) continue;                                // this CAV
                var here = seen.FirstOrDefault(s => Math.Abs(s.X - x) < 3 && Math.Abs(s.Y - y) < 2);
                if (here.Entry is not null)                                                      // perceived here too: what it says of where it goes is its
                {
                    if (objectId == from && intended.TryGetValue(from, out var told) && PredictedPath(told, ms) is { } said) here.Entry["PredictedPath"] = said;
                    continue;
                }
                var (rvx, rvy) = (velocity.X - own.X, velocity.Y - own.Y);
                var relative = (rvx * Math.Cos(heading) + rvy * Math.Sin(heading), -rvx * Math.Sin(heading) + rvy * Math.Cos(heading), 0.0);
                var reportedId = $"{from}/{objectId}";
                var path = objectId == from && intended.TryGetValue(from, out var trajectory) ? PredictedPath(trajectory, ms) : null;
                var entry = new JsonObject
                {
                    ["BasicEnvironmentObject"] = new JsonObject
                    {
                        ["BasicEnvironmentObjectID"] = reportedId,
                        ["InstanceIdentifier"] = source["InstanceIdentifier"]?.DeepClone() ?? EssJson.Identifier("car", 1),
                        ["SpatialAttitude"] = EssJson.Attitude($"{reportedId}-{ms}", ms, (x, y, 0), (1, 1, 1), relative),
                        ["ExistenceConfidence"] = (double?)source["ExistenceConfidence"] ?? 1.0, ["Motion"] = "Dynamic",
                        ["Contributions"] = new JsonArray(new JsonObject { ["Technology"] = "Visual" })
                    },
                    ["Source"] = "Remote"
                };
                if (path is not null) entry["PredictedPath"] = path;
                if (AmsTypes.Match(map!, wx, wy, heading) is { } q) entry["Placement"] = AmsTypes.Placement(q.Segment, q.Along, AmsTypes.LaneOf(q.Left - egoLeft));
                yield return entry;
            }
        }
    }
}
