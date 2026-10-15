using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Paf.Pdx;

namespace Mpai.Paf.Vcr;

// VIDEOCONFERENCE CLIENT RECEIVER (PAF-VCR-V1.6; slide 22 of PAF Reference Models) - ONE AIM, the former Multimodal Scene Creation. In terms of Portable Avatars it adds
// the speech to the avatars, and puts the avatars where they sit. It is the receiving end of a participant's PC.
//
// It takes the Portable Avatars the Avatar Videoconference Server sends. A Portable Avatar is an UPDATE of an avatar: it names the avatar (its ID) and carries what changed;
// the Avatar Model is sent once, at the introduction. The receiver therefore HOLDS THE STATE OF EACH AVATAR BY ITS ID, and merges every update into it. To do any work on a
// Portable Avatar the receiver demultiplexes it (the routine of PAF-PDX: Mpai.Paf.Pdx.PdxAimProcessor.IdOf); the demultiplexing does not surface.
//
//  - PLACES THE AVATAR: a Basic Object of the Scene, named by the Avatar's ID, at the Space-Time of the Spatial Attitude the Portable Avatar carries (the server's PMX put
//    it there). An avatar stays in the Scene, by its ID, as the meeting goes on. The Scene is the room the Portable Avatar carries (AV Scene Descriptors), else an empty one.
//  - ADDS ITS SPEECH TO ITS MOUTH: the speech the avatar utters, at the Space-Time of its mouth - its Spatial Attitude, a standard FacePointHeight (1.6 m) above.
//  - gives out the Multimodal Scene Descriptors and the Portable Avatar - the avatar as the receiver holds it, speech included. Where nothing says what the avatar looks like
//    (the Portable Avatar has never carried a model) it is a DEFAULT AVATAR that is shown: the model named by the setting DefaultAvatar.
//  - The Point of View and the Text go through, unchanged.
// It runs continuously.
public sealed class VideoconferenceClientReceiver(string instanceId, IReadOnlyDictionary<string, string>? settings = null) : IAimProcessor, IAimRunner
{
    public const string Scene = "OSD-BMS-V1.5", Avatar = "PAF-PAV-V1.6", PointOfView = "OSD-OPV-V1.5", Text = "OSD-BTO-V1.5", SpaceTime = "OSD-SPT-V1.5";

