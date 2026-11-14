using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Xrv;

// FROM DESCRIPTORS TO A STATUS, in the simplest reading: the Status takes from the Descriptors the elements that both name (the schema of the Status says which: the properties of the
// individual, collective and object elements it allows). Nothing is computed from the Descriptors here - what a Status Interpretation AIM would infer from audio, video or MoCap is
// the work of a later stage.
public static class Interpretation
{
    private static readonly string[] CommandWords = ["Go", "Hold", "Skip", "Back"];
    private static readonly Dictionary<string, HashSet<string>> Allowed = new();

    private static HashSet<string> AllowedKeys(KindInfo info, params string[] path)
    {
        var key = info.StatusFile + "/" + string.Join("/", path);
        lock (Allowed)
            return Allowed.TryGetValue(key, out var found) ? found : Allowed[key] = XrvSchemas.Properties(info.StatusFile, path);
    }

    // The Status of the Descriptors; the ID and the time are given by the caller.
    public static StatusObject Derive(DescriptorsObject descriptors, string statusId, double nowMs)
    {
        var info = descriptors.Info;
        var status = new JsonObject
        {
            ["Header"] = info.StatusHeader,
            [info.StatusIdKey] = statusId,
            [info.StatusTimeKey] = XrvTime.Simple(statusId + "-t", nowMs)
        };
        if (descriptors.Json["UEnvironmentID"] is { } environment) status["UEnvironmentID"] = environment.DeepClone();
        if (descriptors.Json["MInstanceID"] is { } instance) status["MInstanceID"] = instance.DeepClone();

        var top = XrvSchemas.Properties(info.StatusFile);
        foreach (var key in new[] { "MultimodalSceneDescriptors", "SeatOccupation" })
            if (top.Contains(key) && descriptors.Json[key] is { } shared) status[key] = shared.DeepClone();

        if (info.Family == Family.Operator)
        {
            var allowed = AllowedKeys(info, info.StatusDataKey);
            var inner = new JsonObject();
            if (descriptors.Data is JsonObject d)
                foreach (var (key, value) in d)
                    if (allowed.Contains(key)) inner[key] = value?.DeepClone();
            if (ShowControlIn(descriptors) is { } command) inner["ShowControl"] = command;
            status[info.StatusDataKey] = inner;
        }
        else
        {
            var data = new JsonObject();
            var individuals = new JsonArray();
            var individualKeys = AllowedKeys(info, info.StatusDataKey, "IndividualStatuses", "items");
            foreach (var item in (descriptors.Data as JsonArray) ?? [])
                individuals.Add(Copy((JsonObject)item!, individualKeys));
            data["IndividualStatuses"] = individuals;

            var objectsKey = info.IsRE ? "REObjectDescriptors" : "VEObjectDescriptors";
            if (descriptors.Json[objectsKey] is JsonArray objects && AllowedKeys(info, info.StatusDataKey).Contains("Objects"))
            {
                var objectKeys = AllowedKeys(info, info.StatusDataKey, "Objects", "items");
                data["Objects"] = new JsonArray(objects.Select(o => (JsonNode)Copy((JsonObject)o!, objectKeys)).ToArray());
            }
            status[info.StatusDataKey] = data;
        }
        return new StatusObject(status, info.Kind);
    }

    private static JsonObject Copy(JsonObject source, HashSet<string> allowed)
    {
        var copy = new JsonObject();
        foreach (var (key, value) in source)
            if (allowed.Contains(key)) copy[key] = value?.DeepClone();
        return copy;
    }

    // THE SHOW CONTROL COMMAND, where the Operator Descriptors carry one: the Descriptors of the Show Console hold Control Objects, and a Control Object whose Control Data is Go, Hold, Skip
    // or Back is the command of the operator. (The Operator Descriptors have no element of their own for it; this is the simplest place the schemas give.)
    public static string? ShowControlIn(DescriptorsObject descriptors)
    {
        if (descriptors.Info.Family != Family.Operator || descriptors.Data?["ShowConsole"] is not { } show) return null;
        return Find(show);
    }

