using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;
using AIF.SharedStorage;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Osd.Mse;

// OSD-MSE-V1.5 - Multimodal Scene Editing, for every medium, hence in MPAI-OSD (the author, 2026/10/04: "we need a
// new aim that take basic scenes from at least two different media scenes and create
// a BMS"; 2026/10/05: it "must be able to integrate objects and scenes"; 2026/10/26: it
// is Multimodal Scene Editing, and its Scenes and Objects are the non-Basic ones). Scenes
// of any medium - Audio, Visual, Speech, LiDAR, 3D Model, RADAR, Ultrasound, Offline Map - on
// two Scene Ports, Objects of any medium on two Object Ports, a User Command and a User
// PoV in; the Multimodal Scene (OSD-MSD) as edited, and the User Command with its
// response, out. A voice over music is an MSD of an ASD and an SSD, or of an AUO and an
// SPO. In Audio Scene Management, Scene Port 1 takes the Audio Scene, Scene Port 2 the
// Speech Scene; no Object arrives on the Object Ports there.
//
// OPEN BY PORT, ACT BY COMMAND, as the Scene Editing AIMs do. A User Command acts on
// the open Multimodal Scene - the first member added to none open creates one:
//
//   AddedObjects     place the named Scenes and Objects in the common frame
//   MovedObjects     old attitude -> new attitude of a placed member
//   ChangedObjects   where a placed member is
//   RemovedObjects   take placed members out
//   UserPoV          where the Multimodal Scene is heard and seen from (also on its Port)
//
// A Scene is a sub-scene of the Multimodal Scene (SubAudioScenes, SubSpeechScenes ...), an
// Object one of its Objects (AudioObjects, SpeechObjects ...); which one a named member
// is, its Header says. The Basic Scenes and Basic Objects are held the same way (the
// Data Types of these lists take them): a Scene or Object that Shared Storage holds
// under its identifier, and no Port brought, can be named. The Multimodal Scene has
// no list for LiDAR, RADAR, Ultrasound and Offline Map: a command that places one is
// reported as failed.
//
// MULTIMODAL: the Scene is output when its members are of at least two media.
//
// THE MEMBERS BEING EDITED ARE FOLLOWED. A Scene or an Object is edited in new
// versions; the one arriving on a Port is the one now open there. A member that is the
// version last arrived on that Port is replaced by the new one - a new version of the
// Multimodal Scene - so it holds its members as they now are.
//
// Every edit is a new version in Shared Storage (MSD000001 ...). A member is held by
// its identifier; each is kept as it last arrived - complete, as its Editing AIM gave
// it - and output so.
public sealed class MseAimProcessor : IAimProcessor
{
    // What a Multimodal Scene holds, by Header: its medium, the field that carries its
    // identifier, whether it is an Object (else a Scene), and the list of the Multimodal
    // Scene that takes it with the names of that list's two members (null: it has none).
    private sealed record Kind(string Medium, string IdField, bool IsObject, string? List, string? SpaceTimeField, string? MemberField);

    private static Kind SceneOf(string medium, string idField, string? name) =>
        new(medium, idField, false, name is null ? null : $"Sub{name}Scenes", name is null ? null : $"Sub{name}SceneSpaceTime", name is null ? null : $"Sub{name}Scene");

    private static Kind ObjectOf(string medium, string idField, string? name) =>
        new(medium, idField, true, name is null ? null : $"{name}Objects", name is null ? null : $"{name}ObjectSpaceTime", name is null ? null : $"{name}Object");

