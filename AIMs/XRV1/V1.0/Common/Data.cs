using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Mpai.Xrv;

// TYPED ACCESS TO THE DATA TYPES. Each wrapper holds the datum as JSON, as it was read and unchanged, validates it against the schema of its Data Type, and gives the parts the
// Cue engine needs as C# values. A datum that is not valid is not read.
public abstract class XrvData
{
    public JsonObject Json { get; }
    protected XrvData(JsonObject json) => Json = json;

    public abstract string Header { get; }
    public abstract string SchemaFile { get; }

    public string? EnvironmentId => Json["UEnvironmentID"]?.GetValue<string>();

    public IReadOnlyList<string> Violations()
    {
        var found = new List<string>();
        if (Json["Header"]?.GetValue<string>() != Header) found.Add($"Header is not {Header}");
        found.AddRange(XrvSchemas.Violations(SchemaFile, Json));
        return found;
    }

    // The compact JSON of the datum: what was read, with nothing added or taken away.
    public string ToJson() => Json.ToJsonString();

    public static T Read<T>(string json, Func<JsonObject, T> make) where T : XrvData
    {
        var datum = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("the datum is not a JSON object");
        var typed = make(datum);
        var violations = typed.Violations();
        if (violations.Count > 0) throw new FormatException($"not a valid {typed.Header}: {string.Join("; ", violations)}");
        return typed;
    }

    protected static IReadOnlyList<string> Strings(JsonNode? array) => array is JsonArray a ? a.Select(x => x!.GetValue<string>()).ToList() : [];
}

public sealed class ParticipantData(JsonObject json) : XrvData(json)
{
    public override string Header => Types.ParticipantData;
    public override string SchemaFile => "XRV1/V1.0/data/ParticipantData.json";
    public string Id => Json["ParticipantDataID"]!.GetValue<string>();
    public string? ParticipantId => Json["ParticipantID"]?.GetValue<string>();
    public static ParticipantData Read(string json) => Read(json, j => new ParticipantData(j));
}

public sealed class ActionTable(JsonObject json) : XrvData(json)
{
    public override string Header => Types.ActionTable;
    public override string SchemaFile => "XRV1/V1.0/data/ActionTable.json";
    public IReadOnlyList<string> ActionIds => (Json["ActionTableData"] as JsonArray)?.Select(a => a!["ActionID"]!.GetValue<string>()).ToList() ?? [];
    public bool Has(string actionId) => ActionIds.Contains(actionId);
    // The commands of an Action (ActionCommand and ActionData, left open: they conform to the Annotation of the Venue element they address).
    public JsonArray? Commands(string actionId) => (Json["ActionTableData"] as JsonArray)?.FirstOrDefault(a => a!["ActionID"]!.GetValue<string>() == actionId)?["Action"] as JsonArray;
    public static ActionTable Read(string json) => Read(json, j => new ActionTable(j));
}

public sealed class ScriptVenueAssociations(JsonObject json) : XrvData(json)
{
    public override string Header => Types.ScriptVenueAssociations;
    public override string SchemaFile => "XRV1/V1.0/data/ScriptVenueAssociations.json";
    // The Action Elements of one side: the Action IDs with the sink Qualifier of the Venue element they address.
    public IReadOnlyList<(string ActionId, string? Sink)> ActionElements(bool re) =>
        ((Json[re ? "REAssociations" : "VEAssociations"]?["ActionElements"]) as JsonArray)?.Select(a => (a!["ActionID"]!.GetValue<string>(), a["DataSinkQualifier"]?.GetValue<string>())).ToList() ?? [];
    public static ScriptVenueAssociations Read(string json) => Read(json, j => new ScriptVenueAssociations(j));
}

public sealed class REVenueSpecification(JsonObject json) : XrvData(json)
{
    public override string Header => Types.REVenueSpecification;
    public override string SchemaFile => "XRV1/V1.0/data/REVenueSpecification.json";
    public string Id => Json["REVenueSpecificationID"]!.GetValue<string>();
    // The DMX fixtures: where each is (universe, channel) and what the channel does.
    public IReadOnlyList<(int Universe, int Channel, string Definition, string? FixtureId)> DmxChannels =>
        (Json["DMXUniverses"] as JsonArray)?.SelectMany(u => ((JsonArray)u!["Channels"]!).Select(c =>
            (u["UniverseID"]!.GetValue<int>(), c!["ChannelNumber"]!.GetValue<int>(), c["ChannelDefinition"]!.GetValue<string>(), c["FixtureID"]?.GetValue<string>()))).ToList() ?? [];
    public static REVenueSpecification Read(string json) => Read(json, j => new REVenueSpecification(j));
}

