using System.Text.Json.Nodes;

using AIF.Controller;

namespace Mpai.Cav.Ess;

// BASIC ENVIRONMENT DESCRIPTION, STAGE 1 (CAV-BED-V2.0; M3229 12, M3221 3.4). One
// Environment Sensing Technology contributes, the visual, so the fusion is of one;
// what it does already is what the fusion of several does per technology:
//  - each object of the Basic Visual Scene Descriptors associated with the track
//    nearest to where that track was going, within a gate (the setting Gate, m);
//  - the track's position and velocity estimated by an alpha-beta filter, from one
//    instant to the next, relative to the ego;
//  - its existence confidence raised by each association and lowered by each miss;
//    a track missed DropAfterMisses times in a row dropped;
//  - the Basic Environment Descriptors V2.0 on the ego frame, given out and returned
//    to the describer as its prior.
// Nothing is given out before the ego Spatial Attitude is known: it is the frame.
public sealed class BasicEnvironmentDescription(string instanceId, IReadOnlyDictionary<string, string> settings) : IAimProcessor, IAimRunner
{
    public const string Visual = "OSD-BVS-V1.5", Attitude = "OSD-OSA-V1.5", Weather = "CAV-WDT-V1.1", Full = "CAV-FED-V1.1",
                        Descriptors = "CAV-BED-V2.0";
    private const double Alpha = 0.5, Beta = 0.3;

    private sealed class Track
    {
        public required string Id;
        public required string Class;
        public double Score;
        public double X, Y, Vx, Vy, AccuracyX, AccuracyY;
        public double Existence = 0.5;
        public int Misses;
        public long First, Last;
        public string? From, Object;
    }

    private readonly double gate = EssJson.Setting(settings, "Gate", 3);
    private readonly int dropAfter = (int)EssJson.Setting(settings, "DropAfterMisses", 5);
    private readonly List<Track> tracks = [];
    private JsonNode? ego, weather;
    private long? lastMs;
    private long tracksMade, instances;

    public string InstanceId { get; } = instanceId;
    public Task<Message> ProcessAsync(Message message) => throw new NotSupportedException("CAV-BED runs continuously.");

    public async Task RunAsync(IAimPorts ports, AimContext context)
    {
        while (await ports.SelectAsync(-1, (Visual, 1), (Attitude, 1), (Weather, 1), (Full, 1)) is { } port)
        {
            if (await ports.ReadAsync(port.DataType, 1, 0) is not { } m) continue;
            var json = JsonNode.Parse(m.Json);
            switch (port.DataType)
            {
                case Attitude: ego = json; continue;
                case Weather: weather = json; continue;
                case Full: continue;                                 // other CAVs' objects: a later stage
            }
            var ms = EssJson.Milliseconds(json!["BVSDescriptorsSpaceTime"]?["Time"]) ?? ports.Now.ToUnixTimeMilliseconds();
            Fuse(json!, ms);
            if (ego is null) continue;
            await ports.WriteAsync(Descriptors, 1, Descriptors_(ms).ToJsonString());
        }
    }

    private void Fuse(JsonNode descriptors, long ms)
    {
        var dt = lastMs is { } l ? Math.Max(0.001, (ms - l) / 1000.0) : 0;
        lastMs = ms;
        foreach (var t in tracks) { t.X += t.Vx * dt; t.Y += t.Vy * dt; }

        var free = new HashSet<Track>(tracks);
        var id = descriptors["BasicVisualSceneDescriptorsID"]?.GetValue<string>();
        foreach (var entry in descriptors["BasicVisualSceneDescriptors"]?.AsArray() ?? [])
        {
            var obj = entry?["VObjectIDOrVObject"]?[0];
            var position = EssJson.Vector(entry?["VisualObjectSpaceTime"]?["SpatialAttitude1"]?["Position"]?["CartPosition"]);
            var accuracy = EssJson.Vector(entry?["VisualObjectSpaceTime"]?["SpatialAttitude1"]?["Position"]?["CartPositionAccuracy"]) ?? (1, 1, 1);
            if (position is not { } p) continue;
            var cls = obj?["BasicVisualObjectProperties"]?["BasicVisualObjectIdentifier"]?["InstanceIdentifier"]?.GetValue<string>() ?? "object";
            var score = (double?)obj?["BasicVisualObjectProperties"]?["BasicVisualObjectIdentifier"]?["InstanceIdentifierData"]?[0]?["LabelConfidenceLevel"] ?? 0.5;

            var track = free.Where(t => Distance(t, p) < gate + t.AccuracyX).OrderBy(t => Distance(t, p)).FirstOrDefault();
            if (track is null)
            {
                track = new Track { Id = $"T{++tracksMade:D4}", Class = cls, X = p.X, Y = p.Y, First = ms };
                tracks.Add(track);
            }
            else
            {
                free.Remove(track);
                double rx = p.X - track.X, ry = p.Y - track.Y;
                track.X += Alpha * rx; track.Y += Alpha * ry;
                if (dt > 0) { track.Vx += Beta * rx / dt; track.Vy += Beta * ry / dt; }
                track.Existence += (1 - track.Existence) * 0.3;
            }
            track.Class = cls; track.Score = score; track.Misses = 0; track.Last = ms;
            track.AccuracyX = accuracy.X; track.AccuracyY = accuracy.Y;
            track.From = id; track.Object = obj?["BasicVisualObjectID"]?.GetValue<string>();
        }
        foreach (var t in free) { t.Misses++; t.Existence *= 0.7; }
        tracks.RemoveAll(t => t.Misses >= dropAfter);
    }

    private static double Distance(Track t, (double X, double Y, double Z) p) => Math.Sqrt(Math.Pow(t.X - p.X, 2) + Math.Pow(t.Y - p.Y, 2));

    private JsonObject Descriptors_(long ms)
    {
        var id = $"BED{++instances:D6}";
        var objects = new JsonArray(tracks.Select(t => (JsonNode)new JsonObject
        {
            ["BasicEnvironmentObjectID"] = t.Id,
            ["InstanceIdentifier"] = EssJson.Identifier(t.Class, t.Score),
            ["SpatialAttitude"] = EssJson.Attitude($"{id}-{t.Id}", ms, (t.X, t.Y, 0), (t.AccuracyX, t.AccuracyY, 0.1), (t.Vx, t.Vy, 0)),
            ["ExistenceConfidence"] = Math.Round(t.Existence, 3),
            ["Motion"] = "Dynamic",
            ["Contributions"] = new JsonArray(new JsonObject { ["Technology"] = "Visual", ["SceneDescriptorsID"] = t.From, ["ObjectID"] = t.Object }),
            ["FirstObserved"] = EssJson.SimpleTime($"{id}-{t.Id}-F", t.First),
            ["LastObserved"] = EssJson.SimpleTime($"{id}-{t.Id}-L", t.Last)
        }).ToArray());
        var bed = new JsonObject
        {
            ["Header"] = Descriptors, ["BasicEnvironmentDescriptorsID"] = id,
            ["BasicEnvironmentDescriptorsTime"] = EssJson.SimpleTime(id + "-T", ms),
            ["EgoSpatialAttitude"] = ego!.DeepClone(),
            ["BasicEnvironmentObjectCount"] = tracks.Count,
            ["BasicEnvironmentObjects"] = objects
        };
        if (weather is not null) bed["WeatherData"] = weather.DeepClone();
        return bed;
    }
}