    private static readonly Dictionary<string, Kind> Kinds = new()
    {
        ["OSD-ASD-V1.5"] = SceneOf("Audio",       "AudioSceneDescriptorsID",           "Audio"),
        ["OSD-VSD-V1.5"] = SceneOf("Visual",      "VisualDescriptorsID",               "Visual"),
        ["OSD-SSD-V1.5"] = SceneOf("Speech",      "SpeechSceneDescriptorsID",          "Speech"),
        ["OSD-LSD-V1.5"] = SceneOf("LiDAR",       "LiDARSceneDescriptorsID",           null),
        ["OSD-3SD-V1.5"] = SceneOf("3D Model",    "3DModelSceneDescriptorsID",         "3DModel"),
        ["OSD-RSD-V1.5"] = SceneOf("RADAR",       "RADARSceneDescriptorsID",           null),
        ["OSD-USD-V1.5"] = SceneOf("Ultrasound",  "USDescriptorsID",                   null),
        ["OSD-OSD-V1.5"] = SceneOf("Offline Map", "OMSDescriptorsID",                  null),
        ["OSD-AUO-V1.5"] = ObjectOf("Audio",      "AudioObjectID",                     "Audio"),
        ["OSD-VIO-V1.5"] = ObjectOf("Visual",     "VisualObjectID",                    "Visual"),
        ["OSD-SPO-V1.5"] = ObjectOf("Speech",     "SpeechObjectID",                    "Speech"),
        ["OSD-3DO-V1.5"] = ObjectOf("3D Model",   "3DModelObjectID",                   "3DModel"),
        ["OSD-RAO-V1.5"] = ObjectOf("RADAR",      "RADARObjectID",                     null),
        ["OSD-OMO-V1.5"] = ObjectOf("Offline Map","OfflineMapObjectID",                null),
        // The Basic Scenes and Basic Objects, which the same lists take.
        ["OSD-BAS-V1.5"] = SceneOf("Audio",       "BasicAudioSceneDescriptorsID",      "Audio"),
        ["OSD-BVS-V1.5"] = SceneOf("Visual",      "BasicVisualSceneDescriptorsID",     "Visual"),
        ["OSD-BSS-V1.5"] = SceneOf("Speech",      "BasicSpeechSceneDescriptorsID",     "Speech"),
        ["OSD-BLS-V1.5"] = SceneOf("LiDAR",       "BasicLiDARSceneDescriptorsID",      null),
        ["OSD-B3S-V1.5"] = SceneOf("3D Model",    "Basic3DModelSceneDescriptorsID",    "3DModel"),
        ["OSD-BRS-V1.5"] = SceneOf("RADAR",       "BasicRADARSceneDescriptorsID",      null),
        ["OSD-BUS-V1.5"] = SceneOf("Ultrasound",  "BasicUltrasoundSceneDescriptorsID", null),
        ["OSD-BOS-V1.5"] = SceneOf("Offline Map", "BasicOfflineMapSceneDescriptorsID", null),
        ["OSD-BAO-V1.5"] = ObjectOf("Audio",      "BasicAudioObjectID",                "Audio"),
        ["OSD-BVO-V1.5"] = ObjectOf("Visual",     "BasicVisualObjectID",               "Visual"),
        ["OSD-BSO-V1.5"] = ObjectOf("Speech",     "BasicSpeechObjectID",               "Speech"),
        ["OSD-B3O-V1.5"] = ObjectOf("3D Model",   "Basic3DModelObjectID",              "3DModel"),
        ["OSD-BLO-V1.5"] = ObjectOf("LiDAR",      "BasicLiDARObjectID",                null),
        ["OSD-BRO-V1.5"] = ObjectOf("RADAR",      "BasicRADARObjectID",                null),
        ["OSD-BUO-V1.5"] = ObjectOf("Ultrasound", "BasicUltrasoundObjectID",           null),
        ["OSD-BOO-V1.5"] = ObjectOf("Offline Map","BasicOfflineMapObjectID",           null)
    };

    // The lists of the Multimodal Scene, Scenes first, then Objects: (list, Space-Time field, member field).
    private static readonly (string List, string SpaceTime, string Member)[] Lists =
        Kinds.Values.Where(k => k.List is not null).Select(k => (k.List!, k.SpaceTimeField!, k.MemberField!)).Distinct()
             .OrderBy(l => l.Item1.StartsWith("Sub", StringComparison.Ordinal) ? 0 : 1).ToArray();

    // A member of the open Scene, Scene or Object alike.
    private sealed record Member(string Id, SpaceTime Place);

    private readonly ISharedStorage _storage;
    private readonly string _mInstanceId;

    private readonly string _commandPort;
    private readonly string[] _inPorts;     // Scene Port 1, Scene Port 2, Object Port 1, Object Port 2
    private readonly string _povPort;
    private readonly string _sceneOut;
    private readonly string _reportOut;

