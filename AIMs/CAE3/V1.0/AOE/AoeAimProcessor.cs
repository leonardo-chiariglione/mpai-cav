using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Cae.Aoe;

// CAE-AOE-V1.0 - Audio Object Editing, as the author's Reference Model of Audio Scene
// Management draws it (CAE Reference Models, 2026/10/04): User Command, User PoV and
// a Basic Audio Object in; the Basic Audio Object as edited, and the User Command
// with its report, out. The Object goes on to Audio Scene Editing.
//
// OPEN BY PORT, ACT BY COMMAND. One Object is open at a time, so no Command has to
// name its target. A Basic Audio Object arriving is opened when Shared Storage holds
// it (by its identifier), and created when it is new - a sound the User Agent
// captured. Then a User Command acts on what is open:
//
//   ModifiedObjects   what it is like: loudness (LUFS) and the Object part of the
//                     Acoustic Profile
//   UserPoV           where it is heard from, when heard on its own
//   AddedObjects      the named Objects, each with its place and its time, make a new
//                     Audio Object (AUO), which is kept in Shared Storage; the report
//                     names it (ResultID). The Module gives a User Agent an Audio
//                     Object that way: the User Agent reads it in Shared Storage.
//
// Every edit is a new version in Shared Storage. Where an Object is in a Scene is
// Audio Scene Editing's (AddedObjects, MovedObjects, ChangedObjects): a command
// naming those here is reported as not supported, not silently dropped.
public sealed class AoeAimProcessor : IAimProcessor
{
    private readonly AoeAim _aoe;

    private readonly string _commandPort;
    private readonly string _povPort;
    private readonly string _objectPort;
    private readonly string _objectOut;
    private readonly string _reportOut;
    private readonly string _auoIn;       // an Audio Object the User Agent read from its external storage
    private readonly string _auoOut;      // the Audio Object this made (composed, or stored from outside)

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
        _objectPort  = ports.Input("OSD-BAO-V1.5");
        _objectOut   = ports.Output("OSD-BAO-V1.5");
        _reportOut   = ports.OutputOrDefault("CAE-UCM-V1.0", "");
        _auoIn       = ports.InputOrDefault("OSD-AUO-V1.5", "");
        _auoOut      = ports.OutputOrDefault("OSD-AUO-V1.5", "");
    }

    public Task<Message> ProcessAsync(Message message)
    {
        var outputs = new Dictionary<string, string>();

        // 1. opening, or creating, by Port.
        if (message.Ports.TryGetValue(_objectPort, out var objectJson) && !string.IsNullOrWhiteSpace(objectJson))
        {
            var basic = MpaiJson.FromJson<BasicAudioObject>(objectJson);
            _open = !string.IsNullOrWhiteSpace(basic.BasicAudioObjectID) && _aoe.Has(basic.BasicAudioObjectID) ? basic.BasicAudioObjectID : _aoe.CreateObject(basic).AssetId;      // an Object with no identifier, or one the storage does not hold, is a new one
            Console.WriteLine($"[CAE-AOE-V1.0] open {_open}");
        }

        // 1b. an Audio Object read from outside is stored here, and given back as stored.
        string? made = null;
        if (_auoIn.Length > 0 && message.Ports.TryGetValue(_auoIn, out var auoJson) && !string.IsNullOrWhiteSpace(auoJson))
        {
            made = _aoe.StoreAudioObject(MpaiJson.FromJson<AudioObject>(auoJson)).AssetId;
            Console.WriteLine($"[CAE-AOE-V1.0] audio object {made}");
        }

        // 2. a User PoV on its Port: where the open Object is heard from.
        if (_povPort.Length > 0 && _open is not null && message.Ports.TryGetValue(_povPort, out var povJson) && !string.IsNullOrWhiteSpace(povJson))
            _open = _aoe.EditBasicObjectProperties(_open, listenerPointOfView: MpaiJson.FromJson<PointOfView>(povJson)).AssetId;

        // 3. acting, by Command; the report goes back under the command's identifier.
        if (message.Ports.TryGetValue(_commandPort, out var commandJson) && !string.IsNullOrWhiteSpace(commandJson))
        {
            var command = MpaiJson.FromJson<UserCommand>(commandJson);
            var report = new CommandReport(command);
            if (command.UserCommandData?.RevokedUserCommandID is { } revoked) _undo.Revoke(revoked, report);
            else
            {
                var before = _open;
                Apply(command, report);
                _undo.RecordVersions(command.UserCommandID, before, _open, () => _open, v => _open = v);
            }
            if (_reportOut.Length > 0) outputs[_reportOut] = MpaiJson.ToJson(report.Report());
        }

        // 3b. an Audio Object made, as it is kept in Shared Storage.
        if (made is null) made = _composed;
        _composed = null;
        if (made is not null && _auoOut.Length > 0) outputs[_auoOut] = MpaiJson.ToJson(_aoe.Materialize(made));         // whole: the Objects it is made of are in it

        // 4. the open Object, as it now stands.
        if (_open is not null) outputs[_objectOut] = MpaiJson.ToJson(_aoe.Get(_open));
        return Task.FromResult(new Message
        {
            MessageId   = Guid.NewGuid().ToString(),
            MessageType = _open is null ? "NoOpenObject" : "BasicAudioObject",
            DataType    = _open is null ? "" : "OSD-BAO-V1.5",
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
        var data = command.UserCommandData;
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
