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
// OPEN BY PORT, ACT BY COMMAND. One Basic Object is open at a time, so no Command has to
// name its target. An Audio Object arriving that holds one Basic Audio Object opens
// that Object - from Shared Storage, by its identifier - or creates it when it is new
// (a sound the User Agent captured). One that holds more is stored as an Audio Object,
// from the Basic Objects it holds (each a new Object here, its identifier available to
// a command), and is the Audio Object given back as it was stored. Then a User Command
// acts on what is open:
//
//   ModifiedObjects   what it is like: loudness (LUFS) and the Object part of the
//                     Acoustic Profile
//   UserPoV           where it is heard from, when heard on its own
//   AddedObjects      the named Objects, each with its place and its time, make a new
//                     Audio Object (AUO), which is kept in Shared Storage; the report
//                     names it (ResultID), and it is the Audio Object the AIM gives.
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

    // What is open, between runs.
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

    public Task<Message> ProcessAsync(Message message)
    {
        var outputs = new Dictionary<string, string>();

        // 1. opening, creating or storing, by Port.
        string? made = null;
        if (message.Ports.TryGetValue(_objectPort, out var objectJson) && !string.IsNullOrWhiteSpace(objectJson))
        {
            var auo = MpaiJson.FromJson<AudioObject>(objectJson);
            if (auo.BasicAudioObjects is { Count: 1 } && auo.SubAudioObjects is not { Count: > 0 } && auo.BasicAudioObjects[0].BAObjectIDOrBAObject is { } basic)
            {
                // One Basic Audio Object: that Object is opened - an Object with no identifier, or one the storage does not hold, is a new one.
                _open = !string.IsNullOrWhiteSpace(basic.BasicAudioObjectID) && _aoe.Has(basic.BasicAudioObjectID) ? basic.BasicAudioObjectID : _aoe.CreateObject(basic).AssetId;
                Console.WriteLine($"[CAE-AOE-V1.0] open {_open}");
            }
            else
            {
                // Several: an Audio Object the storage holds is that one; else it is stored here, and given back as stored.
                made = !string.IsNullOrWhiteSpace(auo.AudioObjectID) && auo.AudioObjectID.StartsWith("AUO", StringComparison.Ordinal) && _aoe.Has(auo.AudioObjectID)
                    ? auo.AudioObjectID
                    : _aoe.StoreAudioObject(auo).AssetId;
                Console.WriteLine($"[CAE-AOE-V1.0] audio object {made}");
            }
        }

        // 2. a User PoV on its Port: where the open Object is heard from.
        if (_povPort.Length > 0 && _open is not null && message.Ports.TryGetValue(_povPort, out var povJson) && !string.IsNullOrWhiteSpace(povJson))
            _open = _aoe.EditBasicObjectProperties(_open, listenerPointOfView: MpaiJson.FromJson<PointOfView>(povJson)).AssetId;

        // 3. acting, by Command; the report goes back under the command's identifier.
        if (message.Ports.TryGetValue(_commandPort, out var commandJson) && !string.IsNullOrWhiteSpace(commandJson))
        {
            var command = MpaiJson.FromJson<UserCommand>(commandJson);
            var report = new CommandReport(command);
            if (command.UserCommandRQ?.RevokedUserCommandID is { } revoked) _undo.Revoke(revoked, report);
            else
            {
                var before = _open;
                Apply(command, report);
                _undo.RecordVersions(command.UserCommandID, before, _open, () => _open, v => _open = v);
            }
            if (_reportOut.Length > 0) outputs[_reportOut] = MpaiJson.ToJson(report.Report());
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

    // The Objects, each with its place and its time, as ONE new Audio Object.
    private void Compose(ObjectPlacements composed, PointOfView? pov, CommandReport report)
    {
        var parts = new List<(string, SpaceTime?)>();
        foreach (var entry in composed.Objects)
        {
            var id = !string.IsNullOrWhiteSpace(entry.ObjectID?.ObjectID) ? entry.ObjectID!.ObjectID : entry.ObjectID?.BasicAudioObject?.BasicAudioObjectID;
            if (string.IsNullOrWhiteSpace(id) || !_aoe.Has(id)) { report.Failed("AddedObjects", id, "an Audio Object is made of Objects that Shared Storage holds, named by their identifiers"); return; }
            parts.Add((id, entry.SpatialAttitude is null ? null : new SpaceTime { SpatialAttitude1 = entry.SpatialAttitude, Time = entry.SpatialAttitude.SpatialAttitudeTime ?? new SimpleTime() }));
        }
        try { _composed = _aoe.Compose(parts, pov).AssetId; report.Done("AddedObjects", null, _composed); }
        catch (Exception e) { report.Failed("AddedObjects", null, e.Message); }
    }

    private void Apply(UserCommand command, CommandReport report)
    {
        var data = command.UserCommandRQ;
        if (data is null) return;
        const string inScene = "where an Object is in a Scene is Audio Scene Editing's";
        foreach (var (action, any) in new[] { ("MovedObjects", data.MovedObjects is { Objects.Count: > 0 }),
                                              ("ChangedObjects", data.ChangedObjects is { Objects.Count: > 0 }),
                                              ("RemovedObjects", data.RemovedObjects is { Objects.Count: > 0 }) })
            if (any) report.NotSupported(action, _open, inScene);
        if (data.TranslatedObjects is { Objects.Count: > 0 })
            report.NotSupported("TranslatedObjects", _open, "speech is translated by Speech Object Editing");

        if (data.AddedObjects is { Objects.Count: > 0 } composed) Compose(composed, data.UserPoV, report);

        // The Object the command modifies, where it gives one - itself, or its identifier - is the one open from here on.
        if (data.ModifiedObjects is { Objects.Count: > 0 } first && first.Objects[0].ObjectID is { } target)
        {
            var given = !string.IsNullOrWhiteSpace(target.ObjectID) ? target.ObjectID : target.BasicAudioObject?.BasicAudioObjectID;
            if (!string.IsNullOrWhiteSpace(given) && _aoe.Has(given)) _open = given;
        }

        if (_open is null)
        {
            if (data.UserPoV is not null || data.ModifiedObjects is { Objects.Count: > 0 } || data.LUFS is not null)
                report.Failed("ModifiedObjects", null, "no Basic Audio Object is open");
            return;
        }

        if (data.UserPoV is not null)
        {
            var before = _open;
            _open = _aoe.EditBasicObjectProperties(_open, listenerPointOfView: data.UserPoV).AssetId;
            report.Done("UserPoV", before, _open);
        }

        // What the Object is like: its loudness and its Acoustic Profile (Object part). The Object is the one open, or the one the entry gives (the Object itself, or its
        // identifier), which is then open.
        if (data.ModifiedObjects is { Objects.Count: > 0 } modified)
            foreach (var entry in modified.Objects)
            {
                var before = _open;
                _open = _aoe.EditBasicObjectProperties(_open, level: data.LUFS, acousticProfile: entry.AcousticProfile).AssetId;
                report.Done("ModifiedObjects", before, _open);
            }
        else if (data.LUFS is { } lufs)
        {
            var before = _open;
            _open = _aoe.EditBasicObjectProperties(_open, level: lufs).AssetId;
            report.Done("LUFS", before, _open);
        }
    }
}
