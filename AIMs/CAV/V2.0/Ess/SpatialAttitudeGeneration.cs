using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

using AIF.Controller;

namespace Mpai.Cav.Ess;

// SPATIAL ATTITUDE GENERATION (CAV-SAG-V2.0; M3229 11, M3221 3.2). From the Spatial
// Attitude the Motion Actuation Subsystem gives at its rate - odometry and inertial
// sensors: smooth, frequent, drifting - and GNSS at a lower rate - absolute, noisy,
// lost in tunnels - the ego Spatial Attitude on one frame: east, north and up in
// metres from the origin of the Offline Map, when the ESS has it - the frame of the
// AMS - and from the first GNSS fix otherwise (M3237, Step 7, the author: a frame
// anchored at a fix carries that fix's error for ever).
//
// A complementary filter: the ego position is the odometric position plus an
// offset, and each GNSS fix moves the offset towards what it says by a weight (the
// setting GnssWeight). The accuracy stated is the filter's, for the GNSS noise the
// Qualifier implies, grown by 2% of the distance run since the last fix. Before any
// fix, the frame is the odometry's, and the accuracy says it is not anchored.
public sealed class SpatialAttitudeGeneration(string instanceId, IReadOnlyDictionary<string, string> settings) : IAimProcessor, IAimRunner
{
    public const string Attitude = "OSD-OSA-V1.5", Gnss = "CAV-GNO-V2.0", Map = "OSD-BOO-V1.5";
    private const double Earth = 6_371_000, GnssSigma = 1.5, Unanchored = 1000;

    private readonly double weight = EssJson.Setting(settings, "GnssWeight", 0.1);
    private (double Lat, double Lon)? anchor;
    private bool placed;                                      // a fix has placed the CAV on the frame
    private int fixes;                                        // since it was placed
    private (double East, double North) offset;
    private (double X, double Y, double Z) lastOdometry;
    private (double X, double Y, double Z) atLastFix;
    private long count;

    public string InstanceId { get; } = instanceId;
    public Task<Message> ProcessAsync(Message message) => throw new NotSupportedException("CAV-SAG runs continuously.");

    public async Task RunAsync(IAimPorts ports, AimContext context)
    {
        while (await ports.SelectAsync(-1, (Attitude, 1), (Gnss, 1), (Map, 1)) is { } port)
        {
            if (await ports.ReadAsync(port.DataType, 1, 0) is not { } m) continue;
            var json = JsonNode.Parse(m.Json)!;
            if (port.DataType == Gnss) { Fix(json); continue; }
            if (port.DataType == Map) { Know(json); continue; }

            var odometry = EssJson.Vector(json["Position"]?["CartPosition"]) ?? (0, 0, 0);
            var velocity = EssJson.Vector(json["Position"]?["CartVelocity"]) ?? (0, 0, 0);
            var ms = EssJson.Milliseconds(json["SpatialAttitudeTime"]) ?? ports.Now.ToUnixTimeMilliseconds();
            lastOdometry = odometry;

            var run = Math.Sqrt(Math.Pow(odometry.X - atLastFix.X, 2) + Math.Pow(odometry.Y - atLastFix.Y, 2));
            var accuracy = !placed ? Unanchored : GnssSigma * Math.Sqrt(weight / (2 - weight)) + 0.02 * run;
            var ego = EssJson.Attitude($"EGO{++count:D6}", ms,
                (odometry.X + offset.East, odometry.Y + offset.North, odometry.Z), (accuracy, accuracy, accuracy), velocity, json["Orientation"]);
            await ports.WriteAsync(Attitude, 1, ego.ToJsonString());
        }
    }

    // The Offline Map: its origin anchors the frame; the next fix places the CAV on it.
    public void Know(JsonNode offlineMapObject)
    {
        try
        {
            var map = JsonNode.Parse((string)offlineMapObject["BasicOfflineMapData"]![0]!["Data"]!)!;
            anchor = ((double)map["Origin"]!["Lat"]!, (double)map["Origin"]!["Lon"]!);
            placed = false;
        }
        catch (Exception e) when (e is NullReferenceException or InvalidOperationException or FormatException or System.Text.Json.JsonException) { }
    }

    // A GNSS fix: the first places the CAV, and anchors the frame if no map has;
    // each moves the offset - the first ones by their mean (1/n), so that the
    // error of one fix is not kept, then by the weight.
    private void Fix(JsonNode gnss)
    {
        if (Position(gnss) is not { } fix) return;
        if (!placed) fixes = 0;
        placed = true;
        fixes++;
        anchor ??= fix;
        var east = (fix.Lon - anchor.Value.Lon) * Math.PI / 180 * Earth * Math.Cos(anchor.Value.Lat * Math.PI / 180);
        var north = (fix.Lat - anchor.Value.Lat) * Math.PI / 180 * Earth;
        var errorEast = east - (lastOdometry.X + offset.East);
        var errorNorth = north - (lastOdometry.Y + offset.North);
        var k = Math.Max(weight, 1.0 / fixes);                     // the first fix places the CAV
        offset = (offset.East + k * errorEast, offset.North + k * errorNorth);
        atLastFix = lastOdometry;
    }

    // Latitude and longitude from the GGA sentence of NMEA 0183 in the GNSS Data.
    public static (double Lat, double Lon)? Position(JsonNode gnss)
    {
        foreach (var entry in gnss["GNSSData"]?.AsArray() ?? [])
        {
            if (entry?["Data"]?.GetValue<string>() is not { } b64) continue;
            var text = Encoding.ASCII.GetString(Convert.FromBase64String(b64));
            foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var f = line.Split('*')[0].Split(',');
                if (!f[0].EndsWith("GGA") || f.Length < 6 || f[2] == "" || f[4] == "") continue;
                double Angle(string s, int degrees) =>
                    double.Parse(s[..degrees], CultureInfo.InvariantCulture) + double.Parse(s[degrees..], CultureInfo.InvariantCulture) / 60;
                var lat = Angle(f[2], 2) * (f[3] == "S" ? -1 : 1);
                var lon = Angle(f[4], 3) * (f[5] == "W" ? -1 : 1);
                return (lat, lon);
            }
        }
        return null;
    }
}
