using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AIF.Controller;
using AIF.SharedStorage;

using Mpai.Mmc.Soe;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Mmc.Sse;

// MMC-SSE-V2.5 - Speech Scene Editing, as the author's Reference Model of Audio Scene
// Management draws it (CAE Reference Models, 2026/10/26): a Speech Object (OSD-SPO, from
// Speech Object Editing), a Speech Scene (OSD-SSD), a User Command and a User PoV in; the
// Speech Scene as edited, and the User Command with its response, out. There is no Basic
// Speech Scene on a Port: a Speech Scene is a collection of Speech Objects and of Basic
// Speech Scenes, and the one Basic Speech Scene that is edited is held in it.
//
// OPEN BY PORT, ACT BY COMMAND, as Audio Scene Editing does. A Speech Scene arriving that
// holds a Basic Speech Scene - or whose own identifier is one - opens that Scene (kept,
// when the Module's Private Storage does not hold it yet and the Speech Scene holds it in full). A User
// Command acts on the open Scene - the first Object added to no open Scene creates one:
//
//   AddedObjects     place the named Basic Speech Objects
//   MovedObjects     old attitude -> new attitude
//   ChangedObjects   where a placed Object is
//   RemovedObjects   take placed Objects out
//   UserPoV          where the Scene is heard from (also on its own Port)
//
// The Speech Scene given is the open Basic Speech Scene inside a Speech Scene of one
// sub-scene, under the Basic Scene's identifier.
//
// A SCENE AND ITS OBJECTS ARE NAMED BY THE USER (the author, 2026/10/26): in the data, the
// commands and the replies they are identified only by their names; the Module's storage
// keys never leave it. The first Object added to no open Scene creates one, under the
// name the command gives (ObjectID of AddedObjects, required then); a second Scene given
// a name that is taken is "dialogue 2". Where an Object is put, to where it is moved,
// are the command's amounts: a command without them is reported as failed, not guessed.
// ListedObjects lists the Speech Scenes the Module keeps, by name, with their members.
//
// Every edit is a new version in the Private Storage of the Module, at OSD-SSD-V1.5#1/<the Scene's ID>@<version> (the author, 2026/11/08), and the Scene the Module gives is
// also offered in its Shared Storage at <Module>/OSD-SSD-V1.5#1/<ID>. A member is held by its name and read, when the Scene is output, from where Speech Object Editing keeps
// it - OSD-SPO-V1.5#1 and OSD-BSO-V1.5#2, by Data Type and Port, not by that AIM's own way of keying - so a Speech Object translated in its place is heard translated in every
// Scene holding it. THIS AIM ONLY READS Speech Objects: Speech Object Editing writes them.
public sealed class SseAimProcessor : IAimProcessor
{
    private readonly ModuleStore _store;
    private readonly string _mInstanceId;

    private readonly string _commandPort;
    private readonly string _objectPort;
    private readonly string _scenePort;
    private readonly string _povPort;
    private readonly string _sceneOut;
    private readonly string _reportOut;

    private string? _open;      // the name of the Scene (its versions are kept under it; the version in force is the highest)
    private SpeechSceneDescriptors? _frame;     // the Full Speech Scene the open Basic Scene was given in, if it was Full
    private readonly UndoJournal _undo = new();

    private static string? N(string? id) => id;

    public string InstanceId { get; }

    public SseAimProcessor(string instanceId, ModuleStore store, AimPortReader ports, string mInstanceId = "ASM")
    {
        InstanceId   = instanceId;
        _store       = store;
        _mInstanceId = mInstanceId;
        store.Changed += Offer;
        _commandPort = ports.Input("CAE-UCM-V1.0");
        _objectPort  = ports.InputOrDefault("OSD-SPO-V1.5", "");
        _scenePort   = ports.Input("OSD-SSD-V1.5");
        _povPort     = ports.InputOrDefault("OSD-OPV-V1.5", "");
        _sceneOut    = ports.Output("OSD-SSD-V1.5");
        _reportOut   = ports.OutputOrDefault("CAE-UCM-V1.0", "");
    }