    private string? _open;
    private readonly UndoJournal _undo = new();
    private readonly string?[] _last;       // the version last arrived on each Port

    public string InstanceId { get; }

    public MseAimProcessor(string instanceId, ISharedStorage storage, AimPortReader ports, string mInstanceId = "")
    {
        InstanceId   = instanceId;
        _storage     = storage;
        _mInstanceId = mInstanceId;
        _commandPort = ports.Input("CAE-UCM-V1.0");
        // Each Port takes any medium: a Scene Port is found by the first Data Type it
        // lists (OSD-ASD), an Object Port by its own first (OSD-AUO).
        _inPorts     = [ports.InputOrDefault("OSD-ASD-V1.5", 1, ""), ports.InputOrDefault("OSD-ASD-V1.5", 2, ""),
                        ports.InputOrDefault("OSD-AUO-V1.5", 1, ""), ports.InputOrDefault("OSD-AUO-V1.5", 2, "")];
        _last        = new string?[_inPorts.Length];
        _povPort     = ports.InputOrDefault("OSD-OPV-V1.5", "");
        _sceneOut    = ports.Output("OSD-MSD-V1.5");
        _reportOut   = ports.OutputOrDefault("CAE-UCM-V1.0", "");
    }

    public Task<Message> ProcessAsync(Message message)
    {
        var outputs = new Dictionary<string, string>();
        string? In(string port) =>
            port.Length > 0 && message.Ports.TryGetValue(port, out var json) && !string.IsNullOrWhiteSpace(json) ? json : null;

        // 1. the Scenes and Objects being edited, followed.
        for (var i = 0; i < _inPorts.Length; i++)
            if (In(_inPorts[i]) is { } json && Keep(json) is { } id)
                _last[i] = Follow(_last[i], id);

        PointOfView? pov = In(_povPort) is { } povJson ? MpaiJson.FromJson<PointOfView>(povJson) : null;

        // 2. acting, by Command; the report goes back under the command's identifier.
        if (In(_commandPort) is { } commandJson)
        {
            var command = MpaiJson.FromJson<UserCommand>(commandJson);
            var report = new CommandReport(command);
            if (command.UserCommandRQ?.RevokedUserCommandID is { } revoked) _undo.Revoke(revoked, report);
            else
            {
                var before = _open;
                Apply(command, pov, report);
                _undo.RecordVersions(command.UserCommandID, before, _open, () => _open, v => _open = v);
            }
            if (_reportOut.Length > 0) outputs[_reportOut] = MpaiJson.ToJson(report.Report());
        }
        else if (pov is not null && Load(_open) is { } scene)
            _open = Save(Members(scene), pov, scene);

        // 3. the open Multimodal Scene, as it now stands - when it is multimodal.
        if (Load(_open) is { } open && Media(Members(open)) >= 2)
            outputs[_sceneOut] = Materialized(open).ToJsonString();
        return Task.FromResult(new Message
        {
            MessageId   = Guid.NewGuid().ToString(),
            MessageType = outputs.ContainsKey(_sceneOut) ? "MultimodalSceneDescriptors" : "NoMultimodalScene",
            DataType    = outputs.ContainsKey(_sceneOut) ? "OSD-MSD-V1.5" : "",
            Ports       = outputs
        });
    }

