using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mpai.Aif.Tests;

// THE FAULTS OF THE CAV SCHEMAS, corrected in V2.0 (the author, 2026/09/28): a schema that admits no valid instance, or requires a property it does not define.
// Each corrected schema admits an instance of what it describes, and refuses one without what it requires.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class CavSchemaTests
{
    private static JsonObject Time(string id) => new()
    {
        ["Header"] = "OSD-STM-V1.5", ["SimpleTimeID"] = id,
        ["SimpleTimeData"] = new JsonArray(new JsonObject { ["FlagsByte"] = 3, ["StartTime"] = 1_767_254_400_000, ["EndTime"] = 1_767_254_400_000, ["AccuracyMode"] = "single", ["AccuracyPlusMinus"] = 1 })
    };

    private static JsonObject WheelCommand(bool timed)
    {
        var o = new JsonObject { ["Header"] = "CAV-WHC-V2.0", ["WheelCommandID"] = "WHC1", ["WheelID"] = "W1" };
        if (timed) o["WheelCommandTime"] = Time("WHC1-T");
        o["WheelCommand"] = new JsonObject { ["Angle"] = 2.5, ["SteeringMode"] = "SteerByWire" };
        return o;
    }

    private static JsonObject WheelResponse(bool timed)
    {
        var o = new JsonObject { ["Header"] = "CAV-WHR-V2.0", ["WheelResponseID"] = "WHR1", ["WheelID"] = "W1" };
        if (timed) o["WheelResponseTime"] = Time("WHR1-T");
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
        var result = new Dictionary<string, string>
        {
            ["V2.0 Wheel Command with its time"] = Check("V2.0", "WheelCommand", WheelCommand(true)),
            ["V2.0 Wheel Command without its time"] = Check("V2.0", "WheelCommand", WheelCommand(false)),
            ["V2.0 Wheel Response with its time"] = Check("V2.0", "WheelResponse", WheelResponse(true)),
            ["V2.0 Wheel Response without its time"] = Check("V2.0", "WheelResponse", WheelResponse(false)),
            ["V2.0 AMS Data, its identifier alone"] = Check("V2.0", "AMSData", new JsonObject { ["Header"] = "CAV-AMD-V2.0", ["AMSDataID"] = "AMD1" }),
            ["V2.0 AMS Data without its identifier"] = Check("V2.0", "AMSData", new JsonObject { ["Header"] = "CAV-AMD-V2.0" })
        };
        Expected.Match("cav-schema-faults.json", result);
    }
}
