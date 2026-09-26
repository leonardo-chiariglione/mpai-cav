using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Mpai.Cav.Recordings;

// A VEHICLE OF A SCENARIO: its lane relative to the ego's (+1 left, -1 right), where
// it starts along the ego's Route, and its speed: the target speed of each time
// from which it holds, reached within its acceleration and deceleration.
public sealed record ScenarioVehicle(string Id, int Lane, double Start, IReadOnlyList<(double From, double Speed)> Speeds, (byte R, byte G, byte B) Paint);

// THE STEPPED SIMULATION OF THE CAV'S WORLD (M3233 3.1). A map, the ego on a Route
// on it, other vehicles on the same Route in their lanes. At each step - 0.1 s of
// simulated time - Sense gives what the CAV's sensors capture: five Spatial
// Attitudes of the Motion Actuation Subsystem (odometry reading 1.5% long), a GNSS
// fix (1.5 m of noise) and a camera frame, rendered from where everything is; and
// the ground truth of the step. Advance then moves the ego as the CAV asks - an
// acceleration, within what a car can do - and every other vehicle as its scenario
// says. Nothing runs against a clock: a step lasts as long as the CAV takes.
public sealed class Simulation
{
    public const double Step = 0.1, MaxAcceleration = 3, MaxDeceleration = 8, VehicleLength = 4.5;
    private const int AttitudesPerStep = 5;
    private const double OdometryScale = 1.015, GnssSigma = 1.5;

    public RoadMap Map { get; }
    public RoutePath Path { get; }
    public DateTimeOffset Start { get; }
    public int StepNumber { get; private set; }
    public double Time => StepNumber * Step;
    public double EgoS { get; private set; }
    public double EgoSpeed { get; private set; }
    public double EgoAcceleration { get; private set; }
    public bool Collided { get; private set; }

    private readonly List<(ScenarioVehicle Spec, double S, double Speed)> others;
    private readonly Random noise, grain;
    private double odoX, odoY;
    private long attitudes;

    public Simulation(RoadMap map, IReadOnlyList<RoadSegment> route, IReadOnlyList<ScenarioVehicle> vehicles, int seed, double egoSpeed = 0, DateTimeOffset? start = null)
    {
        Map = map; Path = new RoutePath(map, route);
        Start = start ?? new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
        EgoSpeed = egoSpeed;
        others = vehicles.Select(v => (v, v.Start, SpeedOf(v, 0))).ToList();
        noise = new Random(seed); grain = new Random(seed + 1);
    }

    public bool Arrived => EgoS >= Path.Length - 0.5;

    private static double SpeedOf(ScenarioVehicle v, double t) => v.Speeds.Last(p => p.From <= t || p == v.Speeds[0]).Speed;

    // ---- what the sensors capture ------------------------------------------------

    public sealed record Sensed(IReadOnlyList<(string DataType, string Json)> Messages, JsonObject Truth, long FrameMs);

    public Sensed Sense()
    {
        var messages = new List<(string, string)>();
        var ms0 = (Start + TimeSpan.FromSeconds(Time)).ToUnixTimeMilliseconds();
        var heading = Path.At(EgoS).Heading;
        for (var k = 0; k < AttitudesPerStep; k++)
        {
            var dt = Step / AttitudesPerStep;
            if (StepNumber > 0 || k > 0) { odoX += EgoSpeed * OdometryScale * Math.Cos(heading) * dt; odoY += EgoSpeed * OdometryScale * Math.Sin(heading) * dt; }
            var ms = ms0 - (long)((AttitudesPerStep - 1 - k) * dt * 1000);
            messages.Add(("OSD-OSA-V1.5", Attitude(ms, heading)));
        }

        var (east, north, _, segment) = Path.At(EgoS);
        var (lat, lon) = Map.Geodetic(east + Gaussian(GnssSigma), north + Gaussian(GnssSigma));
        messages.Add(("CAV-GNO-V1.1", Gnss(ms0, lat, lon)));

        // Positions are of each vehicle's centre; the camera is at the ego's front, so
        // it sees another's rear at the distance between centres less a length -
        // for a vehicle in the ego's lane, the gap between them.
        var inView = others.Select(o => (o.Spec, Ahead: o.S - EgoS - VehicleLength)).Where(o => o.Ahead > 2 && o.Ahead < 90).ToList();
        var (boxes, png) = CameraRenderer.Render(EgoS, inView.Select(o => (o.Ahead, -o.Spec.Lane * RoadMap.LaneWidth, o.Spec.Paint)).ToArray(), grain);
        messages.Add(("OSD-BVO-V1.5", Frame(ms0, png)));

        var vehicles = new JsonArray();
        for (var i = 0; i < inView.Count; i++)
            vehicles.Add(new JsonObject
            {
                ["Id"] = inView[i].Spec.Id, ["Lane"] = inView[i].Spec.Lane, ["Distance"] = Math.Round(inView[i].Ahead, 3),
                ["Speed"] = Math.Round(others.First(o => o.Spec == inView[i].Spec).Speed, 3),
                ["Box"] = new JsonArray(boxes[i].X, boxes[i].Y, boxes[i].W, boxes[i].H)
            });
        var truth = new JsonObject
        {
            ["Step"] = StepNumber, ["At"] = Math.Round(Time, 3), ["Ms"] = ms0,
            ["Ego"] = new JsonObject
            {
                ["S"] = Math.Round(EgoS, 3), ["Speed"] = Math.Round(EgoSpeed, 3), ["Acceleration"] = Math.Round(EgoAcceleration, 3),
                ["East"] = Math.Round(east, 3), ["North"] = Math.Round(north, 3), ["Segment"] = segment.Id, ["SpeedLimit"] = segment.SpeedLimit
            },
            ["Gap"] = GapAhead() is { } g ? Math.Round(g, 3) : null,
            ["Collided"] = Collided,
            ["Vehicles"] = vehicles
        };
        return new Sensed(messages, truth, ms0);
    }