    // A Scene or Object arriving: kept, as it came, under its identifier (Key); null when
    // it is neither or has no identifier.
    private string? Keep(string json)
    {
        var node = JsonNode.Parse(json);
        if (node?["Header"]?.GetValue<string>() is not { } header || !Kinds.TryGetValue(header, out var kind)) return null;
        var id = node[kind.IdField]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id)) return null;
        _storage.MPAI_AIFM_SharedStorage_Put(Key(id), Encoding.UTF8.GetBytes(json));
        return id;
    }

    // Where a member is kept by this AIM: apart from the Editing AIMs' own.
    private static string Key(string id) => "mse:" + id;

    // A member as kept by this AIM, else as Shared Storage holds it; null when none.
    private JsonNode? Stored(string id)
    {
        foreach (var key in new[] { Key(id), id })
            if (_storage.MPAI_AIFM_SharedStorage_Exists(key))
                try { return JsonNode.Parse(Encoding.UTF8.GetString(_storage.MPAI_AIFM_SharedStorage_Get(key))); } catch { }
        return null;
    }

    // What a stored member is - its medium, and whether it is an Object; null when none.
    private Kind? KindOf(string id) =>
        Stored(id)?["Header"]?.GetValue<string>() is { } header && Kinds.TryGetValue(header, out var kind) ? kind : null;

    // The open Multimodal Scene holding the version last arrived holds the new one.
    private string Follow(string? last, string now)
    {
        if (last is null || last == now || Load(_open) is not { } scene) return now;
        var members = Members(scene);
        if (!members.Any(m => m.Id == last)) return now;
        _open = Save(members.Select(m => m.Id == last ? m with { Id = now } : m), UserPoVOf(scene), scene);
        Console.WriteLine($"[OSD-MSE-V1.5] {last} -> {now} in {_open}");
        return now;
    }

    private void Apply(UserCommand command, PointOfView? pov, CommandReport report)
    {
        var data = command.UserCommandRQ;
        if (data is null) return;
        var listener = data.UserPoV ?? pov;

        if (data.ModifiedObjects is { Objects.Count: > 0 })
            report.NotSupported("ModifiedObjects", _open, "what an Object is like is its Object Editing's");
        if (data.TranslatedObjects is { Objects.Count: > 0 })
            report.NotSupported("TranslatedObjects", _open, "speech is translated by Speech Object Editing");

        var scene = Load(_open);
        var members = scene is null ? new List<Member>() : Members(scene);
        var changed = false;

        if (data.AddedObjects is { Objects.Count: > 0 } added)
            foreach (var entry in added.Objects)
            {
                var id = entry.ObjectID?.ObjectID;
                var kind = id is null ? null : KindOf(id);
                if (kind is null)
                {
                    report.Failed("AddedObjects", id, "a Multimodal Scene holds Scenes and Objects, named by their identifiers in Shared Storage");
                    continue;
                }
                if (kind.List is null)
                {
                    report.Failed("AddedObjects", id, $"a Multimodal Scene has no list for {kind.Medium}");
                    continue;
                }
                members.Add(new Member(id!, Placement(entry.SpatialAttitude) ?? new SpaceTime()));
                report.Done("AddedObjects", id); changed = true;
            }

        void Replace(string action, string? id, Func<Member, Member?> change)
        {
            var at = id is null ? -1 : members.FindIndex(m => m.Id == id);
            if (at < 0) { report.Failed(action, id, scene is null && !changed ? "no Multimodal Scene is open" : "the open Scene has no such member"); return; }
            if (change(members[at]) is { } kept) members[at] = kept; else members.RemoveAt(at);
            report.Done(action, id); changed = true;
        }
        Member At(Member m, SpatialAttitude? a) => m with { Place = Placement(a) ?? m.Place };

        if (data.MovedObjects is { Objects.Count: > 0 } moved)
            foreach (var e in moved.Objects) Replace("MovedObjects", e.ObjectID?.ObjectID, m => At(m, e.NewSpatialAttitude));
        if (data.ChangedObjects is { Objects.Count: > 0 } changes)
            foreach (var e in changes.Objects) Replace("ChangedObjects", e.ObjectID?.ObjectID, m => At(m, e.SpatialAttitude));
        if (data.RemovedObjects is { Objects.Count: > 0 } removed)
            foreach (var e in removed.Objects) Replace("RemovedObjects", e.ObjectID?.ObjectID, _ => null);

        if (listener is not null)
        {
            if (scene is null && !changed) report.Failed("UserPoV", null, "no Multimodal Scene is open");
            else { report.Done("UserPoV", _open); changed = true; }
        }

        if (!changed) return;
        _open = Save(members, listener ?? (scene is null ? null : UserPoVOf(scene)), scene);
        report.Produced(_open);
    }

    private int Media(IEnumerable<Member> members) =>
        members.Select(m => KindOf(m.Id)?.Medium).Where(m => m is not null).Distinct().Count();

    private static PointOfView? UserPoVOf(JsonObject scene) =>
        scene["UserPoV"] is { } pov ? MpaiJson.FromJson<PointOfView>(pov.ToJsonString()) : null;

    // The members of a Multimodal Scene, Scenes first, then Objects.
    private static List<Member> Members(JsonObject scene)
    {
        var members = new List<Member>();
        foreach (var (list, spaceTime, member) in Lists)
            foreach (var entry in scene[list]?.AsArray() ?? [])
                if (IdOf(entry?[member]) is { } id)
                    members.Add(new Member(id, entry![spaceTime] is { } place ? MpaiJson.FromJson<SpaceTime>(place.ToJsonString()) : new SpaceTime()));
        return members;
    }

    // The identifier of a member, held as an identifier or as itself.
    private static string? IdOf(JsonNode? held)
    {
        if (held is JsonValue v && v.TryGetValue<string>(out var id)) return id;
        if (held?["Header"]?.GetValue<string>() is { } header && Kinds.TryGetValue(header, out var kind)) return held[kind.IdField]?.GetValue<string>();
        return null;
    }

    // A new version: MSD000001, MSD000002 ... Each member, Scene or Object by its Header,
    // goes in its own list, held by its identifier; a list with no member is left out.
    private string Save(IEnumerable<Member> members, PointOfView? userPoV, JsonObject? from)
    {
        const string counter = "_counter:MSD";
        long next = _storage.MPAI_AIFM_SharedStorage_Exists(counter)
            ? long.Parse(Encoding.UTF8.GetString(_storage.MPAI_AIFM_SharedStorage_Get(counter))) + 1 : 1;
        _storage.MPAI_AIFM_SharedStorage_Put(counter, Encoding.UTF8.GetBytes(next.ToString()));
        var id = $"MSD{next:D6}";
        var kept = members.ToList();
        var scene = new JsonObject
        {
            ["Header"] = "OSD-MSD-V1.5",
            ["MInstanceID"] = from?["MInstanceID"]?.GetValue<string>() is { Length: > 0 } mi ? mi : _mInstanceId,
            ["MultimodalSceneDescriptorsID"] = id,
            ["MultimodalSceneDescriptorsTime"] = Node(SimpleTime.At(DateTimeOffset.UtcNow)),
            ["MultimodalSceneDescriptorsSpaceTime"] = from?["MultimodalSceneDescriptorsSpaceTime"]?.DeepClone() ?? Node(new SpaceTime())
        };
        if (from?["UEnvironmentID"] is { } env) scene["UEnvironmentID"] = env.DeepClone();
        if (userPoV is not null) scene["UserPoV"] = Node(userPoV);
        if (from?["ClosedSpace"] is { } room) scene["ClosedSpace"] = room.DeepClone();
        foreach (var (list, spaceTime, member) in Lists)
        {
            var held = kept.Where(m => KindOf(m.Id) is { } k && k.List == list)
                           .Select(m => (JsonNode)new JsonObject { [spaceTime] = Node(m.Place), [member] = JsonValue.Create(m.Id) }).ToArray();
            if (held.Length == 0) continue;
            scene[list.Replace("Objects", "ObjectCount").Replace("Scenes", "SceneCount")] = held.Length;
            scene[list] = new JsonArray(held);
        }
        _storage.MPAI_AIFM_SharedStorage_Put(id, Encoding.UTF8.GetBytes(scene.ToJsonString()));
        return id;
    }

    private JsonObject? Load(string? id) =>
        id is null || !id.StartsWith("MSD", StringComparison.Ordinal) || !_storage.MPAI_AIFM_SharedStorage_Exists(id) ? null
        : JsonNode.Parse(Encoding.UTF8.GetString(_storage.MPAI_AIFM_SharedStorage_Get(id)))?.AsObject();

    // The Multimodal Scene with each member as Shared Storage now holds it.
    private JsonObject Materialized(JsonObject scene)
    {
        var whole = (JsonObject)scene.DeepClone();
        foreach (var (list, _, member) in Lists)
            foreach (var entry in whole[list]?.AsArray() ?? [])
                if (entry is JsonObject e && IdOf(e[member]) is { } id)
                    e[member] = Stored(id) ?? JsonValue.Create(id);
        return whole;
    }

    private static JsonNode? Node(object value) => JsonNode.Parse(MpaiJson.ToJson(value));

    private static SpaceTime? Placement(SpatialAttitude? attitude) =>
        attitude is null ? null : new SpaceTime { SpatialAttitude1 = attitude };
}
