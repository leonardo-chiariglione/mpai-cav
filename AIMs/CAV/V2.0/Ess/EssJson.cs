using System.Globalization;
using System.Text.Json.Nodes;

namespace Mpai.Cav.Ess;

// THE DATA TYPES OF THE ESS AS JSON, in the form their schemas give: Simple Time,
// Space-Time, Spatial Attitude. One place, so that every AIM of the Subsystem
// writes them alike.
public static class EssJson
{
    // A time in milliseconds since the Unix epoch, as Simple Time: absolute (bit 0),
    // in milliseconds (bits 1-2 = 01), accurate to one.
    public static JsonObject SimpleTime(string id, long ms) => new()
    {
        ["Header"] = "OSD-STM-V1.5", ["SimpleTimeID"] = id,
        ["SimpleTimeData"] = new JsonArray(new JsonObject
        {
            ["FlagsByte"] = 3, ["StartTime"] = ms, ["EndTime"] = ms, ["AccuracyMode"] = "single", ["AccuracyPlusMinus"] = 1
        })
    };

    // The milliseconds of a Simple Time - the CAV's only time; null where there is none.
    // A Simple Time read from text holds a number; one built in this process, the long
    // it was built with.
    public static long? Milliseconds(JsonNode? simpleTime) =>
        simpleTime?["SimpleTimeData"]?[0]?["StartTime"] is not JsonValue v ? null
        : v.TryGetValue<long>(out var l) ? l
        : v.TryGetValue<double>(out var d) ? (long)d
        : v.TryGetValue<int>(out var i) ? i
        : null;

    public static JsonObject SpaceTime(string id, long ms, JsonObject? attitude = null)
    {
        var o = new JsonObject { ["Header"] = "OSD-SPT-V1.5", ["SpaceTimeID"] = id };
        if (attitude is not null) o["SpatialAttitude1"] = attitude;
        o["Time"] = SimpleTime(id + "-T", ms);
        return o;
    }

    // ISO 8855 (the author, 2026-11-11). An Orientation of the CAV is (yaw, pitch, roll), in degrees: intrinsic rotations about Z, then the turned Y', then the twice-turned
    // X''. The frame is right-handed and East-North-Up, with the origin of the Offline Map; the vehicle's axes are X forward, Y left, Z up. A positive yaw turns left
    // (anticlockwise from East: a velocity (cos yaw, sin yaw) East-North), a positive pitch puts the nose down, a positive roll lowers the right side. Every Spatial
    // Attitude of the CAV says its frame in its Spatial Qualifier (TFA-SAQ-V1.5): EnuQualifier or EgoQualifier below.
    public static JsonArray YawPitchRoll(double yaw, double pitch = 0, double roll = 0) => new(Angle(yaw), Math.Round(pitch, 3), Angle(roll));

    // An angle in degrees, rounded, in (-180, 180] - the range of yaw and roll in OSD Orientation.
    public static double Angle(double degrees)
    {
        var a = Math.Round(Math.IEEERemainder(degrees, 360), 3);
        return a <= -180 ? a + 360 : a;
    }

    // The yaw (degrees) of an Orientation, or of the Orientation of a Point of View: its first angle.
    public static double? Yaw(JsonNode? orientation) => orientation is JsonArray { Count: 3 } a && a[0] is not null ? (double)a[0]! : null;

    // The Object whose frame is the ego frame: the CAV itself (the ObjectID of its own Spatial Attitude).
    public const string EgoObjectID = "EGO";

    // THE FRAMES OF THE CAV, as Spatial Qualifiers (TFA-SAQ-V1.5), all with the axes and angles of ISO 8855:
    //   - East-North-Up, from an origin - the Offline Map's (the ego's Spatial Attitude, the AMS trajectories), or where the CAV was when the MAS started;
    //     its geodetic point when it is known (the map anchor);
    //   - the ego frame: X ahead, Y left, Z up, from the CAV (the objects the ESS and the AMS describe around it).
    public static JsonObject EnuQualifier(string origin, (double Lat, double Lon)? geodetic = null)
    {
        var frame = new JsonObject { ["Name"] = "Local ENU", ["Origin"] = origin };
        if (geodetic is { } g) frame["OriginGeodetic"] = new JsonArray(Math.Round(g.Lat, 7), Math.Round(g.Lon, 7));
        return Qualifier("ISO8855-ENU", frame);
    }