    private readonly double faceHeight = settings is not null && settings.TryGetValue("FacePointHeight", out var h) && double.TryParse(h, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 1.6;
    private readonly string defaultAvatar = settings is not null && settings.TryGetValue("DefaultAvatar", out var d) && !string.IsNullOrWhiteSpace(d) ? d : "default-avatar";
    private readonly List<(string Id, JsonObject State)> avatars = [];           // the avatars of the meeting, by ID, in the order first seen
    private JsonNode? room;                                                      // the AV Scene Descriptors the Portable Avatars carried

    public string InstanceId { get; } = instanceId;
    public Task<Message> ProcessAsync(Message message) => throw new NotSupportedException("PAF-VCR runs continuously.");

    public async Task RunAsync(IAimPorts ports, AimContext context)
    {
        while (await ports.SelectAsync(-1, (Avatar, 1), (PointOfView, 1), (Text, 1)) is { } port)
        {
            if (await ports.ReadAsync(port.DataType, port.PortNumber, 0) is not { } m) continue;
            JsonNode? json;
            try { json = JsonNode.Parse(m.Json); } catch (System.Text.Json.JsonException) { json = null; }
            if (json is null) { context.Report($"A {port.DataType} that is not JSON was not used."); continue; }
            foreach (var (dataType, number, text) in Take(port.DataType, json)) await ports.WriteAsync(dataType, number, text);
        }
    }

    // The Point of View and the Text go through; a Portable Avatar updates the avatar it names.
    public List<(string DataType, int Port, string Json)> Take(string dataType, JsonNode json)
    {
        var made = new List<(string, int, string)>();
        if (dataType == PointOfView || dataType == Text) { made.Add((dataType, 1, json.ToJsonString())); return made; }
        if (json is not JsonObject update) return made;

        var id = PdxAimProcessor.IdOf(update, update["AvatarOrAvatarID"] as JsonArray ?? new JsonArray());
        if (id.Length == 0) return made;
        if (update["AVSceneDescriptors"] is JsonObject scene && (string?)scene["Header"] == Scene) room = scene.DeepClone();

        var at = avatars.FindIndex(a => a.Id == id);
        if (at < 0) { avatars.Add((id, new JsonObject())); at = avatars.Count - 1; }
        Merge(avatars[at].State, update);
        var state = avatars[at].State;

        made.Add((Scene, 1, Describe().ToJsonString()));
        made.Add((Avatar, 1, Shown(id, state).ToJsonString()));
        return made;
    }

    // An update into the state of the avatar: what the update carries replaces what the receiver held; what it does not carry stays. The Avatar's ID and its Model are one list
    // (AvatarOrAvatarID): an update that carries only the ID keeps the Model the receiver holds.
    private static void Merge(JsonObject state, JsonObject update)
    {
        foreach (var (name, value) in update)
        {
            if (value is null) continue;
            if (name == "AvatarOrAvatarID" && value is JsonArray fresh && state[name] is JsonArray held)
            {
                var carriesModel = fresh.OfType<JsonObject>().Any();
                if (!carriesModel) { var model = held.OfType<JsonObject>().FirstOrDefault(); if (model is not null) fresh.Add(model.DeepClone()); }
            }
            state[name] = value.DeepClone();
        }
    }

    // The avatar as it is shown: with its Model, or the default avatar where it has none.
    private JsonObject Shown(string id, JsonObject state)
    {
        var shown = state.DeepClone().AsObject();
        var list = shown["AvatarOrAvatarID"] as JsonArray ?? new JsonArray(id);
        if (!list.OfType<JsonObject>().Any())
            list.Add(new JsonObject { ["Header"] = "OSD-B3O-V1.5", ["Basic3DModelObjectID"] = defaultAvatar });
        shown["AvatarOrAvatarID"] = list;
        return shown;
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

    // The speech at the avatar's mouth: its Spatial Attitude, FacePointHeight higher.
    private JsonObject Mouth(string id, string speechId, JsonNode attitude)
    {
        var mouth = attitude.DeepClone().AsObject();
        if (mouth["Position"]?["CartPosition"] is JsonArray { Count: 3 } p) p[2] = Math.Round((double)p[2]! + faceHeight, 4);
        return Member(speechId, id + " mouth", mouth);
    }

    // The Scene of the meeting - the room the Portable Avatars carried, or an empty one - with the avatars placed in it, and the speech of those who have said something.
    private JsonObject Describe()
    {
        var s = room?.DeepClone().AsObject() ?? new JsonObject
        {
            ["Header"] = Scene, ["MInstanceID"] = "", ["BasicAVSceneDescriptorsID"] = "meeting",
            ["BAVSDescriptorsSpaceTime"] = new JsonObject { ["Header"] = SpaceTime, ["MInstanceID"] = "", ["SpaceTimeID"] = "meeting-st" }
        };
        var placed = new List<JsonNode>(); var speaking = new List<JsonNode>();
        foreach (var (id, state) in avatars)
        {
            if (state["AvatarSpatialAttitude"] is not { } attitude) continue;             // not seated yet: not in the Scene
            placed.Add(Member(id, id + " placement", attitude));
            var said = state["SPOOrSPOID"]?.AsArray().Select(x => x is JsonObject o ? (o["SpeechObject"] ?? o) : null).FirstOrDefault(x => x is not null);
            if (said is not null) speaking.Add(Mouth(id, (string?)said["BasicSpeechObjectID"] ?? "speech of " + id, attitude));
        }
        var objects = placed.Concat(speaking).ToArray();
        s["BasicObjectsData"] = new JsonArray(objects);
        s["AVObjectCount"] = (s["BasicAVSceneDescriptorsData"]?.AsArray().Count ?? 0) + objects.Length;
        return s;
    }
}
