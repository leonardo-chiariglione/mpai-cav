using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Cav.Ess;
using Mpai.Cav.Map;

namespace Mpai.Cav.Ams;

// ROUTE SELECTION PLANNING, STAGE 1 (CAV-RSP; M3233 3.4). The Destination is the last
// way point of the Route an AMS-HCI Message requests. From the segment the CAV is on
// - the Full Environment Descriptors place it - the Route of least time: that
// segment, then A* on the map from its end. Given to Path Selection Planning as the
// Route Response of an Interaction, and as a Route. A new Route on a Route Request,
// or when the CAV is found on a segment the Route does not have.
public sealed class RouteSelectionPlanning(string instanceId) : IAimProcessor, IAimRunner
{
    private RoadMap? map;
    private string? destination;
    private IReadOnlyList<RoadSegment>? route;
    private long count;

    public string InstanceId { get; } = instanceId;
    public Task<Message> ProcessAsync(Message message) => throw new NotSupportedException("CAV-RSP runs continuously.");

    public async Task RunAsync(IAimPorts ports, AimContext context)
    {
        var replan = false;
        while (await ports.SelectAsync(-1, (AmsTypes.Map, 1), (AmsTypes.Hci, 1), (AmsTypes.Interaction, 1), (AmsTypes.Fed, 1)) is { } port)
        {
            if (await ports.ReadAsync(port.DataType, 1, 0) is not { } m) continue;
            var json = JsonNode.Parse(m.Json)!;
            switch (port.DataType)
            {
                case AmsTypes.Map: map = RoadMap.FromOfflineMapObject(json); continue;
                case AmsTypes.Hci:
                    destination = json["HCIMessage"]?["RequestedRoutes"]?[0]?["Route"]?["RouteSegments"]?.AsArray().LastOrDefault()?["WayPoint2ID"]?.GetValue<string>();
                    route = null;
                    continue;
                case AmsTypes.Interaction:
                    if (json["RouteRequest"] is not null) replan = true;
                    continue;
            }
            // Full Environment Descriptors: plan where there is no Route, or a new one is asked.
            if (map is null || destination is null) continue;
            var segmentId = json["EgoPlacement"]?["SegmentID"]?.GetValue<string>();
            if (segmentId is null) continue;
            if (route is not null && !replan && route.Any(s => s.Id == segmentId)) continue;
            var here = map.Segments.First(s => s.Id == segmentId);
            var rest = here.To == destination ? [] : map.FastestRoute(here.To, destination);
            if (rest is null) { context.Report($"no Route from {here.To} to {destination}"); continue; }
            route = [here, .. rest];
            replan = false;

            var ms = AmsTypes.Ms(json["FullEnvironmentDescriptorsTime"]);
            var r = RouteObject(ms);
            await ports.WriteAsync(AmsTypes.Interaction, 1, new JsonObject
            {
                ["Header"] = AmsTypes.Interaction, ["InteractionID"] = $"INT-R{count:D4}", ["InteractionTime"] = EssJson.SimpleTime($"INT-R{count:D4}-T", ms),
                ["RouteResponse"] = r.DeepClone()
            }.ToJsonString());
            await ports.WriteAsync(AmsTypes.Route, 1, r.ToJsonString());
        }
    }

    private JsonObject RouteObject(long ms) => new()
    {
        ["Header"] = AmsTypes.Route, ["RouteID"] = $"RTE{++count:D4}", ["RouteTime"] = EssJson.SimpleTime($"RTE{count:D4}-T", ms),
        ["OfflineMapID"] = map!.Id,
        ["RouteSegments"] = new JsonArray(route!.Select(s => (JsonNode)new JsonObject { ["WayPoint1ID"] = s.From, ["WayPoint2ID"] = s.To }).ToArray())
    };
}
