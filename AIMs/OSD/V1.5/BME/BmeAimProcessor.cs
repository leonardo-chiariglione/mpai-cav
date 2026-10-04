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

namespace Mpai.Osd.Bme;

// OSD-BME-V1.5 - Basic Multimodal Scene Editing, for every medium, hence in MPAI-OSD (the author, 2026/10/04: "we need a
// new aim that take basic scenes from at least two different media scenes and create
// a BMS. The name can be BME"). Basic Scenes of any medium - Audio, Visual, Speech,
// LiDAR, 3D Model, RADAR, Ultrasound, Offline Map - on two Ports, a User Command and a
// User PoV in; the Basic Multimodal Scene as edited, and the User Command with its
// report, out. A voice over music is a BMS of a BAS and a BSS. In Audio Scene
// Management, Port 1 takes the Basic Audio Scene, Port 2 the Basic Speech Scene.
//
// OPEN BY PORT, ACT BY COMMAND, as the Scene Editing AIMs do. A User Command acts on
// the open Basic Multimodal Scene - the first Scene added to none open creates one:
//
//   AddedObjects     place the named Basic Scenes in the common frame
//   MovedObjects     old attitude -> new attitude of a placed Scene
//   ChangedObjects   where a placed Scene is
//   RemovedObjects   take placed Scenes out
//   UserPoV          where the Multimodal Scene is heard and seen from (also on its Port)
//
// MULTIMODAL: the Scene is output when its members are of at least two media.
//
// THE SCENES BEING EDITED ARE FOLLOWED. An Audio or a Speech Scene is edited in new
// versions; the Scene arriving on a Port is the one now open there. A member that is
// the version last arrived on that Port is replaced by the new one - a new version of
// the Multimodal Scene - so it holds the Scenes as they now are.
//
// Every edit is a new version in Shared Storage (BMS000001 ...). A member is held by
// its identifier; each Basic Scene is kept as it last arrived - complete, as its
// Scene Editing AIM gave it - and output so.
public sealed class BmeAimProcessor : IAimProcessor
{
    // The Basic Scenes a Basic Multimodal Scene holds, by Header, and the field that
    // carries each one's identifier.
    private static readonly Dictionary<string, (string Medium, string IdField)> Basic = new()
    {
        ["OSD-BAS-V1.5"] = ("Audio",       "BasicAudioSceneDescriptorsID"),
        ["OSD-BVS-V1.5"] = ("Visual",      "BasicVisualSceneDescriptorsID"),
        ["OSD-BSS-V1.5"] = ("Speech",      "BasicSpeechSceneDescriptorsID"),
        ["OSD-BLS-V1.5"] = ("LiDAR",       "BasicLiDARSceneDescriptorsID"),
        ["OSD-B3S-V1.5"] = ("3D Model",    "Basic3DModelSceneDescriptorsID"),
        ["OSD-BRS-V1.5"] = ("RADAR",       "BasicRADARSceneDescriptorsID"),
        ["OSD-BUS-V1.5"] = ("Ultrasound",  "BasicUltrasoundSceneDescriptorsID"),
        ["OSD-BOS-V1.5"] = ("Offline Map", "BasicOfflineMapSceneDescriptorsID")
    };

    private readonly ISharedStorage _storage;
    private readonly string _mInstanceId;

    private readonly string _commandPort;
    private readonly string[] _scenePorts;
    private readonly string _povPort;
    private readonly string _sceneOut;
    private readonly string _reportOut;

    private string? _open;
    private readonly string?[] _last;   // the version last arrived on each Scene Port

    public string InstanceId { get; }

