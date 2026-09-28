using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mpai.Aif.Tests;

// THE FAULTS OF THE CAV SCHEMAS, corrected in D:\AI V1.1 and in V2.0 (the author,
// 2026/09/28): a schema that admits no valid instance, or requires a property it does
// not define. Each corrected schema admits an instance of what it describes, and
// refuses one without what it requires.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class CavSchemaTests
{
    private static JsonObject Time(string id) => new()
    {
        ["Header"] = "OSD-STM-V1.5", ["SimpleTimeID"] = id,
        ["SimpleTimeData"] = new JsonArray(new JsonObject { ["FlagsByte"] = 3, ["StartTime"] = 1_767_254_400_000, ["EndTime"] = 1_767_254_400_000, ["AccuracyMode"] = "single", ["AccuracyPlusMinus"] = 1 })
    };

    // V1.1 took the Time of MPAI-OSD; V2.0, in CAV, only Simple Time.
    private static JsonObject TimeOf(string version, string id) => version == "V1.1"
        ? new JsonObject { ["Header"] = "OSD-TIM-V1.5", ["TimeID"] = id, ["Data"] = "2026-01-01T08:00:00Z" }
        : Time(id);

    private static JsonObject WheelCommand(string version, bool timed)
    {
        var o = new JsonObject { ["Header"] = $"CAV-WHC-{version}", ["WheelCommandID"] = "WHC1", ["WheelID"] = "W1" };
        if (timed) o["WheelCommandTime"] = TimeOf(version, "WHC1-T");
        o["WheelCommand"] = new JsonObject { ["Angle"] = 2.5, ["SteeringMode"] = "SteerByWire" };
        return o;
    }

    private static JsonObject WheelResponse(string version, bool timed)
    {
        var o = new JsonObject { ["Header"] = $"CAV-WHR-{version}", ["WheelResponseID"] = "WHR1", ["WheelID"] = "W1" };
        if (timed) o["WheelResponseTime"] = TimeOf(version, "WHR1-T");
        o["WheelResponse"] = new JsonObject { ["WheelState"] = "Normal", ["WheelAngle"] = 2.5 };
        return o;
    }

    [Fact]
    public void FaultsCorrected()
    {
        var schemas = AIF.Metadata.PublishedSchemas.At(Repository.Schemas);
        string Check(string version, string file, JsonObject instance)
        {
            var schema = schemas[Path.GetFullPath(Path.Combine(Repository.Schemas, "CAV2", version, "data", file + ".json"))];
            using var doc = JsonDocument.Parse(instance.ToJsonString());
            lock (AIF.Metadata.PublishedSchemas.Lock) return schema.Evaluate(doc.RootElement).IsValid ? "valid" : "invalid";
        }
        var result = new Dictionary<string, string>();
        foreach (var version in new[] { "V1.1", "V2.0" })
        {
            result[$"{version} Wheel Command with its time"] = Check(version, "WheelCommand", WheelCommand(version, true));
            result[$"{version} Wheel Command without its time"] = Check(version, "WheelCommand", WheelCommand(version, false));
            result[$"{version} Wheel Response with its time"] = Check(version, "WheelResponse", WheelResponse(version, true));
            result[$"{version} Wheel Response without its time"] = Check(version, "WheelResponse", WheelResponse(version, false));
        }
        result["V1.1 AMS Data, its identifier alone"] = Check("V1.1", "AMSData", new JsonObject { ["Header"] = "CAV-AMD-V1.1", ["AMSDataID"] = "AMD1" });
        result["V1.1 AMS Data without its identifier"] = Check("V1.1", "AMSData", new JsonObject { ["Header"] = "CAV-AMD-V1.1" });
        Expected.Match("cav-schema-faults.json", result);
    }
}
