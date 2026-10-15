using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Paf.Pmx;

// PAF-PMX-V1.6-I02 - Portable Avatar Multiplexing that runs continuously, on its own Ports.
//
// The Portable Avatar is an UPDATE of an avatar: it names the avatar (its ID) and carries what changed; what was sent before, if unchanged, stays, and the receiver holds it by the
// ID. Portable Avatar Multiplexing therefore does not wait for all its inputs, nor for the ID with each of them: whatever arrives is put into an update at once, under the ID it
// holds. The ID is the only thing it needs - what arrives before it is kept until it comes.
//
//  - What is a state (the Avatar Model, the Language Selector, the Personal Status, the Audio-Visual Scene, the Spatial Attitude, the Space-Times) is sent when it changes: the
//    Model once, at the introduction. What is an event (the Speech, the Text, the Face and Body Descriptors, an Input Portable Avatar) is sent each time.
//  - What has arrived together goes out in one update.
// The forming of the Portable Avatar is that of PAF-PMX-I01 (PmxAimProcessor), which it uses; as an IAimProcessor it does just what that does.
public sealed class ContinuousPmx : IAimProcessor, IAimRunner
{
    private static readonly (string DataType, int Port, bool State)[] Inputs =
    [
        ("string", 1, true),                                       // the Avatar ID - first, so that it is known before what arrives with it
        ("OSD-B3O-V1.5", 1, true), ("OSD-SEL-V1.5", 1, true), ("OSD-OSA-V1.5", 1, true), ("OSD-BMS-V1.5", 1, true),
        ("OSD-SPT-V1.5", 1, true), ("OSD-SPT-V1.5", 2, true), ("MMC-EPS-V2.5", 1, true),
        ("OSD-BSO-V1.5", 1, false), ("OSD-BTO-V1.5", 1, false), ("OSD-BTO-V1.5", 2, false),
        ("PAF-FDO-V1.6", 1, false), ("PAF-BDO-V1.6", 1, false), ("PAF-PAV-V1.6", 1, false)
    ];
    private const string Id = "string", Pav = "PAF-PAV-V1.6";

    private readonly PmxAimProcessor pmx;
    private readonly Dictionary<string, string> sent = [];         // the states sent, by avatar and port: "id|key" -> the Object
    private readonly Dictionary<string, string> waiting = [];      // what arrived before the ID: key -> Object
    private string? id;

    public ContinuousPmx(string instanceId, AimPortReader ports)
    {
        InstanceId = instanceId;
        pmx = new PmxAimProcessor(instanceId, ports);
    }

    public string InstanceId { get; }

    public Task<Message> ProcessAsync(Message message) => pmx.ProcessAsync(message);

    public async Task RunAsync(IAimPorts ports, AimContext context)
    {
        var all = Inputs.Select(i => (i.DataType, i.Port)).ToArray();
        while (await ports.SelectAsync(-1, all) is not null)
        {
            var arrived = new List<(string DataType, int Port, string Json)>();
            foreach (var (dataType, number, _) in Inputs)
                if (ports.Pending(dataType, number) > 0 && await ports.ReadAsync(dataType, number, 0) is { } m) arrived.Add((dataType, number, m.Json));
            if (Arrive(arrived) is { } pav) await ports.WriteAsync(Pav, 1, pav);
        }
    }

    // What has arrived together, as one update of the avatar; null where there is nothing to send.
    public string? Arrive(IReadOnlyList<(string DataType, int Port, string Json)> arrived)
    {
        var content = new Dictionary<string, string>();
        var idChanged = false;
        foreach (var (dataType, number, json) in arrived)
        {
            var key = Key(dataType, number);
            if (dataType == Id)
            {
                JsonNode? given; try { given = JsonNode.Parse(json); } catch (System.Text.Json.JsonException) { continue; }
                var name = given is null ? null : PmxAimProcessor.NameOf(given);
                if (string.IsNullOrWhiteSpace(name) || name == id) continue;
                id = name; idChanged = true;
                foreach (var (k, v) in waiting) content.TryAdd(k, v);        // what was waiting for the ID goes with it
                waiting.Clear();
            }
            else content[key] = json;
        }

        if (id is null) { foreach (var (k, v) in content) waiting[k] = v; return null; }

        // A state that has not changed is not sent again.
        foreach (var (dataType, number, state) in Inputs.Where(i => i.State && i.DataType != Id))
        {
            var key = Key(dataType, number);
            if (!content.TryGetValue(key, out var json)) continue;
            if (sent.TryGetValue(id + "|" + key, out var before) && before == json) content.Remove(key); else sent[id + "|" + key] = json;
        }
        if (content.Count == 0 && !idChanged) return null;

        var ports = new Dictionary<string, string>(content) { [Key(Id, 1)] = JsonValue.Create(id).ToJsonString() };
        var made = pmx.ProcessAsync(new Message { MessageId = Guid.NewGuid().ToString(), MessageType = Pav, Ports = ports }).GetAwaiter().GetResult();
        return made.Ports.Values.FirstOrDefault();
    }

    private static string Key(string dataType, int number) => AIF.Controller.PortKey.Of(dataType, number);
}