    public Task<Message> ProcessAsync(Message message)
    {
        var outputs = new Dictionary<string, string>();
        string? In(string port) =>
            port.Length > 0 && message.Ports.TryGetValue(port, out var json) && !string.IsNullOrWhiteSpace(json) ? json : null;

        // A Speech Object comes from Speech Object Editing, which keeps it; this AIM does not write Speech Objects. One that is not kept cannot be named by a command.
        string? unnamed = null;
        if (In(_objectPort) is { } objectJson)
        {
            try
            {
                var arrived = SpeechStore.Parse(objectJson);
                if (NameBook.Clean(arrived.SpeechObjectID) is null) throw new NameNeededException("the Speech Object");
            }
            catch (NameNeededException e) { unnamed = $"{e.Message}: the Object is named in its identifier (BasicSpeechObjectID, SpeechObjectID)"; }
            catch (InvalidOperationException e) { unnamed = e.Message; }
        }

        // 1. opening, by Port.
        if (In(_scenePort) is { } sceneJson)
        {
            var arrived = MpaiJson.FromJson<SpeechSceneDescriptors>(sceneJson);
            // Every Basic Speech Scene a Speech Scene holds, at any depth (a Full Scene: several sub-scenes, sub-scenes that are Full): one the storage does not hold, given in full, is kept; the first
            // is the one opened. A lone Basic Speech Scene, or a Scene of one, is the case of one part and goes as it always did.
            var basics = BasicScenes(arrived).ToList();
            string? first = null;
            foreach (var basic in basics)
            {
                var id = basic.Held?.BasicSpeechSceneDescriptorsID ?? basic.Id;
                var kept = KeptScene(id);
                if (kept is null && basic.Held is { } given && NameBook.Clean(given.BasicSpeechSceneDescriptorsID) is not null)
                    kept = Save(given.BasicSpeechSceneItems, given.UserPoV, given, null, given.BasicSpeechSceneDescriptorsID);      // a Basic Speech Scene the storage does not hold, given in full
                first ??= kept;
            }
            var key = first ?? KeptScene(arrived.SpeechSceneDescriptorsID);
            if (key is not null)
            {
                _open = key;
                _frame = IsFull(arrived) ? arrived : null;      // a Full Speech Scene is the frame the open Basic Scene is edited in: it is given back with the Basic Scenes as they now stand
            }
            if (_open is not null) Console.WriteLine($"[MMC-SSE-V2.5] opened {N(_open)}");
        }

        PointOfView? pov = In(_povPort) is { } povJson ? MpaiJson.FromJson<PointOfView>(povJson) : null;

        // 2. acting, by Command; the report goes back under the command's identifier.
        if (In(_commandPort) is { } commandJson)
        {
            var command = CommandReader.Read(commandJson, out var rejection);
            if (command is null) { if (_reportOut.Length > 0) outputs[_reportOut] = MpaiJson.ToJson(rejection); }
            else
            {
                var report = new CommandReport(command);
                if (unnamed is not null) report.Failed("AcquiredObject", null, unnamed);
                if (command.UserCommandRQ?.RevokedUserCommandID is { } revoked) _undo.Revoke(revoked, report);
                else
                {
                    var before = _open;
                    _store.ForgetSteps();
                    Apply(command, pov, report);
                    // What the command wrote is put back by taking the versions it wrote away; nothing is recorded when it wrote nothing and left the same Scene open.
                    var after = _open;
                    if (_store.TakeSteps() is { } steps)
                        _undo.Record(command.UserCommandID, () => _open == after && steps.Unchanged(), () => { steps.Undo(); _open = before; return before; });
                    else if (before != after)
                        _undo.Record(command.UserCommandID, () => _open == after, () => { _open = before; return before; });
                }
                if (_reportOut.Length > 0) outputs[_reportOut] = MpaiJson.ToJson(report.Report());
            }
        }
        else if (unnamed is not null && _reportOut.Length > 0)
        {
            // An Object that came by its Port and was not taken: no command to report under, so a report of its own.
            var report = new CommandReport(new UserCommand { UserCommandID = Guid.NewGuid().ToString() });
            report.Failed("AcquiredObject", null, unnamed);
            outputs[_reportOut] = MpaiJson.ToJson(report.Report());
            if (pov is not null && Load(_open) is { } sceneKept) _open = Save(sceneKept.BasicSpeechSceneItems, pov, sceneKept, _open, null);
        }
        else if (pov is not null && Load(_open) is { } scene)
            _open = Save(scene.BasicSpeechSceneItems, pov, scene, _open, null);

        // 3. the open Scene, as it now stands: each member read from Shared Storage.
        if (Load(_open) is { } open) outputs[_sceneOut] = MpaiJson.ToJson(GivenFor(open));
        return Task.FromResult(new Message
        {
            MessageId   = Guid.NewGuid().ToString(),
            MessageType = _open is null ? "NoOpenScene" : "SpeechSceneDescriptors",
            DataType    = _open is null ? "" : "OSD-SSD-V1.5",
            Ports       = outputs
        });
    }

