using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Cae.Ase;

// CAE-ASE-V1.0 - Audio Scene Editing, as the author's Reference Model of Audio Scene
// Management draws it (CAE Reference Models, 2026/10/26): an Audio Object (OSD-AUO, from
// Audio Object Editing), an Audio Scene (OSD-ASD), a User Command and a User PoV in; the
// Audio Scene as edited, and the User Command with its response, out. There is no Basic
// Audio Scene on a Port: an Audio Scene is a collection of Audio Objects and of Basic
// Audio Scenes, and the one Basic Audio Scene that is edited is held in it.
//
// OPEN BY PORT, ACT BY COMMAND. One Basic Scene is open at a time, so no Command names
// it. An Audio Scene arriving that holds a Basic Audio Scene - or whose own identifier is
// one - opens that Scene. A User Command acts on the open Scene - the first Object added
// to no open Scene creates one:
//
//   AddedObjects     place the named Basic Audio Objects
//   MovedObjects     old attitude -> new attitude; the Object is the one at the old PoV
//                    (Point of View: position and orientation associated to it, and the
//                    time it sounds, if stated) when no identifier names it
//   ChangedObjects   where a placed Object is
//   ClosedSpace      the room the Scene is in
//   ModifiedScene    the acoustics of the Scene (its Acoustic Profile)
//   UserPoV          where the Scene is heard from (also on its own Port)
//
//   RemovedObjects   take placed Objects out
//
// The Audio Scene given is the open Basic Audio Scene inside an Audio Scene of one
// sub-scene, under the Basic Scene's identifier - its Closed Space, its Acoustic Profile
// and its Objects stay in it, as Shared Storage holds them.
//
// Every edit is a new version in Shared Storage. The Objects are read from Shared
// Storage by their identifiers; what Audio Object Editing leaves is there (the Audio
// Object port is for the Controller to connect; nothing has to be read from it).
public sealed class AseAimProcessor : IAimProcessor
{
    private readonly AseAim _ase;

    private readonly string _commandPort;
    private readonly string _objectPort;
    private readonly string _scenePort;
    private readonly string _povPort;
    private readonly string _sceneOut;
    private readonly string _reportOut;

    // What is open, between runs.
    private string? _open;
    private readonly UndoJournal _undo = new();

    public string InstanceId { get; }

    public AseAimProcessor(string instanceId, AseAim ase, AimPortReader ports)
    {
        InstanceId   = instanceId;
        _ase         = ase;
        _commandPort = ports.Input("CAE-UCM-V1.0");
        _objectPort  = ports.InputOrDefault("OSD-AUO-V1.5", "");
        _scenePort   = ports.Input("OSD-ASD-V1.5");
        _povPort     = ports.InputOrDefault("OSD-OPV-V1.5", "");
        _sceneOut    = ports.Output("OSD-ASD-V1.5");
        _reportOut   = ports.OutputOrDefault("CAE-UCM-V1.0", "");
    }

    public Task<Message> ProcessAsync(Message message)
    {
        var outputs = new Dictionary<string, string>();

        // 1. opening, by Port.
        if (message.Ports.TryGetValue(_scenePort, out var sceneJson) && !string.IsNullOrWhiteSpace(sceneJson))
        {
            var id = BasicSceneOf(MpaiJson.FromJson<AudioSceneDescriptors>(sceneJson));
            // A Scene the storage does not hold cannot be opened: the one open stays, and nothing is raised - an AIM that threw would stop the Module.
            if (!string.IsNullOrWhiteSpace(id) && _ase.Has(id)) { _open = id; Console.WriteLine($"[CAE-ASE-V1.0] opened {_open}"); }
        }

        PointOfView? pov = null;
        if (_povPort.Length > 0 && message.Ports.TryGetValue(_povPort, out var povJson) && !string.IsNullOrWhiteSpace(povJson))
            pov = MpaiJson.FromJson<PointOfView>(povJson);

        // 2. acting, by Command; the report goes back under the command's identifier.
        if (message.Ports.TryGetValue(_commandPort, out var commandJson) && !string.IsNullOrWhiteSpace(commandJson))
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
        else if (pov is not null && _open is not null)
            _open = _ase.SetBasicSceneListener(_open, pov).AssetId;

        // 3. the open Scene, as it now stands.
        if (_open is not null) outputs[_sceneOut] = MpaiJson.ToJson(Given(_open));
        return Task.FromResult(new Message
        {
            MessageId   = Guid.NewGuid().ToString(),
            MessageType = _open is null ? "NoOpenScene" : "AudioSceneDescriptors",
            DataType    = _open is null ? "" : "OSD-ASD-V1.5",
            Ports       = outputs
        });
    }