public sealed class VEVenueSpecification(JsonObject json) : XrvData(json)
{
    public override string Header => Types.VEVenueSpecification;
    public override string SchemaFile => "XRV1/V1.0/data/VEVenueSpecification.json";
    public string Id => Json["VEVenueSpecificationID"]!.GetValue<string>();
    public IReadOnlyList<string> VirtualObjectIds => (Json["VirtualObjects"] as JsonArray)?.Select(o => o!["3DModelObjectID"]!.GetValue<string>()).ToList() ?? [];
    public static VEVenueSpecification Read(string json) => Read(json, j => new VEVenueSpecification(j));
}

public sealed class ShowDescriptors(JsonObject json) : XrvData(json)
{
    public override string Header => "XRV-SHD-V1.0";
    public override string SchemaFile => "XRV1/V1.0/data/ShowDescriptors.json";
    public static ShowDescriptors Read(string json) => Read(json, j => new ShowDescriptors(j));
}

// A Status of one of the six kinds (XRV-RFS, VFS, RTS, VTS, ROS, VOS). Its data are what a Cue Condition is met by; an Operator Status may carry the ShowControl.
public sealed class StatusObject(JsonObject json, StatusKind kind) : XrvData(json)
{
    public StatusKind Kind { get; } = kind;
    public KindInfo Info => KindInfo.Of(Kind);
    public override string Header => Info.StatusHeader;
    public override string SchemaFile => Info.StatusFile;
    public JsonNode? Data => Json[Info.StatusDataKey];
    public string? ShowControl => Kind is StatusKind.REOperator or StatusKind.VEOperator ? Data?["ShowControl"]?.GetValue<string>() : null;
    public static StatusObject Read(StatusKind kind, string json) => Read(json, j => new StatusObject(j, kind));
    public static StatusObject Read(string json)
    {
        var o = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("the datum is not a JSON object");
        return Read(json, j => new StatusObject(j, KindInfo.OfStatusHeader(o["Header"]?.GetValue<string>() ?? "").Kind));
    }
}

public sealed class DescriptorsObject(JsonObject json, StatusKind kind) : XrvData(json)
{
    public StatusKind Kind { get; } = kind;
    public KindInfo Info => KindInfo.Of(Kind);
    public override string Header => Info.DescriptorsHeader;
    public override string SchemaFile => Info.DescriptorsFile;
    public JsonNode? Data => Json[Info.DescriptorsDataKey];
    public static DescriptorsObject Read(StatusKind kind, string json) => Read(json, j => new DescriptorsObject(j, kind));
}

// ID PORTS: the Cue Point ID and the Action IDs are Basic Text Objects (OSD-BTO-V1.5) whose text is the ID.
public sealed class TextId(JsonObject json) : XrvData(json)
{
    public override string Header => Types.BasicTextObject;
    public override string SchemaFile => "OSD/V1.5/data/BasicTextObject.json";
    public string Text => (Json["BasicTextData"] as JsonArray)?.FirstOrDefault()?["Data"]?.GetValue<string>() ?? "";
    public static TextId Read(string json) => Read(json, j => new TextId(j));
    public static TextId Make(string objectId, string text, string? environmentId = null)
    {
        var o = new JsonObject { ["Header"] = Types.BasicTextObject, ["BasicTextObjectID"] = objectId, ["BasicTextData"] = new JsonArray(new JsonObject { ["Data"] = text }) };
        if (environmentId is not null) o["UEnvironmentID"] = environmentId;
        return new TextId(o);
    }
}

public static class XrvTime
{
    // A Simple Time (OSD-STM-V1.5): one absolute time, in milliseconds, exact.
    public static JsonObject Simple(string id, double ms) => new()
    {
        ["Header"] = "OSD-STM-V1.5", ["SimpleTimeID"] = id,
        ["SimpleTimeData"] = new JsonArray(new JsonObject { ["FlagsByte"] = 3, ["StartTime"] = ms, ["EndTime"] = ms, ["AccuracyMode"] = "single", ["AccuracyPlusMinus"] = 0, ["TimeType"] = true, ["TimeUnit"] = "01" })
    };
}
