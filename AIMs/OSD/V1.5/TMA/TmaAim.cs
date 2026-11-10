using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Osd.Tma;

// The Data Types of Television Media Analysis (OSD-TMA V1.5) by Header.
public static class TmaTypes
{
    public const string Spo = "OSD-SPO-V1.5", Bso = "OSD-BSO-V1.5", Bvo = "OSD-BVO-V1.5", Txo = "OSD-TXO-V1.5", Bto = "OSD-BTO-V1.5",
        Spt = "OSD-SPT-V1.5", Bss = "OSD-BSS-V1.5", Bas = "OSD-BAS-V1.5", Bvs = "OSD-BVS-V1.5", Bms = "OSD-BMS-V1.5", Mev = "OSD-MEV-V1.5", Iid = "OSD-IID-V1.5";

    public static string NewId(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..12];

    // One absolute instant (1970 epoch, seconds), or the stretch from start to end where both are given.
    public static JsonObject SimpleTime(DateTimeOffset at, double? seconds = null) => new()
    {
        ["Header"] = "OSD-STM-V1.5",
        ["SimpleTimeID"] = NewId("st"),
        ["SimpleTimeData"] = new JsonArray(new JsonObject
        {
            ["FlagsByte"] = 1,
            ["StartTime"] = at.ToUnixTimeMilliseconds() / 1000.0,
            ["EndTime"] = at.ToUnixTimeMilliseconds() / 1000.0 + (seconds ?? 0),
            ["AccuracyMode"] = "single",
            ["AccuracyPlusMinus"] = 0.0,
            ["TimeType"] = true,
            ["TimeUnit"] = "00"
        })
    };

    public static JsonObject SpaceTime(DateTimeOffset at) => new() { ["Header"] = "OSD-SPT-V1.5", ["SpaceTimeID"] = NewId("spt"), ["Time"] = SimpleTime(at) };
}

// An AIM of Television Media Analysis. The transformation each makes is Handle, from a Datum on an Input Port to the Data it gives on Output Ports,
// so that it can be tried without the Controller; RunAsync only reads the Input Ports as Messages arrive and writes what Handle gives.
public abstract class TmaAim(string instanceId) : IAimProcessor, IAimRunner
{
    public string InstanceId { get; } = instanceId;
    public Task<Message> ProcessAsync(Message message) => throw new NotSupportedException(InstanceId + " runs continuously.");

    protected abstract (string DataType, int Port)[] Inputs { get; }
    public abstract IEnumerable<(string DataType, int Port, JsonObject Json)> Handle(string dataType, int port, JsonObject json);

    public async Task RunAsync(IAimPorts ports, AimContext context)
    {
        while (await ports.SelectAsync(-1, Inputs) is { } p)
        {
            if (await ports.ReadAsync(p.DataType, p.PortNumber, 0) is not { } m) continue;
            if (JsonNode.Parse(m.Json) is not JsonObject json) continue;
            foreach (var (dt, n, o) in Handle(p.DataType, p.PortNumber, json))
                await ports.WriteAsync(dt, n, o.ToJsonString());
        }
    }
}

// OSD-FBO-V1.5 - Full to Basic Object. A Full Object of the medium it is instanced for (here the Speech Object) is given as its Basic Objects, one by one, in order.
public sealed class FullToBasicObject(string instanceId) : TmaAim(instanceId)
{
    protected override (string, int)[] Inputs => [(TmaTypes.Spo, 1)];

    public override IEnumerable<(string DataType, int Port, JsonObject Json)> Handle(string dataType, int port, JsonObject json)
    {
        if (json["BasicSpeechObjects"] is not JsonArray basics) yield break;
        // Each entry of a Speech Object holds its Basic Speech Object as BSObjectIDOrBSObject; the Basic Speech Object is an Object, and goes on as it is.
        foreach (var entry in basics.OfType<JsonObject>())
            if (entry["BSObjectIDOrBSObject"] is JsonArray held)
                foreach (var b in held.OfType<JsonObject>())
                {
                    var basic = (JsonObject)b.DeepClone();
                    basic["Header"] = "OSD-BSO-V1.5";
                    yield return (TmaTypes.Bso, 1, basic);
                }
    }
}

// OSD-BSS-V1.5 - Basic Speech Scene Description. Each Basic Speech Object is described as the one Object of a Scene, where and when it is, as the BVS does for the picture.
public sealed class BasicSpeechSceneDescription(string instanceId) : TmaAim(instanceId)
{
    private JsonObject? spaceTime;
    protected override (string, int)[] Inputs => [(TmaTypes.Spt, 1), (TmaTypes.Bso, 1)];

    public override IEnumerable<(string DataType, int Port, JsonObject Json)> Handle(string dataType, int port, JsonObject json)
    {
        if (dataType == TmaTypes.Spt) { spaceTime = json; yield break; }
        var st = (json["BasicSpeechObjectSpaceTime"] as JsonObject ?? spaceTime ?? TmaTypes.SpaceTime(DateTimeOffset.UtcNow)).DeepClone();
        yield return (TmaTypes.Bss, 1, new JsonObject
        {
            ["Header"] = "OSD-BSS-V1.5",
            ["MInstanceID"] = (string?)json["MInstanceID"] ?? "tma",
            ["BasicSpeechSceneDescriptorsID"] = TmaTypes.NewId("bss"),
            ["BasicSpeechSceneDescriptorsSpaceTime"] = st.DeepClone(),
            ["ObjectCount"] = 1,
            ["BasicSpeechSceneDescriptors"] = new JsonArray(new JsonObject
            {
                ["ObjectSpaceTime"] = st,
                ["ObjectIDOrObject"] = new JsonArray(json.DeepClone())
            })
        });
    }
}