    // The gap to the nearest vehicle ahead in the ego's lane, bumper to bumper.
    public double? GapAhead()
    {
        var ahead = others.Where(o => o.Spec.Lane == 0 && o.S > EgoS).Select(o => o.S - EgoS - VehicleLength).ToList();
        return ahead.Count == 0 ? null : ahead.Min();
    }

    // ---- moving on ---------------------------------------------------------------

    // The ego accelerates as asked, within what a car can do, and stops at the end
    // of its Route; every other vehicle moves towards the speed of its scenario.
    public void Advance(double acceleration)
    {
        EgoAcceleration = Math.Clamp(acceleration, -MaxDeceleration, MaxAcceleration);
        var speed = Math.Max(0, EgoSpeed + EgoAcceleration * Step);
        EgoS = Math.Min(Path.Length, EgoS + (EgoSpeed + speed) / 2 * Step);
        EgoSpeed = EgoS >= Path.Length ? 0 : speed;
        StepNumber++;
        for (var i = 0; i < others.Count; i++)
        {
            var (spec, s, v) = others[i];
            var target = SpeedOf(spec, Time);
            var next = target > v ? Math.Min(target, v + MaxAcceleration * Step) : Math.Max(target, v - MaxDeceleration * Step);
            others[i] = (spec, s + (v + next) / 2 * Step, next);
        }
        if (GapAhead() is { } gap && gap < 0) Collided = true;
    }

    // ---- the Objects, in the form their schemas give -----------------------------

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

    private string Attitude(long ms, double heading)
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
                ["CartVelocity"] = new JsonArray(Math.Round(EgoSpeed * OdometryScale * Math.Cos(heading), 4), Math.Round(EgoSpeed * OdometryScale * Math.Sin(heading), 4), 0.0)
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
            ["Header"] = "CAV-GNO-V1.1", ["GNSSObjectID"] = $"GNO{ms}", ["GNSSObjectTime"] = SimpleTime($"GNO{ms}-T", ms),
            ["GNSSData"] = new JsonArray(new JsonObject { ["Data"] = Convert.ToBase64String(Encoding.ASCII.GetBytes(nmea)) }),
            ["GNSSDataQualifier"] = new JsonObject
            {
                ["Header"] = "TFA-GNQ-V1.5", ["GNSSQualifierID"] = $"GNQ{ms}", ["SubTypes"] = new JsonObject(), ["Formats"] = new JsonObject { ["ContentFormat"] = "GPS" }
            }
        }.ToJsonString();
    }

    private string Frame(long ms, byte[] png) => new JsonObject
    {
        ["Header"] = "OSD-BVO-V1.5", ["BasicVisualObjectID"] = $"BVO{StepNumber:D6}",
        ["BasicVisualObjectTime"] = new JsonObject { ["Header"] = "OSD-SPT-V1.5", ["SpaceTimeID"] = $"BVO{StepNumber:D6}-ST", ["Time"] = SimpleTime($"BVO{StepNumber:D6}-T", ms) },
        ["BasicVisualObjectData"] = new JsonArray(new JsonObject { ["Data"] = Convert.ToBase64String(png) }),
        ["VisualQualifier"] = new JsonObject
        {
            ["Header"] = "TFA-VIQ-V1.5", ["VisualQualifierID"] = $"VIQ{StepNumber:D6}", ["SubTypes"] = new JsonObject(),
            ["Formats"] = new JsonObject { ["Content"] = new JsonObject { ["2D"] = new JsonObject { ["Static"] = "PNG" } } }
        },
        ["DescrMetadata"] = $"Simulated forward camera, t = {Time.ToString("0.0", CultureInfo.InvariantCulture)} s"
    }.ToJsonString();
}
