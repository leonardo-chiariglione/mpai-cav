using System.Text.Json.Nodes;

using AIF.Controller;

namespace Mpai.Paf.Vcr;

// MULTIMODAL SCENE CREATION, THE AVATAR-BASED VIDEOCONFERENCE (PAF-MSC-V1.6; the VCR of the author's figure of 2026/10/11, in which Visual Scene Creation
// is gone: what it did was minimal). For each avatar that PDX gives - its ID, the Avatar (PAF-AVT: the model and its animation), its Spatial Attitude -
// the speech PDX gives for it, and the Multimodal Scene Descriptors (OSD-BMS) of the meeting, it:
//  - PLACES THE AVATAR IN THE SCENE: a Basic Object of the Scene named by its ID - the nickname - at the Space-Time of its Spatial Attitude. The model is not
//    in the Scene: there is nobody yet to display the Scene to. Every avatar placed stays, by its ID, as the meeting goes on;
//  - ADDS ITS SPEECH TO ITS MOUTH: the speech, named by its ID, at the Space-Time of the avatar's mouth. The source of the speech of a Speaking Avatar is
//    implicitly its mouth, and where that is the model could say; a standard FacePointHeight above its Spatial Attitude (1.6 m by default) is taken;
//  - gives Response and Scene Rendering the avatar: the Avatar (PAF-AVT), its ID, its speech and its Spatial Attitude, each on its own Port - what its Speaking
//    Avatar Synthesis needs to form the Speaking Avatar (the author, 2026/10/11: a Speaking Avatar is an Avatar plus speech).
// The speech of an avatar comes straight from PDX, and may come before the rest of the avatar: it is kept, one for each Portable Avatar, and joined to the
// avatar it came with. An avatar that says nothing is given out all the same, without speech. It runs continuously.
public sealed class MultimodalSceneCreation(string instanceId, IReadOnlyDictionary<string, string>? settings = null) : IAimProcessor, IAimRunner
{
    public const string Scene = "OSD-BMS-V1.5", Iid = "OSD-IID-V1.5", Avatar = "PAF-AVT-V1.6", Attitude = "OSD-OSA-V1.5", Speech = "OSD-BSO-V1.5",
                        SpaceTime = "OSD-SPT-V1.5";

    private sealed record Whole(JsonNode? Iid = null, JsonNode? Avatar = null, JsonNode? Attitude = null);

