using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Osd.VisualScene;

namespace Mpai.Cav.Ess;

// BASIC VISUAL SCENE DESCRIPTION FOR THE ESS (OSD-BVS-V1.5, instance I02; M3229 6,
// M3221 3.3). For each camera frame:
//  - the road users in it, by YOLOX: car, truck, bus, motorcycle, bicycle, person;
//    one per object, whatever other class the detector also gave it (a car is
//    often also a truck, a duplicate YOLOX's per-class suppression keeps);
//  - where each is, from the camera's model - its focal length, its height above
//    the road, the row of the horizon: the distance of a thing on the road from the
//    row of the bottom of its box, its lateral offset from its column; the accuracy
//    of the distance from an error of one row;
//  - the Basic Visual Scene Descriptors: each object a Basic Visual Object with its
//    class and its Space-Time relative to the ego - X ahead, Y to the left, Z up -
//    at the time of the frame;
//  - an Alert when an object in the ego's lane is nearer than AlertDistance, or will
//    be reached within AlertTime at the closing speed the prior Basic Environment
//    Descriptors give for the object there.
// THE CAMERA THAT LOOKS BACK (M3253): a picture says where its camera is looking by the orientation in the Space-Time of
// the Basic Visual Object - its yaw, 0 along the CAV's heading, 180 back along the road. The settings of that camera
// are those with the prefix Rear (RearFocalPixels, RearCameraHeight, RearHorizonRow, RearImageWidth, RearCameraAhead,
// RearCameraLeft); without them a picture that looks back is not described. What it gives the Basic Environment
// Description is where the vehicles behind are - the distance of the front of each from the ego's front, negative:
// behind it - and how far to the left of the ego, the Point of View of its descriptors turned back the same way; it
// gives no Alert: what comes up behind is not in the ego's way.
// THE TELEPHOTO WINDOW (the author, 2026/10/14: the ESS reads the turn indicators from the picture). A frame whose description says it is a
// telephoto window - a window of a long-lens camera's picture at its full resolution, looking along the CAV's heading; its settings have the
// prefix Detail - is described with that camera's model, and held: what it shows goes with the front picture that follows it, in one set of
// descriptors - the vehicles it shows beyond where the front picture finds them, and, of each, its indicator: the pixels at either side of the
// vehicle's box that are amber (a lit indicator is, and nothing else of a vehicle is), the side which, seen from behind the vehicle, is its
// left; seen from ahead - it is coming at the CAV, by the prior descriptors - its right. TurnIndicator of the Basic Visual Object's properties
// says Left or Right; none when it is not lit - it blinks, and what is said of it over the instants is the Basic Environment Description's.
// Every frame is described: a frame arriving while another is described waits
// (Block, in its L3) - the CAV is to perform, not to keep up (M3233).
public sealed class BasicVisualSceneDescription : IAimProcessor, IAimRunner, IDisposable
{
    public const string Frame = "OSD-BVO-V1.5", Attitude = "OSD-OSA-V1.5", Prior = "CAV-BED-V2.0",
                        Descriptors = "OSD-BVS-V1.5", Alert = "CAV-ALT-V2.0";
    private static readonly HashSet<string> RoadUsers = ["car", "truck", "bus", "motorcycle", "bicycle", "person"];

    private readonly YoloxObjectDetector detector;
    private sealed record Camera(double Focal, double Height, double Horizon, double Width, double Ahead, double Left);
    private readonly Camera front;
    private readonly Camera? back, far;
    private IReadOnlyList<Seen>? farSeen;
    private long farMs;
    private readonly double laneHalf, alertDistance, alertTime;
    private JsonNode? prior;
    private long frames, alerts;

    public BasicVisualSceneDescription(string instanceId, IReadOnlyDictionary<string, string> settings, string root)
    {
        InstanceId = instanceId;
        var model = settings.TryGetValue("Model", out var m) ? m : @"Models\yolox_s.onnx";
        detector = new YoloxObjectDetector(Path.IsPathRooted(model) ? model : Path.Combine(root, model),
                                           scoreThreshold: (float)EssJson.Setting(settings, "ScoreThreshold", 0.3));
        front = Look("", settings);
        back = settings.ContainsKey("RearFocalPixels") ? Look("Rear", settings) : null;
        far = settings.ContainsKey("DetailFocalPixels") ? Look("Detail", settings) : null;
        // Where the camera is, from the point the distances are given to - the ego's front - and its centreline:
        // CameraAhead metres ahead of that point (negative: behind it), CameraLeft metres to the left. Zero, as
        // the camera of the first simulation was, they change nothing; a rig whose cameras are not there says so.
        laneHalf = EssJson.Setting(settings, "LaneHalfWidth", 1.75);
        alertDistance = EssJson.Setting(settings, "AlertDistance", 15);
        alertTime = EssJson.Setting(settings, "AlertTime", 2);
    }