    // Every Basic Speech Scene a Speech Scene holds, at any depth: the Scene itself when it is given in full, or its identifier.
    private static IEnumerable<(BasicSpeechSceneDescriptors? Held, string? Id)> BasicScenes(SpeechSceneDescriptors scene)
    {
        foreach (var sub in scene.SubSpeechScenes ?? [])
        {
            if (sub.BasicScene is not null || !string.IsNullOrWhiteSpace(sub.SceneId)) yield return (sub.BasicScene, sub.SceneId);
            if (sub.SubScene is { } inner) foreach (var deeper in BasicScenes(inner)) yield return deeper;
        }
    }

    // A Speech Scene that is Full: more than one sub-scene, or a sub-scene that is not Basic, or Objects of its own.
    private static bool IsFull(SpeechSceneDescriptors scene) =>
        (scene.SubSpeechScenes?.Count ?? 0) > 1 || (scene.SubSpeechScenes ?? []).Any(s => s.SubScene is not null) || (scene.Objects?.Count ?? 0) > 0;

    // The Speech Scene this AIM gives for the open Basic Scene: the frame it was given in, if it was Full and still holds the open Scene, with each Basic Scene the Module keeps as it now
    // stands; else the Scene alone.
    private SpeechSceneDescriptors GivenFor(BasicSpeechSceneDescriptors open)
    {
        if (_frame is not null)
        {
            if (BasicScenes(_frame).Any(b => (b.Held?.BasicSpeechSceneDescriptorsID ?? b.Id) == open.BasicSpeechSceneDescriptorsID)) return Refreshed(_frame);
            _frame = null;                                                        // the open Scene is not one of the frame: another was opened, made or renamed: it is given alone
        }
        return Given(Materialized(open));
    }

    private SpeechSceneDescriptors Refreshed(SpeechSceneDescriptors scene) => new()
    {
        MInstanceID = scene.MInstanceID, UEnvironmentID = scene.UEnvironmentID, SpeechSceneDescriptorsID = scene.SpeechSceneDescriptorsID, SpeechSceneDescriptorsSpaceTime = scene.SpeechSceneDescriptorsSpaceTime,
        UserPoV = scene.UserPoV, ClosedSpace = scene.ClosedSpace, ObjectCount = scene.ObjectCount, Objects = scene.Objects, SubSpeechSceneCount = scene.SubSpeechSceneCount,
        SubSpeechScenes = scene.SubSpeechScenes?.Select(e =>
        {
            if (KeptScene(e.BasicScene?.BasicSpeechSceneDescriptorsID ?? e.SceneId) is { } kept && Load(kept) is { } held)
                return new SubSpeechSceneEntry { SubSpeechSceneSpaceTime = e.SubSpeechSceneSpaceTime, BasicScene = Materialized(held) };
            if (e.SubScene is { } inner) return new SubSpeechSceneEntry { SubSpeechSceneSpaceTime = e.SubSpeechSceneSpaceTime, SubScene = Refreshed(inner) };
            return e;
        }).ToList(),
        DataXMData = scene.DataXMData, DescrMetadata = scene.DescrMetadata
    };

    // The Basic Speech Scene as the Speech Scene this AIM gives: the Scene is the one sub-scene, which stands at the origin of the Speech Scene's frame.
    private static SpeechSceneDescriptors Given(BasicSpeechSceneDescriptors basic) => new()
    {
        MInstanceID = basic.MInstanceID, UEnvironmentID = basic.UEnvironmentID,
        SpeechSceneDescriptorsID = basic.BasicSpeechSceneDescriptorsID,
        SpeechSceneDescriptorsSpaceTime = basic.BasicSpeechSceneDescriptorsSpaceTime,
        UserPoV = basic.UserPoV,
        SubSpeechSceneCount = 1,
        SubSpeechScenes = [new SubSpeechSceneEntry { BasicScene = basic }]
    };

