using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Cae.Ase;

// SCENE EDITING, the half of Audio Scene Editing that composes Scenes.
// AseAimProcessor gives it the Ports.
//
// OPEN BY PORT, ACT BY COMMAND, as Object Editing does. One scene is open at a time,
// which is why no Command names it.
//
//   AudioScene    an existing scene, opened
//   AudioObject   an Audio Object as Object Editing leaves it
//
// The Objects ARRIVE from Object Editing, and this half remembers what it has been
// given, rather than expanding each child Object itself.
//
// Four of the seven Command fields belong here:
//
//   AddedObjects     place the named objects in the open scene
//   RemovedObjects   take them out
//   MovedObjects     old attitude -> new attitude
//   ChangedObjects   external attributes of a placed object
//
// with UserPoV setting the listener's Point of View.
//
// BASIC OR FULL, BY HEADER (2026/10/02). The Object Port takes a Basic Audio
// Object or an Audio Object, the Scene Port a Basic Audio Scene or an Audio Scene;
// each arrival says which it is by its Header. A Basic Audio Scene is made of
// Basic Audio Objects, an Audio Scene of Audio Objects: the first Object added to
// no open scene decides which is created. Sub-scenes, and Basic Objects in a full
// scene, are not composed yet.
internal sealed class SceneEditing
{
    private readonly AseAim _ase;

    private readonly string _commandPort;
    private readonly string _objectPort;
    private readonly string _scenePort;
    private readonly string _povPort;
    private readonly string _outputPort;

    // SPEECH TRANSLATION (the author, 2026/10/03): the translation from Text and
    // Speech Translation, and the command that asked for it - which names the
    // Speech Object it replaces and the Qualifier of the translation. "" when the
    // L3 has no such Port.
    private readonly string _objectCommandPort;   // CAE-UCM #2
    private readonly string _translatedPort;      // OSD-BSO

    // The translation asked for and not yet arrived: in continuous execution the
    // command and the translation come in different runs.
    private ObjectTranslation? _pending;

    // What is open, between runs: its identifier says Basic (BAS) or full (ASD).
    private string? _openSceneId;
    private bool OpenIsBasic => _openSceneId?.StartsWith("BAS", StringComparison.Ordinal) == true;

    // What Object Editing has sent, so that materialising a scene needs no call to
    // another AIM. An object placed in a scene has necessarily passed through
    // this Port to get there.
    private readonly Dictionary<string, AudioObject> _received = new();

    public SceneEditing(AseAim ase, string commandPort, string objectPort, string scenePort, string povPort,
                        string outputPort, string objectCommandPort, string translatedPort)
    {
        _ase         = ase;
        _commandPort = commandPort;
        _objectPort  = objectPort;    // the Object as Object Editing leaves it
        _scenePort   = scenePort;     // a Basic Audio Scene or an Audio Scene: one Port
        _povPort     = povPort;
        _outputPort  = outputPort;    // a Basic Audio Scene or an Audio Scene: one Port

        _objectCommandPort = objectCommandPort;
        _translatedPort    = translatedPort;
    }