    // A camera: its focal length (px), its height above the road, the row of the horizon, the width of its picture, and where
    // it is - CameraAhead metres ahead of the ego's front (negative: behind it), CameraLeft metres to the left of its centreline.
    // Zero, as the camera of the first simulation was, they change nothing; a rig whose cameras are not there says so.
    private static Camera Look(string prefix, IReadOnlyDictionary<string, string> settings) => new(
        EssJson.Setting(settings, prefix + "FocalPixels", 500), EssJson.Setting(settings, prefix + "CameraHeight", 1.5),
        EssJson.Setting(settings, prefix + "HorizonRow", 140), EssJson.Setting(settings, prefix + "ImageWidth", 640),
        EssJson.Setting(settings, prefix + "CameraAhead", 0), EssJson.Setting(settings, prefix + "CameraLeft", 0));

    // Whether the picture looks back: the yaw of its Space-Time, more than a quarter turn from the CAV's heading.
    private static bool LooksBack(JsonNode frame) =>
        EssJson.Vector(frame["BasicVisualObjectTime"]?["SpatialAttitude1"]?["Orientation"]?["Orientation"]) is { } o && Math.Abs(Math.IEEERemainder(o.Z, 360)) > 90;

    public string InstanceId { get; }
    public Task<Message> ProcessAsync(Message message) => throw new NotSupportedException("OSD-BVS for the ESS runs continuously.");

    public async Task RunAsync(IAimPorts ports, AimContext context)
    {
        while (await ports.SelectAsync(-1, (Frame, 1), (Attitude, 1), (Prior, 1)) is { } port)
        {
            if (await ports.ReadAsync(port.DataType, 1, 0) is not { } m) continue;
            if (port.DataType == Prior) { prior = JsonNode.Parse(m.Json); continue; }
            if (port.DataType == Attitude) continue;                 // the frame is described relative to the ego itself

            var frame = JsonNode.Parse(m.Json)!;
            var image = Image(frame, ports);
            if (image is null) { context.Report("A frame without image data was not described."); continue; }
            var ms = EssJson.Milliseconds(frame["BasicVisualObjectTime"]?["Time"]) ?? ports.Now.ToUnixTimeMilliseconds();
            if (((string?)frame["DescrMetadata"])?.Contains("telephoto") == true)
            {
                if (far is null) { context.Report("A telephoto window and no settings for it (Detail...): not described."); continue; }
                farSeen = DescribeFar(image, far); farMs = ms;
                continue;
            }
            var rear = LooksBack(frame);
            if (rear && back is null) { context.Report("A frame from a camera that looks back, and no settings for it: not described."); continue; }

            var objects = Describe(image, rear ? back! : front, rear);
            if (!rear && farSeen is not null && ms - farMs <= 150) objects = Merge(objects, farSeen);
            var id = $"BVS{++frames:D6}";
            await ports.WriteAsync(Descriptors, 1, SceneDescriptors(id, ms, objects, rear ? back! : front, rear).ToJsonString());

            var urgent = rear ? new List<Seen>() : objects.Where(o => Urgent(o)).ToList();
            if (urgent.Count > 0)
                await ports.WriteAsync(Alert, 1, new JsonObject
                {
                    ["Header"] = Alert, ["AlertID"] = $"ALT{++alerts:D6}", ["AlertTime"] = EssJson.SimpleTime($"ALT{alerts:D6}-T", ms),
                    ["AlertData"] = new JsonArray(urgent.Select(o => (JsonNode)VisualObject(id, ms, o)).ToArray())
                }.ToJsonString());
        }
    }

    // ---- what is in a frame --------------------------------------------------------

    public sealed record Seen(int Index, string Class, double Score, double Ahead, double Left, double AheadAccuracy, ObjectDetection Box, string? Turn = null);

    public IReadOnlyList<Seen> Describe(byte[] image) => Describe(image, front, false);

