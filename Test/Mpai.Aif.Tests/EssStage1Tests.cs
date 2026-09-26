using System.Text.Json;
using System.Text.Json.Nodes;

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
}