    private void Apply(UserCommand command, PointOfView? pov, CommandReport report)
    {
        var data = command.UserCommandRQ;
        if (data is null) return;
        var listener = data.UserPoV ?? pov;

        if (data.ListedObjects is { } listing) List(listing, report);
        { var gone = Deletions.Apply(data, Delete, "Speech Scene", report); if (_open is not null && gone.Contains(_open)) _open = null; }
        if (data.ModifiedObjects is { Objects.Count: > 0 })
            report.NotSupported("ModifiedObjects", N(_open), "what an Object is like is Speech Object Editing's");
        if (data.TranslatedObjects is { Objects.Count: > 0 })
            report.NotSupported("TranslatedObjects", N(_open), "speech is translated by Speech Object Editing");

        var scene = Load(_open);
        var items = scene?.BasicSpeechSceneItems.ToList() ?? new List<BasicSpeechSceneItem>();
        var before = _open;
        var changed = false;

        if (data.AddedObjects is { Objects.Count: > 0 } added)
            foreach (var entry in added.Objects)
            {
                var id = IdOf(entry.ObjectID);
                // A Basic Speech Object is placed as it is; a Speech Object of several (sub-Objects too) part by part, each where the Object's placement and its own put it: a Basic Speech Scene holds
                // Basic Speech Objects (the simplest reading of how a Full Object is placed).
                var parts = SpeechStore.Leaves(_store, id, Placement(entry.SpatialAttitude));
                if (id is null || parts.Count == 0) { report.Failed("AddedObjects", id, "no such Basic Speech Object"); continue; }
                if (Amounts.PlaceProblem(entry.SpatialAttitude) is { } problem) { report.Failed("AddedObjects", id, problem); continue; }
                if (scene is null && NameBook.Clean(added.ObjectID) is null)
                { report.Failed("AddedObjects", id, "a name is needed for the Speech Scene that is made: the command gives it in ObjectID"); continue; }
                foreach (var (part, at) in parts)
                    items.Add(new BasicSpeechSceneItem { ObjectSpaceTime = at, SpeechObject = new BasicSpeechObject { BasicSpeechObjectID = part } });
                report.Done("AddedObjects", id); changed = true;
            }

        void Replace(string action, ManagedObject? named, Func<BasicSpeechSceneItem, BasicSpeechSceneItem?> change)
        {
            var id = IdOf(named);
            var at = id is null ? -1 : items.FindIndex(i => i.SpeechObject?.BasicSpeechObjectID == id);
            if (at < 0) { report.Failed(action, id, scene is null && !changed ? "no Basic Speech Scene is open" : "the open Scene has no such member"); return; }
            if (change(items[at]) is { } kept) items[at] = kept; else items.RemoveAt(at);
            report.Done(action, id); changed = true;
        }
        BasicSpeechSceneItem At(BasicSpeechSceneItem i, SpatialAttitude? a) =>
            new() { SpeechObject = i.SpeechObject, UserPoV = i.UserPoV, ObjectSpaceTime = Placement(a) ?? i.ObjectSpaceTime };

        // Where a member goes is the command's amount: a position and an orientation; none is not guessed.
        if (data.MovedObjects is { Objects.Count: > 0 } moved)
            foreach (var entry in moved.Objects)
            {
                if (Amounts.PlaceProblem(entry.NewSpatialAttitude) is { } problem) { report.Failed("MovedObjects", IdOf(entry.ObjectID), problem); continue; }
                Replace("MovedObjects", entry.ObjectID, i => At(i, entry.NewSpatialAttitude));
            }
        if (data.ChangedObjects is { Objects.Count: > 0 } changes)
            foreach (var entry in changes.Objects)
            {
                if (entry.SpatialAttitude is null && entry.HasFeature) { report.NotSupported("ChangedObjects", IdOf(entry.ObjectID), "the features of an Object are Audio Object Editing's; here, where an Object is in the Scene"); continue; }
                if (Amounts.PlaceProblem(entry.SpatialAttitude) is { } problem) { report.Failed("ChangedObjects", IdOf(entry.ObjectID), problem); continue; }
                Replace("ChangedObjects", entry.ObjectID, i => At(i, entry.SpatialAttitude));
            }
        if (data.RemovedObjects is { Objects.Count: > 0 } removed)
            foreach (var entry in removed.Objects) Replace("RemovedObjects", entry.ObjectID, _ => null);

        if (listener is not null)
        {
            if (scene is null && !changed) report.Failed("UserPoV", null, "no Basic Speech Scene is open");
            else { report.Done("UserPoV", N(before)); changed = true; }
        }

        if (!changed) return;
        _open = Save(items, listener ?? scene?.UserPoV ?? Origin(), scene, before, data.AddedObjects?.ObjectID);
        report.Produced(N(_open)!);   // every action done made this version, which the Scene's name now designates
    }

