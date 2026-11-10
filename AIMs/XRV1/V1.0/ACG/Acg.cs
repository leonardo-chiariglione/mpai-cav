using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Xrv.Acg;

// XRV-ACG-V1.0, Action Generation. In: the six Statuses (optional), the Participant Data (optional), the Script, the Action Table and the Script-Venue Associations. Out: the RE Action ID
// (Port Number 1), the VE Action ID (2) and the Cue Point ID (3), each a Basic Text Object (OSD-BTO-V1.5), and the Participant Data.
//
// The Module runs continuously, so the AIM runs from Start to Stop: it waits for the Script and the Action Table (the User Agent gives them before the show), starts the Cue engine, and
// then follows the Statuses as they come. The Participant Data passes through unchanged; Action Generation is the only AIM that outputs it. The Script-Venue Associations are received and
// not used here.
public sealed class AcgAimProcessor : IAimProcessor, IAimRunner
{
    private readonly Task? startGate;

    public AcgAimProcessor(string instanceId, Task? startGate = null) { InstanceId = instanceId; this.startGate = startGate; }

    public string InstanceId { get; }
    public Task<Message> ProcessAsync(Message message) => throw new NotSupportedException("XRV-ACG runs continuously.");

    // The engine of the last run (for the tests that look at the state of the show).
    public CueEngine? Engine { get; private set; }

    public async Task RunAsync(IAimPorts ports, AimContext context)
    {
        if (startGate is not null) await startGate;
        var statuses = new List<(string DataType, int PortNumber)>();
        foreach (var kind in KindInfo.All) statuses.Add((kind.StatusHeader, 1));

        // The Script and the Action Table first; what else arrives waits on its Channel.
        var scriptMessage = await ports.ReadAsync(Types.Script, 1, -1);
        var tableMessage = await ports.ReadAsync(Types.ActionTable, 1, -1);
        if (scriptMessage is null || tableMessage is null) return;
        Script script; ActionTable table;
        try { script = Script.Read(scriptMessage.Json); table = ActionTable.Read(tableMessage.Json); }
        catch (FormatException bad) { context.Report(bad.Message); return; }
        var engine = Engine = new CueEngine(script, table, context.Report);
        long count = 0;

        async Task Write(IEnumerable<EngineEvent> events)
        {
            foreach (var e in events)
            {
                var port = e.Kind switch { EventKind.REAction => 1, EventKind.VEAction => 2, _ => 3 };
                var text = TextId.Make($"{(e.Kind == EventKind.CuePoint ? "cue" : "action")}-{++count}", e.Id, script.EnvironmentId);
                await ports.WriteAsync(Types.BasicTextObject, port, text.ToJson(), -1);
            }
        }

        await Write(engine.Start());

        var inputs = new List<(string, int)>(statuses) { (Types.ParticipantData, 1), (Types.ScriptVenueAssociations, 1), (Types.Script, 1), (Types.ActionTable, 1) };
        while (await ports.SelectAsync(-1, inputs.ToArray()) is { } port)
        {
            if (await ports.ReadAsync(port.DataType, port.PortNumber, 0) is not { } message) continue;
            try
            {
                if (port.DataType == Types.ParticipantData)
                    await ports.WriteAsync(Types.ParticipantData, 1, message.Json, -1);                // passes through, unchanged
                else if (KindInfo.All.Any(k => k.StatusHeader == port.DataType))
                    await Write(engine.Receive(StatusObject.Read(message.Json)));
                // The Script-Venue Associations, a second Script or Action Table: received, not used.
            }
            catch (FormatException bad) { context.Report(bad.Message); }
        }
    }
}