    private static string? Find(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                if (o["ControlData"] is JsonValue v && v.TryGetValue<string>(out var word) && CommandWords.Contains(word)) return word;
                return o.Select(p => Find(p.Value)).FirstOrDefault(r => r is not null);
            case JsonArray a: return a.Select(Find).FirstOrDefault(r => r is not null);
            default: return null;
        }
    }
}

// THE STATUS INTERPRETATION AIMs (XRV-PFI, XRV-PTI, XRV-OSI): the Descriptors of the Real and of the Virtual Environment in, the Status out - only the Status that the current Cue Point
// asks for. The Cue Point ID is a pointer to a Cue of the Script, which all the AIMs know: the AIM watches the Descriptors and, when the Status they give meets a target the Cue names for
// its kind, sends that Status. A new Cue Point ID replaces the one before: what the earlier one asked for is no longer watched. The Operator Status Interpretation also gives every
// ShowControl command that the Operator Descriptors carry, whatever the Cue.
//
// The Participant Data is received and not used.
public abstract class StatusInterpretationAim : IAimProcessor, IAimRunner
{
    private readonly Family family;
    private readonly Task? startGate;

    protected StatusInterpretationAim(string instanceId, Family family, Task? startGate = null)
    {
        InstanceId = instanceId; this.family = family; this.startGate = startGate;
    }

    public string InstanceId { get; }
    public Task<Message> ProcessAsync(Message message) => throw new NotSupportedException("The Status Interpretation AIMs run continuously.");

    private KindInfo Info(bool re) => KindInfo.Of(Enum.Parse<StatusKind>((re ? "RE" : "VE") + family));

    public async Task RunAsync(IAimPorts ports, AimContext context)
    {
        if (startGate is not null) await startGate;
        Script? script = null;
        string? cueId = null;
        var latest = new Dictionary<bool, DescriptorsObject>();
        long count = 0;

        // what sets up the show first (SelectAsync gives the first Port with a Message): Descriptors or a Cue waiting with it are taken after it
        var inputs = new[]
        {
            (Types.Script, 1), (Types.ActionTable, 1), (Types.ScriptVenueAssociations, 1), (Types.ParticipantData, 1),
            (Info(true).DescriptorsHeader, 1), (Info(false).DescriptorsHeader, 1), (Types.BasicTextObject, 1)
        };

        async Task Evaluate(bool re, bool fromDescriptors)
        {
            if (!latest.TryGetValue(re, out var descriptors)) return;
            var info = Info(re);
            var derived = Interpretation.Derive(descriptors, $"{info.StatusKey}-{++count}", ports.Now.ToUnixTimeMilliseconds());
            var command = fromDescriptors ? derived.ShowControl : null;
            var targets = script is not null && cueId is not null && script.Find(cueId) is { } cue ? cue.TargetsOf(info.Kind).ToList() : [];
            var met = targets.Any(t => JsonMatch.Meets(t.Data, derived.Data));
            if (!met && command is null) return;
            await ports.WriteAsync(info.StatusHeader, 1, derived.ToJson(), -1);
        }

        while (await ports.SelectAsync(-1, inputs) is { } port)
        {
            if (await ports.ReadAsync(port.DataType, port.PortNumber, 0) is not { } message) continue;
            try
            {
                if (port.DataType == Types.Script) { script = Script.Read(message.Json); if (cueId is not null) { await Evaluate(true, false); await Evaluate(false, false); } }
                else if (port.DataType == Types.BasicTextObject)
                {
                    cueId = TextId.Read(message.Json).Text;                 // a new Cue Point ID replaces the one before
                    if (script is not null && script.Find(cueId) is null) context.Report($"the Cue Point ID '{cueId}' is not a Cue of the Script");
                    await Evaluate(true, false); await Evaluate(false, false);
                }
                else if (port.DataType == Info(true).DescriptorsHeader || port.DataType == Info(false).DescriptorsHeader)
                {
                    var re = port.DataType == Info(true).DescriptorsHeader;
                    latest[re] = DescriptorsObject.Read(Info(re).Kind, message.Json);
                    await Evaluate(re, true);
                }
                // The Action Table, the Script-Venue Associations and the Participant Data: received, not used.
            }
            catch (FormatException bad) { context.Report(bad.Message); }
        }
    }
}