    // The name of the Speech Scene the Module keeps under it (its ID); null when it keeps none.
    private string? KeptScene(string? name) => NameBook.Clean(name) is { } clean && _store.Holds(Kinds.SpeechScene, clean) ? clean : null;

    // DELETION of a Speech Scene by its name, with all its versions. Nothing holds a Speech Scene.
    private (DeletionOutcome Outcome, string? Reason) Delete(string name)
    {
        if (KeptScene(name) is not { } id) return (DeletionOutcome.NotKept, null);
        _store.Delete(Kinds.SpeechScene, id);
        return (DeletionOutcome.Deleted, null);
    }

    // The Speech Scene the Module gives, in its Shared Storage: the version in force, with each member's Speech Object in it. One that is gone is withdrawn.
    private void Offer(StoredKind kind, string id)
    {
        if (kind != Kinds.SpeechScene) return;
        _store.Offer(Kinds.SpeechScene, id, Load(id) is { } scene ? Encoding.UTF8.GetBytes(MpaiJson.ToJson(Given(Materialized(scene)))) : null);
    }

    // A new version under the Scene's name (a Scene made before, oldId, keeps its name); a new one has the name given, or the next free one - and no name, no Scene. A member is
    // kept by its name when Speech Object Editing keeps its Speech Object; one it does not keep stays in the Scene as it was given.
    private string Save(IEnumerable<BasicSpeechSceneItem> items, PointOfView? userPoV, BasicSpeechSceneDescriptors? from, string? oldId, string? newName)
    {
        var existingName = KeptScene(oldId);
        if (existingName is null && NameBook.Clean(newName) is null) throw new NameNeededException("the Speech Scene");
        var id = existingName ?? NameBook.Free(_store, newName, Kinds.SpeechScene);
        var kept = items.Select(i => new BasicSpeechSceneItem
        {
            ObjectSpaceTime = i.ObjectSpaceTime, UserPoV = i.UserPoV,
            SpeechObject = i.SpeechObject is { } s ? (SpeechStore.Get(_store, s.BasicSpeechObjectID) is null ? s : new BasicSpeechObject { BasicSpeechObjectID = s.BasicSpeechObjectID }) : null
        }).ToList();
        var scene = new BasicSpeechSceneDescriptors
        {
            MInstanceID = from?.MInstanceID is { Length: > 0 } m ? m : _mInstanceId,
            UEnvironmentID = from?.UEnvironmentID,
            BasicSpeechSceneDescriptorsID = id,
            BasicSpeechSceneDescriptorsTime = from?.BasicSpeechSceneDescriptorsTime ?? SimpleTime.At(DateTimeOffset.UtcNow),
            BasicSpeechSceneDescriptorsSpaceTime = from?.BasicSpeechSceneDescriptorsSpaceTime ?? new SpaceTime(),
            UserPoV = userPoV, ClosedSpace = from?.ClosedSpace, AcousticProfile = from?.AcousticProfile,
            ObjectCount = kept.Count, BasicSpeechSceneItems = kept
        };
        _store.Write(Kinds.SpeechScene, id, Encoding.UTF8.GetBytes(MpaiJson.ToJson(scene)));
        return id;
    }

