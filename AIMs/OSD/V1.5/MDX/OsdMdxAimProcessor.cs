using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Osd.Mdx;

// OSD-MDX-V1.5 - Multimodal Scene Demultiplexing.
//
// A demultiplexer extracts from a composite data type what it is made of. It does the same work on a Basic Multimodal Scene Descriptors (OSD-BMS) as on a Multimodal Scene Descriptors (OSD-MSD), and the Objects and
// Scenes it gives are Basic or full as they stand in what it received: a Basic Speech Object leaves as a Basic Speech Object, a Speech Object as a Speech Object.
//
// It gives: the Space-Time of the scene; the Speech, Text, Audio and Visual Objects, as many of each as the scene holds, in the order they stand (a datum after the other on the Port of its kind, as an AIM that runs
// gives them - IAimRunner; run as a processor for one Message, it gives the first of each); the Face Objects (the Visual Objects whose Visual Object Type is Face, Port 2 of the Visual Objects); the Face and Body
// Descriptors; every Audio Scene Descriptors; and the geometry of every Audio, Speech and Visual Scene - where each member is, with its qualifier - Basic for a Basic Scene, full for a full one.
//
// It reads the data by the Header of each element, not by where it stands. The Objects that stand in the scene directly are the ones given; where there are none of a kind, those inside its Scenes.
// What the scene does not hold is not produced. The geometry of a full scene lists all its members, those of its sub-scenes included (the sub-scenes are not given as a structure of their own).
public sealed class OsdMdxAimProcessor : IAimProcessor, IAimRunner
{
    private readonly string _instanceId;
    private readonly AimPortReader _ports;

    public OsdMdxAimProcessor(string instanceId, AimPortReader ports)
    {
        _instanceId = instanceId;
        _ports = ports;
    }

    public string InstanceId => _instanceId;

    private sealed record Found(string Header, JsonObject Node, bool InScene);

    private static readonly HashSet<string> Roots = ["OSD-BMS-V1.5", "OSD-MSD-V1.5"];
    private static readonly HashSet<string> AudioScenes = ["OSD-BAS-V1.5", "OSD-ASD-V1.5"], SpeechScenes = ["OSD-BSS-V1.5", "OSD-SSD-V1.5"], VisualScenes = ["OSD-BVS-V1.5", "OSD-VSD-V1.5"];
    private static readonly HashSet<string> Scenes = [.. AudioScenes, .. SpeechScenes, .. VisualScenes, "OSD-LSD-V1.5", "OSD-BLS-V1.5", "OSD-MSD-V1.5"];
    private static readonly HashSet<string> SpeechObjects = ["OSD-BSO-V1.5", "OSD-SPO-V1.5"], TextObjects = ["OSD-BTO-V1.5", "OSD-TXO-V1.5"], AudioObjects = ["OSD-BAO-V1.5", "OSD-AUO-V1.5"], VisualObjects = ["OSD-BVO-V1.5", "OSD-VIO-V1.5"];