// OSD-MSM-V1.5 - Multimodal Scene Multiplexing. The Scenes of the speech, of the audio and of the pictures, the Recognised Text and the User ID that arrive are put in one Basic Multimodal
// Scene: one for each Scene of speech, of audio or of pictures, with the Text and the ID last received.
// combine: the Multimodal Scene it gives holds the Scene last received of EACH kind - of speech, of audio and of pictures - and not only the one that has just arrived (the HMC Entity and Context Understanding
// gives one Multimodal Scene Descriptors, whatever the modality of the Scene that came last). By default (Television Media Analysis) it is one Scene for each Scene that arrives.
public sealed class MultimodalSceneMultiplexing(string instanceId, bool combine = false) : TmaAim(instanceId)
{
    private readonly Dictionary<string, JsonObject> latest = [];
    private JsonObject? userId;
    private readonly List<JsonObject> texts = [];
    // The Ports of the L3 (1OSD-MSM-V1.5-I01): a Text Object arriving at Handle is taken too, but its Port is not read here.
    protected override (string, int)[] Inputs => [(TmaTypes.Bss, 1), (TmaTypes.Bas, 1), (TmaTypes.Bvs, 1), (TmaTypes.Bto, 1), (TmaTypes.Iid, 1)];

    public override IEnumerable<(string DataType, int Port, JsonObject Json)> Handle(string dataType, int port, JsonObject json)
    {
        switch (dataType)
        {
            case TmaTypes.Bto: texts.Add(json); yield break;
            case TmaTypes.Txo:
                // A Text Object is given as its Basic Text Objects.
                if (json["BasicTextObjects"] is JsonArray basics) texts.AddRange(basics.OfType<JsonObject>());
                else texts.Add(json);
                yield break;
            case TmaTypes.Iid: userId = json; yield break;
        }
        var st = (json["BasicSpeechSceneDescriptorsSpaceTime"] ?? json["BASSpaceTime"] ?? json["BasicVisualSceneDescriptorsSpaceTime"]) as JsonObject ?? TmaTypes.SpaceTime(DateTimeOffset.UtcNow);
        // The Scenes the Multimodal Scene holds: this one, or - combining - the one last received of each kind, in the order speech, audio, pictures.
        var scenes = new JsonArray();
        if (combine)
        {
            latest[dataType] = (JsonObject)json.DeepClone();
            foreach (var kind in new[] { TmaTypes.Bss, TmaTypes.Bas, TmaTypes.Bvs })
                if (latest.TryGetValue(kind, out var held)) scenes.Add(new JsonObject { ["BXSSpaceTime"] = st.DeepClone(), ["BXSOrBXSID"] = held.DeepClone() });
        }
        else scenes.Add(new JsonObject { ["BXSSpaceTime"] = st.DeepClone(), ["BXSOrBXSID"] = json.DeepClone() });
        var scene = new JsonObject
        {
            ["Header"] = "OSD-BMS-V1.5",
            ["MInstanceID"] = (string?)json["MInstanceID"] ?? "tma",
            ["BasicMultimodalSceneDescriptorsID"] = TmaTypes.NewId("bms"),
            ["BMSDescriptorsSpaceTime"] = st.DeepClone(),
            ["MultimodalObjectCount"] = scenes.Count,
            ["BasicMultimodalSceneDescriptorsData"] = scenes
        };
        // The Basic Texts that arrived and the User ID last received go in with the Scene, as Basic Objects of it.
        var objects = new JsonArray();
        foreach (var extra in texts.Concat(userId is null ? [] : [userId]))
            objects.Add(new JsonObject { ["BXOSpaceTime"] = st.DeepClone(), ["BXOOrBXOID"] = extra.DeepClone() });
        texts.Clear();
        if (objects.Count > 0) scene["BasicObjectsData"] = objects;
        yield return (TmaTypes.Bms, 1, scene);
    }
}

// OSD-MED-V1.5 - Multimodal Event Description. It collects the Descriptors of the Scenes that arrive into the Multimodal Event Descriptors, nothing more.
public sealed class MultimodalEventDescription(string instanceId) : TmaAim(instanceId)
{
    private readonly JsonArray collected = [];
    private readonly string id = TmaTypes.NewId("mev");
    protected override (string, int)[] Inputs => [(TmaTypes.Bms, 1)];

    public override IEnumerable<(string DataType, int Port, JsonObject Json)> Handle(string dataType, int port, JsonObject json)
    {
        collected.Add(json.DeepClone());
        yield return (TmaTypes.Mev, 1, new JsonObject
        {
            ["Header"] = "OSD-MEV-V1.5",
            ["MInstanceID"] = (string?)json["MInstanceID"] ?? "tma",
            ["MultimodalEventDescriptorsID"] = id,
            ["MultimodalEventDescriptorsTime"] = TmaTypes.SimpleTime(DateTimeOffset.UtcNow),
            ["MultimodalSceneDescriptors"] = collected.DeepClone()
        });
    }
}