    public Task<Message> ProcessAsync(Message message)
    {
        // 1. an Audio Object from Object Editing - remembered, not fetched.
        if (message.Ports.TryGetValue(_objectPort, out var objectJson) && HeaderOf(objectJson) == "OSD-AUO-V1.5")
        {
            var received = MpaiJson.FromJson<AudioObject>(objectJson);
            if (!string.IsNullOrWhiteSpace(received.AudioObjectID))
            {
                _received[received.AudioObjectID] = received;
                Console.WriteLine($"[CAE-ASE-V1.0] received {received.AudioObjectID} from the Topology");
            }
        }

        // 2. opening, by Port.
        if (message.Ports.TryGetValue(_scenePort, out var sceneJson))
        {
            var id = HeaderOf(sceneJson) == "OSD-BAS-V1.5"
                ? MpaiJson.FromJson<BasicAudioSceneDescriptors>(sceneJson).BasicAudioSceneDescriptorsID
                : MpaiJson.FromJson<AudioSceneDescriptors>(sceneJson).AudioSceneDescriptorsID;
            if (!string.IsNullOrWhiteSpace(id))
            {
                _openSceneId = id;
                Console.WriteLine($"[CAE-ASE-V1.0] opened {_openSceneId}");
            }
        }

        // A translation asked for: kept until the translation arrives.
        if (_objectCommandPort.Length > 0 && message.Ports.TryGetValue(_objectCommandPort, out var objectCommandJson) &&
            MpaiJson.FromJson<UserCommand>(objectCommandJson).UserCommandData?.TranslatedObjects?.Objects is { Count: > 0 } asked)
            _pending = asked[0];

        // A translation: it replaces the Speech Object the command named.
        if (_translatedPort.Length > 0 && _pending is { } entry &&
            message.Ports.TryGetValue(_translatedPort, out var translatedJson))
        {
            _pending = null;
            var id = entry.ObjectID?.ObjectID ?? entry.ObjectID?.SpeechObject?.BasicSpeechObjectID;
            var replaced = id is not null && _ase.ReplaceSpeech(id, MpaiJson.FromJson<BasicSpeechObject>(translatedJson), entry.SpeechQualifier);
            Console.WriteLine(replaced ? $"[CAE-ASE-V1.0] {id} replaced by its translation into {entry.TargetLanguage}"
                                       : $"[CAE-ASE-V1.0] no Speech Object {id} to replace");
        }

        PointOfView? pov = null;
        if (message.Ports.TryGetValue(_povPort, out var povJson))
            pov = MpaiJson.FromJson<PointOfView>(povJson);

        // 3. acting, by Command.
        if (message.Ports.TryGetValue(_commandPort, out var commandJson))
            Apply(MpaiJson.FromJson<UserCommand>(commandJson), pov);
        else if (pov is not null && _openSceneId is not null)
            _openSceneId = SetListener(_openSceneId, pov);

        if (_openSceneId is null)
            return Task.FromResult(new Message
            {
                MessageId   = Guid.NewGuid().ToString(),
                MessageType = "NoOpenScene"
            });

        // 4. the open scene, as it now stands. Resolution comes from what this
        // AIM has been given.
        var json = OpenIsBasic
            ? MpaiJson.ToJson(_ase.MaterializeBasicScene(_openSceneId))
            : MpaiJson.ToJson(_ase.Materialize(_openSceneId, Resolve));

        return Task.FromResult(new Message
        {
            MessageId   = Guid.NewGuid().ToString(),
            MessageType = OpenIsBasic ? "BasicAudioSceneDescriptors" : "AudioSceneDescriptors",
            DataType    = OpenIsBasic ? "OSD-BAS-V1.5" : "OSD-ASD-V1.5",
            Ports       = { [_outputPort] = json }
        });
    }

    // An object this AIM has not been sent stays an identifier. That is honest -
    // AudioSceneObjectEntry carries an ObjectOrID - and it is better than
    // reaching for another AIM to fill the gap.
    private AudioObject? Resolve(string audioObjectId) =>
        _received.TryGetValue(audioObjectId, out var found) ? found : null;

