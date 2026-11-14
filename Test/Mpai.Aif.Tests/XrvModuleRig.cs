using System.Diagnostics;
using System.Text.Json.Nodes;

using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Xrv;

namespace Mpai.Aif.Tests;

// A TEST MODULE, NOT THE LIVE THEATRICAL PERFORMANCE. The Module of Action Generation and the three Status Interpretation AIMs, with the Topology the Live Theatrical Performance gives them
// (the Script, the Action Table, the Script-Venue Associations and the Participant Data to the four; the Descriptors to the Interpretation AIMs; their Statuses and the Participant Data...
// to Action Generation; the Cue Point ID of Action Generation to the three Interpretation AIMs), executed continuously. It exists only in the tests: it is written into a copy of the AMDs
// that the test makes, and is named 1TST-XRM-V1.0-I01 (a test Module); the Environment Description and the Experience Generation AIMs are not in it.
public sealed class XrvModuleRig : IDisposable
{
    public const string Module = "1TST-XRM-V1.0-I01";
    public const string Acg = XrvProvider.Acg, Pfi = XrvProvider.Pfi, Pti = XrvProvider.Pti, Osi = XrvProvider.Osi;
    private static readonly string[] Interpreters = [Pfi, Pti, Osi];

    private readonly string work = Path.Combine(Path.GetTempPath(), "xrv-" + Guid.NewGuid().ToString("N"));
    private readonly ControllerApi api;
    public string ModuleName { get; }
    public int ModuleId { get; }                                                                                     // the MODULE_ID the Controller returned when it started the Module

    // gate: the AIMs begin to read their Ports when it completes. only: a Module of one AIM, all of whose Ports are the Module's.
    public XrvModuleRig(Task? gate = null, string? only = null)
    {
        Directory.CreateDirectory(work);
        var amds = Path.Combine(work, "amds"); Directory.CreateDirectory(amds);
        foreach (var file in Directory.EnumerateFiles(Repository.Amds, "*.json")) File.Copy(file, Path.Combine(amds, Path.GetFileName(file)));
        ModuleName = only is null ? Module : "1TST-XRM-" + only[5..8] + "-V1.0-I01";
        File.WriteAllText(Path.Combine(amds, ModuleName + ".json"), (only is null ? ModuleL3() : SingleL3(only, amds)).ToJsonString());
        var settings = Path.Combine(work, "aim-settings.json"); File.WriteAllText(settings, "{}");
        api = new ControllerApi(amds, settings, new XrvProvider(gate));
        var started = api.StartFlow(ModuleName, out var moduleId);
        if (started != AifError.OK) throw new InvalidOperationException($"{ModuleName} did not start: {started}");
        ModuleId = moduleId;
    }

    public void Dispose() { try { api.StopFlow(ModuleId); } catch { } api.Dispose(); try { Directory.Delete(work, true); } catch { } }

    public AifError Write(string dataType, string json, int number = 1) => api.InputWrite(ModuleId, dataType, number, json, 10_000);

    // The text of the next Basic Text Object on an ID Port (1 RE Action ID, 2 VE Action ID, 3 Cue Point ID), null when none comes in time.
    public string? ReadId(int port, int timeoutMs = 10_000)
    {
        var read = api.OutputRead(ModuleId, Types.BasicTextObject, port, timeoutMs);
        return read.Error == AifError.OK ? TextId.Read(read.Json!).Text : null;
    }

    public List<string> ReadIds(int port, int count, int timeoutMs = 10_000)
    {
        var found = new List<string>();
        for (var i = 0; i < count; i++) { if (ReadId(port, timeoutMs) is { } id) found.Add(id); else break; }
        return found;
    }

    public string? ReadRaw(string dataType, int port = 1, int timeoutMs = 10_000)
    {
        var read = api.OutputRead(ModuleId, dataType, port, timeoutMs);
        return read.Error == AifError.OK ? read.Json : null;
    }

    // The Script, the Action Table and the Script-Venue Associations: given on the boundary before the show.
    public void Give(string script, string? table = null, string? associations = null)
    {
        Assert.Equal(AifError.OK, Write(Types.Script, script));
        Assert.Equal(AifError.OK, Write(Types.ActionTable, table ?? XrvShow.Text("ActionTable.json")));
        Assert.Equal(AifError.OK, Write(Types.ScriptVenueAssociations, associations ?? XrvShow.Text("ScriptVenueAssociations.json")));
    }

    public ControllerApi.ModuleStatus Status => api.Status(ModuleId);

    // ---- the L3 of the test Module -------------------------------------------------------------------------------------------------------------------

    private static JsonObject Port(string name, string direction, string dataType, int? number = null)
    {
        var p = new JsonObject { ["Name"] = name, ["Direction"] = direction, ["DataType"] = dataType };
        if (number is { } n) p["PortNumber"] = n;
        p["Technology"] = "Software"; p["Protocol"] = ""; p["IsRemote"] = false;
        return p;
    }

    private static JsonObject End(string aim, string dataType, int? number = null)
    {
        var e = new JsonObject { ["AIMName"] = aim, ["DataType"] = dataType };
        if (number is { } n) e["PortNumber"] = n;
        return e;
    }

