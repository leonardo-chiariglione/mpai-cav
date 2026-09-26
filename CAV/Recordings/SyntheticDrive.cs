using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

using AIF.SharedStorage;

namespace Mpai.Cav.Recordings;

// A MESSAGE OF A DRIVE: when it is given, from the start of the drive, to which
// boundary Port of the Environment Sensing Subsystem, and the Object.
public sealed record DriveMessage(TimeSpan At, string DataType, int PortNumber, string Json);

// A SYNTHETIC DRIVE (M3219 3.6): the inputs of ESS Stage 1 (1CAV-ESS-V1.1-I01)
// made from a seed, where no sensor data exists - GNSS Objects, Spatial Attitude
// and the frames of a forward camera - with the ground truth they were made
// from, for Phase 7 to measure against.
//
// The ego vehicle drives east on a straight road, its speed rising and falling;
// a vehicle ahead in its lane comes nearer, then draws away. Everything follows
// from the seed and the start time: the same seed, the same drive, byte for byte.
public sealed class SyntheticDrive
{
    public const string Gnss = "CAV-GNO-V1.1", Attitude = "OSD-OSA-V1.5", Camera = "OSD-BVO-V1.5";

    // The camera: 640 x 360, a focal length of 500 pixels, 1.5 m above the road,
    // the horizon at row 140. The road three lanes of 3.5 m, the ego in the middle
    // one. At 320 x 180 a vehicle 48 m ahead was 9 pixels wide, which no detector
    // finds (M3221 3.5).
    public const int Width = CameraRenderer.Width, Height = CameraRenderer.Height;
    private const double Focal = CameraRenderer.Focal, CameraHeight = CameraRenderer.CameraHeight, Horizon = CameraRenderer.Horizon,
                         Lane = CameraRenderer.Lane, NearThreshold = 15;

    // Where the drive begins: Turin, heading east.
    private const double Lat0 = 45.0703, Lon0 = 7.6869, Earth = 6_371_000, OdometryScale = 1.015;

    public int Seed { get; }
    public TimeSpan Duration { get; }
    public DateTimeOffset Start { get; }

    public SyntheticDrive(int seed, TimeSpan duration, DateTimeOffset? start = null)
    {
        Seed = seed;
        Duration = duration;
        Start = start ?? new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
    }

    // The ego's speed (m/s) and the distance to the vehicle ahead (m) at a time.
    private double Speed(double t) => 12 + 3 * Math.Sin(2 * Math.PI * t / 20);
    private double Ahead(double t) => 30 + 18 * Math.Cos(2 * Math.PI * t / Duration.TotalSeconds);

    // A slower vehicle in the left lane, which the ego overtakes: from 55 m ahead to
    // 5 m ahead over the drive. Near, and never in the ego's path: no Alert for it.
    private double Left(double t) => 55 - 50 * t / Duration.TotalSeconds;

