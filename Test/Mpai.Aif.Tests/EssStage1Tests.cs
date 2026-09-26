using System.Text.Json;
using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Cav.Ess;
using Mpai.Cav.Recordings;
using Mpai.Osd.VisualScene;

namespace Mpai.Aif.Tests;

// STAGE 1 OF THE ENVIRONMENT SENSING SUBSYSTEM (M3221, against the L2s of M3229).
// Step 1: what a detector of general objects finds in the frames of a drive,
// against the ground truth the frames were made from. The detector is the YOLOX
// of the Middleware (Models/yolox_s.onnx); a vehicle is found when a car, a truck
// or a bus is detected over its box with an IoU of 0.5 or more.
// Run alone: a detector keeps the processor busy, and a test that judges time run
// beside it measures the load (Timing).
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
[Collection(Timing.Name)]
public class EssStage1Tests
{
    private static readonly HashSet<string> Vehicles = ["car", "truck", "bus"];

    private static double IoU((double X1, double Y1, double X2, double Y2) a, (double X1, double Y1, double X2, double Y2) b)
    {
        double w = Math.Max(0, Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1)), h = Math.Max(0, Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1));
        var inter = w * h;
        var union = (a.X2 - a.X1) * (a.Y2 - a.Y1) + (b.X2 - b.X1) * (b.Y2 - b.Y1) - inter;
        return union <= 0 ? 0 : inter / union;
    }

    // The band of distance a vehicle is in, for the report.
    private static string Band(double d) => d < 12 ? "under 12 m" : d < 20 ? "12-20 m" : d < 30 ? "20-30 m" : d < 40 ? "30-40 m" : "40 m and more";

    [SkippableFact]
    public void Step1DetectorOnTheDrive()
    {
        var model = Path.Combine(Repository.Root, "Models", "yolox_s.onnx");
        Skip.IfNot(File.Exists(model), "Models/yolox_s.onnx is absent: the model files are obtained separately.");

        var (messages, truth) = new SyntheticDrive(11, TimeSpan.FromSeconds(20)).Make();
        var frames = messages.Where(m => m.DataType == SyntheticDrive.Camera).ToList();
        var truthFrames = truth["Frames"]!.AsArray();
        Assert.Equal(truthFrames.Count, frames.Count);

        using var detector = new YoloxObjectDetector(model);
        var found = new Dictionary<(string Vehicle, string Band), (int Found, int Frames)>();
        int falseVehicles = 0, others = 0;
        var scores = new List<double>();

        for (var i = 0; i < frames.Count; i++)
        {
            var png = Convert.FromBase64String(JsonNode.Parse(frames[i].Json)!["BasicVisualObjectData"]![0]!["Data"]!.GetValue<string>());
            var detections = detector.Detect(png);
            var matched = new HashSet<ObjectDetection>();

            foreach (var v in truthFrames[i]!["Vehicles"]!.AsArray())
            {
                var b = v!["Box"]!.AsArray().Select(n => (double)n!.GetValue<int>()).ToArray();
                if (b[2] <= 0) continue;                                                // not in view
                var box = (b[0], b[1], b[0] + b[2], b[1] + b[3]);
                var hit = detections.Where(d => Vehicles.Contains(d.ClassName))
                                    .Select(d => (Detection: d, IoU: IoU(box, (d.X1, d.Y1, d.X2, d.Y2))))
                                    .Where(x => x.IoU >= 0.5).OrderByDescending(x => x.IoU).FirstOrDefault();
                var key = ((string)v["Id"]!, Band((double)v["Distance"]!));
                var (f, n) = found.GetValueOrDefault(key);
                found[key] = (f + (hit.Detection is null ? 0 : 1), n + 1);
                if (hit.Detection is not null) { matched.Add(hit.Detection); scores.Add(hit.Detection.Score); }
            }
            falseVehicles += detections.Count(d => Vehicles.Contains(d.ClassName) && !matched.Contains(d));
            others += detections.Count(d => !Vehicles.Contains(d.ClassName));
        }

        var result = new Dictionary<string, string>();
        foreach (var ((vehicle, band), (f, n)) in found.OrderBy(p => p.Key.Vehicle).ThenBy(p => p.Key.Band))
            result[$"{vehicle}, {band}"] = $"found in {f} of {n} frames";
        result["vehicles detected where there is none"] = falseVehicles.ToString();
        result["detections of other classes"] = others.ToString();
        result["score of the vehicles found, median"] = scores.Count == 0 ? "none" : scores.Order().ElementAt(scores.Count / 2).ToString("0.00");

        var report = Path.Combine(Repository.Root, "Test", "Reports", "ess-stage1-detector.json");
        File.WriteAllText(report, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Expected.Match("ess-stage1-detector.json", result);
    }

    // Step 3: Spatial Attitude Generation on a drive of 20 s whose odometry reads 1.5%
    // long. The ego position it gives at each camera frame, against the truth on its
    // frame - east and north of the first GNSS fix - and against what the odometry
    // alone would say; and whether the accuracy it states covers its error.
    [Fact]
    public async Task Step3SpatialAttitude()
    {
        var (messages, truth) = new SyntheticDrive(11, TimeSpan.FromSeconds(20)).Make();
        var ports = DrivePorts.Of(messages, SyntheticDrive.Attitude, SyntheticDrive.Gnss);
        await new SpatialAttitudeGeneration(EssProvider.Sag, new Dictionary<string, string> { ["GnssWeight"] = "0.1" }).RunAsync(ports, AimContext.None);

        const double earth = 6_371_000;
        var frames = truth["Frames"]!.AsArray();
        double lat0 = (double)truth["Origin"]!["Lat"]!, lon0 = (double)truth["Origin"]!["Lon"]!;
        double anchorLat = (double)frames[0]!["GnssLat"]!, anchorLon = (double)frames[0]!["GnssLon"]!;
        var scale = (double)truth["OdometryScale"]!;
        double East(double lon) => (lon - anchorLon) * Math.PI / 180 * earth * Math.Cos(anchorLat * Math.PI / 180);
        var north = (lat0 - anchorLat) * Math.PI / 180 * earth;

        var byTime = ports.Written.GroupBy(w => w.At).ToDictionary(g => g.Key, g => g.Last().Json);
        var sag = new List<double>(); var odometry = new List<double>(); var covered = 0;
        foreach (var f in frames)
        {
            var at = TimeSpan.FromSeconds((double)f!["At"]!);
            if (!byTime.TryGetValue(at, out var json)) continue;
            var position = System.Text.Json.Nodes.JsonNode.Parse(json)!["Position"]!;
            var p = position["CartPosition"]!.AsArray();
            var accuracy = (double)position["CartPositionAccuracy"]![0]!;
            var x = (double)f["EgoEast"]!;
            var trueEast = East(lon0 + x / (earth * Math.Cos(lat0 * Math.PI / 180)) * 180 / Math.PI);
            var error = Math.Sqrt(Math.Pow((double)p[0]! - trueEast, 2) + Math.Pow((double)p[1]! - north, 2));
            sag.Add(error);
            odometry.Add(Math.Abs(x * scale - x));
            if (error <= 2 * accuracy) covered++;
        }
        string Stats(List<double> e) { var s = e.Order().ToList(); return $"median {s[s.Count / 2]:0.00} m, 95th percentile {s[(int)(s.Count * 0.95)]:0.00} m, max {s[^1]:0.00} m"; }
        var result = new Dictionary<string, string>
        {
            ["ego attitudes given"] = $"{ports.Written.Count} for {messages.Count(m => m.DataType == SyntheticDrive.Attitude)} of the MAS",
            ["position error, SAG"] = Stats(sag),
            ["position error, odometry alone"] = Stats(odometry),
            ["error within twice the stated accuracy"] = $"{covered} of {sag.Count} frames"
        };
        var report = Path.Combine(Repository.Root, "Test", "Reports", "ess-stage1-sag.json");
        File.WriteAllText(report, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Expected.Match("ess-stage1-sag.json", result);
    }

    // Step 4: Basic Visual Scene Description on the frames of a drive of 20 s, alone:
    // the vehicle ahead and the one in the left lane, each against the truth - found,
    // at what distance, in which lane; the Alert against the truth's first instant
    // nearer than 15 m, and where the truth has none; every output valid against its
    // schema; the time each frame takes.
    [SkippableFact]
    public async Task Step4VisualSceneDescription()
    {
        var model = Path.Combine(Repository.Root, "Models", "yolox_s.onnx");
        Skip.IfNot(File.Exists(model), "Models/yolox_s.onnx is absent: the model files are obtained separately.");

        var (messages, truth) = new SyntheticDrive(11, TimeSpan.FromSeconds(20)).Make();
        var ports = DrivePorts.Of(messages, SyntheticDrive.Camera);
        var settings = new Dictionary<string, string>
        {
            ["Model"] = model, ["FocalPixels"] = "500", ["CameraHeight"] = "1.5", ["HorizonRow"] = "140", ["ImageWidth"] = "640",
            ["LaneHalfWidth"] = "1.75", ["AlertDistance"] = "15", ["AlertTime"] = "2"
        };
        using var bvs = new BasicVisualSceneDescription(EssProvider.Bvs, settings, Repository.Root);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await bvs.RunAsync(ports, AimContext.None);
        var perFrame = clock.Elapsed.TotalMilliseconds / messages.Count(m => m.DataType == SyntheticDrive.Camera);

        var schemas = AIF.Metadata.PublishedSchemas.At(Repository.Schemas);
        Json.Schema.JsonSchema Schema(string rel) => schemas[Path.GetFullPath(Path.Combine(Repository.Schemas, rel))];
        var descriptorsSchema = Schema("OSD/V1.5/data/BasicVisualSceneDescriptors.json");
        var alertSchema = Schema("CAV2/V1.1/data/Alert.json");
        bool Valid(Json.Schema.JsonSchema s, string json)
        {
            using var doc = JsonDocument.Parse(json);
            lock (AIF.Metadata.PublishedSchemas.Lock) return s.Evaluate(doc.RootElement).IsValid;
        }

        var frames = truth["Frames"]!.AsArray();
        var byTime = frames.ToDictionary(f => TimeSpan.FromSeconds((double)f!["At"]!), f => f!);
        var nearFrom = TimeSpan.FromSeconds((double)truth["Near"]!["From"]!);
        var nearTo = TimeSpan.FromSeconds((double)truth["Near"]!["To"]!);
        int aheadFound = 0, aheadFrames = 0, leftFound = 0, leftFrames = 0, leftInItsLane = 0, validDescriptors = 0, validAlerts = 0;
        var distanceErrors = new List<double>();
        TimeSpan? firstAlert = null; int alertsOutside = 0, alertsForLeft = 0;

        foreach (var (at, dataType, _, json) in ports.Written)
        {
            var node = JsonNode.Parse(json)!;
            var f = byTime[at];
            if (dataType == BasicVisualSceneDescription.Alert)
            {
                if (Valid(alertSchema, json)) validAlerts++;
                if (at < nearFrom - TimeSpan.FromMilliseconds(100) || at > nearTo + TimeSpan.FromMilliseconds(100)) alertsOutside++;
                else firstAlert ??= at;
                foreach (var o in node["AlertData"]!.AsArray())
                    if ((double)o!["BasicVisualObjectProperties"]!["BasicVisualObjectSpaceTime"]!["SpatialAttitude1"]!["Position"]!["CartPosition"]![1]! > 1.75) alertsForLeft++;
                continue;
            }
            if (Valid(descriptorsSchema, json)) validDescriptors++;
            var seen = node["BasicVisualSceneDescriptors"]!.AsArray()
                .Select(e => e!["VisualObjectSpaceTime"]!["SpatialAttitude1"]!["Position"]!["CartPosition"]!.AsArray())
                .Select(p => (Ahead: (double)p[0]!, Left: (double)p[1]!)).ToList();
            foreach (var v in f["Vehicles"]!.AsArray())
            {
                var d = (double)v!["Distance"]!;
                if (d < 12) continue;                                        // nearer, not drawn as a car (Step 1)
                var lane = (int)v["Lane"]!;
                var match = seen.Where(s => Math.Abs(s.Ahead - d) < 0.25 * d + 2).OrderBy(s => Math.Abs(s.Ahead - d)).Cast<(double Ahead, double Left)?>().FirstOrDefault();
                if (lane == 0)
                {
                    aheadFrames++;
                    var inLane = seen.Where(s => Math.Abs(s.Left) < 1.75).OrderBy(s => Math.Abs(s.Ahead - d)).Cast<(double Ahead, double Left)?>().FirstOrDefault();
                    if (inLane is { } a && Math.Abs(a.Ahead - d) < 0.25 * d + 2) { aheadFound++; distanceErrors.Add(Math.Abs(a.Ahead - d)); }
                }
                else
                {
                    leftFrames++;
                    var inLeft = seen.Where(s => s.Left > 1.75 && s.Left < 5.25).OrderBy(s => Math.Abs(s.Ahead - d)).Cast<(double Ahead, double Left)?>().FirstOrDefault();
                    if (match is not null) leftFound++;
                    if (inLeft is { } l && Math.Abs(l.Ahead - d) < 0.25 * d + 2) leftInItsLane++;
                }
            }
        }
        var sorted = distanceErrors.Order().ToList();
        var descriptorsCount = ports.Written.Count(w => w.DataType == BasicVisualSceneDescription.Descriptors);
        var result = new Dictionary<string, string>
        {
            ["descriptors given"] = $"{descriptorsCount}, {validDescriptors} valid against their schema",
            ["vehicle ahead, 12 m and more, found in the ego's lane"] = $"{aheadFound} of {aheadFrames} frames",
            ["vehicle ahead, distance error"] = sorted.Count == 0 ? "none found" : $"median {sorted[sorted.Count / 2]:0.00} m, 95th percentile {sorted[(int)(sorted.Count * 0.95)]:0.00} m",
            ["vehicle in the left lane, 12 m and more, found"] = $"{leftFound} of {leftFrames} frames, in the left lane in {leftInItsLane}",
            ["the first Alert, against the truth nearer than 15 m"] = firstAlert is { } fa ? ((fa - nearFrom).TotalMilliseconds is var lag && lag < 0 ? $"{-lag:0} ms before it" : $"{lag:0} ms after it") : "none",
            ["Alerts where the truth has none"] = alertsOutside.ToString(),
            ["Alerts naming the vehicle in the left lane"] = alertsForLeft.ToString(),
            ["Alerts valid against their schema"] = $"{validAlerts} of {ports.Written.Count(w => w.DataType == BasicVisualSceneDescription.Alert)}"
        };
        var report = Path.Combine(Repository.Root, "Test", "Reports", "ess-stage1-bvs.json");
        File.WriteAllText(report, JsonSerializer.Serialize(result.Append(new("time per frame", $"{perFrame:0} ms")).ToDictionary(), new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Expected.Match("ess-stage1-bvs.json", result);
    }

    // Step 5: Basic Environment Description on what SAG and BVS give for a drive of
    // 20 s, in the order of the drive's times: the Basic Environment Descriptors
    // valid; each vehicle followed by one track - identity switches counted - and
    // its speed relative to the ego against the truth; the confidence of its
    // existence.
    [SkippableFact]
    public async Task Step5EnvironmentDescription()
    {
        var model = Path.Combine(Repository.Root, "Models", "yolox_s.onnx");
        Skip.IfNot(File.Exists(model), "Models/yolox_s.onnx is absent: the model files are obtained separately.");

        var (messages, truth) = new SyntheticDrive(11, TimeSpan.FromSeconds(20)).Make();
        var sagPorts = DrivePorts.Of(messages, SyntheticDrive.Attitude, SyntheticDrive.Gnss);
        await new SpatialAttitudeGeneration(EssProvider.Sag, new Dictionary<string, string>()).RunAsync(sagPorts, AimContext.None);
        var bvsPorts = DrivePorts.Of(messages, SyntheticDrive.Camera);
        using (var bvs = new BasicVisualSceneDescription(EssProvider.Bvs, new Dictionary<string, string> { ["Model"] = model }, Repository.Root))
            await bvs.RunAsync(bvsPorts, AimContext.None);

        // The ego attitude before the frame of the same time: the order the Controller gives.
        var inputs = sagPorts.Written.Select(w => (w.At, w.DataType, w.PortNumber, w.Json))
            .Concat(bvsPorts.Written.Where(w => w.DataType == BasicVisualSceneDescription.Descriptors).Select(w => (At: w.At + TimeSpan.FromTicks(1), w.DataType, w.PortNumber, w.Json)));
        var bedPorts = new DrivePorts(inputs);
        await new BasicEnvironmentDescription(EssProvider.Bed, new Dictionary<string, string> { ["Gate"] = "3", ["DropAfterMisses"] = "5" }).RunAsync(bedPorts, AimContext.None);

        var schema = AIF.Metadata.PublishedSchemas.At(Repository.Schemas)[Path.GetFullPath(Path.Combine(Repository.Schemas, "CAV2", "V2.0", "data", "BasicEnvironmentDescriptors.json"))];
        var frames = truth["Frames"]!.AsArray().ToDictionary(f => TimeSpan.FromSeconds((double)f!["At"]!), f => f!);
        int valid = 0, given = 0;
        var tracks = new Dictionary<string, List<string>> { ["ahead"] = [], ["left"] = [] };
        var speedErrors = new List<double>(); var existence = new List<double>();
        foreach (var (at, _, _, json) in bedPorts.Written)
        {
            given++;
            using (var doc = JsonDocument.Parse(json))
                lock (AIF.Metadata.PublishedSchemas.Lock) if (schema.Evaluate(doc.RootElement).IsValid) valid++;
            var f = frames[at - TimeSpan.FromTicks(1)];
            var objects = JsonNode.Parse(json)!["BasicEnvironmentObjects"]!.AsArray()
                .Select(o => (Id: (string)o!["BasicEnvironmentObjectID"]!, P: o["SpatialAttitude"]!["Position"]!["CartPosition"]!.AsArray(),
                              V: o["SpatialAttitude"]!["Position"]!["CartVelocity"]!.AsArray(), E: (double)o["ExistenceConfidence"]!))
                .Select(o => (o.Id, X: (double)o.P[0]!, Y: (double)o.P[1]!, Vx: (double)o.V[0]!, o.E)).ToList();
            foreach (var v in f["Vehicles"]!.AsArray())
            {
                var d = (double)v!["Distance"]!;
                if (d < 12) continue;
                var name = (string)v["Id"]!;
                var lane = (int)v["Lane"]!;
                var match = objects.Where(o => lane == 0 ? Math.Abs(o.Y) < 1.75 : o.Y > 1.75)
                                   .Where(o => Math.Abs(o.X - d) < 0.25 * d + 2).OrderBy(o => Math.Abs(o.X - d)).Cast<(string Id, double X, double Y, double Vx, double E)?>().FirstOrDefault();
                if (match is not { } m) { tracks[name].Add("-"); continue; }
                tracks[name].Add(m.Id);
                existence.Add(m.E);
                if (name == "ahead" && frames.TryGetValue(at - TimeSpan.FromTicks(1) + TimeSpan.FromMilliseconds(100), out var next))
                {
                    var trueSpeed = ((double)next["Vehicles"]![0]!["Distance"]! - d) / 0.1;       // the distance's rate: the speed relative to the ego
                    speedErrors.Add(Math.Abs(m.Vx - trueSpeed));
                }
            }
        }
        string Switches(List<string> ids) =>
            $"followed in {ids.Count(i => i != "-")} of {ids.Count} frames, by {ids.Where(i => i != "-").Distinct().Count()} track(s)";
        var s = speedErrors.Order().ToList();
        var e = existence.Order().ToList();
        var result = new Dictionary<string, string>
        {
            ["Basic Environment Descriptors given"] = $"{given}, {valid} valid against their schema",
            ["vehicle ahead, 12 m and more"] = Switches(tracks["ahead"]),
            ["vehicle in the left lane, 12 m and more"] = Switches(tracks["left"]),
            ["speed of the vehicle ahead relative to the ego, error"] = s.Count == 0 ? "none" : $"median {s[s.Count / 2]:0.00} m/s, 95th percentile {s[(int)(s.Count * 0.95)]:0.00} m/s",
            ["existence confidence of the vehicles followed"] = e.Count == 0 ? "none" : $"median {e[e.Count / 2]:0.00}, lowest {e[0]:0.00}"
        };
        var report = Path.Combine(Repository.Root, "Test", "Reports", "ess-stage1-bed.json");
        File.WriteAllText(report, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Expected.Match("ess-stage1-bed.json", result);
    }
}
