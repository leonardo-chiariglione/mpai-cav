using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Mmc.Jsi;

namespace Mpai.Aif.Tests;

// THE JSON INTERFACE OF AUDIO SCENE MANAGEMENT, as a test builds it: the L3 1MMC-JSI-V2.5-I02 (its Ports), the schema of the User Command and the guide of the application
// (what its Access gives it), and a language model that is the test's. And the scene of Test/AsmVoice/cases.json, told as the ApplicationContext is: a User Command response
// that lists the scene, its members with their PoV, and the sounds that are kept.
internal static class JsiRig
{
    public const string UserCommandUri = "https://schemas.mpai.community/CAE3/V1.0/data/UserCommand.json";
    public const string Jsi = "1MMC-JSI-V2.5-I02", NeutralJsi = "1MMC-JSI-V2.5-I01";

    public static string UserCommandFile => Path.Combine(Repository.Schemas, "CAE3", "V1.0", "data", "UserCommand.json");
    public static string GuideFile => Path.Combine(Repository.Root, "Apps", "CAE", "ASM", "Access", "AsmApplicationGuide.json");

    public static AimPortReader Ports(string aim)
    {
        var store = new AIF.Store.AmdStore(Repository.Amds); store.Scan();
        return AimPortReader.Load(store, aim);
    }

    public static JsiConfiguration Configuration() => new(UserCommandUri, UserCommandFile, UserCommandUri, UserCommandFile, GuideFile, Repository.Schemas);

    public static JsiAimProcessor Processor(StructuredModel? model, string aim = Jsi) =>
        new(aim, Ports(aim), aim == Jsi ? Configuration() : new JsiConfiguration("", null, "", null, null), model);

