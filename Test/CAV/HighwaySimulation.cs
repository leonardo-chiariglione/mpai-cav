using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

using Mpai.Cav.Map;

namespace Mpai.Cav.Recordings;

// THE HIGHWAY AS THE CAV'S LOOP SEES IT (M3253). A HighwayWorld, and what the CAV's sensors capture of it each
// step, in the form the ESS takes - five Spatial Attitudes of the Motion Actuation Subsystem (odometry reading
// 1.5% long), a GNSS fix (1.5 m of noise) and a picture of one front camera of the rig - with the truth of the
// step; and the Offline Map the AMS reads: one straight road, 60 km, with the speed limit the CAV is to keep.
// The AMS (Stage 1) moves the CAV by an acceleration, as in the first simulation; it keeps its lane.
// Nothing runs against a clock: a step lasts as long as the CAV takes.
public sealed class HighwaySimulation
{
    public const double Step = HighwayWorld.Step;
    private const int AttitudesPerStep = 5;
    private const double OdometryScale = 1.015, GnssSigma = 1.5, MaxAcceleration = 3, MaxDeceleration = 8;

    public const int RearEvery = 3;                  // a look behind every 0.3 s
    public const int DetailEvery = 2;                // a look far ahead every 0.2 s: an indicator is lit for 0.33 s, whatever the time it is
    public const int DetailCropX = 640, DetailCropY = 390;   // the window of the telephoto picture the detector is given, in its full resolution: the horizon (row 540) is in it
    public const int RearCropX = 640, RearCropY = 390;   // the window of the rear picture the detector is given, in its full resolution: the horizon (row 540) is in it
    private readonly Random noise, grain, rearGrain = new(77), detailGrain = new(91);
    private double odoX, odoY;
    private int attitudes;

    public HighwayWorld World { get; }
    public RoadMap Map { get; }
    public RigCamera Camera { get; }                 // the camera whose pictures go to the ESS
    public RigCamera RearCamera { get; } = HighwayRig.Standard(1, 1)[2];   // RearLeft, not reduced: the second camera, looking back
    // The telephoto camera that looks far ahead, between the two of the front pair: 30 degrees, not reduced. A vehicle 150 m away is 44 pixels
    // wide in it - its indicator, seven.
    public RigCamera TeleCamera { get; } = new("FrontTele", 1.9, 0, 1.40, 0, 30);
    public DateTimeOffset Start { get; }
    public double SpeedLimit { get; }                // m/s
    public double EgoSpeed => World.Ego.Speed;
    public bool Collided => World.EgoCollisions.Count > 0;
    public bool Arrived => World.Ego.X >= 59_950;

    public HighwaySimulation(HighwayWorld world, double speedLimit = 30, int cameraReduction = 3, int seed = 1, DateTimeOffset? start = null)
    {
        World = world; SpeedLimit = speedLimit;
        Camera = HighwayRig.Standard(cameraReduction, cameraReduction)[0];
        Map = HighwayMap(speedLimit, junction: world.Junction);
        Start = start ?? new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
        noise = new Random(seed * 7919 + 1); grain = new Random(seed * 104729 + 3);
    }

    // The Offline Map: a way point at each end of one road, east, of two lanes (the lane the CAV is in: the
    // AMS keeps to the centreline of its road, the right lane: the Path), the speed limit in m/s.
    public static RoadMap HighwayMap(double speedLimit, double length = 60_000, Junction? junction = null) =>
        junction is null
            ? new("Highway", 45.0703, 7.6869, [new WayPoint("H0", 0, 0, "Start"), new WayPoint("H1", length, 0, "End")],
                  [new RoadSegment("H01", "H0", "H1", speedLimit, HighwayRoad.Lanes)])
            // With a junction: the highway in two segments that meet at the way point J, and the road that crosses it, N to S, 2 km each side.
            : new("Highway", 45.0703, 7.6869,
                  [new WayPoint("H0", 0, 0, "Start"), new WayPoint("J", junction.X, 0, "Junction"), new WayPoint("H1", length, 0, "End"),
                   new WayPoint("N", junction.X, 2000, "North"), new WayPoint("S", junction.X, -2000, "South")],
                  [new RoadSegment("H0J", "H0", "J", speedLimit, HighwayRoad.Lanes), new RoadSegment("JH1", "J", "H1", speedLimit, HighwayRoad.Lanes),
                   new RoadSegment("NJ", "N", "J", 14, 2), new RoadSegment("JN", "J", "N", 14, 2),
                   new RoadSegment("SJ", "S", "J", 14, 2), new RoadSegment("JS", "J", "S", 14, 2)]);