    private readonly double faceHeight = settings is not null && settings.TryGetValue("FacePointHeight", out var h) && double.TryParse(h, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 1.6;
    private readonly Dictionary<string, Whole> waiting = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<JsonNode>> speeches = new(StringComparer.Ordinal);
    private readonly List<(string Id, JsonObject Entry)> placed = [];            // the avatars in the Scene, in the order first placed
    private readonly List<(string Id, JsonObject Entry)> speaking = [];          // the speech of each avatar in the Scene, by ID
    private JsonObject? scene;

    public string InstanceId { get; } = instanceId;
    public Task<Message> ProcessAsync(Message message) => throw new NotSupportedException("PAF-MSC runs continuously.");

    public async Task RunAsync(IAimPorts ports, AimContext context)
    {
        while (await ports.SelectAsync(-1, (Scene, 1), (Iid, 1), (Avatar, 1), (Attitude, 1), (Speech, 1)) is { } port)
        {
            if (await ports.ReadAsync(port.DataType, port.PortNumber, 0) is not { } m) continue;
            JsonNode? json;
            try { json = JsonNode.Parse(m.Json); } catch (System.Text.Json.JsonException) { json = null; }
            if (json is null) { context.Report($"A {port.DataType} that is not JSON was not used."); continue; }
            foreach (var (dataType, number, text) in Take(port.DataType, json)) await ports.WriteAsync(dataType, number, text);
        }
    }

    public List<(string DataType, int Port, string Json)> Take(string dataType, JsonNode json)
    {
        var made = new List<(string, int, string)>();
        string? id;
        switch (dataType)
        {
            case Scene: scene = json.DeepClone().AsObject(); return made;
            case Speech:
                id = (string?)json["BasicSpeechObjectID"] is { } sid ? AvatarIds.AvatarOf(sid) : null;
                if (id is not null) { if (!speeches.TryGetValue(id, out var q)) speeches[id] = q = new Queue<JsonNode>(); q.Enqueue(json); }
                return made;
            case Iid: id = (string?)json["InstanceIdentifier"]; break;
            case Avatar: id = (string?)json["AvatarID"]; break;
            default: id = (string?)json["ObjectSpatialAttitudeID"] is { } oid ? AvatarIds.AvatarOf(oid) : null; break;
        }
        if (id is null) return made;
        var part = waiting.GetValueOrDefault(id) ?? new Whole();
        part = dataType switch { Iid => part with { Iid = json }, Avatar => part with { Avatar = json }, _ => part with { Attitude = json } };
        if (part.Iid is null || part.Avatar is null || part.Attitude is null) { waiting[id] = part; return made; }
        waiting.Remove(id);

        JsonNode? said = speeches.TryGetValue(id, out var queue) && queue.Count > 0 ? queue.Dequeue() : null;
        Place(id, part.Attitude);
        if (said is not null) Hear(id, part.Attitude);
        made.Add((Scene, 1, Describe().ToJsonString()));
        made.Add((Iid, 1, part.Iid.ToJsonString()));
        made.Add((Avatar, 1, part.Avatar.ToJsonString()));
        if (said is not null) made.Add((Speech, 1, said.ToJsonString()));
        made.Add((Attitude, 1, part.Attitude.ToJsonString()));
        return made;
    }

    // A member of the Scene: the ID, at the Space-Time of a Spatial Attitude.
    private static JsonObject Member(string objectId, string spaceTimeId, JsonNode attitude) => new()
    {
        ["BXOSpaceTime"] = new JsonObject
        {
            ["Header"] = SpaceTime, ["MInstanceID"] = "", ["SpaceTimeID"] = spaceTimeId,
            ["SpatialAttitude1"] = attitude.DeepClone(), ["Time"] = attitude["SpatialAttitudeTime"]?.DeepClone() ?? new JsonObject()
        },
        ["BXOOrBXOID"] = objectId
    };

    private static void Keep(List<(string Id, JsonObject Entry)> list, string id, JsonObject entry)
    {
        var at = list.FindIndex(p => p.Id == id);
        if (at >= 0) list[at] = (id, entry); else list.Add((id, entry));
    }

    // The avatar at its Spatial Attitude; placed anew where it was placed.
    private void Place(string id, JsonNode attitude) => Keep(placed, id, Member(id, AvatarIds.Part(id, "placement"), attitude));

    // The speech at the avatar's mouth: its Spatial Attitude, FacePointHeight higher; the last of the avatar is the one that is heard.
    private void Hear(string id, JsonNode attitude)
    {
        var mouth = attitude.DeepClone().AsObject();
        if (mouth["Position"]?["CartPosition"] is JsonArray { Count: 3 } p) p[2] = Math.Round((double)p[2]! + faceHeight, 4);
        Keep(speaking, id, Member(AvatarIds.Part(id, "speech"), AvatarIds.Part(id, "mouth-st"), mouth));
    }

    // The Scene of the meeting, or an empty one, with the avatars placed in it and the speech they have said.
    private JsonObject Describe()
    {
        var s = scene?.DeepClone().AsObject() ?? new JsonObject
        {
            ["Header"] = Scene, ["MInstanceID"] = "", ["BasicAVSceneDescriptorsID"] = "meeting",
            ["BAVSDescriptorsSpaceTime"] = new JsonObject { ["Header"] = SpaceTime, ["MInstanceID"] = "", ["SpaceTimeID"] = "meeting-st" }
        };
        var objects = placed.Concat(speaking).Select(p => (JsonNode)p.Entry.DeepClone()).ToArray();
        s["BasicObjectsData"] = new JsonArray(objects);
        s["AVObjectCount"] = (s["BasicAVSceneDescriptorsData"]?.AsArray().Count ?? 0) + objects.Length;
        return s;
    }
}
