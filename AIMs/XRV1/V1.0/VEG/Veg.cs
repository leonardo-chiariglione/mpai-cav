using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Xrv.Veg;

// XRV-VEG-V1.0, VE Experience Generation. In: the Show Descriptors, the VE Action IDs (OSD-BTO-V1.5 #1), the VE Venue Specification, the Participant Data, the Script, the Action Table
// and the Script-Venue Associations. Out: VE Data Out.
//
// For each VE Action ID it gives the commands of that Action in the Action Table, in line with the VE Venue Specification: one VE Data Out per Action, each command a Metaverse Object.
// THE SIMPLEST READING: a command that names an Object must name a Virtual Object of the VE Venue Specification; otherwise it is left out (reported). The Metaverse Object carries the
// command (the ActionCommand, as JSON text) as its Data; its Metaverse Qualifier names, in its Attributes, the Object and the Action. An Action ID that is not in the Action Table gives
// nothing (reported). The Show Descriptors, the Participant Data, the Script and the Script-Venue Associations are received and not used in this first slice.
public sealed class VegAimProcessor : IAimProcessor, IAimRunner
{
    public const string VDO = "XRV-VDO-V1.0";
    private readonly Task? startGate;

    public VegAimProcessor(string instanceId, Task? startGate = null) { InstanceId = instanceId; this.startGate = startGate; }

    public string InstanceId { get; }
    public Task<Message> ProcessAsync(Message message) => throw new NotSupportedException("XRV-VEG runs continuously.");

    public static JsonObject? Generate(string actionId, ActionTable table, VEVenueSpecification? venue, string id, double nowMs, string? environment, List<string> why)
    {
        if (table.Commands(actionId) is not { } commands) { why.Add($"the VE Action ID '{actionId}' is not in the Action Table"); return null; }
        var objects = new JsonArray();
        var n = 0;
        foreach (var command in commands)
        {
            if (command?["ActionCommand"] is not JsonObject c) continue;
            var target = c["Object"]?.GetValue<string>();
            if (target is not null && (venue is null || !venue.VirtualObjectIds.Contains(target))) { why.Add($"{actionId}: '{target}' is not a Virtual Object of the VE Venue Specification: left out"); continue; }
            var mid = $"{id}-m{++n}";
            var attributes = new JsonObject { ["ActionID"] = actionId };
            if (target is not null) attributes["Object"] = target;
            objects.Add(new JsonObject
            {
                ["MetaverseObject"] = new JsonObject
                {
                    ["Header"] = "XRV-MVO-V1.0", ["MetaverseObjectID"] = mid, ["UEnvironmentID"] = environment ?? "", ["MetaverseObjectTime"] = XrvTime.Simple(mid + "-t", nowMs),
                    ["MetaverseObjectData"] = new JsonArray(new JsonObject { ["Data"] = c.ToJsonString() }),
                    ["MetaverseQualifier"] = new JsonObject { ["Header"] = "TFA-MAQ-V1.5", ["MetaverseQualifierID"] = mid + "-q", ["SubTypes"] = new JsonObject(), ["Formats"] = new JsonObject(), ["Attributes"] = attributes }
                }
            });
        }
        if (objects.Count == 0) { why.Add($"{actionId}: no command could be given"); return null; }
        return new JsonObject
        {
            ["Header"] = VDO, ["VEDataOutID"] = id, ["UEnvironmentID"] = environment ?? "", ["VEDataOutTime"] = XrvTime.Simple(id + "-t", nowMs),
            ["VEDataOut"] = new JsonObject { ["MetaverseObjects"] = objects }
        };
    }

    public async Task RunAsync(IAimPorts ports, AimContext context)
    {
        if (startGate is not null) await startGate;
        ActionTable? table = null; VEVenueSpecification? venue = null;
        var waiting = new Queue<string>();
        long count = 0;

        async Task Give(string actionId)
        {
            var why = new List<string>();
            var output = Generate(actionId, table!, venue, $"veg-{++count}", ports.Now.ToUnixTimeMilliseconds(), table!.EnvironmentId, why);
            foreach (var w in why) context.Report(w);
            if (output is null) return;
            var violations = XrvSchemas.Violations("XRV1/V1.0/data/VEDataOut.json", output);
            if (violations.Count > 0) { context.Report($"the VE Data Out of {actionId} is not valid: {string.Join("; ", violations)}"); return; }
            await ports.WriteAsync(VDO, 1, output.ToJsonString(), -1);
        }

        // what sets up the show first (SelectAsync gives the first Port with a Message): a Cue or Show Descriptors waiting with it are taken after it
        var inputs = new[] { (Types.ActionTable, 1), (Types.VEVenueSpecification, 1), (Types.ParticipantData, 1), (Types.Script, 1), (Types.ScriptVenueAssociations, 1), (Types.BasicTextObject, 1), ("XRV-SHD-V1.0", 1) };
        while (await ports.SelectAsync(-1, inputs) is { } port)
        {
            if (await ports.ReadAsync(port.DataType, port.PortNumber, 0) is not { } message) continue;
            try
            {
                if (port.DataType == Types.ActionTable) table = ActionTable.Read(message.Json);
                else if (port.DataType == Types.VEVenueSpecification) venue = VEVenueSpecification.Read(message.Json);
                else if (port.DataType == Types.BasicTextObject) waiting.Enqueue(TextId.Read(message.Json).Text);
                // The Show Descriptors, the Participant Data, the Script and the Script-Venue Associations: received, not used.
                if (table is not null && venue is not null)
                    while (waiting.Count > 0) await Give(waiting.Dequeue());
            }
            catch (FormatException bad) { context.Report(bad.Message); }
        }
    }
}
