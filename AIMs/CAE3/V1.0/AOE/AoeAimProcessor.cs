using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Cae.Aoe;

// CAE-AOE-V1.0 - Audio Object Editing, as the author's Reference Model of Audio Scene
// Management draws it (CAE Reference Models, 2026/10/26): User Command, User PoV and
// an Audio Object (OSD-AUO) in; the Audio Object as edited, and the User Command with
// its response, out. The Object goes on to Audio Scene Editing. There is no Basic Audio
// Object on a Port: an Audio Object is a collection of Basic Audio Objects, and one that
// holds a single Basic Audio Object is that Object.
//
// AN OBJECT IS NAMED BY THE USER (the author, 2026/10/26): in the data, the commands, the
// context and the replies it is identified only by its name - "violin" - the value of its
// BasicAudioObjectID or AudioObjectID. The Module's own storage keys never leave it. A
// second Object given a name that is taken is "violin 2". An Object arriving with no name
// is not created: the report says that a name is needed.
//
// OPEN BY PORT, ACT BY COMMAND. One Basic Object is open at a time, so no Command has to
// name its target. An Audio Object arriving that holds one Basic Audio Object opens
// that Object - the one the Module keeps under that name, when it is the same sound - or
// creates it when it is new (a sound the User Agent captured). One that holds more is
// stored as an Audio Object, from the Basic Objects it holds (each a new Object here,
// named as it came), and is the Audio Object given back as it was stored. Then a User
// Command acts on what is open:
//
//   ModifiedObjects   what it is like: loudness (LUFS) and the Object part of the
//                     Acoustic Profile; the amount is the command's: no amount, no edit
//   UserPoV           where it is heard from, when heard on its own
//   AddedObjects      the named Objects, each with its place (required) and its time, make a
//                     new Audio Object (AUO) under the name the command gives it
//                     (ObjectID, required), which is kept in Shared Storage; the report
//                     names it (ResultID), and it is the Audio Object the AIM gives.
//   ListedObjects     the Audio Objects and Basic Audio Objects the Module keeps, by name
//
// The Audio Object given is the one a command made or an Audio Object stored in this
// run, else the open Basic Object as an Audio Object of one.
//
// Every edit is a new version in Shared Storage. Where an Object is in a Scene is
// Audio Scene Editing's (AddedObjects, MovedObjects, ChangedObjects): a command
// naming those here is reported as not supported, not silently dropped.
public sealed class AoeAimProcessor : IAimProcessor
{
    private readonly AoeAim _aoe;

    private readonly string _commandPort;
    private readonly string _povPort;
    private readonly string _objectPort;  // the Audio Object in (OSD-AUO)
    private readonly string _objectOut;   // the Audio Object out (OSD-AUO)
    private readonly string _reportOut;

    // What is open, between runs: the key of the version in force (the Module's own; the User is told the name).
    private string? _open;
    private readonly UndoJournal _undo = new();
    private string? _composed;            // the Audio Object a command of this run made

    public string InstanceId { get; }

    public AoeAimProcessor(string instanceId, AoeAim aoe, AimPortReader ports)
    {
        InstanceId   = instanceId;
        _aoe         = aoe;
        _commandPort = ports.Input("CAE-UCM-V1.0");
        _povPort     = ports.InputOrDefault("OSD-OPV-V1.5", "");
        _objectPort  = ports.Input("OSD-AUO-V1.5");
        _objectOut   = ports.Output("OSD-AUO-V1.5");
        _reportOut   = ports.OutputOrDefault("CAE-UCM-V1.0", "");
    }

    private string? N(string? key) => _aoe.NameOf(key);