    private IReadOnlyList<Seen> Describe(byte[] image, Camera camera, bool rear)
    {
        var (focal, height, horizon, width, cameraAhead, cameraLeft) = camera;
        var found = detector.Detect(image).Where(d => RoadUsers.Contains(d.ClassName)).OrderByDescending(d => d.Score).ToList();
        var kept = new List<ObjectDetection>();
        foreach (var d in found)
            if (kept.All(k => IoU(k, d) < 0.6)) kept.Add(d);

        var seen = new List<Seen>();
        foreach (var d in kept)
        {
            var rows = d.Y2 - horizon;
            if (rows < 1) continue;                                   // at or above the horizon: not on the road
            var fromCamera = focal * height / rows;
            // Looking back, the picture's right is the ego's left, and what is further from the camera is further behind.
            var left = (rear ? 1 : -1) * (d.CentreX - width / 2) * fromCamera / focal + cameraLeft;
            var ahead = rear ? cameraAhead - fromCamera : fromCamera + cameraAhead;
            seen.Add(new Seen(seen.Count + 1, d.ClassName, d.Score, ahead, left, fromCamera * fromCamera / (focal * height), d));
        }
        return seen;
    }

    // What the last telephoto window showed, for whoever watches the run (the test harness); not an output of the AIM.
    public static volatile string LastFar = "";

    // What the telephoto window shows, each vehicle with its indicator, if it is lit.
    private IReadOnlyList<Seen> DescribeFar(byte[] image, Camera camera)
    {
        var seen = Describe(image, camera, false);
        using var picture = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgb24>(image);
        var read = seen.Select(o => o with { Turn = ReadIndicator(picture, o) }).ToList();
        LastFar = string.Join("; ", read.Select(o => $"{o.Class} {o.Score:0.00} ahead {o.Ahead:0} left {o.Left:0.0} box {o.Box.X1:0}-{o.Box.X2:0},{o.Box.Y1:0}-{o.Box.Y2:0} turn {o.Turn ?? "-"}"));
        return read;
    }

    // The front picture's objects and the telephoto window's: the same vehicle, once - with the window's indicator, and the position of the
    // one that knows it better - and those only the window shows.
    private static IReadOnlyList<Seen> Merge(IReadOnlyList<Seen> front, IReadOnlyList<Seen> far)
    {
        var merged = front.ToList();
        foreach (var f in far)
        {
            var i = merged.FindIndex(g => Math.Abs(g.Left - f.Left) < 2.0 && Math.Abs(g.Ahead - f.Ahead) < 4 + 0.15 * f.Ahead);
            if (i < 0) merged.Add(f);
            else
            {
                var better = f.AheadAccuracy < merged[i].AheadAccuracy ? f : merged[i];
                merged[i] = better with { Turn = f.Turn ?? merged[i].Turn };
            }
        }
        return merged.Select((o, k) => o with { Index = k + 1 }).ToList();
    }

