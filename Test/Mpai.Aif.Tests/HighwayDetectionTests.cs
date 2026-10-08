using System.Text.Json;
using Mpai.Cav.Recordings;
using Mpai.Osd.VisualScene;

namespace Mpai.Aif.Tests;

// WHAT THE VEHICLE DETECTOR FINDS IN THE RIG'S PICTURES (M3253, a first measurement). A car at a distance, in
// the CAV's lane and in the next one, seen by a front camera and by a rear camera, in two ways: the whole
// picture reduced to 640 x 360 (what the detector takes), and a full-resolution crop of 640 x 360 around the
// horizon (the "far search"). Per distance, in how many of the trials the detector finds it (a box of its
// class at IoU 0.5 of the truth). And what a stereo distance made from the detector's own boxes is worth.
// Not an assertion: a measurement, written to Test/Reports, to design the two scales from.
[Trait("Group", "Slow")]
[Trait("Blocks", "No")]
public class HighwayDetectionTests
{
    private static readonly HashSet<string> Vehicles = ["car", "truck", "bus"];
    private static readonly (byte R, byte G, byte B)[] Paints =
        [CameraRenderer.Silver, CameraRenderer.DarkRed, CameraRenderer.Blue, CameraRenderer.White, (40, 40, 44)];

    private static double IoU((double X1, double Y1, double X2, double Y2) a, (double X1, double Y1, double X2, double Y2) b)
    {
        double w = Math.Max(0, Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1)), h = Math.Max(0, Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1));
        var inter = w * h;
        var union = (a.X2 - a.X1) * (a.Y2 - a.Y1) + (b.X2 - b.X1) * (b.Y2 - b.Y1) - inter;
        return union <= 0 ? 0 : inter / union;
    }

    // A 640 x 360 window of a full-resolution picture, its top left at (x0, y0).
    private static byte[] Crop(CameraFrame f, int x0, int y0)
    {
        var crop = new byte[640 * 360 * 3];
        for (var y = 0; y < 360; y++) Buffer.BlockCopy(f.Rgb, ((y0 + y) * f.Width + x0) * 3, crop, y * 640 * 3, 640 * 3);
        return crop;
    }

    private static ObjectDetection? Find(YoloxObjectDetector detector, byte[] rgb, SeenVehicle truth, int offX, int offY)
    {
        var box = (X1: Math.Max(0.0, truth.X - offX), Y1: Math.Max(0.0, truth.Y - offY),
                   X2: Math.Min(640.0, truth.X + truth.W - offX), Y2: Math.Min(360.0, truth.Y + truth.H - offY));
        return detector.Detect(Png.Encode(640, 360, rgb)).Where(d => Vehicles.Contains(d.ClassName))
            .Select(d => (Detection: d, Iou: IoU(box, (d.X1, d.Y1, d.X2, d.Y2)))).Where(x => x.Iou >= 0.5)
            .OrderByDescending(x => x.Iou).Select(x => x.Detection).FirstOrDefault();
    }

    [SkippableFact]
    public void WhatTheDetectorFindsAtWhichDistance()
    {
        var model = Path.Combine(Repository.Root, "Models", "yolox_s.onnx");
        Skip.IfNot(File.Exists(model), "Models/yolox_s.onnx is absent: the model files are obtained separately.");
        using var detector = new YoloxObjectDetector(model);
        var result = new Dictionary<string, string>();
        var rig1 = HighwayRig.Standard(1, 1);
        var rig3 = HighwayRig.Standard(3, 3);
        var stereoErrors = new Dictionary<int, List<double>>();

        // FRONT: the rear of a car ahead, its rear face at D metres from the cameras.
        foreach (var d in new[] { 20, 30, 40, 60, 80, 100, 130, 160, 200 })
        {
            int reducedHits = 0, cropHits = 0, trials = 0;
            stereoErrors[d] = [];
            foreach (var lane in new[] { 0, 1 })
                for (var p = 0; p < Paints.Length; p++)
                {
                    trials++;
                    var car = HighwayWorld.Vehicle("A", 1.9 + d + 2.25, lane, 25, paint: Paints[p]);
                    var w = HighwayWorld.Scenario([car], egoSpeed: 30, egoLane: 0);

                    var reduced = HighwayCameras.Capture(rig3[0], w.Ego, w.Others, new Random(100 + p));
                    if (reduced.Vehicles.Single() is { InPicture: true } r3 && Find(detector, reduced.Rgb, r3, 0, 0) is not null) reducedHits++;

                    var left = HighwayCameras.Capture(rig1[0], w.Ego, w.Others, new Random(200 + p));
                    var right = HighwayCameras.Capture(rig1[1], w.Ego, w.Others, new Random(300 + p));
                    var tl = left.Vehicles.Single(); var tr = right.Vehicles.Single();
                    var dl = Find(detector, Crop(left, 640, 360), tl, 640, 360);
                    if (dl is not null) cropHits++;
                    var dr = Find(detector, Crop(right, 640, 360), tr, 640, 360);
                    if (dl is not null && dr is not null)
                    {
                        var disparity = dl.CentreX - dr.CentreX;
                        if (disparity > 0.2) stereoErrors[d].Add(Math.Abs(rig1[0].Focal * HighwayRig.Baseline / disparity - d) / d);
                    }
                }
            result[$"front, car {d} m ahead: whole picture reduced to 640 x 360"] = $"found in {reducedHits} of {trials}";
            result[$"front, car {d} m ahead: full-resolution crop around the horizon"] = $"found in {cropHits} of {trials}";
            var e = stereoErrors[d];
            result[$"front, car {d} m ahead: stereo distance from the detector's boxes"] =
                e.Count == 0 ? "no pair of detections" : $"{e.Count} pairs, median error {e.Order().ElementAt(e.Count / 2) * 100:0}% ({e.Order().ElementAt(e.Count / 2) * d:0.0} m)";
        }

        // REAR: the front of a car coming up in the left lane, its front face at D metres from the cameras.
        foreach (var d in new[] { 40, 60, 80, 100, 130, 160, 200, 250 })
        {
            int reducedHits = 0, cropHits = 0, trials = 0;
            for (var p = 0; p < Paints.Length; p++)
            {
                trials++;
                var car = HighwayWorld.Vehicle("F", -(d + 2.0 + 2.25), 1, 38, paint: Paints[p]);
                var w = HighwayWorld.Scenario([car], egoSpeed: 30, egoLane: 0);
                var reduced = HighwayCameras.Capture(rig3[2], w.Ego, w.Others, new Random(400 + p));
                if (reduced.Vehicles.Single() is { InPicture: true } r3 && Find(detector, reduced.Rgb, r3, 0, 0) is not null) reducedHits++;
                var full = HighwayCameras.Capture(rig1[2], w.Ego, w.Others, new Random(500 + p));
                var t = full.Vehicles.Single();
                // The crop follows the car: a window of 640 x 360 around where the car is, on the horizon's row.
                int x0 = Math.Clamp(t.X + t.W / 2 - 320, 0, 1920 - 640), y0 = Math.Clamp(540 - 150, 0, 1080 - 360);
                if (Find(detector, Crop(full, x0, y0), t, x0, y0) is not null) cropHits++;
            }
            result[$"rear, car {d} m behind in the left lane: whole picture reduced to 640 x 360"] = $"found in {reducedHits} of {trials}";
            result[$"rear, car {d} m behind in the left lane: full-resolution crop"] = $"found in {cropHits} of {trials}";
        }

        var report = Path.Combine(Repository.Root, "Test", "Reports", "highway-stage0-detector.json");
        File.WriteAllText(report, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }
}
