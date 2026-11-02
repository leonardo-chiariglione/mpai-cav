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
// A SCENE AND AN OBJECT ARE NAMED BY THE USER (the author, 2026/10/26): in the data, the
// commands and the replies they are identified only by their names; the Module's storage
// keys never leave it. The first Object added to no open Scene creates one, under the
// name the command gives (ObjectID of AddedObjects, required then); a second Scene given
// a name that is taken is "concert 2". The amounts - where an Object is put, to where it
// is moved - are the command's: a command without them is reported as failed, not guessed.
//
// Every edit is a new version in Shared Storage. The Objects are read from Shared
// Storage by their names; what Audio Object Editing leaves is there (the Audio
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

    // What is open, between runs: the key of the version in force (the Module's own; the User is told the name).
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

    private string? N(string? key) => _ase.SceneName(key);

    public Task<Message> ProcessAsync(Message message)
    {
        var outputs = new Dictionary<string, string>();

        // 1. opening, by Port.
        if (message.Ports.TryGetValue(_scenePort, out var sceneJson) && !string.IsNullOrWhiteSpace(sceneJson))
        {
            var id = BasicSceneOf(MpaiJson.FromJson<AudioSceneDescriptors>(sceneJson));
            // A Scene the storage does not hold cannot be opened: the one open stays, and nothing is raised - an AIM that threw would stop the Module.
            if (id is not null && _ase.Has(id)) { _open = id; Console.WriteLine($"[CAE-ASE-V1.0] opened {N(_open)}"); }
        }

        PointOfView? pov = null;
        if (_povPort.Length > 0 && message.Ports.TryGetValue(_povPort, out var povJson) && !string.IsNullOrWhiteSpace(povJson))
            pov = MpaiJson.FromJson<PointOfView>(povJson);

        // 2. acting, by Command; the report goes back under the command's identifier.
        if (message.Ports.TryGetValue(_commandPort, out var commandJson) && !string.IsNullOrWhiteSpace(commandJson))
        {
            var command = CommandReader.Read(commandJson, out var rejection);
            if (command is null) { if (_reportOut.Length > 0) outputs[_reportOut] = MpaiJson.ToJson(rejection); }
            else
            {
                var report = new CommandReport(command);
                if (command.UserCommandRQ?.RevokedUserCommandID is { } revoked) _undo.Revoke(revoked, report);
                else
                {
                    var before = _open;
                    Apply(command, pov, report);
                    _undo.RecordVersions(command.UserCommandID, before, _open, () => _open, v => { _open = v; _ase.Select(v); }, N);
                }
                if (_reportOut.Length > 0) outputs[_reportOut] = MpaiJson.ToJson(report.Report());
            }
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

    // The Basic Audio Scene an Audio Scene holds: the first sub-scene that is one the Module keeps - in full, or by its name - else the Audio Scene's own name, which is a
    // Basic Scene's when the Scene is one this AIM gave. The key of the version in force.
    private string? BasicSceneOf(AudioSceneDescriptors scene)
    {
        foreach (var sub in scene.SubAudioScenes ?? [])
            if (_ase.SceneKey(sub.BasicScene?.BasicAudioSceneDescriptorsID ?? sub.SceneId) is { } held) return held;
        return _ase.SceneKey(scene.AudioSceneDescriptorsID);
    }

    // The open Basic Audio Scene as the Audio Scene this AIM gives: the Scene is the one sub-scene, which stands at the origin of the Audio Scene's frame.
    private AudioSceneDescriptors Given(string basicSceneId)
    {
        var basic = _ase.MaterializeBasicScene(basicSceneId);
        return new AudioSceneDescriptors
        {
            MInstanceID = basic.MInstanceID, UEnvironmentID = basic.UEnvironmentID,
            AudioSceneDescriptorsID = basic.BasicAudioSceneDescriptorsID,
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

        if (data.ListedObjects is { } listing) List(listing, report);
        if (data.DeliveredObject is { } delivered) Deliver(delivered, report);

        // THE SCENES BY NAME (the author, 2026/10/28): a new empty Scene is opened, a Scene kept is opened, the open Scene is given a name. The Scene that was open stays kept.
        if (NameBook.Clean(data.NewScene) is { } fresh)
        {
            if (_ase.SceneKey(fresh) is not null) report.Failed("NewScene", fresh, "a Scene of that name is kept");
            else
                try { _open = _ase.CreateEmptyBasicScene(listener ?? Origin(), fresh).AssetId; report.Done("NewScene", fresh, N(_open)); }
                catch (Exception failure) { report.Failed("NewScene", fresh, failure.Message); }
        }
        if (NameBook.Clean(data.OpenedScene) is { } wanted)
        {
            if (_ase.SceneKey(wanted) is not { } held) report.Failed("OpenedScene", wanted, "the Module has no saved Audio Scene of that name");
            else { _open = held; _ase.Select(held); report.Done("OpenedScene", wanted, N(_open)); }
        }
        if (NameBook.Clean(data.NamedScene) is { } given)
        {
            if (_open is null) report.Failed("NamedScene", given, "no Basic Audio Scene is open");
            else if (_ase.RenameScene(_open, given) is { } problem) report.Failed("NamedScene", given, problem);
            else report.Done("NamedScene", given, N(_open));
        }
        { var gone = Deletions.Apply(data, _ase.Names, NamedKind.AudioScene, "Audio Scene", report); if (_open is not null && gone.Contains(_open)) _open = null; }
        foreach (var entry in data.DeletedObjects?.Objects ?? [])
            if (entry.ObjectID is null && NameBook.Clean(entry.AcousticProfileID) is { } kept)
            {
                if (_ase.Profiles.Delete(kept)) report.Done("DeletedObjects", kept);
                else report.Failed("DeletedObjects", kept, "the Module keeps no Acoustic Profile of that name");
            }
        // THE LOUDNESS OF A SCENE (M3282 UCM-5, the programme): the Audio Scene Descriptors carry no loudness of a Scene, so it is not set here; the Level of each Object is.
        if (data.LUFS is not null)
            report.NotSupported("LUFS", N(_open), "the loudness of a Scene is not carried by the Audio Scene Descriptors: set the loudness of each Object (Level, or LUFS in a ModifiedObjects entry)");
        if (data.ModifiedObjects is { Objects.Count: > 0 })
            report.NotSupported("ModifiedObjects", N(_open), "what an Object is like is Audio Object Editing's");
        if (data.TranslatedObjects is { Objects.Count: > 0 })
            report.NotSupported("TranslatedObjects", N(_open), "speech is translated by Speech Object Editing");

        // What is taken out first, then what is put in: one command replaces an Object with another at the same PoV. An Object is named by an identifier, by the Object
        // itself (the Object or ID of the data types), or - for taking out and moving - by where it is.
        if (_open is not null && data.RemovedObjects is { Objects.Count: > 0 }) TakeOut(data.RemovedObjects, report);

        if (data.AddedObjects is { Objects.Count: > 0 } added)
            foreach (var entry in added.Objects)
            {
                var id = IdOf(entry.ObjectID);
                var key = _ase.ObjectKey(id);
                if (!_ase.IsBasicObject(key))
                {
                    report.Failed("AddedObjects", id, "a Basic Audio Scene holds Basic Audio Objects, named by their names");
                    continue;
                }
                if (Amounts.PlaceProblem(entry.SpatialAttitude) is { } problem) { report.Failed("AddedObjects", id, problem); continue; }
                if (_open is null && NameBook.Clean(added.ObjectID) is null)
                {
                    report.Failed("AddedObjects", id, "a name is needed for the Audio Scene that is made: the command gives it in ObjectID");
                    continue;
                }
                try
                {
                    _open = _open is null
                        ? _ase.CreateBasicScene(key!, listener ?? Origin(), Placement(entry.SpatialAttitude), added.ObjectID).AssetId
                        : _ase.AddObjectToBasicScene(_open, key!, Placement(entry.SpatialAttitude)).AssetId;
                    report.Done("AddedObjects", id, N(_open));
                }
                catch (Exception failure) { report.Failed("AddedObjects", id, failure.Message); }
            }

        // A room asked for when no Scene is open makes the (empty) Scene, under the name the command gives it (AddedObjects.ObjectID, with no Objects).
        if (_open is null && data.ClosedSpace is { Count: > 0 } && NameBook.Clean(data.AddedObjects?.ObjectID) is not null)
        {
            try { _open = _ase.CreateEmptyBasicScene(listener ?? Origin(), data.AddedObjects!.ObjectID).AssetId; }
            catch (Exception failure) { report.Failed("ClosedSpace", null, failure.Message); return; }
        }

        if (_open is null)
        {
            foreach (var (action, any) in new[] { ("MovedObjects", data.MovedObjects is { Objects.Count: > 0 }),
                                                  ("ChangedObjects", data.ChangedObjects is { Objects.Count: > 0 }),
                                                  ("RemovedObjects", data.RemovedObjects is { Objects.Count: > 0 }) })
                if (any) report.Failed(action, null, "no Basic Audio Scene is open");
            if (data.ClosedSpace is { Count: > 0 }) report.Failed("ClosedSpace", null, "no Basic Audio Scene is open");
            if (data.ModifiedScene?.AcousticProfile is not null) report.Failed("ModifiedScene", null, "no Basic Audio Scene is open");
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
            {
                if (entry.SpatialAttitude is null && entry.HasFeature) { report.NotSupported("ChangedObjects", IdOf(entry.ObjectID), "the features of an Object are Audio Object Editing's; here, where an Object is in the Scene"); continue; }
                Place("ChangedObjects", IdOf(entry.ObjectID), entry.SpatialAttitude, report);
            }

        // The acoustics of the Scene: its Acoustic Profile, as given.
        if (data.ModifiedScene?.AcousticProfile is { } acoustics)
        {
            var toApply = acoustics.Reference ? _ase.Profiles.Get(acoustics.AcousticProfileID) : acoustics;
            if (toApply is null) report.Failed("ModifiedScene", N(_open), $"the Module keeps no Acoustic Profile named \"{acoustics.AcousticProfileID}\"");
            else
            {
                _open = _ase.SetBasicSceneAcousticProfile(_open, toApply).AssetId;
                // A profile given in full is kept under its identifier - as the Scene now has it, with what the Scene had and the profile did not state.
                if (!acoustics.Reference && _ase.AcousticProfileOf(_open) is { } now) _ase.Profiles.Keep(now);
                report.Done("ModifiedScene", N(_open), N(_open));
            }
        }

        // The room: the space the Scene is in, replaced as given.
        if (data.ClosedSpace is { Count: > 0 } room)
        {
            _open = _ase.SetBasicSceneClosedSpace(_open, room).AssetId;
            report.Done("ClosedSpace", N(_open), N(_open));
        }

        // Once, on the Scene: its User PoV overrides each member's.
        if (listener is not null)
        {
            _open = _ase.SetBasicSceneListener(_open, listener).AssetId;
            report.Done("UserPoV", N(_open), N(_open));
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
                else { _open = result.AssetId; report.Done("RemovedObjects", id, N(_open)); }
            }
        }
    }

    // A move or a change is the member re-placed at the new attitude.
    private void Place(string action, string? id, SpatialAttitude? attitude, CommandReport report)
    {
        // The amount is the command's: a position, an orientation or a time it carries; none is not guessed. A command may state only the parts that change: the others keep the value the
        // member has in the Scene (M3282 UCM-1).
        if (Amounts.ChangeProblem(attitude) is { } problem) { report.Failed(action, id, problem); return; }
        var before = id is null ? null : _ase.PlacementOf(_open!, id);
        if (id is null || before is null) { report.Failed(action, id, "the open Scene has no such member"); return; }
        var result = _ase.ReplaceInBasicScene(_open!, id, Placement(Merged(attitude!, before)));
        if (result is null) { report.Failed(action, id, "the open Scene has no such member"); return; }
        _open = result.AssetId;
        report.Done(action, id, N(_open));
    }

    // The attitude the member is to have: what the command states, and for what it does not state what the member has.
    private static SpatialAttitude Merged(SpatialAttitude change, SpaceTime before)
    {
        var old = before.SpatialAttitude1;
        return new SpatialAttitude
        {
            Header = change.Header, MInstanceID = change.MInstanceID ?? old?.MInstanceID, UEnvironmentID = change.UEnvironmentID ?? old?.UEnvironmentID,
            ObjectSpatialAttitudeID = string.IsNullOrEmpty(change.ObjectSpatialAttitudeID) ? old?.ObjectSpatialAttitudeID ?? "" : change.ObjectSpatialAttitudeID,
            SpatialAttitudeTime = change.SpatialAttitudeTime ?? (before.Time is { SimpleTimeData.Count: > 0 } ? before.Time : null),
            General = change.General ?? old?.General,
            Position = change.PositionStated ? change.Position : old?.Position ?? new Position(),
            Orientation = change.OrientationStated ? change.Orientation : old?.Orientation ?? new Orientation(),
            DataXMData = change.DataXMData ?? old?.DataXMData, DescrMetadata = change.DescrMetadata ?? old?.DescrMetadata
        };
    }

    // PLAYING (the author, 2026/11/02: "play clip777"): the Module does not play - the User Agent has the loudspeakers. It says which it is to play: the open Scene (named, or "stage", the name
    // the grammar gives the whole of it) or one Object that is a member of it. The report is Done for either; a name that is neither, and no Scene open, are failed with the reason.
    private void Deliver(ManagedObject delivered, CommandReport report)
    {
        var name = NameBook.Clean(IdOf(delivered));
        if (name is null) { report.Failed("DeliveredObject", null, "what is to be played is named: the Scene, or an Object in it"); return; }
        if (_open is null) { report.Failed("DeliveredObject", name, "no Basic Audio Scene is open"); return; }
        var scene = N(_open);
        if (name == "stage" || name == scene) { report.Done("DeliveredObject", scene); return; }
        var members = _ase.Listing(ListKinds.AudioScene, scene).FirstOrDefault()?.Members;
        if (members is not null && members.Any(m => m.ObjectID == name)) report.Done("DeliveredObject", name);
        else report.Failed("DeliveredObject", name, "the open Scene has no such member");
    }

    private void List(ObjectListing listing, CommandReport report)
    {
        if (!ListKinds.Known(listing.Kind)) { report.Failed("ListedObjects", null, $"the Kind is required: one of {string.Join(", ", ListKinds.Every)}"); return; }
        if (listing.Kind == ListKinds.AcousticProfile && NameBook.Clean(listing.ObjectID) is null)
        {
            // No Scene named: the Acoustic Profiles the Module keeps, by their identifiers (M3282 UCM-4).
            report.Listed(_ase.Profiles.All().Select(p => new ListedObject { ObjectID = p.AcousticProfileID, Kind = ListKinds.AcousticProfile, AcousticProfile = p }).ToList());
            return;
        }
        if (listing.Kind is ListKinds.AcousticProfile or ListKinds.Listener or ListKinds.ClosedSpace)
        {
            // Of the Scene named in the request, else of the open one: the Acoustic Profile it has, the Point of View it is heard from, the room it is in.
            var key = listing.ObjectID is { } named && NameBook.Clean(named) is { } clean ? _ase.SceneKey(clean) : _open;
            if (key is null || !_ase.Has(key)) { report.Failed("ListedObjects", listing.ObjectID, listing.ObjectID is null ? "no Basic Audio Scene is open" : "the Module keeps no Audio Scene of that name"); return; }
            report.Listed([listing.Kind == ListKinds.Listener ? _ase.ListedListener(key)
                         : listing.Kind == ListKinds.ClosedSpace ? _ase.ListedClosedSpace(key)
                         : new ListedObject { ObjectID = _ase.SceneName(key) ?? "", Kind = ListKinds.AcousticProfile, AcousticProfile = _ase.AcousticProfileOf(key) }]);
            return;
        }
        if (listing.Kind != ListKinds.All && listing.Kind != ListKinds.AudioScene)
        { report.NotSupported("ListedObjects", null, $"the {listing.Kind}s are listed by {ListKinds.ListedBy(listing.Kind)}"); return; }
        if (listing.ObjectID is not null && listing.Kind == ListKinds.All) { report.Failed("ListedObjects", listing.ObjectID, "ObjectID names one thing of one Kind: the Kind is required, not All"); return; }
        var listed = _ase.Listing(listing.Kind!, NameBook.Clean(listing.ObjectID));
        if (listing.ObjectID is not null && listed.Count == 0) { report.Failed("ListedObjects", listing.ObjectID, $"the Module keeps no {listing.Kind} of that name"); return; }
        report.Listed(listed);
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
