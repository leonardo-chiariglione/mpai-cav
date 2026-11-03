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
// when Shared Storage does not hold it yet and the Speech Scene holds it in full). A User
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
// Every edit is a new version in Shared Storage (BSS000001 ... as the Module keeps them).
// A member is held by its name and read from Shared Storage when the Scene is output, so
// a Speech Object translated in its place is heard translated in every Scene holding it.
public sealed class SseAimProcessor : IAimProcessor
{
    private readonly ISharedStorage _storage;
    private readonly string _mInstanceId;

    private readonly string _commandPort;
    private readonly string _objectPort;
    private readonly string _scenePort;
    private readonly string _povPort;
    private readonly string _sceneOut;
    private readonly string _reportOut;

    private string? _open;      // the key of the version in force (the Module's own; the User is told the name)
    private readonly UndoJournal _undo = new();
    private readonly NameBook _names;

    private string? N(string? key) => _names.NameOf(key);

    public string InstanceId { get; }

    public SseAimProcessor(string instanceId, ISharedStorage storage, AimPortReader ports, string mInstanceId = "ASM")
    {
        InstanceId   = instanceId;
        _storage     = storage;
        _mInstanceId = mInstanceId;
        _names       = new NameBook(storage);
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

        // A Speech Object from Speech Object Editing is in Shared Storage already; one
        // given otherwise is kept, so that a command can name it. One with no name is not kept.
        string? unnamed = null;
        if (In(_objectPort) is { } objectJson)
        {
            try
            {
                var arrived = SpeechStore.Parse(objectJson);
                if (SpeechStore.Alone(arrived) is { } speech) { if (SpeechStore.Get(_storage, speech.BasicSpeechObjectID) is null) SpeechStore.Keep(_storage, speech); }
                else SpeechStore.KeepObject(_storage, arrived);       // several: kept, its Basic Speech Objects with it
            }
            catch (NameNeededException e) { unnamed = $"{e.Message}: the Object is named in its identifier (BasicSpeechObjectID, SpeechObjectID)"; }
            catch (InvalidOperationException e) { unnamed = e.Message; }
        }

        // 1. opening, by Port.
        if (In(_scenePort) is { } sceneJson)
        {
            var arrived = MpaiJson.FromJson<SpeechSceneDescriptors>(sceneJson);
            var held = arrived.SubSpeechScenes?.FirstOrDefault(s => s.BasicScene is not null)?.BasicScene;
            var id = held?.BasicSpeechSceneDescriptorsID
                ?? arrived.SubSpeechScenes?.Select(s => s.SceneId).FirstOrDefault(i => !string.IsNullOrWhiteSpace(i))
                ?? arrived.SpeechSceneDescriptorsID;
            var key = _names.KeyOf(NamedKind.SpeechScene, id) ?? _names.KeyOf(NamedKind.SpeechScene, arrived.SpeechSceneDescriptorsID);
            if (key is not null) _open = key;
            else if (held is not null && NameBook.Clean(held.BasicSpeechSceneDescriptorsID) is not null)
                _open = Save(held.BasicSpeechSceneItems, held.UserPoV, held, null, held.BasicSpeechSceneDescriptorsID);      // a Basic Speech Scene the storage does not hold, given in full
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
                    Apply(command, pov, report);
                    _undo.RecordVersions(command.UserCommandID, before, _open, () => _open, v => { _open = v; if (v is not null && N(v) is { } n) _names.Point(NamedKind.SpeechScene, n, v); }, N);
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
        if (Load(_open) is { } open) outputs[_sceneOut] = MpaiJson.ToJson(Given(Materialized(open)));
        return Task.FromResult(new Message
        {
            MessageId   = Guid.NewGuid().ToString(),
            MessageType = _open is null ? "NoOpenScene" : "SpeechSceneDescriptors",
            DataType    = _open is null ? "" : "OSD-SSD-V1.5",
            Ports       = outputs
        });
    }

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
        { var gone = Deletions.Apply(data, _names, NamedKind.SpeechScene, "Speech Scene", report); if (_open is not null && gone.Contains(_open)) _open = null; }
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
                if (id is null || SpeechStore.Get(_storage, id) is null) { report.Failed("AddedObjects", id, "no such Basic Speech Object"); continue; }
                if (Amounts.PlaceProblem(entry.SpatialAttitude) is { } problem) { report.Failed("AddedObjects", id, problem); continue; }
                if (scene is null && NameBook.Clean(added.ObjectID) is null)
                { report.Failed("AddedObjects", id, "a name is needed for the Speech Scene that is made: the command gives it in ObjectID"); continue; }
                items.Add(new BasicSpeechSceneItem { ObjectSpaceTime = Placement(entry.SpatialAttitude), SpeechObject = new BasicSpeechObject { BasicSpeechObjectID = id } });
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

    // A new version: BSS000001, BSS000002 ... as the Module keys them. A Scene made before (oldKey) keeps its name, which designates the new version from now on; a new
    // one has the name given, or the next free one - and no name, no Scene.
    private string Save(IEnumerable<BasicSpeechSceneItem> items, PointOfView? userPoV, BasicSpeechSceneDescriptors? from, string? oldKey, string? newName)
    {
        var existingName = _names.NameOf(oldKey);
        if (existingName is null && NameBook.Clean(newName) is null) throw new NameNeededException("the Speech Scene");
        const string counter = "_counter:BSS";
        long next = _storage.MPAI_AIFM_SharedStorage_Exists(counter)
            ? long.Parse(Encoding.UTF8.GetString(_storage.MPAI_AIFM_SharedStorage_Get(counter))) + 1 : 1;
        _storage.MPAI_AIFM_SharedStorage_Put(counter, Encoding.UTF8.GetBytes(next.ToString()));
        var key = $"BSS{next:D6}";
        string id;
        if (existingName is not null) { id = existingName; _names.Point(NamedKind.SpeechScene, id, key); }
        else id = _names.Claim(NamedKind.SpeechScene, newName, key);
        // A member is kept by its name only; its Speech Object stays where it is.
        var kept = items.Select(i => new BasicSpeechSceneItem
        {
            ObjectSpaceTime = i.ObjectSpaceTime, UserPoV = i.UserPoV,
            SpeechObject = i.SpeechObject is { } s ? new BasicSpeechObject { BasicSpeechObjectID = SpeechStore.Get(_storage, s.BasicSpeechObjectID) is null ? SpeechStore.Keep(_storage, s) : s.BasicSpeechObjectID } : null
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
        _storage.MPAI_AIFM_SharedStorage_Put(key, Encoding.UTF8.GetBytes(MpaiJson.ToJson(scene)));
        return key;
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
        _names.All(NamedKind.SpeechScene).Where(e => named is null || e.Name == named).Select(e =>
        {
            var items = Load(e.Key)?.BasicSpeechSceneItems;
            return new ListedObject
            {
                ObjectID = e.Name, Kind = ListKinds.SpeechScene,
                MemberObjectIDs = items?.Select(i => i.SpeechObject?.BasicSpeechObjectID).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToList(),
                ObjectCount = named is null ? null : items?.Count,
                Members = named is null || items is null ? null
                    : items.Select(i => new ListedMember { ObjectID = i.SpeechObject?.BasicSpeechObjectID ?? "", PoV = i.ObjectSpaceTime?.SpatialAttitude1 }).ToList()
            };
        }).ToList();

    private BasicSpeechSceneDescriptors? Load(string? id) =>
        id is null || !_storage.MPAI_AIFM_SharedStorage_Exists(id) ? null
        : MpaiJson.FromJson<BasicSpeechSceneDescriptors>(Encoding.UTF8.GetString(_storage.MPAI_AIFM_SharedStorage_Get(id)));

    // The Scene with each member's Speech Object as Shared Storage now holds it.
    private BasicSpeechSceneDescriptors Materialized(BasicSpeechSceneDescriptors scene) => new()
    {
        MInstanceID = scene.MInstanceID, UEnvironmentID = scene.UEnvironmentID,
        BasicSpeechSceneDescriptorsID = scene.BasicSpeechSceneDescriptorsID, BasicSpeechSceneDescriptorsTime = scene.BasicSpeechSceneDescriptorsTime,
        BasicSpeechSceneDescriptorsSpaceTime = scene.BasicSpeechSceneDescriptorsSpaceTime, UserPoV = scene.UserPoV,
        ClosedSpace = scene.ClosedSpace, AcousticProfile = scene.AcousticProfile, ObjectCount = scene.ObjectCount,
        BasicSpeechSceneItems = scene.BasicSpeechSceneItems.Select(i => new BasicSpeechSceneItem
        {
            ObjectSpaceTime = i.ObjectSpaceTime, UserPoV = i.UserPoV,
            SpeechObject = i.SpeechObject is { } s ? SpeechStore.Get(_storage, s.BasicSpeechObjectID) ?? s : null
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