    public Task<Message> ProcessAsync(Message message)
    {
        var outputs = new Dictionary<string, string>();

        // 1. opening, creating or storing, by Port.
        string? made = null;
        string? unnamed = null;     // why an arriving Object was not taken, to be reported
        if (message.Ports.TryGetValue(_objectPort, out var objectJson) && !string.IsNullOrWhiteSpace(objectJson))
        {
            try
            {
                var auo = MpaiJson.FromJson<AudioObject>(objectJson);
                if (auo.BasicAudioObjects is { Count: 1 } && auo.SubAudioObjects is not { Count: > 0 } && auo.BasicAudioObjects[0].BAObjectIDOrBAObject is { } basic)
                {
                    // One Basic Audio Object: the Object the Module keeps under its name is opened when it is the same sound; any other is a new one, under that name or the next
                    // free one. An Object with no name is not created.
                    if (NameBook.Clean(basic.BasicAudioObjectID) is not { } name) throw new NameNeededException("the Basic Audio Object");
                    var held = _aoe.KeyOf(name);
                    _open = held is not null && _aoe.IsTheSound(held, basic) ? held : _aoe.CreateObject(basic, name).AssetId;
                    Console.WriteLine($"[CAE-AOE-V1.0] open {N(_open)}");
                }
                else
                {
                    // Several: an Audio Object the storage holds under its name is that one; else it is stored here, and given back as stored.
                    var held = _aoe.KeyOf(auo.AudioObjectID);
                    made = held is not null && held.StartsWith("AUO", StringComparison.Ordinal) ? held : _aoe.StoreAudioObject(auo).AssetId;
                    Console.WriteLine($"[CAE-AOE-V1.0] audio object {N(made)}");
                }
            }
            catch (NameNeededException e) { unnamed = $"{e.Message}: the Object is named in its identifier (BasicAudioObjectID, AudioObjectID)"; }
            catch (InvalidOperationException e) { unnamed = e.Message; }
        }

        // 2. a User PoV on its Port: where the open Object is heard from.
        if (_povPort.Length > 0 && _open is not null && message.Ports.TryGetValue(_povPort, out var povJson) && !string.IsNullOrWhiteSpace(povJson))
            _open = _aoe.EditBasicObjectProperties(_open, listenerPointOfView: MpaiJson.FromJson<PointOfView>(povJson)).AssetId;

        // 3. acting, by Command; the report goes back under the command's identifier.
        if (message.Ports.TryGetValue(_commandPort, out var commandJson) && !string.IsNullOrWhiteSpace(commandJson))
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
                    Apply(command, report);
                    _undo.RecordVersions(command.UserCommandID, before, _open, () => _open, v => { _open = v; _aoe.Select(v); }, N);
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
        }

