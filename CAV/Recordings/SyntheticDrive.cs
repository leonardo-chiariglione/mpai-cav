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
    public const int Width = 640, Height = 360;
    private const double Focal = 500, CameraHeight = 1.5, Horizon = 140, Lane = 3.5, RoadHalf = 1.5 * Lane, NearThreshold = 15;

    // Where the drive begins: Turin, heading east.
    private const double Lat0 = 45.0703, Lon0 = 7.6869, Earth = 6_371_000;

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

            messages.Add(new DriveMessage(at, Attitude, 1, SpatialAttitude(i, t, x, v)));
            if (i % 5 != 0) continue;

            var eastNoisy = x + Noise(1.5);
            var northNoisy = Noise(1.5);
            var lat = Lat0 + northNoisy / Earth * 180 / Math.PI;
            var lon = Lon0 + eastNoisy / (Earth * Math.Cos(Lat0 * Math.PI / 180)) * 180 / Math.PI;
            messages.Add(new DriveMessage(at, Gnss, 1, GnssObject(i, t, lat, lon, v)));

            var d = Ahead(t);
            var left = Left(t);
            var (boxes, png) = Frame(x, [(d, 0.0, Silver), (left, -Lane, DarkRed)], grain);
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
        ["BasicVisualObjectData"] = new JsonArray(new JsonObject { ["Data"] = Convert.ToBase64String(png) }),
        ["VisualQualifier"] = new JsonObject
        {
            ["Header"] = "TFA-VIQ-V1.5", ["VisualQualifierID"] = $"VIQ{i:D6}",
            ["SubTypes"] = new JsonObject(),
            ["Formats"] = new JsonObject { ["Content"] = new JsonObject { ["2D"] = new JsonObject { ["Static"] = "PNG" } } }
        },
        ["DescrMetadata"] = $"Synthetic forward camera, seed {Seed}, t = {t.ToString("0.00", CultureInfo.InvariantCulture)} s"
    }.ToJsonString();

    // ---- the camera --------------------------------------------------------------

    private static readonly (byte R, byte G, byte B) Silver = (150, 150, 158), DarkRed = (120, 30, 35);

    // What a pinhole camera 1.5 m above the road sees: sky, grass, three lanes and
    // their lines, dashes moving with the ego, and the rear of each vehicle - 1.8 m
    // wide, 1.45 m high - at its distance and lateral offset, shaded as light falls
    // on a car, the farthest drawn first. Then the grain and the blur of a real
    // camera, without which a detector sees a drawing. Each vehicle's box on the
    // image is returned, in the order given; an empty box where it is not in view.
    // A detector takes the rear drawn so for a car from 12 m to 35 m; nearer, for
    // a bench (M3221 Step 1): the drives keep the vehicle ahead beyond 12 m.
    private ((int X, int Y, int W, int H)[] Boxes, byte[] Png) Frame(double odometer, (double Distance, double Lateral, (byte R, byte G, byte B) Paint)[] vehicles, Random grain)
    {
        var rgb = new byte[Width * Height * 3];
        void Put(int px, int py, int r, int g, int b)
        {
            if (px < 0 || px >= Width || py < 0 || py >= Height) return;
            var at = (py * Width + px) * 3;
            rgb[at] = (byte)Math.Clamp(r, 0, 255); rgb[at + 1] = (byte)Math.Clamp(g, 0, 255); rgb[at + 2] = (byte)Math.Clamp(b, 0, 255);
        }

        for (var y = 0; y < Height; y++)
        {
            if (y <= Horizon) { for (var px = 0; px < Width; px++) Put(px, y, 150 - y / 3, 190 - y / 4, 235); continue; }
            var z = Focal * CameraHeight / (y - Horizon);                      // the distance this row shows
            var half = Focal * RoadHalf / z;
            var laneHalf = Focal * Lane / 2 / z;
            var line = Math.Max(1, Focal * 0.15 / z);
            var dash = ((z + odometer) % 6) < 3;
            for (var px = 0; px < Width; px++)
            {
                var off = Math.Abs(px - Width / 2.0);
                if (off > half) Put(px, y, 70, 130, 60);                                            // grass
                else if (off > half - line) Put(px, y, 235, 235, 235);                              // edge lines
                else if (dash && Math.Abs(off - laneHalf) < line / 2) Put(px, y, 235, 235, 235);    // lane lines, dashed
                else { var n = 85 + (px * 7 + y * 13) % 9; Put(px, y, n, n, n); }                   // asphalt
            }
        }

        var boxes = new (int X, int Y, int W, int H)[vehicles.Length];
        foreach (var i in Enumerable.Range(0, vehicles.Length).OrderByDescending(i => vehicles[i].Distance))
        {
            var (d, lateral, paint) = vehicles[i];
            if (d < 2) continue;
            var s = Focal / d;
            int cw = (int)Math.Round(1.8 * s), ch = (int)Math.Round(1.45 * s);
            int bottom = (int)Math.Round(Horizon + Focal * CameraHeight / d);
            int left = (int)Math.Round(Width / 2.0 + Focal * lateral / d) - cw / 2, top = bottom - ch;
            boxes[i] = (left, top, cw, ch);
            Car(left, top, cw, ch, paint, Put);
        }

        // Grain, then a blur of 3 x 3.
        for (var k = 0; k < rgb.Length; k += 3)
        {
            var n = grain.Next(-3, 4);
            for (var c = 0; c < 3; c++) rgb[k + c] = (byte)Math.Clamp(rgb[k + c] + n, 0, 255);
        }
        var blurred = new byte[rgb.Length];
        for (var y = 0; y < Height; y++)
            for (var px = 0; px < Width; px++)
                for (var c = 0; c < 3; c++)
                {
                    int sum = 0, weight = 0;
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            int yy = y + dy, xx = px + dx;
                            if (yy < 0 || yy >= Height || xx < 0 || xx >= Width) continue;
                            var w = dx == 0 && dy == 0 ? 4 : (dx == 0 || dy == 0 ? 2 : 1);
                            sum += rgb[(yy * Width + xx) * 3 + c] * w; weight += w;
                        }
                    blurred[(y * Width + px) * 3 + c] = (byte)(sum / weight);
                }
        return (boxes, Png.Encode(Width, Height, blurred));
    }

    // The rear of a car in its box: shadow, tyres, a body shaded from light above,
    // the cabin tapering to the roof, the rear window with a reflection, lights,
    // indicators, the plate, the bumper, the lines of the trunk and the shoulder.
    private static void Car(int left, int top, int cw, int ch, (byte R, byte G, byte B) paint, Action<int, int, int, int, int> put)
    {
        void Fill(Func<double, double, bool> inside, Func<double, double, (double R, double G, double B)> colour)
        {
            for (var y = top - 2; y < top + ch + ch / 10 + 2; y++)
                for (var x = left - cw / 10; x < left + cw + cw / 10; x++)
                {
                    double u = (x - left + 0.5) / cw, v = (y - top + 0.5) / ch;
                    if (!inside(u, v)) continue;
                    var (r, g, b) = colour(u, v);
                    put(x, y, (int)r, (int)g, (int)b);
                }
        }
        static (double, double, double) Shade((byte R, byte G, byte B) c, double k) => (c.R * k, c.G * k, c.B * k);
        static (double, double, double) Plain(int r, int g, int b) => (r, g, b);
        (byte, byte, byte) glass = (40, 55, 70), bumper = (60, 60, 64);

        Fill((u, v) => v > 0.93 && v < 1.06 && u > -0.06 && u < 1.06, (u, v) => Plain(35, 35, 38));                                   // shadow
        Fill((u, v) => v > 0.8 && v < 1.0 && ((u > 0.04 && u < 0.2) || (u > 0.8 && u < 0.96)), (u, v) => Plain(22, 22, 24));           // tyres
        Fill((u, v) => v > 0.38 && v < 0.88 && u > 0 && u < 1, (u, v) => Shade(paint, 1.15 - 0.45 * v - 0.25 * Math.Abs(u - 0.5)));   // body
        Fill((u, v) => v >= 0 && v <= 0.38 && u > 0.15 - 0.1 * v / 0.38 && u < 0.85 + 0.1 * v / 0.38, (u, v) => Shade(paint, 1.2 - 0.3 * v)); // cabin
        Fill((u, v) => v > 0.06 && v < 0.34 && u > 0.2 - 0.08 * v / 0.34 && u < 0.8 + 0.08 * v / 0.34,
             (u, v) => Shade(glass, 0.7 + 0.9 * (1 - v) * (u < 0.45 ? 1.0 : 0.6)));                                                     // rear window
        Fill((u, v) => v > 0.42 && v < 0.55 && ((u > 0.02 && u < 0.24) || (u > 0.76 && u < 0.98)), (u, v) => Plain(190, 25, 30));      // lights
        Fill((u, v) => v > 0.45 && v < 0.5 && ((u > 0.04 && u < 0.1) || (u > 0.9 && u < 0.96)), (u, v) => Plain(250, 150, 60));        // indicators
        Fill((u, v) => v > 0.62 && v < 0.72 && u > 0.39 && u < 0.61, (u, v) => Plain(235, 235, 225));                                   // plate
        Fill((u, v) => v > 0.75 && v < 0.88 && u > 0 && u < 1, (u, v) => Shade(bumper, 1.1 - 0.4 * (v - 0.75) / 0.13));                // bumper
        Fill((u, v) => Math.Abs(v - 0.58) < 0.012 && u > 0.05 && u < 0.95, (u, v) => Shade(paint, 0.55));                              // trunk line
        Fill((u, v) => Math.Abs(v - 0.38) < 0.01 && u > 0.08 && u < 0.92, (u, v) => Shade(paint, 0.6));                                // shoulder line
        Fill((u, v) => v > 0.02 && v < 0.05 && u > 0.35 && u < 0.65, (u, v) => Plain(160, 20, 25));                                    // third brake light
    }

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