    // What the ESS is told of its camera: the settings of the scene-description AIM that follow from the rig.
    public IReadOnlyDictionary<string, string> EssSettings() => new Dictionary<string, string>
    {
        ["FocalPixels"] = Camera.OutputFocal.ToString("0.####", CultureInfo.InvariantCulture),
        ["CameraHeight"] = Camera.Z.ToString("0.###", CultureInfo.InvariantCulture),
        ["HorizonRow"] = (Camera.OutputHeight / 2.0).ToString("0.###", CultureInfo.InvariantCulture),
        ["ImageWidth"] = Camera.OutputWidth.ToString(CultureInfo.InvariantCulture),
        // Distances are given from the ego's front; the camera is Camera.X from its centre, which has a front at Length / 2.
        ["CameraAhead"] = (Camera.X - World.Ego.Length / 2).ToString("0.###", CultureInfo.InvariantCulture),
        ["CameraLeft"] = Camera.Y.ToString("0.###", CultureInfo.InvariantCulture)
    };

    // What the ESS is told of the camera that looks back. The camera captures at full resolution (1920 x 1080, a focal
    // length of 2,637 px, a view of 40 degrees); a window of 640 x 360 around the horizon is cut from it and given to the
    // vehicle detector - at the reduced scale a car behind is found only to 80 m, in the window to 250 m. Its horizon
    // is at row 150 of the window and the middle of the picture at its column 320.
    public IReadOnlyDictionary<string, string> RearEssSettings() => new Dictionary<string, string>
    {
        ["RearFocalPixels"] = RearCamera.Focal.ToString("0.####", CultureInfo.InvariantCulture),
        ["RearCameraHeight"] = RearCamera.Z.ToString("0.###", CultureInfo.InvariantCulture),
        ["RearHorizonRow"] = (RearCamera.Height / 2.0 - RearCropY).ToString("0.###", CultureInfo.InvariantCulture),
        ["RearImageWidth"] = "640",
        // The camera is 2.0 m behind the ego's centre: 4.4 m behind its front.
        ["RearCameraAhead"] = (RearCamera.X - World.Ego.Length / 2).ToString("0.###", CultureInfo.InvariantCulture),
        ["RearCameraLeft"] = RearCamera.Y.ToString("0.###", CultureInfo.InvariantCulture)
    };

    // What the ESS is told of the telephoto camera: the settings of the scene-description AIM that follow from it, with the Detail prefix. Its
    // window is 640 x 360 around the horizon (row 150 of it), the middle of the picture at its column 320.
    public IReadOnlyDictionary<string, string> DetailEssSettings() => new Dictionary<string, string>
    {
        ["DetailFocalPixels"] = TeleCamera.Focal.ToString("0.####", CultureInfo.InvariantCulture),
        ["DetailCameraHeight"] = TeleCamera.Z.ToString("0.###", CultureInfo.InvariantCulture),
        ["DetailHorizonRow"] = (TeleCamera.Height / 2.0 - DetailCropY).ToString("0.###", CultureInfo.InvariantCulture),
        ["DetailImageWidth"] = "640",
        ["DetailCameraAhead"] = (TeleCamera.X - World.Ego.Length / 2).ToString("0.###", CultureInfo.InvariantCulture),
        ["DetailCameraLeft"] = TeleCamera.Y.ToString("0.###", CultureInfo.InvariantCulture)
    };

    // The window of the telephoto camera's picture, as the detector is given it: clean at full resolution, cut, then the grain and the blur of a
    // real camera.
    public byte[] DetailWindow()
    {
        var frame = HighwayCameras.Capture(TeleCamera, World.Ego, World.Others, time: World.Time);
        var window = new byte[640 * 360 * 3];
        for (var y = 0; y < 360; y++) Buffer.BlockCopy(frame.Rgb, ((DetailCropY + y) * frame.Width + DetailCropX) * 3, window, y * 640 * 3, 640 * 3);
        HighwayCameras.Finish(window, 640, 360, detailGrain);
        return window;
    }

