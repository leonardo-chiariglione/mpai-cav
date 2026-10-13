using System.Collections.Concurrent;
using System.Text.Json.Nodes;

using AIF.Controller;

namespace Mpai.Paf.Vcr;

// THE AVATARS OF A MEETING, BY ID (the author, 2026/10/11): an avatar is introduced once, at the start - its model, with its ID, which is its
// nickname - and referred to by its ID afterwards; a Module that has an ID has access to the data it stands for. This is where a Module of a
// client finds it.
public sealed class AvatarStore
{
    private readonly ConcurrentDictionary<string, JsonNode> models = new(StringComparer.Ordinal);

    public void Put(string avatarId, JsonNode model) => models[avatarId] = model.DeepClone();

    public JsonNode? Model(string avatarId) => models.TryGetValue(avatarId, out var m) ? m.DeepClone() : null;
}

// THE ID OF AN AVATAR ON EVERY OBJECT OF IT. The objects that leave PDX for one avatar are separate Messages on separate Ports, and nothing
// but themselves says that they belong together: each is given an ID of its own made of the Avatar ID, a '#' and what it is - "anna#attitude" -
// and the Modules after PDX tell whose it is by what comes before the '#'. The Avatar ID is the participant's nickname.
public static class AvatarIds
{
    public static string Part(string avatarId, string what) => $"{avatarId}#{what}";

    public static string AvatarOf(string objectId) => objectId.Contains('#') ? objectId[..objectId.IndexOf('#')] : objectId;
}

// PORTABLE AVATAR DE-MULTIPLEXING, THE AVATAR-BASED VIDEOCONFERENCE (PAF-PDX-V1.6; the VCR of the author's figure of 2026/10/11). One Portable
// Avatar comes in - an avatar's ID, where it is in the Audio-Visual Scene, the descriptors of its face and body, its speech and text, its Entity
// Personal Status, the scene it is in - and its parts go out, each on the Port of its Data Type:
//   Avatar ID (OSD-IID), Avatar (PAF-AVT: model and animation), Spatial Attitude (OSD-OSA), Face Descriptors (PAF-FDO), Body Descriptors (PAF-BDO),
//   Speech (OSD-BSO), Text (OSD-BTO), Language Selector (OSD-SEL), Entity Personal Status (MMC-EPS), Scene Descriptors (OSD-BMS), and the
//   Space-Time of the avatar (OSD-SPT) made of its Simple Time.
// Only the parts the Portable Avatar has go out. The model goes with the avatar's ID when it is introduced, and is kept; a Portable Avatar with the ID
// alone has the model found by it. Every object but the scene carries the Avatar ID in its own ID (AvatarIds).
// It runs continuously: each Portable Avatar is taken as it comes.
public sealed class PortableAvatarDemultiplexing(string instanceId, AvatarStore? store = null) : IAimProcessor, IAimRunner
{
    public const string Pav = "PAF-PAV-V1.6", Iid = "OSD-IID-V1.5", Avatar = "PAF-AVT-V1.6", Attitude = "OSD-OSA-V1.5", Face = "PAF-FDO-V1.6",
                        Body = "PAF-BDO-V1.6", Speech = "OSD-BSO-V1.5", Text = "OSD-BTO-V1.5", Language = "OSD-SEL-V1.5",
                        Status = "MMC-EPS-V2.5", Scene = "OSD-BMS-V1.5", SpaceTime = "OSD-SPT-V1.5";

    private readonly AvatarStore avatars = store ?? new AvatarStore();

    public string InstanceId { get; } = instanceId;
    public Task<Message> ProcessAsync(Message message) => throw new NotSupportedException("PAF-PDX runs continuously.");

    public async Task RunAsync(IAimPorts ports, AimContext context)
    {
        while (await ports.SelectAsync(-1, (Pav, 1)) is { })
        {
            if (await ports.ReadAsync(Pav, 1, 0) is not { } m) continue;
            JsonNode? pav;
            try { pav = JsonNode.Parse(m.Json); } catch (System.Text.Json.JsonException) { pav = null; }
            if (pav is null) { context.Report("A Portable Avatar that is not JSON was not demultiplexed."); continue; }
            var parts = Demultiplex(pav, out var why);
            if (why is not null) { context.Report(why); continue; }
            foreach (var (dataType, json) in parts) await ports.WriteAsync(dataType, 1, json);
        }
    }