    public (IReadOnlyList<DriveMessage> Messages, JsonObject GroundTruth) Make()
    {
        var random = new Random(Seed);
        var grain = new Random(Seed + 1);                                    // the camera's noise, apart from the GNSS noise
        double Noise(double sigma)                                          // Box-Muller
        {
            var u1 = 1.0 - random.NextDouble();
            var u2 = random.NextDouble();
            return sigma * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }

        var messages = new List<DriveMessage>();
        var frames = new JsonArray();
        double x = 0;                                                        // metres east of the start
        const int stepsPerSecond = 50;                                       // the attitude's rate; GNSS and camera every fifth
        var steps = (int)Math.Round(Duration.TotalSeconds * stepsPerSecond);
        var nearFrom = double.NaN; var nearTo = double.NaN;

        for (var i = 0; i <= steps; i++)
        {
            var t = i / (double)stepsPerSecond;
            var at = TimeSpan.FromSeconds(t);
            var v = Speed(t);
            if (i > 0) x += v / stepsPerSecond;

            // The Motion Actuation Subsystem's odometry: its wheels read 1.5% long, so
            // its position drifts from the truth, as odometry does (M3221 3.2).
            messages.Add(new DriveMessage(at, Attitude, 1, SpatialAttitude(i, t, x * OdometryScale, v * OdometryScale)));
            if (i % 5 != 0) continue;

            var eastNoisy = x + Noise(1.5);
            var northNoisy = Noise(1.5);
            var lat = Lat0 + northNoisy / Earth * 180 / Math.PI;
            var lon = Lon0 + eastNoisy / (Earth * Math.Cos(Lat0 * Math.PI / 180)) * 180 / Math.PI;
            messages.Add(new DriveMessage(at, Gnss, 1, GnssObject(i, t, lat, lon, v)));

            var d = Ahead(t);
            var left = Left(t);
            var (boxes, png) = CameraRenderer.Render(x, [(d, 0.0, CameraRenderer.Silver), (left, -Lane, CameraRenderer.DarkRed)], grain);
            var box = boxes[0];
            messages.Add(new DriveMessage(at, Camera, 1, VisualObject(i, t, png)));

            var near = d < NearThreshold;
            if (near && double.IsNaN(nearFrom)) nearFrom = t;
            if (near) nearTo = t;
            frames.Add(new JsonObject
            {
                ["At"] = Math.Round(t, 3), ["EgoEast"] = Math.Round(x, 3), ["EgoSpeed"] = Math.Round(v, 3),
                ["GnssLat"] = Math.Round(lat, 7), ["GnssLon"] = Math.Round(lon, 7),
                ["AheadDistance"] = Math.Round(d, 3), ["Near"] = near,
                ["AheadBox"] = new JsonArray(box.X, box.Y, box.W, box.H),
                ["Vehicles"] = new JsonArray(
                    Vehicle("ahead", 0, d, boxes[0]),
                    Vehicle("left", -1, left, boxes[1]))
            });
        }

        static JsonObject Vehicle(string id, int lane, double distance, (int X, int Y, int W, int H) b) => new()
        {
            ["Id"] = id, ["Lane"] = lane, ["Distance"] = Math.Round(distance, 3), ["Box"] = new JsonArray(b.X, b.Y, b.W, b.H)
        };

        var truth = new JsonObject
        {
            ["Seed"] = Seed, ["Duration"] = Duration.TotalSeconds, ["Start"] = Start.ToString("O"),
            ["Origin"] = new JsonObject { ["Lat"] = Lat0, ["Lon"] = Lon0, ["Heading"] = "east" },
            ["OdometryScale"] = OdometryScale,
            ["Camera"] = new JsonObject { ["Width"] = Width, ["Height"] = Height, ["Focal"] = Focal, ["Height m"] = CameraHeight, ["Horizon"] = Horizon },
            ["Lanes"] = new JsonObject { ["Width"] = Lane, ["Ego"] = 0, ["Count"] = 3 },
            ["NearThreshold"] = NearThreshold,
            ["Near"] = double.IsNaN(nearFrom) ? null : new JsonObject { ["From"] = Math.Round(nearFrom, 3), ["To"] = Math.Round(nearTo, 3) },
            ["Frames"] = frames
        };
        return (messages, truth);
    }

    // ---- the Objects, in the form their schemas give ------------------------------

    private JsonObject SimpleTime(string id, double t)
    {
        var ms = (Start + TimeSpan.FromSeconds(t)).ToUnixTimeMilliseconds();
        return new JsonObject
        {
            ["Header"] = "OSD-STM-V1.5", ["SimpleTimeID"] = id,
            // Absolute (bit 0), in milliseconds (bits 1-2 = 01); made to the
            // millisecond, so accurate to one.
            ["SimpleTimeData"] = new JsonArray(new JsonObject
            {
                ["FlagsByte"] = 3, ["StartTime"] = ms, ["EndTime"] = ms, ["AccuracyMode"] = "single", ["AccuracyPlusMinus"] = 1
            })
        };
    }

    private string SpatialAttitude(int i, double t, double east, double speed) => new JsonObject
    {
        ["Header"] = "OSD-OSA-V1.5", ["ObjectSpatialAttitudeID"] = $"OSA{i:D6}",
        ["SpatialAttitudeTime"] = SimpleTime($"OSA{i:D6}-T", t),
        ["General"] = new JsonObject { ["CoordType"] = "Cartesian" },
        ["Position"] = new JsonObject
        {
            ["Header"] = "OSD-OPS-V1.5", ["PositionID"] = $"OPS{i:D6}",
            ["General"] = new JsonObject { ["CoordType"] = "Cartesian" },
            ["CartPosition"] = new JsonArray(Math.Round(east, 4), 0.0, 0.0),
            ["CartVelocity"] = new JsonArray(Math.Round(speed, 4), 0.0, 0.0)
        },
        ["Orientation"] = new JsonObject
        {
            ["Header"] = "OSD-OOR-V1.5", ["OrientationID"] = $"OOR{i:D6}",
            ["Orientation"] = new JsonArray(0.0, 0.0, 0.0)
        }
    }.ToJsonString();

    private string GnssObject(int i, double t, double lat, double lon, double speed)
    {
        var utc = Start + TimeSpan.FromSeconds(t);
        var nmea = Nmea.Gga(utc, lat, lon) + "\r\n" + Nmea.Rmc(utc, lat, lon, speed) + "\r\n";
        return new JsonObject
        {
            ["Header"] = Gnss, ["GNSSObjectID"] = $"GNO{i:D6}",
            ["GNSSObjectTime"] = SimpleTime($"GNO{i:D6}-T", t),
            ["GNSSData"] = new JsonArray(new JsonObject { ["Data"] = Convert.ToBase64String(Encoding.ASCII.GetBytes(nmea)) }),
            ["GNSSDataQualifier"] = new JsonObject
            {
                ["Header"] = "TFA-GNQ-V1.5", ["GNSSQualifierID"] = $"GNQ{i:D6}",
                ["SubTypes"] = new JsonObject(), ["Formats"] = new JsonObject { ["ContentFormat"] = "GPS" }
            }
        }.ToJsonString();
    }