    public static JsonNode Scene() => JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Root, "Test", "AsmVoice", "cases.json")))!["scene"]!;

    // ---- the ports of the JSON Interface ------------------------------------------------------------------------------------------------------

    public static string Request(AimPortReader p) => p.Input("OSD-BTO-V1.5");
    public static string Response(AimPortReader p) => p.Input("MMC-AJI-V2.5", 1);
    public static string Context(AimPortReader p) => p.Input("MMC-AJI-V2.5", 2);
    public static string Command(AimPortReader p) => p.Output("MMC-AJI-V2.5");
    public static string Facts(AimPortReader p) => p.Output("OSD-BTO-V1.5");

    public static Message Say(AimPortReader p, string words, JsonNode? context = null)
    {
        var ports = new Dictionary<string, string> { [Request(p)] = MpaiJson.ToJson(BasicTextObject.FromText(words)) };
        if (context is not null) ports[Context(p)] = context.ToJsonString();
        return new Message { MessageId = Guid.NewGuid().ToString(), Ports = ports };
    }

    public static string? FactsOf(Message answer, AimPortReader p) =>
        answer.Ports.TryGetValue(Facts(p), out var json) ? MpaiJson.FromJson<BasicTextObject>(json)?.GetText() : null;

    public static JsonObject? CommandOf(Message answer, AimPortReader p) =>
        answer.Ports.TryGetValue(Command(p), out var json) ? JsonNode.Parse(json) as JsonObject : null;

    // ---- the scene, as the application tells it ----------------------------------------------------------------------------------------------------

    private static JsonObject Time() => new()
    {
        ["Header"] = "OSD-STM-V1.5", ["SimpleTimeID"] = Guid.NewGuid().ToString(),
        ["SimpleTimeData"] = new JsonArray(new JsonObject { ["FlagsByte"] = 1, ["StartTime"] = 1.0, ["EndTime"] = 1.0, ["AccuracyPlusMinus"] = 0 })
    };

    public static JsonObject Attitude(double[] position, double[] orientation) => new()
    {
        ["Header"] = "OSD-OSA-V1.5", ["ObjectSpatialAttitudeID"] = Guid.NewGuid().ToString(),
        ["Position"] = new JsonObject { ["Header"] = "OSD-OPS-V1.5", ["PositionID"] = Guid.NewGuid().ToString(), ["CartPosition"] = new JsonArray(position.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()) },
        ["Orientation"] = new JsonObject { ["Header"] = "OSD-OOR-V1.5", ["OrientationID"] = Guid.NewGuid().ToString(), ["Orientation"] = new JsonArray(orientation.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()) }
    };

    // The ApplicationContext: an Application JSON Instance whose Instance is the response that lists the scene.
    public static JsonObject Context(JsonNode scene, string[]? stored = null)
    {
        var members = new JsonArray();
        foreach (var (name, m) in scene["members"]!.AsObject())
            members.Add(new JsonObject
            {
                ["ObjectID"] = name,
                ["PoV"] = Attitude(m!["position"]!.AsArray().Select(x => x!.GetValue<double>()).ToArray(), m["orientation"]!.AsArray().Select(x => x!.GetValue<double>()).ToArray())
            });
        var listed = new JsonArray(new JsonObject { ["ObjectID"] = (string)scene["name"]!, ["Kind"] = "Audio Scene", ["ObjectCount"] = members.Count, ["Members"] = members });
        foreach (var s in stored ?? scene["library"]!.AsArray().Select(x => (string)x!))
            listed.Add(new JsonObject { ["ObjectID"] = s, ["Kind"] = "Basic Audio Object" });
        return Aji(new JsonObject
        {
            ["Header"] = "CAE-UCM-V1.0", ["UserCommandID"] = "context", ["UserCommandTime"] = Time(),
            ["UserCommandRS"] = new JsonObject { ["Outcome"] = "Done", ["ListedObjects"] = listed }
        });
    }

    public static JsonObject Aji(JsonObject instance, string? inReplyTo = null)
    {
        var aji = new JsonObject { ["Header"] = "MMC-AJI-V2.5", ["AJIID"] = Guid.NewGuid().ToString(), ["ApplicationSchemaURI"] = UserCommandUri, ["Instance"] = instance };
        if (inReplyTo is not null) aji["InReplyTo"] = inReplyTo;
        return aji;
    }

    // A response of the application to a command: what it did, action by action.
    public static JsonObject ResponseTo(string commandId, string outcome, params (string Action, string Object, string Outcome, string? Reason)[] actions) =>
        Aji(new JsonObject
        {
            ["Header"] = "CAE-UCM-V1.0", ["UserCommandID"] = commandId, ["UserCommandTime"] = Time(),
            ["UserCommandRS"] = new JsonObject
            {
                ["Outcome"] = outcome,
                ["Actions"] = new JsonArray(actions.Select(a =>
                {
                    var o = new JsonObject { ["Action"] = a.Action, ["ObjectID"] = a.Object, ["Outcome"] = a.Outcome };
                    if (a.Reason is not null) o["Reason"] = a.Reason;
                    return (JsonNode)o;
                }).ToArray())
            }
        });

    // ---- what a command does, in the words of the scene ------------------------------------------------------------------------------------------------

    // The operations of a command, as plain values to compare: delivered, moved (name, position, orientation), level, lufs, added, removed, pov, revoked.
    public static JsonObject Normal(JsonNode instance)
    {
        var rq = instance["UserCommandRQ"] as JsonObject ?? new JsonObject();
        var result = new JsonObject();
        static JsonNode? Arr(JsonNode? n) => n is JsonArray a ? new JsonArray(a.Select(x => x?.DeepClone()).ToArray()) : null;
        if (rq["DeliveredObject"] is JsonValue d) result["delivered"] = (string?)d;
        if (rq["MovedObjects"]?["Objects"] is JsonArray moved)
            result["moved"] = new JsonArray(moved.Select(m => (JsonNode)new JsonObject
            {
                ["name"] = (string?)m?["ObjectID"],
                ["position"] = Arr(m?["NewSpatialAttitude"]?["Position"]?["CartPosition"]),
                ["orientation"] = Arr(m?["NewSpatialAttitude"]?["Orientation"]?["Orientation"])
            }).ToArray());
        if (rq["ChangedObjects"]?["Objects"] is JsonArray changed)
            result["level"] = new JsonArray(changed.Select(m => (JsonNode)new JsonObject { ["name"] = (string?)m?["ObjectID"], ["lufs"] = m?["Level"]?.DeepClone() }).ToArray());
        if (rq["LUFS"] is not null) result["lufs"] = rq["LUFS"]!.DeepClone();
        if (rq["AddedObjects"]?["Objects"] is JsonArray added)
            result["added"] = new JsonArray(added.Select(m => (JsonNode)new JsonObject
            {
                ["name"] = (string?)m?["ObjectID"],
                ["position"] = Arr(m?["SpatialAttitude"]?["Position"]?["CartPosition"]),
                ["orientation"] = Arr(m?["SpatialAttitude"]?["Orientation"]?["Orientation"])
            }).ToArray());
        if (rq["RemovedObjects"]?["Objects"] is JsonArray removed)
            result["removed"] = new JsonArray(removed.Select(m => (JsonNode)JsonValue.Create((string?)m?["ObjectID"])!).ToArray());
        if (rq["UserPoV"]?["CartPosition"] is JsonArray pov) result["pov"] = Arr(pov);
        if (rq["RevokedUserCommandID"] is JsonValue r) result["revoked"] = (string?)r;
        return result;
    }
}