    // The parts of one Portable Avatar, in the order they leave; why says what was missing where nothing could be made of it.
    public List<(string DataType, string Json)> Demultiplex(JsonNode pav, out string? why)
    {
        why = null;
        var parts = new List<(string, string)>();

        // The avatar: its ID (a string) and, when it is introduced, its model (an object).
        string? id = null; JsonNode? model = null;
        foreach (var item in pav["AvatarOrAvatarID"]?.AsArray() ?? [])
        {
            if (item is JsonValue v && v.TryGetValue<string>(out var s)) id ??= s;
            else if (item is JsonObject) model ??= item;
        }
        if (id is null) { why = $"Portable Avatar {(string?)pav["PortableAvatarID"]}: no Avatar ID."; return parts; }
        if (model is not null) avatars.Put(id, model); else model = avatars.Model(id);

        // The scene first: the AIM that takes the avatar whole has the Scene it is in by then.
        if (pav["AVSceneDescriptors"] is JsonObject scene) parts.Add((Scene, scene.DeepClone().ToJsonString()));
        // The speech next: the AIM that joins it to the avatar - which comes by another way - has it by the time the avatar is whole.
        if (pav["SPOOrSPOID"]?.AsArray().FirstOrDefault(x => x is JsonObject) is JsonObject spo) parts.Add((Speech, Named(spo, "BasicSpeechObjectID", id, "speech").ToJsonString()));
        var time = pav["PortableAvatarTime"];
        parts.Add((Iid, Identifier(id, time).ToJsonString()));
        if (model is not null) parts.Add((Avatar, AvatarOf(id, model, pav).ToJsonString()));
        if (pav["AvatarSpaceTime"] is JsonObject when)
            parts.Add((SpaceTime, new JsonObject { ["Header"] = SpaceTime, ["SpaceTimeID"] = AvatarIds.Part(id, "spacetime"), ["Time"] = when.DeepClone() }.ToJsonString()));
        if (pav["AvatarSpatialAttitude"] is JsonObject osa) parts.Add((Attitude, Named(osa, "ObjectSpatialAttitudeID", id, "attitude").ToJsonString()));
        if (pav["FaceDescriptorsObject"] is JsonObject fdo) parts.Add((Face, Named(fdo, "FaceDescriptorsObjectID", id, "face").ToJsonString()));
        if (pav["BodyDescriptorsObject"] is JsonObject bdo) parts.Add((Body, Named(bdo, "BodyDescriptorsObjectID", id, "body").ToJsonString()));
        if (pav["TextObject"] is JsonObject txt) parts.Add((Text, Named(txt, "BasicTextObjectID", id, "text").ToJsonString()));
        if (pav["LanguageSelector"] is JsonObject sel) parts.Add((Language, Named(sel, "SelectorID", id, "language").ToJsonString()));
        if (pav["EntityPersonalStatus"] is JsonObject eps) parts.Add((Status, Named(eps, "EntityPersonalStatusID", id, "status").ToJsonString()));
        return parts;
    }

    // THE AVATAR: its ID, its model, and its animation - the Face and Body Descriptors the Portable Avatar has - as one Avatar (PAF-AVT), the form
    // the Modules after PDX take it in (the author, 2026/10/11: an avatar is a model and its animation).
    private static JsonObject AvatarOf(string id, JsonNode model, JsonNode pav)
    {
        var data = new JsonObject { ["ModelOrModelID"] = new JsonArray(new JsonObject { ["3DModel"] = model.DeepClone() }) };
        if (pav["FaceDescriptorsObject"] is JsonObject fdo) data["FaceDescriptorsObject"] = Named(fdo, "FaceDescriptorsObjectID", id, "face");
        if (pav["BodyDescriptorsObject"] is JsonObject bdo) data["BodyDescriptorsObject"] = Named(bdo, "BodyDescriptorsObjectID", id, "body");
        return new JsonObject { ["Header"] = Avatar, ["AvatarID"] = id, ["AvatarData"] = data };
    }

    // The same object with its ID made of the Avatar ID.
    private static JsonNode Named(JsonObject o, string idProperty, string avatarId, string what)
    {
        var copy = o.DeepClone().AsObject();
        copy[idProperty] = AvatarIds.Part(avatarId, what);
        return copy;
    }

    // The Avatar ID as an Instance Identifier: the nickname, at the time of the Portable Avatar.
    private static JsonObject Identifier(string id, JsonNode? time)
    {
        var iid = new JsonObject
        {
            ["Header"] = Iid, ["MInstanceID"] = "", ["InstanceIdentifier"] = id, ["ObjectID"] = id,
            ["InstanceIdentifierData"] = new JsonArray(new JsonObject
            {
                ["InstanceLabel"] = id, ["LabelConfidenceLevel"] = 1,
                ["Taxonomy"] = new JsonObject { ["TaxonomyLevelIDs"] = new JsonArray("Avatar", id), ["TaxonomyDataURI"] = "https://mpai.community/standards/mpai-paf/" }
            })
        };
        if (time is not null) iid["InstanceTime"] = time.DeepClone();
        return iid;
    }
}

// THE AIMs OF THE VCR, as the Controller asks for them by their AIM Instance. The composite - the VCR itself - is a graph the Controller builds
// from its Metadata. The avatars are kept in one store for the AIMs of a client.
public sealed class VcrProvider : IAimProvider
{
    public const string Pdx = "1PAF-PDX-V1.6-I01", Msc = "1PAF-MSC-V1.6-I01";

    private readonly AvatarStore avatars;

    public VcrProvider(AvatarStore? store = null) => avatars = store ?? new AvatarStore();

    public bool CanCreate(string aimName) => aimName is Pdx or Msc;

    public string? ImplementationOf(string aimName) => CanCreate(aimName) ? typeof(VcrProvider).Assembly.Location : null;

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage) => aimName switch
    {
        Pdx => new PortableAvatarDemultiplexing(aimName, avatars),
        Msc => new MultimodalSceneCreation(aimName, settings),
        _ => throw new ArgumentException($"{aimName} is not an AIM of the VCR.")
    };
}
