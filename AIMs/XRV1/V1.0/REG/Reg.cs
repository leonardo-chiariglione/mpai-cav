using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Xrv.Reg;

// XRV-REG-V1.0, RE Experience Generation. In: the Show Descriptors, the RE Action IDs (OSD-BTO-V1.5 #1), the RE Venue Specification, the Participant Data, the Script, the Action Table
// and the Script-Venue Associations. Out: RE Data Out.
//
// For each RE Action ID it gives the commands of that Action in the Action Table, in line with the RE Venue Specification: one RE Data Out per Action, each command a Control Object.
// THE SIMPLEST READING: a command whose Protocol is DMX or RDM must address a Channel of a Universe that the RE Venue Specification declares; otherwise it is left out (reported).
// The Control Object carries the command (the ActionCommand, as JSON text) as its Control Data; its ControllerID is the Fixture of that Channel; its Control Qualifier says the format
// (DMX and RDM: "ANSI E1.20-2010", the only DMX-family format the Control Qualifier lists; the protocol is also written in its Attributes). Commands of other protocols (MIDI, OSC) are
// given with that format, unchecked. An Action ID that is not in the Action Table gives nothing (reported).
// The Show Descriptors, the Participant Data, the Script and the Script-Venue Associations are received and not used in this first slice.
public sealed class RegAimProcessor : IAimProcessor, IAimRunner
{
    public const string RDO = "XRV-RDO-V1.0";
    private readonly Task? startGate;

    public RegAimProcessor(string instanceId, Task? startGate = null) { InstanceId = instanceId; this.startGate = startGate; }

    public string InstanceId { get; }
    public Task<Message> ProcessAsync(Message message) => throw new NotSupportedException("XRV-REG runs continuously.");

    public static string FormatOf(string? protocol) => protocol?.ToUpperInvariant() switch
    {
        "MIDI" => "MIDI",
        "OSC" => "OSC",
        _ => "ANSI E1.20-2010"
    };

    // The RE Data Out of one Action, or null with the reason.
    public static JsonObject? Generate(string actionId, ActionTable table, REVenueSpecification? venue, string id, double nowMs, string? environment, List<string> why)
    {
        if (table.Commands(actionId) is not { } commands) { why.Add($"the RE Action ID '{actionId}' is not in the Action Table"); return null; }
        var controls = new JsonArray();
        var n = 0;
        foreach (var command in commands)
        {
            var c = command?["ActionCommand"] as JsonObject;
            if (c is null) continue;
            var protocol = c["Protocol"]?.GetValue<string>();
            string? controller = null;
            if (protocol is "DMX" or "RDM")
            {
                var universe = c["Universe"]?.GetValue<int>(); var channel = c["Channel"]?.GetValue<int>();
                var found = venue?.DmxChannels.FirstOrDefault(x => x.Universe == universe && x.Channel == channel);
                if (venue is null || found is null || found.Value.Definition is null) { why.Add($"{actionId}: Universe {universe} Channel {channel} is not in the RE Venue Specification: left out"); continue; }
                controller = found.Value.FixtureId ?? $"{universe}:{channel}";
            }
            var cid = $"{id}-c{++n}";
            var control = new JsonObject
            {
                ["Header"] = "XRV-CTO-V1.0", ["ControlObjectID"] = cid, ["UEnvironmentID"] = environment ?? "", ["ControlObjectTime"] = XrvTime.Simple(cid + "-t", nowMs),
                ["ControlData"] = c.ToJsonString(),
                ["ControlQualifier"] = new JsonObject { ["Header"] = "TFA-CXQ-V1.5", ["ControlQualifierID"] = cid + "-q", ["SubTypes"] = new JsonObject(), ["Formats"] = FormatOf(protocol), ["Attributes"] = new JsonObject { ["Protocol"] = protocol ?? "", ["ActionID"] = actionId } }
            };
            if (controller is not null) control["ControllerID"] = controller;
            controls.Add(new JsonObject { ["ControlObject"] = control });
        }
        if (controls.Count == 0) { why.Add($"{actionId}: no command could be given"); return null; }
        return new JsonObject
        {
            ["Header"] = RDO, ["REDataOutID"] = id, ["UEnvironmentID"] = environment ?? "", ["REDataOutTime"] = XrvTime.Simple(id + "-t", nowMs),
            ["REDataOut"] = new JsonObject { ["ControlObjects"] = controls }
        };
    }

    public async Task RunAsync(IAimPorts ports, AimContext context)
    {
        if (startGate is not null) await startGate;
        ActionTable? table = null; REVenueSpecification? venue = null;
        var waiting = new Queue<string>();
        long count = 0;

        async Task Give(string actionId)
        {
            var why = new List<string>();
            var id = $"reg-{++count}";
            var output = Generate(actionId, table!, venue, id, ports.Now.ToUnixTimeMilliseconds(), table!.EnvironmentId, why);
            foreach (var w in why) context.Report(w);
            if (output is not null)
            {
                var violations = XrvSchemas.Violations("XRV1/V1.0/data/REDataOut.json", output);
                if (violations.Count > 0) { context.Report($"the RE Data Out of {actionId} is not valid: {string.Join("; ", violations)}"); return; }
                await ports.WriteAsync(RDO, 1, output.ToJsonString(), -1);
            }
        }

        // what sets up the show first (SelectAsync gives the first Port with a Message): a Cue or Show Descriptors waiting with it are taken after it
        var inputs = new[] { (Types.ActionTable, 1), (Types.REVenueSpecification, 1), (Types.ParticipantData, 1), (Types.Script, 1), (Types.ScriptVenueAssociations, 1), (Types.BasicTextObject, 1), (EndShd, 1) };
        while (await ports.SelectAsync(-1, inputs) is { } port)
        {
            if (await ports.ReadAsync(port.DataType, port.PortNumber, 0) is not { } message) continue;
            try
            {
                if (port.DataType == Types.ActionTable) table = ActionTable.Read(message.Json);
                else if (port.DataType == Types.REVenueSpecification) venue = REVenueSpecification.Read(message.Json);
                else if (port.DataType == Types.BasicTextObject) waiting.Enqueue(TextId.Read(message.Json).Text);
                // The Show Descriptors, the Participant Data, the Script and the Script-Venue Associations: received, not used.
                if (table is not null && venue is not null)
                    while (waiting.Count > 0) await Give(waiting.Dequeue());
            }
            catch (FormatException bad) { context.Report(bad.Message); }
        }
    }

    private const string EndShd = "XRV-SHD-V1.0";
}