    private void List(ObjectListing listing, CommandReport report)
    {
        if (!ListKinds.Known(listing.Kind)) { report.Failed("ListedObjects", null, $"the Kind is required: one of {string.Join(", ", ListKinds.Every)}"); return; }
        if (listing.Kind != ListKinds.All && listing.Kind != ListKinds.SpeechScene)
        { report.NotSupported("ListedObjects", null, $"the {listing.Kind}s are listed by {ListKinds.ListedBy(listing.Kind)}"); return; }
        if (listing.ObjectID is not null && listing.Kind == ListKinds.All) { report.Failed("ListedObjects", listing.ObjectID, "ObjectID names one thing of one Kind: the Kind is required, not All"); return; }
        var listed = Listing(NameBook.Clean(listing.ObjectID));
        if (listing.ObjectID is not null && listed.Count == 0) { report.Failed("ListedObjects", listing.ObjectID, $"the Module keeps no {listing.Kind} of that name"); return; }
        report.Listed(listed);
    }

    // THE LIST (the author, 2026/10/26): the Speech Scenes the Module keeps - the version in force of each - by name, with the names of the Objects each holds.
    // A Scene named in the request is listed with the number of its members and, for each, its name and its PoV: the position and orientation the Scene gives it.
    private IReadOnlyList<ListedObject> Listing(string? named = null) =>
        _store.All(Kinds.SpeechScene).Where(e => named is null || e.Id == named).Select(e =>
        {
            var items = Load(e.Id)?.BasicSpeechSceneItems;
            return new ListedObject
            {
                ObjectID = e.Id, Kind = ListKinds.SpeechScene,
                MemberObjectIDs = items?.Select(i => i.SpeechObject?.BasicSpeechObjectID).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToList(),
                ObjectCount = named is null ? null : items?.Count,
                Members = named is null || items is null ? null
                    : items.Select(i => new ListedMember { ObjectID = i.SpeechObject?.BasicSpeechObjectID ?? "", PoV = i.ObjectSpaceTime?.SpatialAttitude1 }).ToList()
            };
        }).ToList();

    private BasicSpeechSceneDescriptors? Load(string? id) =>
        _store.Get(Kinds.SpeechScene, id) is { } bytes ? MpaiJson.FromJson<BasicSpeechSceneDescriptors>(Encoding.UTF8.GetString(bytes)) : null;

    // The Scene with each member's Speech Object as the Module now holds it.
    private BasicSpeechSceneDescriptors Materialized(BasicSpeechSceneDescriptors scene) => new()
    {
        MInstanceID = scene.MInstanceID, UEnvironmentID = scene.UEnvironmentID,
        BasicSpeechSceneDescriptorsID = scene.BasicSpeechSceneDescriptorsID, BasicSpeechSceneDescriptorsTime = scene.BasicSpeechSceneDescriptorsTime,
        BasicSpeechSceneDescriptorsSpaceTime = scene.BasicSpeechSceneDescriptorsSpaceTime, UserPoV = scene.UserPoV,
        ClosedSpace = scene.ClosedSpace, AcousticProfile = scene.AcousticProfile, ObjectCount = scene.ObjectCount,
        BasicSpeechSceneItems = scene.BasicSpeechSceneItems.Select(i => new BasicSpeechSceneItem
        {
            ObjectSpaceTime = i.ObjectSpaceTime, UserPoV = i.UserPoV,
            SpeechObject = i.SpeechObject is { } s ? SpeechStore.Get(_store, s.BasicSpeechObjectID) ?? s : null
        }).ToList(),
        DataXMData = scene.DataXMData, DescrMetadata = scene.DescrMetadata
    };

    private static PointOfView Origin() => new() { PointOfViewID = Guid.NewGuid().ToString(), CartPosition = [0, 0, 0], Orientation = [0, 0, 0] };

    private static SpaceTime? Placement(SpatialAttitude? attitude) =>
        attitude is null ? null : new SpaceTime { SpatialAttitude1 = attitude };

    private static string? IdOf(ManagedObject? managed) =>
        managed is null ? null
        : !string.IsNullOrWhiteSpace(managed.ObjectID) ? managed.ObjectID
        : !string.IsNullOrWhiteSpace(managed.SpeechObject?.BasicSpeechObjectID) ? managed.SpeechObject!.BasicSpeechObjectID
        : null;
}