    public BmeAimProcessor(string instanceId, ISharedStorage storage, AimPortReader ports, string mInstanceId = "")
    {
        InstanceId   = instanceId;
        _storage     = storage;
        _mInstanceId = mInstanceId;
        _commandPort = ports.Input("CAE-UCM-V1.0");
        // Each Scene Port takes any Basic Scene: it is found by the first Data Type it lists.
        _scenePorts  = [ports.InputOrDefault("OSD-BAS-V1.5", 1, ""), ports.InputOrDefault("OSD-BAS-V1.5", 2, "")];
        _last        = new string?[_scenePorts.Length];
        _povPort     = ports.InputOrDefault("OSD-OPV-V1.5", "");
        _sceneOut    = ports.Output("OSD-BMS-V1.5");
        _reportOut   = ports.OutputOrDefault("CAE-UCM-V1.0", "");
    }

    public Task<Message> ProcessAsync(Message message)
    {
        var outputs = new Dictionary<string, string>();
        string? In(string port) =>
            port.Length > 0 && message.Ports.TryGetValue(port, out var json) && !string.IsNullOrWhiteSpace(json) ? json : null;

        // 1. the Scenes being edited, followed.
        for (var i = 0; i < _scenePorts.Length; i++)
            if (In(_scenePorts[i]) is { } sceneJson && Keep(sceneJson) is { } id)
                _last[i] = Follow(_last[i], id);

        PointOfView? pov = In(_povPort) is { } povJson ? MpaiJson.FromJson<PointOfView>(povJson) : null;

        // 2. acting, by Command; the report goes back under the command's identifier.
        if (In(_commandPort) is { } commandJson)
        {
            var command = MpaiJson.FromJson<UserCommand>(commandJson);
            var report = new CommandReport(command);
            Apply(command, pov, report);
            if (_reportOut.Length > 0) outputs[_reportOut] = MpaiJson.ToJson(report.Report());
        }
        else if (pov is not null && Load(_open) is { } scene)
            _open = Save(scene.BasicAVSceneDescriptorsData, pov, scene);

        // 3. the open Multimodal Scene, as it now stands - when it is multimodal.
        if (Load(_open) is { } open && Media(open.BasicAVSceneDescriptorsData) >= 2)
            outputs[_sceneOut] = MpaiJson.ToJson(Materialized(open));
        return Task.FromResult(new Message
        {
            MessageId   = Guid.NewGuid().ToString(),
            MessageType = outputs.ContainsKey(_sceneOut) ? "BasicAudioVisualSceneDescriptors" : "NoMultimodalScene",
            DataType    = outputs.ContainsKey(_sceneOut) ? "OSD-BMS-V1.5" : "",
            Ports       = outputs
        });
    }