    public static JsonObject EgoQualifier() =>
        Qualifier("ISO8855-EGO", new JsonObject { ["Name"] = "Ego", ["Origin"] = "the CAV", ["OriginObjectID"] = EgoObjectID });

    private static JsonObject Qualifier(string id, JsonObject frame) => new()
    {
        ["Header"] = "TFA-SAQ-V1.5", ["SpatialQualifierID"] = id, ["SubTypes"] = new JsonObject { ["Kind"] = "SpatialAttitude" },
        ["Formats"] = new JsonObject { ["PositionFormat"] = "Cartesian", ["OrientationFormat"] = "EulerAngles" },
        ["Attributes"] = new JsonObject
        {
            ["ReferenceFrame"] = frame,
            ["Handedness"] = "Right", ["UpAxis"] = "+Z", ["ForwardAxis"] = "+X", ["LengthUnit"] = "m", ["AngleUnit"] = "degree", ["PositiveRotation"] = "Anticlockwise",
            ["Euler"] = new JsonObject { ["Order"] = "ZYX", ["Rotations"] = "Intrinsic" }
        }
    };

    // A Spatial Attitude on a Cartesian frame: position, its accuracy, velocity; the orientation as ISO 8855 says it.
    public static JsonObject Attitude(string id, long ms, (double X, double Y, double Z) position, (double X, double Y, double Z) accuracy,
                                      (double X, double Y, double Z) velocity, JsonNode? orientation = null, JsonObject? qualifier = null) => new JsonObject
    {
        ["Header"] = "OSD-OSA-V1.5", ["ObjectSpatialAttitudeID"] = id,
        ["SpatialAttitudeTime"] = SimpleTime(id + "-T", ms),
        ["General"] = new JsonObject { ["CoordType"] = "Cartesian" },
        ["Position"] = new JsonObject
        {
            ["Header"] = "OSD-OPS-V1.5", ["PositionID"] = id + "-P",
            ["General"] = new JsonObject { ["CoordType"] = "Cartesian" },
            ["CartPosition"] = Triple(position), ["CartPositionAccuracy"] = Triple(accuracy), ["CartVelocity"] = Triple(velocity)
        },
        ["Orientation"] = orientation?.DeepClone() ?? new JsonObject
        {
            ["Header"] = "OSD-OOR-V1.5", ["OrientationID"] = id + "-O", ["Orientation"] = YawPitchRoll(0)
        },
    }.WithIf("SpatialQualifier", qualifier);

    // What an object is: its class against the taxonomy of the detector's classes
    // (COCO), with its confidence.
    public static JsonObject Identifier(string label, double confidence) => new()
    {
        ["Header"] = "OSD-IID-V1.5", ["MInstanceID"] = "", ["InstanceIdentifier"] = label,
        ["InstanceIdentifierData"] = new JsonArray(new JsonObject
        {
            ["InstanceLabel"] = label, ["LabelConfidenceLevel"] = Math.Round(confidence, 3),
            ["Taxonomy"] = new JsonObject { ["TaxonomyLevelIDs"] = new JsonArray("COCO", label), ["TaxonomyDataURI"] = "https://cocodataset.org/#explore" }
        })
    };

    // The object with the property, if there is a value for it: none, where there is none (a property that is absent is not a null).
    public static JsonObject WithIf(this JsonObject o, string name, JsonNode? value)
    {
        if (value is not null) o[name] = value;
        return o;
    }

    public static JsonObject WithIf(this JsonObject o, string name, string? value)
    {
        if (value is not null) o[name] = value;
        return o;
    }

    public static JsonArray Triple((double X, double Y, double Z) v) => new(Math.Round(v.X, 4), Math.Round(v.Y, 4), Math.Round(v.Z, 4));

    public static (double X, double Y, double Z)? Vector(JsonNode? array) =>
        array is JsonArray { Count: 3 } a ? ((double)a[0]!, (double)a[1]!, (double)a[2]!) : null;

    public static string Number(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static double Setting(IReadOnlyDictionary<string, string> settings, string name, double fallback) =>
        settings.TryGetValue(name, out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