    // What the scene holds, kind by kind, in the order it stands there: (Data Type, Port Number, the datum).
    public static IReadOnlyList<(string DataType, int Port, string Json)> Demultiplexed(string json)
    {
        JsonObject? root;
        try { root = JsonNode.Parse(json) as JsonObject; }
        catch (System.Text.Json.JsonException) { root = null; }
        if (root is null || (string?)root["Header"] is not { } header || !Roots.Contains(header))
            throw new FormatException("what was received is not Multimodal Scene Descriptors, Basic or full (OSD-BMS-V1.5, OSD-MSD-V1.5)");

        var found = new List<Found>();
        Walk(root, false, found, isRoot: true);
        // The Objects of a kind that stand in the scene directly; where there are none, the ones inside its Scenes.
        List<Found> All(Func<Found, bool> where)
        {
            var of = found.Where(where).ToList();
            return (of.Any(f => !f.InScene) ? of.Where(f => !f.InScene) : of).ToList();
        }
        bool IsFace(Found f) => VisualObjects.Contains(f.Header) && (string?)f.Node["VisualQualifier"]?["Attributes"]?["VisualObjectType"] == "Face";

        var given = new List<(string, int, string)>();
        void Give(string dataType, int port, JsonNode? node) { if (node is not null) given.Add((dataType, port, node.ToJsonString())); }
        Give("OSD-SPT-V1.5", 1, root[header == "OSD-BMS-V1.5" ? "BMSDescriptorsSpaceTime" : "MultimodalSceneDescriptorsSpaceTime"]);
        foreach (var o in All(f => SpeechObjects.Contains(f.Header))) Give(o.Header, 1, o.Node);
        foreach (var o in All(f => TextObjects.Contains(f.Header))) Give(o.Header, 1, o.Node);
        foreach (var o in All(f => AudioObjects.Contains(f.Header))) Give(o.Header, 1, o.Node);
        foreach (var o in All(f => VisualObjects.Contains(f.Header) && !IsFace(f))) Give(o.Header, 1, o.Node);
        foreach (var o in All(IsFace)) Give(o.Header, 2, o.Node);
        foreach (var o in All(f => f.Header == "PAF-FDO-V1.6")) Give(o.Header, 1, o.Node);
        foreach (var o in All(f => f.Header == "PAF-BDO-V1.6")) Give(o.Header, 1, o.Node);
        foreach (var scene in found.Where(f => AudioScenes.Contains(f.Header)))
        {
            Give(scene.Header, 1, scene.Node);
            Give(scene.Header == "OSD-BAS-V1.5" ? "OSD-BAG-V1.5" : "OSD-ASG-V1.5", 1, Geometry(scene.Node, scene.Header == "OSD-BAS-V1.5", "Audio"));
        }
        foreach (var scene in found.Where(f => SpeechScenes.Contains(f.Header)))
            Give(scene.Header == "OSD-BSS-V1.5" ? "OSD-BSG-V1.5" : "OSD-SSG-V1.5", 1, Geometry(scene.Node, scene.Header == "OSD-BSS-V1.5", "Speech"));
        foreach (var scene in found.Where(f => VisualScenes.Contains(f.Header)))
            Give(scene.Header == "OSD-BVS-V1.5" ? "OSD-BVG-V1.5" : "OSD-VSG-V1.5", 1, Geometry(scene.Node, scene.Header == "OSD-BVS-V1.5", "Visual"));
        return given;
    }