    // Which indicator of the vehicle in the box is lit, if one is: amber pixels in the middle band of the box, at its left third or its right.
    private string? ReadIndicator(SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24> picture, Seen o)
    {
        var b = o.Box;
        int x0 = Math.Max(0, (int)b.X1), x1 = Math.Min(picture.Width - 1, (int)b.X2), h = (int)(b.Y2 - b.Y1);
        int y0 = Math.Max(0, (int)(b.Y1 + 0.30 * h)), y1 = Math.Min(picture.Height - 1, (int)(b.Y1 + 0.80 * h));
        if (x1 - x0 < 6 || y1 - y0 < 3) return null;
        int third = (x1 - x0 + 1) / 3, left = 0, right = 0;
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                var p = picture[x, y];
                if (!Amber(p.R, p.G, p.B)) continue;
                if (x < x0 + third) left++; else if (x > x1 - third) right++;
            }
        if (Math.Max(left, right) < 3 || Math.Max(left, right) < 2 * Math.Min(left, right)) return null;
        var onPictureLeft = left > right;
        // From behind, the vehicle's left is the picture's left; from ahead, its right.
        return onPictureLeft == !Oncoming(o) ? "Left" : "Right";
    }

    // Amber: red, a good half of it green, little blue - what a vehicle's body is not, and a lit indicator is, through the grain of the camera.
    private static bool Amber(byte r, byte g, byte b) => r >= 170 && g >= 0.35 * r && g <= 0.92 * r && b <= 0.5 * g;

    // Whether the vehicle comes at the CAV (its front is what is seen): by the prior descriptors' object nearest to it, whose speed relative
    // to the CAV is then high and against it; without a prior, one in the lane to the left does.
    private bool Oncoming(Seen o)
    {
        double best = 8, speed = 0; var known = false;
        foreach (var e in prior?["BasicEnvironmentObjects"]?.AsArray() ?? [])
        {
            var p = EssJson.Vector(e?["SpatialAttitude"]?["Position"]?["CartPosition"]);
            var v = EssJson.Vector(e?["SpatialAttitude"]?["Position"]?["CartVelocity"]);
            if (p is null || v is null) continue;
            var dist = Math.Sqrt(Math.Pow(p.Value.X - o.Ahead, 2) + Math.Pow(p.Value.Y - o.Left, 2));
            if (dist < best) { best = dist; speed = v.Value.X; known = true; }
        }
        // A prior that knows no speed yet (a track just made) says nothing: the lane to the left is the other way's.
        return known && Math.Abs(speed) > 1 ? speed < -8 : o.Left > laneHalf;
    }

    // In the ego's lane and nearer than the distance, or reached within the time at
    // the closing speed of the prior's object nearest to it.
    private bool Urgent(Seen o)
    {
        if (Math.Abs(o.Left) > laneHalf) return false;
        if (o.Ahead < alertDistance) return true;
        var closing = ClosingSpeed(o);
        return closing > 0.1 && o.Ahead / closing < alertTime;
    }

    private double ClosingSpeed(Seen o)
    {
        double best = 3, speed = 0;
        foreach (var e in prior?["BasicEnvironmentObjects"]?.AsArray() ?? [])
        {
            var p = EssJson.Vector(e?["SpatialAttitude"]?["Position"]?["CartPosition"]);
            var v = EssJson.Vector(e?["SpatialAttitude"]?["Position"]?["CartVelocity"]);
            if (p is null || v is null) continue;
            var dist = Math.Sqrt(Math.Pow(p.Value.X - o.Ahead, 2) + Math.Pow(p.Value.Y - o.Left, 2));
            if (dist < best) { best = dist; speed = -v.Value.X; }
        }
        return speed;
    }

    // ---- the Data Types ------------------------------------------------------------

    private JsonObject SceneDescriptors(string id, long ms, IReadOnlyList<Seen> objects, Camera camera, bool rear) => new()
    {
        ["Header"] = Descriptors, ["MInstanceID"] = "", ["BasicVisualSceneDescriptorsID"] = id,
        ["BVSDescriptorsSpaceTime"] = EssJson.SpaceTime(id + "-ST", ms),
        // Where the Scene is seen from: the camera, at its height, looking ahead.
        ["UserPoV"] = new JsonObject
        {
            ["Header"] = "OSD-OPV-V1.5", ["PointOfViewID"] = $"{id}-POV",
            ["General"] = new JsonObject { ["CoordType"] = "Cartesian", ["ObjectType"] = "Generic", ["MediaType"] = "Visual" },
            ["CartPosition"] = new JsonArray(0.0, 0.0, camera.Height), ["Orientation"] = new JsonArray(0.0, 0.0, rear ? 180.0 : 0.0)
        },
        ["VisualObjectCount"] = objects.Count,
        ["BasicVisualSceneDescriptors"] = new JsonArray(objects.Select(o => (JsonNode)new JsonObject
        {
            ["VisualObjectSpaceTime"] = EssJson.SpaceTime($"{id}-O{o.Index}-ST", ms, Placed($"{id}-O{o.Index}", ms, o)),
            ["VObjectIDOrVObject"] = new JsonArray(VisualObject(id, ms, o))
        }).ToArray())
    };

    private JsonObject Placed(string id, long ms, Seen o) =>
        EssJson.Attitude(id + "-SA", ms, (o.Ahead, o.Left, 0), (o.AheadAccuracy, o.AheadAccuracy * Math.Abs(o.Left) / Math.Abs(o.Ahead) + 0.1, 0.1), (0, 0, 0));

    private JsonObject VisualObject(string id, long ms, Seen o) => new()
    {
        ["Header"] = Frame, ["BasicVisualObjectID"] = $"{id}-O{o.Index}",
        ["BasicVisualObjectProperties"] = new JsonObject
        {
            ["BasicVisualObjectIdentifier"] = EssJson.Identifier(o.Class, o.Score),
            ["BasicVisualObjectSpaceTime"] = EssJson.SpaceTime($"{id}-O{o.Index}-ST", ms, Placed($"{id}-O{o.Index}", ms, o))
        }.WithIf("TurnIndicator", o.Turn)
    };

    private static byte[]? Image(JsonNode frame, IAimPorts ports)
    {
        var data = frame["BasicVisualObjectData"]?[0];
        if (data?["Data"]?.GetValue<string>() is { } b64) return Convert.FromBase64String(b64);
        if (data?["DataURI"]?.GetValue<string>() is { } reference) return ports.GetPayload(reference).ToArray();
        return null;
    }

    private static double IoU(ObjectDetection a, ObjectDetection b)
    {
        double w = Math.Max(0, Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1)), h = Math.Max(0, Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1));
        var inter = w * h;
        var union = a.Width * a.Height + b.Width * b.Height - inter;
        return union <= 0 ? 0 : inter / union;
    }

    public void Dispose() => detector.Dispose();
}