    // The Basic Audio Scene an Audio Scene holds: the first sub-scene that is one - in full, or by its identifier - else the Audio Scene's own identifier, which is a
    // Basic Scene's when the Scene is one this AIM gave.
    private static string? BasicSceneOf(AudioSceneDescriptors scene)
    {
        foreach (var sub in scene.SubAudioScenes ?? [])
        {
            var id = sub.BasicScene?.BasicAudioSceneDescriptorsID ?? sub.SceneId;
            if (id is not null && id.StartsWith("BAS", StringComparison.Ordinal)) return id;
        }
        return scene.AudioSceneDescriptorsID.StartsWith("BAS", StringComparison.Ordinal) ? scene.AudioSceneDescriptorsID : null;
    }

    // The open Basic Audio Scene as the Audio Scene this AIM gives: the Scene is the one sub-scene, which stands at the origin of the Audio Scene's frame.
    private AudioSceneDescriptors Given(string basicSceneId)
    {
        var basic = _ase.MaterializeBasicScene(basicSceneId);
        return new AudioSceneDescriptors
        {
            MInstanceID = basic.MInstanceID, UEnvironmentID = basic.UEnvironmentID,
            AudioSceneDescriptorsID = basicSceneId,
            AudioSceneDescriptorsTime = basic.BasicAudioSceneDescriptorsTime,
            AudioSceneDescriptorsSpaceTime = basic.BASSpaceTime ?? new SpaceTime(),
            UserPoV = basic.UserPoV,
            SubAudioSceneCount = 1,
            SubAudioScenes = [new SubAudioSceneEntry { BasicScene = basic }]
        };
    }

    private void Apply(UserCommand command, PointOfView? pov, CommandReport report)
    {
        var data = command.UserCommandRQ;
        if (data is null) return;
        var listener = data.UserPoV ?? pov;

        if (data.ModifiedObjects is { Objects.Count: > 0 })
            report.NotSupported("ModifiedObjects", _open, "what an Object is like is Audio Object Editing's");
        if (data.TranslatedObjects is { Objects.Count: > 0 })
            report.NotSupported("TranslatedObjects", _open, "speech is translated by Speech Object Editing");

        // What is taken out first, then what is put in: one command replaces an Object with another at the same PoV. An Object is named by an identifier, by the Object
        // itself (the Object or ID of the data types), or - for taking out and moving - by where it is.
        if (_open is not null && data.RemovedObjects is { Objects.Count: > 0 }) TakeOut(data.RemovedObjects, report);

        if (data.AddedObjects is { Objects.Count: > 0 } added)
            foreach (var entry in added.Objects)
            {
                var id = IdOf(entry.ObjectID);
                if (id is null || !id.StartsWith("BAO", StringComparison.Ordinal))
                {
                    report.Failed("AddedObjects", id, "a Basic Audio Scene holds Basic Audio Objects, named by their identifiers");
                    continue;
                }
                try
                {
                    _open = _open is null
                        ? _ase.CreateBasicScene(id, listener ?? Origin(), Placement(entry.SpatialAttitude)).AssetId
                        : _ase.AddObjectToBasicScene(_open, id, Placement(entry.SpatialAttitude)).AssetId;
                    report.Done("AddedObjects", id, _open);
                }
                catch (Exception failure) { report.Failed("AddedObjects", id, failure.Message); }
            }

        if (_open is null)
        {
            foreach (var (action, any) in new[] { ("MovedObjects", data.MovedObjects is { Objects.Count: > 0 }),
                                                  ("ChangedObjects", data.ChangedObjects is { Objects.Count: > 0 }),
                                                  ("RemovedObjects", data.RemovedObjects is { Objects.Count: > 0 }) })
                if (any) report.Failed(action, null, "no Basic Audio Scene is open");
            return;
        }

        if (data.MovedObjects is { Objects.Count: > 0 } moved)
        {
            var handled = new List<SpatialAttitude>();
            foreach (var entry in moved.Objects)
            {
                if (IdOf(entry.ObjectID) is { } named) { Place("MovedObjects", named, entry.NewSpatialAttitude, report); continue; }
                if (handled.Any(h => SamePoV(h, entry.OldSpatialAttitude))) continue;       // those at that PoV have moved already
                foreach (var id in At("MovedObjects", entry.OldSpatialAttitude, report)) Place("MovedObjects", id, entry.NewSpatialAttitude, report);
                if (entry.OldSpatialAttitude is { } old) handled.Add(old);
            }
        }

        if (data.ChangedObjects is { Objects.Count: > 0 } changed)
            foreach (var entry in changed.Objects)
                Place("ChangedObjects", IdOf(entry.ObjectID), entry.SpatialAttitude, report);

        // The acoustics of the Scene: its Acoustic Profile, as given.
        if (data.ModifiedScene?.AcousticProfile is { } acoustics)
        {
            var before = _open;
            _open = _ase.SetBasicSceneAcousticProfile(_open, acoustics).AssetId;
            report.Done("ModifiedScene", before, _open);
        }

        // The room: the space the Scene is in, replaced as given.
        if (data.ClosedSpace is { Count: > 0 } room)
        {
            var before = _open;
            _open = _ase.SetBasicSceneClosedSpace(_open, room).AssetId;
            report.Done("ClosedSpace", before, _open);
        }

        // Once, on the Scene: its User PoV overrides each member's.
        if (listener is not null)
        {
            var before = _open;
            _open = _ase.SetBasicSceneListener(_open, listener).AssetId;
            report.Done("UserPoV", before, _open);
        }
    }