    private void Apply(UserCommand command, PointOfView? pov)
    {
        var data = command.UserCommandData;
        if (data is null) return;

        var listener = data.UserPoV ?? pov;

        if (data.AddedObjects is { Objects.Count: > 0 } added)
        {
            foreach (var entry in added.Objects)
            {
                var objectId = IdOf(entry.ObjectID);
                if (objectId is null) continue;

                var placement = Placement(entry.SpatialAttitude);

                // The listener is NOT passed here. An entity reused in another
                // context keeps its own attributes unless the context provides
                // them, and the containing context is the SCENE: stamping the
                // scene's Point of View onto every entry would flatten exactly
                // the override the rule describes. It is set once, below.
                var basic = objectId.StartsWith("BAO", StringComparison.Ordinal);
                if (_openSceneId is not null && basic != OpenIsBasic)
                {
                    Console.WriteLine($"[CAE-ASE-V1.0] {objectId} not placed: a {(OpenIsBasic ? "Basic Audio Scene holds Basic" : "full Audio Scene holds full")} Audio Objects only, for now.");
                    continue;
                }
                _openSceneId = (_openSceneId, basic) switch
                {
                    (null, true)  => _ase.CreateBasicScene(objectId, listener ?? Origin(), placement).AssetId,
                    (null, false) => _ase.CreateScene(objectId, placement).AssetId,
                    (_, true)     => _ase.AddObjectToBasicScene(_openSceneId!, objectId, placement).AssetId,
                    (_, false)    => _ase.AddObjectToScene(_openSceneId!, objectId, placement).AssetId
                };

                Console.WriteLine($"[CAE-ASE-V1.0] placed {objectId} -> {_openSceneId}");
            }
        }

        if (_openSceneId is null)
        {
            Console.WriteLine("[CAE-ASE-V1.0] a Command arrived with no scene open - ignored.");
            return;
        }

        if (data.MovedObjects is { Objects.Count: > 0 } moved)
        {
            foreach (var entry in moved.Objects)
            {
                var objectId = IdOf(entry.ObjectID);
                if (objectId is null) continue;

                // A move is expressed as a re-placement at the new attitude. The
                // old one is carried by the Command for continuity of rendering;
                // the engine does not take it.
                _openSceneId = Place(_openSceneId, objectId, Placement(entry.NewSpatialAttitude));

                Console.WriteLine($"[CAE-ASE-V1.0] moved {objectId}");
            }
        }

        if (data.ChangedObjects is { Objects.Count: > 0 } changed)
        {
            foreach (var entry in changed.Objects)
            {
                var objectId = IdOf(entry.ObjectID);
                if (objectId is null) continue;

                _openSceneId = Place(_openSceneId, objectId, Placement(entry.SpatialAttitude));

                Console.WriteLine($"[CAE-ASE-V1.0] changed (external) {objectId}");
            }
        }

        if (data.RemovedObjects is { Objects.Count: > 0 })
        {
            // AseAim has no removal today. Saying so is better than silently
            // doing nothing, or than inventing a removal whose semantics for
            // scene identity nobody has decided.
            Console.WriteLine("[CAE-ASE-V1.0] RemovedObjects: not implemented by AseAim.");
        }

        // Once, on the scene - whatever else the Command did. The scene's Point
        // of View overrides each entry's; the entries keep their own when the
        // scene has none.
        if (listener is not null)
            _openSceneId = SetListener(_openSceneId, listener);
    }

    private string Place(string sceneId, string objectId, SpaceTime? placement) =>
        sceneId.StartsWith("BAS", StringComparison.Ordinal)
            ? _ase.AddObjectToBasicScene(sceneId, objectId, placement).AssetId
            : _ase.AddObjectToScene(sceneId, objectId, placement).AssetId;

    private string SetListener(string sceneId, PointOfView userPoV) =>
        sceneId.StartsWith("BAS", StringComparison.Ordinal)
            ? _ase.SetBasicSceneListener(sceneId, userPoV).AssetId
            : _ase.SetSceneListener(sceneId, userPoV).AssetId;

    // A Basic Audio Scene is heard from somewhere; until told, from the origin.
    private static PointOfView Origin() => new() { PointOfViewID = Guid.NewGuid().ToString(), CartPosition = [0, 0, 0], Orientation = [0, 0, 0] };

    private static string? HeaderOf(string json)
    {
        try { return System.Text.Json.Nodes.JsonNode.Parse(json)?["Header"]?.GetValue<string>(); }
        catch { return null; }
    }

    private static SpaceTime? Placement(SpatialAttitude? attitude) =>
        attitude is null ? null : new SpaceTime { SpatialAttitude1 = attitude };

    private static string? IdOf(ManagedObject? managed) =>
        managed is null ? null
        : !string.IsNullOrWhiteSpace(managed.ObjectID) ? managed.ObjectID
        : !string.IsNullOrWhiteSpace(managed.AudioObject?.AudioObjectID) ? managed.AudioObject!.AudioObjectID
        : null;
}