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
// Basic Environment Descriptors they were made from, one for each of them. Other
// CAVs' descriptors: a later stage.
public sealed class FullEnvironmentDescription(string instanceId) : IAimProcessor, IAimRunner
{
    private RoadMap? map;
    private long count;

    public string InstanceId { get; } = instanceId;
    public Task<Message> ProcessAsync(Message message) => throw new NotSupportedException("CAV-FED runs continuously.");

    public async Task RunAsync(IAimPorts ports, AimContext context)
    {
        while (await ports.SelectAsync(-1, (AmsTypes.Map, 1), (AmsTypes.Bed, 1)) is { } port)
        {
            if (await ports.ReadAsync(port.DataType, 1, 0) is not { } m) continue;
            var json = JsonNode.Parse(m.Json)!;
            if (port.DataType == AmsTypes.Map) { map = RoadMap.FromOfflineMapObject(json); continue; }
            if (map is null) { context.Report("Basic Environment Descriptors before the Offline Map: not placed."); continue; }
            await ports.WriteAsync(AmsTypes.Fed, 1, Describe(json).ToJsonString());
        }
    }

    // The Offline Map, as a Basic Offline Map Object.
    public void Know(JsonNode offlineMapObject) => map = RoadMap.FromOfflineMapObject(offlineMapObject);

    public JsonObject Describe(JsonNode bed)
    {
        var id = $"FED{++count:D6}";
        var attitude = bed["EgoSpatialAttitude"]!;
        var (east, north, heading, _) = AmsTypes.Ego(attitude);
        var ego = AmsTypes.Match(map!, east, north, heading);

        var objects = new JsonArray();
        foreach (var o in bed["BasicEnvironmentObjects"]?.AsArray() ?? [])
        {
            var p = EssJson.Vector(o?["SpatialAttitude"]?["Position"]?["CartPosition"]);
            if (p is null) continue;
            // Ahead X and left Y of the CAV, turned to the map's frame.
            var (x, y) = (east + p.Value.X * Math.Cos(heading) - p.Value.Y * Math.Sin(heading), north + p.Value.X * Math.Sin(heading) + p.Value.Y * Math.Cos(heading));
            var placed = AmsTypes.Match(map!, x, y, heading);
            var entry = new JsonObject { ["BasicEnvironmentObject"] = o!.DeepClone(), ["Source"] = "Ego" };
            if (placed is { } q) entry["Placement"] = AmsTypes.Placement(q.Segment, q.Along, AmsTypes.LaneOf(q.Left - (ego?.Left ?? 0)));
            objects.Add(entry);
        }

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
}