    // THE OBJECT AT A PoV: a command that names no identifier says where the Object is - its position, and when it sounds if that is stated. The members of the open Scene
    // there are the Object (several at one PoV are moved or taken out together, for nothing tells them apart); none there is a failure.
    private List<string> At(string action, SpatialAttitude? at, CommandReport report)
    {
        var found = new List<string>();
        if (at?.Position?.CartPosition is not { Length: >= 3 } want)
        {
            report.Failed(action, null, "the Object is named neither by an identifier nor by its PoV");
            return found;
        }
        double? wantStart = at.SpatialAttitudeTime is null ? null : StartOf(at.SpatialAttitudeTime);
        foreach (var entry in _ase.MaterializeBasicScene(_open!).BasicAudioSceneDescriptorsEntries)
        {
            if (entry.AudioObjectIDOrAudioObject is not { } member) continue;
            if (entry.AudioObjectSpaceTime?.SpatialAttitude1?.Position?.CartPosition is not { Length: >= 3 } there) continue;
            var d = Math.Sqrt(Math.Pow(there[0] - want[0], 2) + Math.Pow(there[1] - want[1], 2) + Math.Pow(there[2] - want[2], 2));
            if (d > 0.05) continue;
            if (wantStart is { } w && Math.Abs(StartOf(entry.AudioObjectSpaceTime?.Time) - w) > 0.05) continue;
            found.Add(member.BasicAudioObjectID);
        }
        if (found.Count == 0) report.Failed(action, null, "the open Scene has no Object at that PoV");
        return found;
    }

    private static bool SamePoV(SpatialAttitude? a, SpatialAttitude? b) =>
        a?.Position?.CartPosition is { Length: >= 3 } p && b?.Position?.CartPosition is { Length: >= 3 } q
        && Math.Abs(p[0] - q[0]) < 0.05 && Math.Abs(p[1] - q[1]) < 0.05 && Math.Abs(p[2] - q[2]) < 0.05
        && (a.SpatialAttitudeTime is null || b.SpatialAttitudeTime is null || Math.Abs(StartOf(a.SpatialAttitudeTime) - StartOf(b.SpatialAttitudeTime)) < 0.05);

    private static double StartOf(SimpleTime? time)
    {
        var segment = time?.SimpleTimeData?.FirstOrDefault(s => s.TimeType == false);
        if (segment is null) return 0;
        return segment.TimeUnit switch { "01" => segment.StartTime / 1e3, "10" => segment.StartTime / 1e6, "11" => segment.StartTime / 1e9, _ => segment.StartTime };
    }

    private void TakeOut(ObjectPlacements removed, CommandReport report)
    {
        var handled = new List<SpatialAttitude>();
        foreach (var entry in removed.Objects)
        {
            var ids = IdOf(entry.ObjectID) is { } named ? [named] : handled.Any(h => SamePoV(h, entry.SpatialAttitude)) ? [] : At("RemovedObjects", entry.SpatialAttitude, report);
            if (IdOf(entry.ObjectID) is null && entry.SpatialAttitude is { } where) handled.Add(where);
            foreach (var id in ids)
            {
                var result = _ase.RemoveFromBasicScene(_open!, id);
                if (result is null) report.Failed("RemovedObjects", id, "the open Scene has no such member");
                else { _open = result.AssetId; report.Done("RemovedObjects", id, _open); }
            }
        }
    }

    // A move or a change is the member re-placed at the new attitude.
    private void Place(string action, string? id, SpatialAttitude? attitude, CommandReport report)
    {
        var result = id is null ? null : _ase.ReplaceInBasicScene(_open!, id, Placement(attitude));
        if (result is null) { report.Failed(action, id, "the open Scene has no such member"); return; }
        _open = result.AssetId;
        report.Done(action, id, _open);
    }

    private static PointOfView Origin() => new() { PointOfViewID = Guid.NewGuid().ToString(), CartPosition = [0, 0, 0], Orientation = [0, 0, 0] };

    private static SpaceTime? Placement(SpatialAttitude? attitude) =>
        attitude is null ? null : new SpaceTime { SpatialAttitude1 = attitude, Time = attitude.SpatialAttitudeTime ?? new SimpleTime() };   // when the attitude is stated for a time, that time is when the member sounds

    private static string? IdOf(ManagedObject? managed) =>
        managed is null ? null
        : !string.IsNullOrWhiteSpace(managed.ObjectID) ? managed.ObjectID
        : !string.IsNullOrWhiteSpace(managed.BasicAudioObject?.BasicAudioObjectID) ? managed.BasicAudioObject!.BasicAudioObjectID
        : null;
}