    private string VisualObject(int i, double t, byte[] png) => new JsonObject
    {
        ["Header"] = Camera, ["BasicVisualObjectID"] = $"BVO{i:D6}",
        // The time of what the frame shows: the time of its capture (M3221 3.3).
        ["BasicVisualObjectTime"] = new JsonObject { ["Header"] = "OSD-SPT-V1.5", ["SpaceTimeID"] = $"BVO{i:D6}-ST", ["Time"] = SimpleTime($"BVO{i:D6}-T", t) },
        ["BasicVisualObjectData"] = new JsonArray(new JsonObject { ["Data"] = Convert.ToBase64String(png) }),
        ["VisualQualifier"] = new JsonObject
        {
            ["Header"] = "TFA-VIQ-V1.5", ["VisualQualifierID"] = $"VIQ{i:D6}",
            ["SubTypes"] = new JsonObject(),
            ["Formats"] = new JsonObject { ["Content"] = new JsonObject { ["2D"] = new JsonObject { ["Static"] = "PNG" } } }
        },
        ["DescrMetadata"] = $"Synthetic forward camera, seed {Seed}, t = {t.ToString("0.00", CultureInfo.InvariantCulture)} s"
    }.ToJsonString();

    // ---- as a record --------------------------------------------------------------

    // The drive as a record of the boundary of ESS Stage 1, in the form of the
    // records the Controller makes, readable by the User Agent: a StoredRecord
    // plays it. Its writer is the generator, and says so in its Trace.
    public string WriteRecord(IRuledStorage storage, IReadOnlyList<DriveMessage> messages, string? recordId = null)
    {
        var id = recordId ?? $"S{Start:yyyyMMdd-HHmmss}-seed{Seed}";
        long sequence = 0;
        foreach (var m in messages)
        {
            var record = new JsonObject
            {
                ["Sequence"] = ++sequence, ["Stamp"] = (Start + m.At).ToString("O"), ["SinceStart"] = m.At.TotalMilliseconds,
                ["Direction"] = "In", ["DataType"] = m.DataType, ["PortNumber"] = m.PortNumber, ["Json"] = m.Json
            };
            Check(storage.MPAI_AIFM_RuledStorage_Put($"{id}/{sequence:D9}", Encoding.UTF8.GetBytes(record.ToJsonString()), "Boundary", [RuledStore.UserAgent]));
        }
        var header = new JsonObject
        {
            ["Record"] = id, ["Module"] = "1CAV-ESS-V1.1-I01", ["Synthetic"] = true, ["Seed"] = Seed,
            ["Started"] = Start.ToString("O"), ["Stopped"] = (Start + Duration).ToString("O")
        };
        Check(storage.MPAI_AIFM_RuledStorage_Put($"{id}/header", Encoding.UTF8.GetBytes(header.ToJsonString()), "Boundary", [RuledStore.UserAgent]));
        return id;

        static void Check(StorageOutcome outcome)
        {
            if (outcome != StorageOutcome.OK) throw new InvalidOperationException($"The drive could not be written: {outcome}.");
        }
    }
}

// NMEA 0183 sentences a GNSS receiver gives: position fix (GGA) and recommended
// minimum data (RMC), each with its checksum.
public static class Nmea
{
    public static string Gga(DateTimeOffset utc, double lat, double lon) =>
        Sentence($"GPGGA,{utc:HHmmss.ff},{Angle(lat, 2)},{(lat >= 0 ? 'N' : 'S')},{Angle(lon, 3)},{(lon >= 0 ? 'E' : 'W')},1,09,0.9,239.0,M,48.0,M,,");

    public static string Rmc(DateTimeOffset utc, double lat, double lon, double speed) =>
        Sentence($"GPRMC,{utc:HHmmss.ff},A,{Angle(lat, 2)},{(lat >= 0 ? 'N' : 'S')},{Angle(lon, 3)},{(lon >= 0 ? 'E' : 'W')}," +
                 $"{(speed * 1.943844).ToString("0.00", CultureInfo.InvariantCulture)},090.0,{utc:ddMMyy},,,A");

    // Degrees and decimal minutes: ddmm.mmmmm or dddmm.mmmmm.
    private static string Angle(double degrees, int width)
    {
        var a = Math.Abs(degrees);
        var d = (int)a;
        return d.ToString(new string('0', width)) + ((a - d) * 60).ToString("00.00000", CultureInfo.InvariantCulture);
    }

    public static string Sentence(string body)
    {
        var sum = body.Aggregate(0, (c, ch) => c ^ ch);
        return $"${body}*{sum:X2}";
    }

    public static bool Valid(string sentence)
    {
        var star = sentence.LastIndexOf('*');
        return sentence.StartsWith('$') && star > 0 &&
               sentence[1..star].Aggregate(0, (c, ch) => c ^ ch).ToString("X2") == sentence[(star + 1)..].Trim();
    }
}