    private static JsonObject Line(JsonObject from, JsonObject to) => new() { ["Output"] = from, ["Input"] = to };

    private static JsonObject Wrapper(string name, JsonArray ports, JsonArray subs, JsonArray topology) => new()
    {
        ["Identifier"] = new JsonObject { ["ImplementerID"] = "1", ["ImplementationID"] = name[1..], ["AIMName"] = name },
        ["Header"] = "TST-XRM-V1.0", ["APIProfile"] = "Basic", ["Execution"] = "Continuous",
        ["Description"] = "A TEST Module of the XRV-LTP Cue engine (not the Live Theatrical Performance): Action Generation and the Performance, Participant and Operator Status Interpretation AIMs.",
        ["ExternalPorts"] = ports, ["SubAIMs"] = subs, ["Topology"] = topology,
        ["Implementations"] = new JsonArray(new JsonObject { ["ImplementationURI"] = "https://github.com/leonardo-chiariglione/mpai-cav/tree/main/AIMs/XRV1/V1.0/Provider", ["MASPort"] = 443, ["BinaryName"] = "Mpai.Xrv", ["Architecture"] = "x86-64", ["OperatingSystem"] = "Windows", ["Version"] = "1.0", ["Source"] = "AIMStorage" }),
        ["ResourcePolicies"] = new JsonArray(new JsonObject { ["Name"] = "Memory", ["Minimum"] = "1 GB", ["Request"] = "4 GB", ["Maximum"] = "8 GB" }),
        ["DataXMData"] = new JsonObject(),
        ["Documentation"] = new JsonArray(new JsonObject { ["Type"] = "Specification", ["URI"] = "https://mpai.community/standards/mpai-xrv/ltp/v1-0/" })
    };

    private static JsonObject Sub(string aim) => new() { ["Identifier"] = new JsonObject { ["ImplementerID"] = "1", ["ImplementationID"] = aim[1..], ["AIMName"] = aim, ["Relation"] = "Internal" } };

    public static JsonObject ModuleL3()
    {
        var ports = new JsonArray();
        var topology = new JsonArray();
        string[] four = [Acg, Pfi, Pti, Osi];
        // The boundary Inputs: the Script, the Action Table, the Script-Venue Associations and the Participant Data go to the four AIMs.
        foreach (var type in new[] { Types.Script, Types.ActionTable, Types.ScriptVenueAssociations, Types.ParticipantData })
        {
            ports.Add(Port(type, "Input", type));
            foreach (var aim in four) topology.Add(Line(End("", type), End(aim, type)));
        }
        // The Descriptors of the two Environments to the Interpretation AIM of their family.
        foreach (var info in KindInfo.All)
        {
            var interpreter = info.Family switch { Family.Performance => Pfi, Family.Participant => Pti, _ => Osi };
            ports.Add(Port(info.DescriptorsKey, "Input", info.DescriptorsHeader));
            topology.Add(Line(End("", info.DescriptorsHeader), End(interpreter, info.DescriptorsHeader)));
            topology.Add(Line(End(interpreter, info.StatusHeader), End(Acg, info.StatusHeader)));
        }
        // The Cue Point ID of Action Generation (Port Number 3) to the Interpretation AIMs.
        foreach (var aim in Interpreters) topology.Add(Line(End(Acg, Types.BasicTextObject, 3), End(aim, Types.BasicTextObject)));
        // The Outputs of the Module: the RE Action ID, the VE Action ID, the Cue Point ID, the Participant Data.
        ports.Add(Port("REActionID", "Output", Types.BasicTextObject, 1));
        ports.Add(Port("VEActionID", "Output", Types.BasicTextObject, 2));
        ports.Add(Port("CuePointID", "Output", Types.BasicTextObject, 3));
        ports.Add(Port("ParticipantDataOut", "Output", Types.ParticipantData));
        for (var n = 1; n <= 3; n++) topology.Add(Line(End(Acg, Types.BasicTextObject, n), End("", Types.BasicTextObject, n)));
        topology.Add(Line(End(Acg, Types.ParticipantData), End("", Types.ParticipantData)));
        return Wrapper(Module, ports, new JsonArray(four.Select(a => (JsonNode)Sub(a)).ToArray()), topology);
    }

    // A Module of one AIM: its Ports are the Module's, one for one.
    private static JsonObject SingleL3(string aim, string amds)
    {
        var l3 = JsonNode.Parse(File.ReadAllText(Path.Combine(amds, aim + ".json")))!;
        var ports = new JsonArray(); var topology = new JsonArray();
        foreach (var p in l3["ExternalPorts"]!.AsArray())
        {
            var direction = p!["Direction"]!.GetValue<string>(); var type = p["DataType"]!.GetValue<string>(); int? number = p["PortNumber"]?.GetValue<int>();
            ports.Add(Port(p["Name"]!.GetValue<string>(), direction, type, number));
            topology.Add(direction == "Input" ? Line(End("", type, number), End(aim, type, number)) : Line(End(aim, type, number), End("", type, number)));
        }
        return Wrapper("1TST-XRM-" + aim[5..8] + "-V1.0-I01", ports, new JsonArray(Sub(aim)), topology);
    }
}