    // A Basic Scene arriving: kept, as it came, under its identifier (Key); null when it
    // is not a Basic Scene or has no identifier.
    private string? Keep(string json)
    {
        var node = JsonNode.Parse(json);
        if (node?["Header"]?.GetValue<string>() is not { } header || !Basic.TryGetValue(header, out var kind)) return null;
        var id = node[kind.IdField]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id)) return null;
        _storage.MPAI_AIFM_SharedStorage_Put(Key(id), Encoding.UTF8.GetBytes(json));
        return id;
    }

    // Where a Basic Scene is kept by this AIM: apart from the Scene Editing AIMs' own.
    private static string Key(string id) => "bme:" + id;

    // A Basic Scene as kept by this AIM, else as Shared Storage holds it; null when none.
    private JsonNode? Stored(string id)
    {
        foreach (var key in new[] { Key(id), id })
            if (_storage.MPAI_AIFM_SharedStorage_Exists(key))
                try { return JsonNode.Parse(Encoding.UTF8.GetString(_storage.MPAI_AIFM_SharedStorage_Get(key))); } catch { }
        return null;
    }

    // The open Multimodal Scene holding the version last arrived holds the new one.
    private string Follow(string? last, string now)
    {
        if (last is null || last == now || Load(_open) is not { } scene) return now;
        var members = scene.BasicAVSceneDescriptorsData;
        if (!members.Any(m => IdOf(m) == last)) return now;
        _open = Save(members.Select(m => IdOf(m) == last ? Entry(now, m.BXSSpaceTime, m.UserPoV) : m), scene.UserPoV, scene);
        Console.WriteLine($"[OSD-BME-V1.5] {last} -> {now} in {_open}");
        return now;
    }

    private void Apply(UserCommand command, PointOfView? pov, CommandReport report)
    {
        var data = command.UserCommandData;
        if (data is null) return;
        var listener = data.UserPoV ?? pov;

        if (data.ModifiedObjects is { Objects.Count: > 0 })
            report.NotSupported("ModifiedObjects", _open, "what an Object is like is its Object Editing's");
        if (data.TranslatedObjects is { Objects.Count: > 0 })
            report.NotSupported("TranslatedObjects", _open, "speech is translated by Speech Object Editing");

        var scene = Load(_open);
        var members = scene?.BasicAVSceneDescriptorsData.ToList() ?? new List<BasicAVSceneEntry>();
        var changed = false;

        if (data.AddedObjects is { Objects.Count: > 0 } added)
            foreach (var entry in added.Objects)
            {
                var id = entry.ObjectID?.ObjectID;
                if (id is null || MediumOf(id) is null)
                {
                    report.Failed("AddedObjects", id, "a Basic Multimodal Scene holds Basic Scenes, named by their identifiers in Shared Storage");
                    continue;
                }
                members.Add(Entry(id, Placement(entry.SpatialAttitude), null));
                report.Done("AddedObjects", id); changed = true;
            }

        void Replace(string action, string? id, Func<BasicAVSceneEntry, BasicAVSceneEntry?> change)
        {
            var at = id is null ? -1 : members.FindIndex(m => IdOf(m) == id);
            if (at < 0) { report.Failed(action, id, scene is null && !changed ? "no Basic Multimodal Scene is open" : "the open Scene has no such member"); return; }
            if (change(members[at]) is { } kept) members[at] = kept; else members.RemoveAt(at);
            report.Done(action, id); changed = true;
        }
        BasicAVSceneEntry At(BasicAVSceneEntry m, SpatialAttitude? a) => Entry(IdOf(m)!, Placement(a) ?? m.BXSSpaceTime, m.UserPoV);

        if (data.MovedObjects is { Objects.Count: > 0 } moved)
            foreach (var e in moved.Objects) Replace("MovedObjects", e.ObjectID?.ObjectID, m => At(m, e.NewSpatialAttitude));
        if (data.ChangedObjects is { Objects.Count: > 0 } changes)
            foreach (var e in changes.Objects) Replace("ChangedObjects", e.ObjectID?.ObjectID, m => At(m, e.SpatialAttitude));
        if (data.RemovedObjects is { Objects.Count: > 0 } removed)
            foreach (var e in removed.Objects) Replace("RemovedObjects", e.ObjectID?.ObjectID, _ => null);

        if (listener is not null)
        {
            if (scene is null && !changed) report.Failed("UserPoV", null, "no Basic Multimodal Scene is open");
            else { report.Done("UserPoV", _open); changed = true; }
        }

        if (!changed) return;
        _open = Save(members, listener ?? scene?.UserPoV, scene);
        report.Produced(_open);
    }

    // The medium of a stored Basic Scene, by its Header; null when there is none.
    private string? MediumOf(string id) =>
        Stored(id)?["Header"]?.GetValue<string>() is { } header && Basic.TryGetValue(header, out var kind) ? kind.Medium : null;

    private int Media(IEnumerable<BasicAVSceneEntry> members) =>
        members.Select(m => IdOf(m) is { } id ? MediumOf(id) : null).Where(m => m is not null).Distinct().Count();

    private static BasicAVSceneEntry Entry(string id, SpaceTime? placement, PointOfView? userPoV) =>
        new() { BXSOrBXSID = id, BXSSpaceTime = placement ?? new SpaceTime(), UserPoV = userPoV };

    // The identifier of a member, held as an identifier or as a Scene.
    private static string? IdOf(BasicAVSceneEntry m)
    {
        JsonNode? node = m.BXSOrBXSID switch
        {
            string s => JsonValue.Create(s),
            JsonElement e => JsonNode.Parse(e.GetRawText()),
            JsonNode n => n,
            null => null,
            var o => JsonNode.Parse(MpaiJson.ToJson(o))
        };
        if (node is JsonValue v && v.TryGetValue<string>(out var id)) return id;
        if (node?["Header"]?.GetValue<string>() is { } header && Basic.TryGetValue(header, out var kind)) return node[kind.IdField]?.GetValue<string>();
        return null;
    }

    // A new version: BMS000001, BMS000002 ...
    private string Save(IEnumerable<BasicAVSceneEntry> members, PointOfView? userPoV, BasicAudioVisualSceneDescriptors? from)
    {
        const string counter = "_counter:BMS";
        long next = _storage.MPAI_AIFM_SharedStorage_Exists(counter)
            ? long.Parse(Encoding.UTF8.GetString(_storage.MPAI_AIFM_SharedStorage_Get(counter))) + 1 : 1;
        _storage.MPAI_AIFM_SharedStorage_Put(counter, Encoding.UTF8.GetBytes(next.ToString()));
        var id = $"BMS{next:D6}";
        var kept = members.Select(m => Entry(IdOf(m)!, m.BXSSpaceTime, m.UserPoV)).ToList();
        var scene = new BasicAudioVisualSceneDescriptors
        {
            MInstanceID = from?.MInstanceID is { Length: > 0 } mi ? mi : _mInstanceId,
            UEnvironmentID = from?.UEnvironmentID,
            BasicAVSceneDescriptorsID = id,
            BAVSDescriptorsTime = SimpleTime.At(DateTimeOffset.UtcNow),
            BAVSDescriptorsSpaceTime = from?.BAVSDescriptorsSpaceTime ?? new SpaceTime(),
            UserPoV = userPoV, ClosedSpace = from?.ClosedSpace, AcousticProfile = from?.AcousticProfile,
            AVObjectCount = kept.Count, BasicAVSceneDescriptorsData = kept
        };
        _storage.MPAI_AIFM_SharedStorage_Put(id, Encoding.UTF8.GetBytes(MpaiJson.ToJson(scene)));
        return id;
    }

    private BasicAudioVisualSceneDescriptors? Load(string? id) =>
        id is null || !id.StartsWith("BMS", StringComparison.Ordinal) || !_storage.MPAI_AIFM_SharedStorage_Exists(id) ? null
        : MpaiJson.FromJson<BasicAudioVisualSceneDescriptors>(Encoding.UTF8.GetString(_storage.MPAI_AIFM_SharedStorage_Get(id)));

    // The Multimodal Scene with each member Scene as Shared Storage now holds it.
    private BasicAudioVisualSceneDescriptors Materialized(BasicAudioVisualSceneDescriptors scene) => new()
    {
        MInstanceID = scene.MInstanceID, UEnvironmentID = scene.UEnvironmentID,
        BasicAVSceneDescriptorsID = scene.BasicAVSceneDescriptorsID, BAVSDescriptorsTime = scene.BAVSDescriptorsTime,
        BAVSDescriptorsSpaceTime = scene.BAVSDescriptorsSpaceTime, UserPoV = scene.UserPoV,
        ClosedSpace = scene.ClosedSpace, AcousticProfile = scene.AcousticProfile, AVObjectCount = scene.AVObjectCount,
        BasicAVSceneDescriptorsData = scene.BasicAVSceneDescriptorsData.Select(m => new BasicAVSceneEntry
        {
            BXSSpaceTime = m.BXSSpaceTime, UserPoV = m.UserPoV, BXSOrBXSID = Member(IdOf(m))
        }).ToList(),
        DataXMData = scene.DataXMData, DescrMetadata = scene.DescrMetadata
    };

    // A member Scene as it was kept; its identifier when there is none.
    private object? Member(string? id) => id is null ? null : (object?)Stored(id) ?? id;

    private static SpaceTime? Placement(SpatialAttitude? attitude) =>
        attitude is null ? null : new SpaceTime { SpatialAttitude1 = attitude };
}