    // The window of the rear camera's picture, as the detector is given it: clean at full resolution, cut, then given the
    // grain and the blur of a real camera.
    public byte[] RearWindow()
    {
        var frame = HighwayCameras.Capture(RearCamera, World.Ego, World.Others);
        var window = new byte[640 * 360 * 3];
        for (var y = 0; y < 360; y++) Buffer.BlockCopy(frame.Rgb, ((RearCropY + y) * frame.Width + RearCropX) * 3, window, y * 640 * 3, 640 * 3);
        HighwayCameras.Finish(window, 640, 360, rearGrain);
        return window;
    }

    // ---- what the sensors capture --------------------------------------------------------------

    // camera: false where nothing looks at the picture (perfect perception): it is then not made.
    // detail: the telephoto window goes with the front picture, before it.
    public Simulation.Sensed Sense(bool camera = true, bool rear = true, bool detail = false)
    {
        var ego = World.Ego;
        var messages = new List<(string, string)>();
        var ms0 = (Start + TimeSpan.FromSeconds(World.Time)).ToUnixTimeMilliseconds();

        for (var k = 0; k < AttitudesPerStep; k++)
        {
            var dt = Step / AttitudesPerStep;
            if (World.StepNumber > 0 || k > 0)
            {
                odoX += ego.Speed * OdometryScale * Math.Cos(ego.Heading) * dt;
                odoY += ego.Speed * OdometryScale * Math.Sin(ego.Heading) * dt;
            }
            var ms = ms0 - (long)((AttitudesPerStep - 1 - k) * dt * 1000);
            messages.Add(("OSD-OSA-V1.5", Attitude(ms, ego.Heading, ego.Speed)));
        }

        var (lat, lon) = Map.Geodetic(ego.X + Gaussian(GnssSigma), ego.Y + Gaussian(GnssSigma));
        messages.Add(("CAV-GNO-V2.0", Gnss(ms0, lat, lon)));

        if (camera)
        {
            var frame = HighwayCameras.Capture(Camera, ego, World.Others, grain, World.Time);
            if (detail && World.StepNumber % DetailEvery == 0)
                messages.Add(("OSD-BVO-V1.5", Frame(ms0, Png.Encode(640, 360, DetailWindow()), "BVD", $"telephoto camera {TeleCamera.Name}, window")));
            messages.Add(("OSD-BVO-V1.5", Frame(ms0, frame.Png, "BVO", $"front camera {Camera.Name}")));
            // The camera that looks back, on the same port: its picture says by its orientation which way it looks.
            if (rear && World.StepNumber % RearEvery == 0)
                messages.Add(("OSD-BVO-V1.5", Frame(ms0, Png.Encode(640, 360, RearWindow()), "BVR", $"rear camera {RearCamera.Name}, window", RearCamera.YawDegrees)));
        }

        var vehicles = new JsonArray();
        foreach (var v in World.Others.OrderBy(v => v.X))
        {
            var ahead = (v.X - v.Length / 2) - (ego.X + ego.Length / 2);
            if (ahead < -60 || ahead > 250) continue;
            vehicles.Add(new JsonObject
            {
                ["Id"] = v.Id, ["Lane"] = v.Lane - ego.Lane, ["Distance"] = Math.Round(ahead, 3), ["Speed"] = Math.Round(v.Velocity, 3), ["Truck"] = v.Truck
            });
        }
        var gap = GapAhead();
        var truth = new JsonObject
        {
            ["Step"] = World.StepNumber, ["At"] = Math.Round(World.Time, 3), ["Ms"] = ms0,
            ["Ego"] = new JsonObject
            {
                ["S"] = Math.Round(ego.X, 3), ["Speed"] = Math.Round(ego.Speed, 3), ["Acceleration"] = Math.Round(ego.Acceleration, 3),
                ["Lane"] = ego.Lane, ["SpeedLimit"] = SpeedLimit
            },
            ["Gap"] = gap is { } g ? Math.Round(g, 3) : null,
            ["Collided"] = Collided,
            ["Vehicles"] = vehicles
        };
        return new Simulation.Sensed(messages, truth, ms0);
    }

    // The gap to the nearest vehicle ahead in the ego's way, bumper to bumper.
    public double? GapAhead()
    {
        var (lead, gap) = World.Leader(World.Ego, World.Ego.Y);
        return lead is null ? null : gap;
    }

    // The ego accelerates as asked, within what a car can do, and - where the Trajectory it follows says where it
    // is to be, to the left of the road's centreline, north on the map's frame - steers to it; the world moves on.
    public void Advance(double acceleration, double? north = null)
    {
        World.EgoAcceleration = Math.Clamp(acceleration, -MaxDeceleration, MaxAcceleration);
        World.EgoTargetY = north;
        World.Advance();
    }