    // THE PROCESSOR: one Message in, one out - the first of each kind.
    public Task<Message> ProcessAsync(Message message)
    {
        var input = _ports.Input("OSD-BMS-V1.5");
        if (!message.Ports.TryGetValue(input, out var json) || string.IsNullOrWhiteSpace(json))
            return Task.FromResult(Message.Error(message.MessageId, _instanceId, "no Multimodal Scene Descriptors on input port"));
        IReadOnlyList<(string DataType, int Port, string Json)> all;
        try { all = Demultiplexed(json); }
        catch (FormatException e) { return Task.FromResult(Message.Error(message.MessageId, _instanceId, e.Message)); }
        var ports = new Dictionary<string, string>();
        foreach (var (dataType, port, datum) in all) ports.TryAdd(_ports.Output(dataType, port), datum);
        return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = message.MessageType, Ports = ports });
    }

    // THE RUNNER: each scene that arrives is taken apart, and every datum written on its Port, one after the other.
    public async Task RunAsync(IAimPorts ports, AimContext context)
    {
        while (await ports.ReadAsync("OSD-BMS-V1.5", 1, -1) is { } scene)
        {
            IReadOnlyList<(string DataType, int Port, string Json)> all;
            try { all = Demultiplexed(scene.Json); }
            catch (FormatException e) { context.Report(e.Message); continue; }
            foreach (var (dataType, port, datum) in all) await ports.WriteAsync(dataType, port, datum);
        }
    }

    // Every element that has a Header, in the order they stand; those inside a Scene are marked. An element is not searched inside once found, except a Scene, which holds Objects.
    private static void Walk(JsonNode? node, bool inScene, List<Found> found, bool isRoot = false)
    {
        switch (node)
        {
            case JsonObject o:
                var header = o["Header"] is JsonValue v && v.TryGetValue<string>(out var h) ? h : null;
                var scene = !isRoot && header is not null && Scenes.Contains(header);
                if (header is not null && !isRoot) found.Add(new Found(header, o, inScene));
                if (header is not null && !isRoot && !scene) return;        // an Object: what it holds is its data
                foreach (var (_, child) in o) Walk(child, inScene || scene, found);
                break;
            case JsonArray a:
                foreach (var child in a) Walk(child, inScene, found);
                break;
        }
    }

    // The members of a Scene, each with where it is and its qualifier: at any depth, an entry of an array that has a Space-Time of its own and holds an Object.
    private static List<(JsonNode Space, JsonNode Qualifier)> Members(JsonNode? node)
    {
        var members = new List<(JsonNode, JsonNode)>();
        void Visit(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject o:
                    var space = o.FirstOrDefault(p => p.Key.EndsWith("SpaceTime", StringComparison.Ordinal) && p.Value is JsonObject).Value;
                    var held = o.Where(p => p.Value is JsonArray).SelectMany(p => ((JsonArray)p.Value!).OfType<JsonObject>()).FirstOrDefault(m => m["Header"] is not null && !Scenes.Contains((string)m["Header"]!));
                    var qualifier = held?.FirstOrDefault(p => p.Key.EndsWith("Qualifier", StringComparison.Ordinal) && p.Value is JsonObject).Value;
                    if (space is not null && qualifier is not null) { members.Add((space, qualifier)); return; }
                    foreach (var (_, child) in o) Visit(child);
                    break;
                case JsonArray a:
                    foreach (var child in a) Visit(child);
                    break;
            }
        }
        foreach (var (_, child) in (JsonObject)node!) Visit(child);
        return members;
    }

    private static string IdOf(JsonObject scene, string medium) =>
        scene.FirstOrDefault(p => p.Key.EndsWith("ID", StringComparison.Ordinal) && p.Key.Contains(medium, StringComparison.Ordinal) && p.Value is JsonValue).Value?.ToString()
        ?? scene.FirstOrDefault(p => p.Key.EndsWith("ID", StringComparison.Ordinal) && p.Key != "MInstanceID" && p.Key != "UEnvironmentID" && p.Value is JsonValue).Value?.ToString()
        ?? Guid.NewGuid().ToString();

    private static JsonNode? SpaceOf(JsonObject scene) =>
        scene.FirstOrDefault(p => p.Key.EndsWith("SpaceTime", StringComparison.Ordinal) && p.Value is JsonObject).Value?.DeepClone();

    // The geometry of a Scene of a medium, Basic or full as the Scene is: the Basic one of a Basic Scene, the full one of a full Scene (BAG/ASG, BSG/SSG, BVG/VSG).
    private static JsonObject Geometry(JsonObject scene, bool basic, string medium)
    {
        var members = Members(scene);
        var geometry = new JsonObject { ["Header"] = "OSD-" + (basic ? "B" : "") + medium switch { "Audio" => "A", "Speech" => "S", _ => "V" } + (basic || medium != "Audio" && medium != "Speech" && medium != "Visual" ? "G" : "G") + "-V1.5" };
        // the Header of each: BAG, BSG, BVG for Basic; ASG, SSG, VSG for full
        geometry["Header"] = medium switch
        {
            "Audio" => basic ? "OSD-BAG-V1.5" : "OSD-ASG-V1.5",
            "Speech" => basic ? "OSD-BSG-V1.5" : "OSD-SSG-V1.5",
            _ => basic ? "OSD-BVG-V1.5" : "OSD-VSG-V1.5"
        };
        if ((string?)scene["MInstanceID"] is { } m) geometry["MInstanceID"] = m;
        var space = SpaceOf(scene);
        var id = IdOf(scene, medium);
        var objects = new JsonArray();
        switch (medium, basic)
        {
            case ("Audio", true):
                geometry["AudioSceneGeometryID"] = id; geometry["AudioObjectCount"] = members.Count;
                if (space is not null) geometry["AudioSceneGeometrySpaceTime"] = space;
                foreach (var (s, q) in members) objects.Add(new JsonObject { ["BasicAudioSceneObjectSpaceTime"] = s.DeepClone(), ["AudioSceneObjectQualifiers"] = q.DeepClone() });
                geometry["BasicAudioSceneObjects"] = objects;
                break;
            case ("Audio", false):
                geometry["AudioSceneGeometryID"] = id; geometry["ObjectCount"] = members.Count;
                if (space is not null) geometry["AudioSceneGeometrySpaceTime"] = space;
                foreach (var (s, q) in members) objects.Add(new JsonObject { ["AudioSceneObjectSpaceTime"] = s.DeepClone(), ["AudioSceneObjectQualifier"] = q.DeepClone() });
                geometry["AudioSceneObjects"] = objects;
                break;
            case ("Speech", true):
                geometry["SpeechSceneGeometryID"] = id; geometry["SpeechObjectCount"] = members.Count;
                if (space is not null) geometry["SpeechSceneGeometrySpaceTime"] = space;
                foreach (var (s, q) in members) objects.Add(new JsonObject { ["BasicSpeechSceneObjectSpaceTime"] = s.DeepClone(), ["BasicSpeechSceneObjectQualifiers"] = q.DeepClone() });
                geometry["BasicSpeechSceneObjects"] = objects;
                break;
            case ("Speech", false):
                geometry["SpeechSceneGeometryID"] = id; geometry["ObjectCount"] = members.Count;
                if (space is not null) geometry["SpeechSceneGeometrySpaceTime"] = space;
                foreach (var (s, q) in members) objects.Add(new JsonObject { ["SpeechSceneObjectSpaceTime"] = s.DeepClone(), ["SpeechSceneObjectQualifier"] = q.DeepClone() });
                geometry["SpeechSceneObjects"] = objects;
                break;
            case ("Visual", true):
                geometry["BasicVisualSceneGeometryID"] = id; geometry["VisualObjectCount"] = members.Count;
                geometry["BasicVisualSceneGeometrySpaceTime"] = space ?? new JsonObject { ["Header"] = "OSD-SPT-V1.5", ["SpaceTimeID"] = Guid.NewGuid().ToString() };
                foreach (var (s, q) in members) objects.Add(new JsonObject { ["VisualObjectSpaceTime"] = s.DeepClone(), ["VisualObjectQualifier"] = q.DeepClone() });
                geometry["VisualObjects"] = objects;
                break;
            default:
                geometry["VisualSceneGeometryID"] = id; geometry["ObjectCount"] = members.Count;
                geometry["VisualSceneGeometrySpaceTime"] = space ?? new JsonObject { ["Header"] = "OSD-SPT-V1.5", ["SpaceTimeID"] = Guid.NewGuid().ToString() };
                foreach (var (s, q) in members)
                    objects.Add(new JsonObject { ["ObjectSpaceTime"] = s.DeepClone(), ["ObjectIDOrGeometry"] = new JsonArray(new JsonObject { ["ObjectSpaceTime"] = s.DeepClone(), ["ObjectQualifier"] = q.DeepClone() }) });
                geometry["Objects"] = objects;
                break;
        }
        return geometry;
    }
}

public sealed class MdxPlugin : IAimPlugin
{
    public string AimName => "OSD-MDX-V1.5";
    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings) => new OsdMdxAimProcessor(AimName, ports);
}
