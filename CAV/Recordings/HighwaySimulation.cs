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

    private readonly Random noise, grain;
    private double odoX, odoY;
    private int attitudes;

    public HighwayWorld World { get; }
    public RoadMap Map { get; }
    public RigCamera Camera { get; }                 // the camera whose pictures go to the ESS
    public DateTimeOffset Start { get; }
    public double SpeedLimit { get; }                // m/s
    public double EgoSpeed => World.Ego.Speed;
    public bool Collided => World.EgoCollisions.Count > 0;
    public bool Arrived => World.Ego.X >= 59_950;

    public HighwaySimulation(HighwayWorld world, double speedLimit = 30, int cameraReduction = 3, int seed = 1, DateTimeOffset? start = null)
    {
        World = world; SpeedLimit = speedLimit;
        Camera = HighwayRig.Standard(cameraReduction, cameraReduction)[0];
        Map = HighwayMap(speedLimit);
        Start = start ?? new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
        noise = new Random(seed * 7919 + 1); grain = new Random(seed * 104729 + 3);
    }

    // The Offline Map: a way point at each end of one road, east, of one lane (the lane the CAV is in: the
    // AMS keeps to the centreline of its road), the speed limit in m/s.
    public static RoadMap HighwayMap(double speedLimit, double length = 60_000) =>
        new("Highway", 45.0703, 7.6869, [new WayPoint("H0", 0, 0, "Start"), new WayPoint("H1", length, 0, "End")],
            [new RoadSegment("H01", "H0", "H1", speedLimit, 1)]);

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

    // ---- what the sensors capture --------------------------------------------------------------

    public Simulation.Sensed Sense()
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

        var frame = HighwayCameras.Capture(Camera, ego, World.Others, grain);
        messages.Add(("OSD-BVO-V1.5", Frame(ms0, frame.Png)));

        var vehicles = new JsonArray();
        foreach (var v in World.Others.OrderBy(v => v.X))
        {
            var ahead = (v.X - v.Length / 2) - (ego.X + ego.Length / 2);
            if (ahead < -60 || ahead > 250) continue;
            vehicles.Add(new JsonObject
            {
                ["Id"] = v.Id, ["Lane"] = v.Lane - ego.Lane, ["Distance"] = Math.Round(ahead, 3), ["Speed"] = Math.Round(v.Speed, 3), ["Truck"] = v.Truck
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

    // The ego accelerates as asked, within what a car can do, and the world moves on.
    public void Advance(double acceleration)
    {
        World.EgoAcceleration = Math.Clamp(acceleration, -MaxDeceleration, MaxAcceleration);
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

    private string Frame(long ms, byte[] png) => new JsonObject
    {
        ["Header"] = "OSD-BVO-V1.5", ["BasicVisualObjectID"] = $"BVO{World.StepNumber:D6}",
        ["BasicVisualObjectTime"] = new JsonObject { ["Header"] = "OSD-SPT-V1.5", ["SpaceTimeID"] = $"BVO{World.StepNumber:D6}-ST", ["Time"] = SimpleTime($"BVO{World.StepNumber:D6}-T", ms) },
        ["BasicVisualObjectData"] = new JsonArray(new JsonObject { ["Data"] = Convert.ToBase64String(png) }),
        ["VisualQualifier"] = new JsonObject
        {
            ["Header"] = "TFA-VIQ-V1.5", ["VisualQualifierID"] = $"VIQ{World.StepNumber:D6}", ["SubTypes"] = new JsonObject(),
            ["Formats"] = new JsonObject { ["Content"] = new JsonObject { ["2D"] = new JsonObject { ["Static"] = "PNG" } } }
        },
        ["DescrMetadata"] = $"Simulated front camera {Camera.Name}, t = {World.Time.ToString("0.0", CultureInfo.InvariantCulture)} s"
    }.ToJsonString();
}