    // ---- the Objects, in the form their schemas give -------------------------------------------

    private double Gaussian(double sigma)
    {
        var u1 = 1.0 - noise.NextDouble();
        var u2 = noise.NextDouble();
        return sigma * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    private static JsonObject SimpleTime(string id, long ms) => new()
    {
        ["Header"] = "OSD-STM-V1.5", ["SimpleTimeID"] = id,
        ["SimpleTimeData"] = new JsonArray(new JsonObject { ["FlagsByte"] = 3, ["StartTime"] = ms, ["EndTime"] = ms, ["AccuracyMode"] = "single", ["AccuracyPlusMinus"] = 1 })
    };

    private string Attitude(long ms, double heading, double speed)
    {
        var id = $"OSA{++attitudes:D6}";
        return new JsonObject
        {
            ["Header"] = "OSD-OSA-V1.5", ["ObjectSpatialAttitudeID"] = id, ["SpatialAttitudeTime"] = SimpleTime(id + "-T", ms),
            ["General"] = new JsonObject { ["CoordType"] = "Cartesian" },
            ["Position"] = new JsonObject
            {
                ["Header"] = "OSD-OPS-V1.5", ["PositionID"] = id + "-P", ["General"] = new JsonObject { ["CoordType"] = "Cartesian" },
                ["CartPosition"] = new JsonArray(Math.Round(odoX, 4), Math.Round(odoY, 4), 0.0),
                ["CartVelocity"] = new JsonArray(Math.Round(speed * OdometryScale * Math.Cos(heading), 4), Math.Round(speed * OdometryScale * Math.Sin(heading), 4), 0.0)
            },
            ["Orientation"] = new JsonObject
            {
                ["Header"] = "OSD-OOR-V1.5", ["OrientationID"] = id + "-O", ["Orientation"] = new JsonArray(0.0, 0.0, Math.Round(heading * 180 / Math.PI, 3))
            }
        }.ToJsonString();
    }

    private static string Gnss(long ms, double lat, double lon)
    {
        var utc = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        var nmea = Nmea.Gga(utc, lat, lon) + "\r\n";
        return new JsonObject
        {
            ["Header"] = "CAV-GNO-V2.0", ["GNSSObjectID"] = $"GNO{ms}", ["GNSSObjectTime"] = SimpleTime($"GNO{ms}-T", ms),
            ["GNSSData"] = new JsonArray(new JsonObject { ["Data"] = Convert.ToBase64String(Encoding.ASCII.GetBytes(nmea)) }),
            ["GNSSDataQualifier"] = new JsonObject
            {
                ["Header"] = "TFA-GNQ-V1.5", ["GNSSQualifierID"] = $"GNQ{ms}", ["SubTypes"] = new JsonObject(), ["Formats"] = new JsonObject { ["ContentFormat"] = "GPS" }
            }
        }.ToJsonString();
    }

    private string Frame(long ms, byte[] png, string prefix, string what, double yaw = 0) => new JsonObject
    {
        ["Header"] = "OSD-BVO-V1.5", ["BasicVisualObjectID"] = $"{prefix}{World.StepNumber:D6}",
        ["BasicVisualObjectTime"] = new JsonObject { ["Header"] = "OSD-SPT-V1.5", ["SpaceTimeID"] = $"{prefix}{World.StepNumber:D6}-ST", ["Time"] = SimpleTime($"{prefix}{World.StepNumber:D6}-T", ms),
            // Where the camera looks: its yaw from the CAV's heading.
            ["SpatialAttitude1"] = new JsonObject { ["Orientation"] = new JsonObject { ["Orientation"] = new JsonArray(0.0, 0.0, yaw) } } },
        ["BasicVisualObjectData"] = new JsonArray(new JsonObject { ["Data"] = Convert.ToBase64String(png) }),
        ["VisualQualifier"] = new JsonObject
        {
            ["Header"] = "TFA-VIQ-V1.5", ["VisualQualifierID"] = $"{prefix}-VIQ{World.StepNumber:D6}", ["SubTypes"] = new JsonObject(),
            ["Formats"] = new JsonObject { ["Content"] = new JsonObject { ["2D"] = new JsonObject { ["Static"] = "PNG" } } }
        },
        ["DescrMetadata"] = $"Simulated {what}, t = {World.Time.ToString("0.0", CultureInfo.InvariantCulture)} s"
    }.ToJsonString();
}
