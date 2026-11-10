using System.Text.Json.Nodes;

using Mpai.Xrv;

namespace Mpai.Aif.Tests;

// WHAT THE XRV-LTP TESTS SHARE: the mini show (Test/Data/XRV/MiniShow), and the Descriptors that a recording of a performance of it holds, built as the schemas say them.
public static class XrvShow
{
    public const string Environment = "UE1";
    public static string Folder { get; } = Path.Combine(Repository.Root, "Test", "Data", "XRV", "MiniShow");
    public static string Text(string file) => File.ReadAllText(Path.Combine(Folder, file));
    public static JsonObject Json(string file) => (JsonObject)JsonNode.Parse(Text(file))!;

    private static JsonObject Time(string id) => XrvTime.Simple(id, 0);

    // ---- Descriptors, as a recording of the mini show holds them ----------------------------------------------------------------------------------

    public static string PerformanceDescriptors(bool re, string id, params string[] performers)
    {
        var s = re ? "RE" : "VE";
        return new JsonObject
        {
            ["Header"] = re ? "XRV-RFD-V1.0" : "XRV-VFD-V1.0", [$"{s}PerformanceDescriptorsID"] = id, ["UEnvironmentID"] = Environment, [$"{s}PerformanceDescriptorsTime"] = Time(id + "-t"),
            [$"{s}PerformanceDescriptorsData"] = new JsonArray(performers.Select(p => (JsonNode)new JsonObject { [$"{s}PerformerID"] = p }).ToArray())
        }.ToJsonString();
    }

    public static string ParticipantDescriptors(bool re, string id, params (string Id, string? Seat)[] participants)
    {
        var s = re ? "RE" : "VE";
        return new JsonObject
        {
            ["Header"] = re ? "XRV-RTD-V1.0" : "XRV-VTD-V1.0", [$"{s}ParticipantDescriptorsID"] = id, ["UEnvironmentID"] = Environment, [$"{s}ParticipantDescriptorsTime"] = Time(id + "-t"),
            [$"{s}ParticipantDescriptorsData"] = new JsonArray(participants.Select(p =>
            {
                var o = new JsonObject { [$"{s}ParticipantID"] = p.Id };
                if (p.Seat is not null) o["SeatID"] = p.Seat;
                return (JsonNode)o;
            }).ToArray())
        }.ToJsonString();
    }

    // Operator Descriptors; a show control command is carried as the Control Data of a Control Object of the Show Console.
    public static string OperatorDescriptors(bool re, string id, string? showControl = null)
    {
        var s = re ? "RE" : "VE";
        var inner = new JsonObject();
        if (showControl is not null)
            inner["ShowConsole"] = new JsonObject
            {
                ["Header"] = "XRV-SHC-V1.0", ["ShowConsoleID"] = id + "-sc", ["UEnvironmentID"] = Environment, ["ShowConsoleTime"] = Time(id + "-sc-t"),
                ["ShowConsoleData"] = new JsonArray(new JsonObject
                {
                    ["Rigging"] = new JsonObject
                    {
                        ["OperationID"] = "show-control",
                        ["ControlObject"] = new JsonObject
                        {
                            ["Header"] = "XRV-CTO-V1.0", ["ControlObjectID"] = id + "-cto", ["UEnvironmentID"] = Environment, ["ControlObjectTime"] = Time(id + "-cto-t"),
                            ["ControlData"] = showControl,
                            ["ControlQualifier"] = new JsonObject { ["Header"] = "TFA-CXQ-V1.5", ["ControlQualifierID"] = id + "-q", ["SubTypes"] = new JsonObject(), ["Formats"] = "OSC" }
                        }
                    }
                })
            };
        return new JsonObject
        {
            ["Header"] = re ? "XRV-ROD-V1.0" : "XRV-VOD-V1.0", [$"{s}OperatorDescriptorsID"] = id, ["UEnvironmentID"] = Environment, [$"{s}OperatorDescriptorsTime"] = Time(id + "-t"),
            [$"{s}OperatorDescriptors"] = inner
        }.ToJsonString();
    }

    // ---- Statuses (what Action Generation receives) -------------------------------------------------------------------------------------------------

    public static string Status(StatusKind kind, string id, JsonNode data)
    {
        var info = KindInfo.Of(kind);
        return new JsonObject { ["Header"] = info.StatusHeader, [info.StatusIdKey] = id, ["UEnvironmentID"] = Environment, [info.StatusTimeKey] = Time(id + "-t"), [info.StatusDataKey] = data.DeepClone() }.ToJsonString();
    }

    public static string ShowControlStatus(bool re, string id, string command) =>
        Status(re ? StatusKind.REOperator : StatusKind.VEOperator, id, new JsonObject { ["ShowControl"] = command });

    // The text of an ID Port message.
    public static string IdOf(string json) => TextId.Read(json).Text;

    // A copy of the mini show Script changed by the caller (Cue List, Operators, ...), validated.
    public static string ScriptWith(Action<JsonObject> change)
    {
        var script = Json("Script.json");
        change(script);
        return script.ToJsonString();
    }

    // A Cue of the Script as a test writes it: {"Cue": [ {kind: {...}} ]}.
    public static JsonObject RealCue(string id, params JsonObject[] groups) => new() { ["Cue"] = new JsonArray(new JsonObject { ["RealCue"] = new JsonObject { ["RECueID"] = id, ["REGroups"] = new JsonArray(groups) } }) };
    public static JsonObject VirtualCue(string id, params JsonObject[] groups) => new() { ["Cue"] = new JsonArray(new JsonObject { ["VirtualCue"] = new JsonObject { ["VECueID"] = id, ["VEGroups"] = new JsonArray(groups) } }) };
    public static JsonObject MixedCue(string id, params JsonObject[] groups) => new() { ["Cue"] = new JsonArray(new JsonObject { ["MixedCue"] = new JsonObject { ["MECueID"] = id, ["MEGroups"] = new JsonArray(groups) } }) };

    // A group whose Conditions are Status targets: conditions = (kind, data) pairs; actions as RE / VE Action IDs; the NextCue (or none).
    public static JsonObject Group(IEnumerable<(StatusKind Kind, JsonNode Data)> conditions, string[] re, string[] ve, string? next = null)
    {
        var rec = new JsonObject(); var vec = new JsonObject();
        foreach (var (kind, data) in conditions)
        {
            var info = KindInfo.Of(kind);
            (info.IsRE ? rec : vec)[info.StatusKey] = JsonNode.Parse(Status(kind, "target-" + info.StatusKey, data));
        }
        var g = new JsonObject();
        if (rec.Count > 0) g["RECueConditions"] = rec;
        if (vec.Count > 0) g["VECueConditions"] = vec;
        g["REActions"] = new JsonArray(re.Select(x => (JsonNode)x).ToArray());
        g["VEActions"] = new JsonArray(ve.Select(x => (JsonNode)x).ToArray());
        if (next is not null) g["NextCue"] = next;
        return g;
    }

    public static JsonNode Performers(bool re, params string[] ids) => new JsonObject
    {
        ["IndividualStatuses"] = new JsonArray(ids.Select(i => (JsonNode)new JsonObject { [re ? "REPerformerID" : "VEPerformerID"] = i }).ToArray())
    };

    public static JsonNode Participants(bool re, params string[] ids) => new JsonObject
    {
        ["IndividualStatuses"] = new JsonArray(ids.Select(i => (JsonNode)new JsonObject { [re ? "REParticipantID" : "VEParticipantID"] = i }).ToArray())
    };
}
