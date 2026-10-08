using Mpai.Cav.Recordings;
using Mpai.Osd.VisualScene;

namespace Mpai.Aif.Tests;

// THE REAR WATCH, A PROTOTYPE (M3253): what the CAV could know of the vehicles that come up behind it, from one
// rear camera of the rig. Not an AIM of the ESS: a first attempt, in the harness, to see what the picture allows.
// The camera captures at full resolution (1920 x 1080, a focal length of 2,637 px, a view of 40 degrees); a window
// of 640 x 360 around the horizon is cut from it - at the reduced scale a car behind is found only to 80 m, in
// the window to 250 m - and the vehicle detector is run on the window. Each vehicle found is placed from the row
// of the bottom of its box (the height of the camera and the horizon give the distance, as the front camera's
// scene description does) and its column; the vehicles are followed from one look to the next, and the closing
// speed is the slope of the distance over the last seconds.
public sealed class HighwayRearWatch : IDisposable
{
    private static readonly HashSet<string> Vehicles = ["car", "truck", "bus"];
    public const int CropX = 640, CropY = 390;                 // the window, in the full resolution picture: the horizon (row 540) is in it
    private const double Horizon = 540, CentreColumn = 960, History = 3.0;

    private readonly YoloxObjectDetector detector;
    private readonly RigCamera camera = HighwayRig.Standard(1, 1)[2];   // RearLeft, not reduced
    private readonly Random grain = new(77);
    private readonly List<Track> tracks = [];
    private int nextId;

    // A vehicle behind: how far behind the ego's rear bumper its front is, how far to the left of the ego's centreline,
    // how fast it is closing (m/s, positive: coming nearer; null until it has been followed for a second), and the
    // detector's score.
    public sealed record Seen(int Id, double Behind, double Left, double? Closing, double Score);

    private sealed class Track
    {
        public int Id; public double Left, Score, LastSeen;
        public List<(double Time, double Behind)> Positions = [];
    }

    public HighwayRearWatch(string model) => detector = new YoloxObjectDetector(model);

    // One look: the vehicles behind as they are seen now, and the window of the picture the detector was given.
    public (IReadOnlyList<Seen> Seen, byte[] Window) Look(HighwayWorld world)
    {
        var frame = HighwayCameras.Capture(camera, world.Ego, world.Others);        // clean, at full resolution
        var window = new byte[640 * 360 * 3];
        for (var y = 0; y < 360; y++) Buffer.BlockCopy(frame.Rgb, ((CropY + y) * frame.Width + CropX) * 3, window, y * 640 * 3, 640 * 3);
        HighwayCameras.Finish(window, 640, 360, grain);

        var found = detector.Detect(Png.Encode(640, 360, window)).Where(d => Vehicles.Contains(d.ClassName)).OrderByDescending(d => d.Score).ToList();
        var kept = new List<ObjectDetection>();
        foreach (var d in found) if (kept.All(k => Iou(k, d) < 0.6)) kept.Add(d);      // a car is often also a truck

        var now = world.Time;
        var seen = new List<Seen>();
        foreach (var d in kept)
        {
            var rows = CropY + d.Y2 - Horizon;
            if (rows < 2) continue;
            var depth = camera.Focal * camera.Z / rows;                            // along the camera's axis, to the front of the vehicle
            var left = camera.Y + (CropX + d.CentreX - CentreColumn) * depth / camera.Focal;
            var behind = depth - (world.Ego.Length / 2 + camera.X);                // from the ego's rear bumper: the camera is 2.0 m behind its centre
            var track = tracks.Where(t => now - t.LastSeen < 1.5 && Math.Abs(t.Left - left) < 1.5 && Math.Abs(t.Positions[^1].Behind - behind) < 30)
                              .OrderBy(t => Math.Abs(t.Positions[^1].Behind - behind)).FirstOrDefault();
            if (track is null) tracks.Add(track = new Track { Id = ++nextId });
            track.Left = left; track.Score = d.Score; track.LastSeen = now;
            track.Positions.Add((now, behind));
            track.Positions.RemoveAll(p => now - p.Time > History);
            seen.Add(new Seen(track.Id, behind, left, Closing(track), d.Score));
        }
        tracks.RemoveAll(t => now - t.LastSeen >= 1.5);
        return (seen, window);
    }

    // The slope of the distance over the last seconds, by least squares; null with fewer than 4 looks over at least a second.
    private static double? Closing(Track t)
    {
        var p = t.Positions;
        if (p.Count < 4 || p[^1].Time - p[0].Time < 1.0) return null;
        double mt = p.Average(x => x.Time), mb = p.Average(x => x.Behind);
        var den = p.Sum(x => (x.Time - mt) * (x.Time - mt));
        return den <= 0 ? null : -p.Sum(x => (x.Time - mt) * (x.Behind - mb)) / den;
    }

    private static double Iou(ObjectDetection a, ObjectDetection b)
    {
        double w = Math.Max(0, Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1)), h = Math.Max(0, Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1));
        var inter = w * h;
        var union = a.Width * a.Height + b.Width * b.Height - inter;
        return union <= 0 ? 0 : inter / union;
    }

    public void Dispose() => detector.Dispose();
}