        // 4. the Audio Object: the one made or stored in this run, else the open Object as an Audio Object of one. Whole: the Objects it is made of are in it.
        if (made is null) made = _composed;
        _composed = null;
        var given = made ?? _open;
        if (given is not null) outputs[_objectOut] = MpaiJson.ToJson(_aoe.Materialize(given));
        return Task.FromResult(new Message
        {
            MessageId   = Guid.NewGuid().ToString(),
            MessageType = given is null ? "NoOpenObject" : "AudioObject",
            DataType    = given is null ? "" : "OSD-AUO-V1.5",
            Ports       = outputs
        });
    }

    // The Objects, each with its place and its time, as ONE new Audio Object under the name the command gives it.
    private void Compose(ObjectPlacements composed, PointOfView? pov, CommandReport report)
    {
        if (NameBook.Clean(composed.ObjectID) is not { } madeName)
        {
            report.Failed("AddedObjects", null, "a name is needed for the Audio Object that is made: the command gives it in ObjectID");
            return;
        }
        var parts = new List<(string, SpaceTime?)>();
        foreach (var entry in composed.Objects)
        {
            var name = IdOf(entry.ObjectID);
            var key = _aoe.KeyOf(name);
            if (key is null) { report.Failed("AddedObjects", name, "an Audio Object is made of Objects that the Module keeps, named by their names"); return; }
            if (Amounts.PlaceProblem(entry.SpatialAttitude) is { } problem) { report.Failed("AddedObjects", name, problem); return; }
            parts.Add((key, new SpaceTime { SpatialAttitude1 = entry.SpatialAttitude, Time = entry.SpatialAttitude!.SpatialAttitudeTime ?? new SimpleTime() }));
        }
        try { _composed = _aoe.Compose(parts, pov, madeName).AssetId; report.Done("AddedObjects", N(_composed), N(_composed)); }
        catch (Exception e) { report.Failed("AddedObjects", madeName, e.Message); }
    }

    private void List(ObjectListing listing, CommandReport report)
    {
        if (!ListKinds.Known(listing.Kind)) { report.Failed("ListedObjects", null, $"the Kind is required: one of {string.Join(", ", ListKinds.Every)}"); return; }
        if (listing.Kind != ListKinds.All && ListKinds.ListedBy(listing.Kind) != "Audio Object Editing")
        { report.NotSupported("ListedObjects", null, $"the {listing.Kind}s are listed by {ListKinds.ListedBy(listing.Kind)}"); return; }
        report.Listed(_aoe.Listing(listing.Kind!));
    }

    private void Apply(UserCommand command, CommandReport report)
    {
        var data = command.UserCommandRQ;
        if (data is null) return;
        if (data.ListedObjects is { } listing) List(listing, report);

        const string inScene = "where an Object is in a Scene is Audio Scene Editing's";
        foreach (var (action, any) in new[] { ("MovedObjects", data.MovedObjects is { Objects.Count: > 0 }),
                                              ("ChangedObjects", data.ChangedObjects is { Objects.Count: > 0 }),
                                              ("RemovedObjects", data.RemovedObjects is { Objects.Count: > 0 }) })
            if (any) report.NotSupported(action, N(_open), inScene);
        if (data.TranslatedObjects is { Objects.Count: > 0 })
            report.NotSupported("TranslatedObjects", N(_open), "speech is translated by Speech Object Editing");

        if (data.AddedObjects is { Objects.Count: > 0 } composed) Compose(composed, data.UserPoV, report);

        // The Object the command modifies, where it gives one - itself, or its name - is the one open from here on.
        if (data.ModifiedObjects is { Objects.Count: > 0 } first && first.Objects[0].ObjectID is { } target)
        {
            var given = IdOf(target);
            var key = _aoe.KeyOf(given);
            if (key is not null && key.StartsWith("BAO", StringComparison.Ordinal)) _open = key;
            else { report.Failed("ModifiedObjects", given, key is null ? "the Module keeps no Basic Audio Object of that name" : "only a Basic Audio Object is modified here"); return; }
        }

        if (_open is null)
        {
            if (data.UserPoV is not null || data.ModifiedObjects is { Objects.Count: > 0 } || data.LUFS is not null)
                report.Failed("ModifiedObjects", null, "no Basic Audio Object is open");
            return;
        }

        if (data.UserPoV is not null)
        {
            _open = _aoe.EditBasicObjectProperties(_open, listenerPointOfView: data.UserPoV).AssetId;
            report.Done("UserPoV", N(_open), N(_open));
        }

        // What the Object is like: its loudness and its Acoustic Profile (Object part). The Object is the one open, or the one the entry gives (the Object itself, or its
        // name), which is then open. The amount is the command's: an entry that carries neither a loudness nor an Acoustic Profile asks for nothing that can be done.
        if (data.ModifiedObjects is { Objects.Count: > 0 } modified)
            foreach (var entry in modified.Objects)
            {
                if (entry.AcousticProfile is null && data.LUFS is null)
                { report.Failed("ModifiedObjects", N(_open), "nothing to modify: an Acoustic Profile, or a loudness (LUFS) as a number, is required"); continue; }
                _open = _aoe.EditBasicObjectProperties(_open, level: data.LUFS, acousticProfile: entry.AcousticProfile).AssetId;
                report.Done("ModifiedObjects", N(_open), N(_open));
            }
        else if (data.LUFS is { } lufs)
        {
            _open = _aoe.EditBasicObjectProperties(_open, level: lufs).AssetId;
            report.Done("LUFS", N(_open), N(_open));
        }
    }

    private static string? IdOf(ManagedObject? managed) =>
        managed is null ? null
        : !string.IsNullOrWhiteSpace(managed.ObjectID) ? managed.ObjectID
        : !string.IsNullOrWhiteSpace(managed.BasicAudioObject?.BasicAudioObjectID) ? managed.BasicAudioObject!.BasicAudioObjectID
        : !string.IsNullOrWhiteSpace(managed.AudioObject?.AudioObjectID) ? managed.AudioObject!.AudioObjectID
        : null;
}
