using System.Text.Json.Nodes;

using AIF.Controller;

namespace Mpai.Paf.Vcr;

// MULTIMODAL SCENE CREATION, THE AVATAR-BASED VIDEOCONFERENCE (PAF-MSC-V1.6; the VCR of the author's figure, slide 27 of PAF Reference Models). It takes what PDX gives
// for each Portable Avatar - the Avatar's ID (OSD-IID), the Avatar (PAF-AVT: its ID, its 3D model, its animation and, where it speaks, its speech), its Spatial Attitude
// (OSD-OSA) - and the Multimodal Scene Descriptors (OSD-BMS) of the meeting, and
//  - PLACES THE AVATAR IN THE SCENE: a Basic Object of the Scene, named by the Avatar's ID, at the Space-Time of its Spatial Attitude. Every avatar placed stays, by its
//    ID, as the meeting goes on;
//  - ADDS ITS SPEECH TO ITS MOUTH: the speech of the Avatar, at the Space-Time of the avatar's mouth - its Spatial Attitude, a standard FacePointHeight (1.6 m) above;
//  - gives the Rendering the set: the Scene, the ID, the Avatar, its speech and its Spatial Attitude.
// THE DATA OF ONE PORTABLE AVATAR TRAVEL TOGETHER (the author, 2026/10/14): PDX gives, for each Portable Avatar, its ID, its Avatar and its Spatial Attitude, and the
// Avatar holds its speech; so the n-th of each is of the n-th Portable Avatar, and the Avatar says whose it is. The Speech Objects PDX also gives on their own Port are
// not needed: the Avatar has its speech. It runs continuously.
public sealed class MultimodalSceneCreation(string instanceId, IReadOnlyDictionary<string, string>? settings = null) : IAimProcessor, IAimRunner
{
    public const string Scene = "OSD-BMS-V1.5", Iid = "OSD-IID-V1.5", Avatar = "PAF-AVT-V1.6", Attitude = "OSD-OSA-V1.5", Speech = "OSD-BSO-V1.5",
                        SpaceTime = "OSD-SPT-V1.5";

    private readonly double faceHeight = settings is not null && settings.TryGetValue("FacePointHeight", out var h) && double.TryParse(h, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 1.6;
    private readonly Queue<JsonNode> ids = new(), avatars = new(), attitudes = new();
    private readonly List<(string Id, JsonObject Entry)> placed = [];            // the avatars in the Scene, in the order first placed
    private readonly List<(string Id, JsonObject Entry)> speaking = [];          // the speech of each avatar in the Scene, by the avatar's ID
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
        switch (dataType)
        {
            case Scene: scene = json.DeepClone().AsObject(); return made;
            case Speech: return made;                                            // the Avatar has its speech
            case Iid: ids.Enqueue(json); break;
            case Avatar: avatars.Enqueue(json); break;
            default: attitudes.Enqueue(json); break;
        }
        if (ids.Count == 0 || avatars.Count == 0 || attitudes.Count == 0) return made;

        var iid = ids.Dequeue(); var avatar = avatars.Dequeue(); var attitude = attitudes.Dequeue();
        var id = (string?)avatar["AvatarID"] ?? (string?)iid["InstanceIdentifierData"]?[0]?["InstanceLabel"];
        if (id is null) return made;
        var said = avatar["AvatarData"]?["SpeechObjectOrSpeechObjectID"]?.AsArray().Select(x => x?["SpeechObject"]).FirstOrDefault(x => x is not null);

        Place(id, attitude);
        if (said is not null) Hear(id, (string?)said["BasicSpeechObjectID"] ?? "speech of " + id, attitude);
        made.Add((Scene, 1, Describe().ToJsonString()));
        made.Add((Iid, 1, iid.ToJsonString()));
        made.Add((Avatar, 1, avatar.ToJsonString()));
        if (said is not null) made.Add((Speech, 1, said.ToJsonString()));
        made.Add((Attitude, 1, attitude.ToJsonString()));
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
    private void Place(string id, JsonNode attitude) => Keep(placed, id, Member(id, id + " placement", attitude));

    // The speech at the avatar's mouth: its Spatial Attitude, FacePointHeight higher; the last of the avatar is the one that is heard.
    private void Hear(string id, string speechId, JsonNode attitude)
    {
        var mouth = attitude.DeepClone().AsObject();
        if (mouth["Position"]?["CartPosition"] is JsonArray { Count: 3 } p) p[2] = Math.Round((double)p[2]! + faceHeight, 4);
        Keep(speaking, id, Member(speechId, id + " mouth", mouth));
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
