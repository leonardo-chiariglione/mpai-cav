using System;
using System.Linq;
using System.Threading.Tasks;

using AIF.Controller;

using Mpai.Core;
using Mpai.Core.OSD;

using Mpai.Cae.Aoe;

namespace Mpai.Cae.Ase;

// OBJECT EDITING, the half of Audio Scene Editing that was Audio Object Editing
// (CAE-AOE) until the author's decision of 2026/10/04 - "ASE is ASM": one AIM
// executes every User Command, on Objects and on Scenes. AseAimProcessor gives it
// the Ports; what it does is unchanged.
//
// OPEN BY PORT, ACT BY COMMAND.
//
// This AIM edits ONE object at a time, so no Command has to name its target.
// What is open is decided by which Port delivered something:
//
//   BasicAudioObject   a Basic Audio Object — from CAE-AOA, or opened
//   AudioObject        an existing composed object, opened
//
// The Data Type answers a question that would otherwise need the content
// inspected. A Basic Audio Object arriving with no Command is a creation, and
// creation opens implicitly.
//
// Then a User Command acts on whatever is open. WHICH FIELD IS POPULATED IS THE
// OPERATION - there is no operation name to validate:
//
//   AddedObjects      compose the named objects into the open one
//   ChangedObjects    EXTERNAL attributes: where it is, how it is placed
//   ModifiedObjects   INTERNAL attributes: what it is like
//
// A Command carrying none of those three is not this AIM's; it is left alone
// rather than treated as an error, because one Command is broadcast to whichever
// AIM its Port leads to and the others simply have nothing to do.
//
// The engine underneath is AoeAim, unchanged. This class adds no editing
// behaviour: it turns Ports into calls and a result into a Port.
internal sealed class ObjectEditing
{
    private readonly AoeAim _aoe;

    private readonly string _commandPort;
    private readonly string _basicPort;
    private readonly string _objectPort;
    private readonly string _outputPort;

    // SPEECH TRANSLATION (the author, 2026/10/03). A Speech Object arriving is kept;
    // a command naming Translated Objects sends one Speech Object, read from Shared
    // Storage, to Text and Speech Translation, with a Language Selector for the
    // target language. "" when the L3 has no such Port.
    private readonly string _speechPort;       // OSD-BSO in
    private readonly string _toTranslatePort;  // OSD-BSO out
    private readonly string _languagePort;     // OSD-SEL out

    // What is open, between runs. The run is stateless; the AIM is not. That is
    // what lets an interactive session be a sequence of runs rather than one
    // long one, with the assets themselves in Shared Storage.
    private string? _openObjectId;
    private string? _openBasicId;

    public ObjectEditing(AoeAim aoe, string commandPort, string objectPort, string outputPort,
                         string speechPort, string toTranslatePort, string languagePort)
    {
        _aoe         = aoe;
        _commandPort = commandPort;
        _basicPort   = objectPort;    // one Port takes either kind; the Header says which
        _objectPort  = objectPort;
        _outputPort  = outputPort;

        _speechPort      = speechPort;
        _toTranslatePort = toTranslatePort;
        _languagePort    = languagePort;
    }

    // Whether this run brings anything for Object Editing.
    public bool Concerned(Message message) =>
        message.Ports.ContainsKey(_commandPort) || message.Ports.ContainsKey(_objectPort) ||
        (_speechPort.Length > 0 && message.Ports.ContainsKey(_speechPort));

    public Task<Message> ProcessAsync(Message message)
    {
        // 1. opening, by Port.
        // One Port takes either kind; the Header says which arrived.
        var header = message.Ports.TryGetValue(_objectPort, out var arrived) ? HeaderOf(arrived) : null;

        if (header == "OSD-AUO-V1.5" && message.Ports.TryGetValue(_objectPort, out var openJson))
        {
            var opened = MpaiJson.FromJson<AudioObject>(openJson);
            if (!string.IsNullOrWhiteSpace(opened.AudioObjectID))
            {
                _openObjectId = opened.AudioObjectID;
                Console.WriteLine($"[CAE-ASE-V1.0] opened {_openObjectId}");
            }
        }

        if (header == "OSD-BAO-V1.5" && message.Ports.TryGetValue(_basicPort, out var basicJson))
        {
            var basic = MpaiJson.FromJson<BasicAudioObject>(basicJson);

            // OPEN or CREATE, and the identifier decides. A Basic Audio Object
            // already in the repository is being opened for editing; one that is
            // not is new - a capture from CAE-AOA, say.
            //
            // The first version of this file treated EVERY arriving Basic Audio
            // Object as a creation, which meant an existing one could never be
            // edited, only replaced. That contradicted the whole point of this
            // AIM knowing whether it is editing a basic object or a composed one.
            if (_aoe.Has(basic.BasicAudioObjectID))
            {
                _openBasicId = basic.BasicAudioObjectID;
                _openObjectId = _openBasicId;   // a Basic Audio Object opened is what is open
                Console.WriteLine($"[CAE-ASE-V1.0] opened basic {_openBasicId}");
            }
            else
            {
                var asset     = _aoe.CreateObject(basic);
                _openObjectId = asset.AssetId;
                _openBasicId  = BasicOf(_openObjectId);
                Console.WriteLine($"[CAE-ASE-V1.0] created and opened {_openObjectId}");
            }
        }

        // A Speech Object arriving: kept, under its identifier.
        if (_speechPort.Length > 0 && message.Ports.TryGetValue(_speechPort, out var speechJson) && !string.IsNullOrWhiteSpace(speechJson))
            Console.WriteLine($"[CAE-ASE-V1.0] kept speech {_aoe.KeepSpeech(MpaiJson.FromJson<BasicSpeechObject>(speechJson))}");

        // 2. acting, by Command.
        var translation = new Dictionary<string, string>();
        if (message.Ports.TryGetValue(_commandPort, out var commandJson))
        {
            var command = MpaiJson.FromJson<UserCommand>(commandJson);
            Apply(command);
            Translate(command, translation);
        }

        Message With(Message m)
        {
            foreach (var (port, json) in translation) m.Ports[port] = json;
            return m;
        }

        // 3. the open object, as it now stands.
        if (_openObjectId is null)
            return Task.FromResult(With(Nothing(message)));

        // A Basic Audio Object open goes out as itself, not as an Audio Object of
        // one: an Object holding one Object is Basic.
        if (_openObjectId.StartsWith("BAO", StringComparison.Ordinal))
            return Task.FromResult(With(new Message
            {
                MessageId   = Guid.NewGuid().ToString(),
                MessageType = "BasicAudioObject",
                DataType    = "OSD-BAO-V1.5",
                Ports       = { [_outputPort] = MpaiJson.ToJson(_aoe.Get(_openObjectId)) }
            }));

        var materialised = _aoe.Materialize(_openObjectId);

        return Task.FromResult(With(new Message
        {
            MessageId   = Guid.NewGuid().ToString(),
            MessageType = "AudioObject",
            DataType    = "OSD-AUO-V1.5",
            Ports       = { [_outputPort] = MpaiJson.ToJson(materialised) }
        }));
    }

    private void Apply(UserCommand command)
    {
        var data = command.UserCommandData;
        if (data is null) return;
        if (data.AddedObjects is null && data.ChangedObjects is null && data.ModifiedObjects is null) return;   // not an edit (a translation, say)

        if (_openObjectId is null)
        {
            Console.WriteLine("[CAE-ASE-V1.0] a Command arrived with nothing open - ignored.");
            return;
        }

        // Compose: add each named object as a child of the open one.
        if (data.AddedObjects is { Objects.Count: > 0 } added)
        {
            foreach (var entry in added.Objects)
            {
                var childId = IdOf(entry.ObjectID);
                if (childId is null) continue;

                _openObjectId = _aoe.AddSubObject(_openObjectId, childId).AssetId;
                Console.WriteLine($"[CAE-ASE-V1.0] added {childId} -> {_openObjectId}");
            }
        }

        // EXTERNAL attributes.
        if (data.ChangedObjects is { Objects.Count: > 0 } changed)
        {
            foreach (var entry in changed.Objects)
            {
                // Where it is: the acoustics are the Basic Objects' (ModifiedObjects).
                _openObjectId = _aoe.EditObjectProperties(
                    _openObjectId,
                    placement: Placement(entry.SpatialAttitude)).AssetId;

                Console.WriteLine($"[CAE-ASE-V1.0] changed (external) -> {_openObjectId}");
            }
        }

        // INTERNAL attributes: what the object IS - frequency range, loudness,
        // spectrogram. They belong to the Basic Audio Object inside whatever is
        // OPEN, symmetrically with ChangedObjects above, which edits the open
        // object's external attributes.
        //
        // An earlier version took the identifier from the Command entry instead,
        // so a Command could modify an object other than the one open. That was
        // wrong twice over: it broke the symmetry, and it contradicted the rule
        // that no Command needs to name its target because one thing is open.
        if (data.ModifiedObjects is { Objects.Count: > 0 } modified)
        {
            var basicId = _openBasicId ?? BasicOf(_openObjectId);

            if (basicId is null)
            {
                Console.WriteLine("[CAE-ASE-V1.0] nothing open has a Basic Audio Object to modify.");
            }
            else
            {
                foreach (var entry in modified.Objects)
                {
                    var edited = _aoe.EditBasicObjectProperties(
                        basicId,
                        level:           data.LUFS,
                        acousticProfile: entry.AcousticProfile).AssetId;

                    // Every edit is a new version: a Basic Object open is now the
                    // new one. (An Audio Object holding it keeps the version it holds.)
                    if (_openObjectId == basicId) _openObjectId = edited;
                    _openBasicId = edited;
                    basicId = edited;

                    Console.WriteLine($"[CAE-ASE-V1.0] modified (internal) -> {edited}");
                }
            }
        }
    }

    // TRANSLATED OBJECTS: the Speech Object named, as Shared Storage holds it, and a
    // Language Selector from the language its Qualifier states to the one the
    // command's Speech Qualifier states. Text and Speech Translation translates one
    // Speech Object at a time: the first named is sent, any other is reported.
    private void Translate(UserCommand command, Dictionary<string, string> outputs)
    {
        if (command.UserCommandData?.TranslatedObjects is not { Objects.Count: > 0 } translated) return;
        if (_toTranslatePort.Length == 0 || _languagePort.Length == 0)
        {
            Console.WriteLine("[CAE-ASE-V1.0] a translation was asked, and this AIM has no Port to send it on.");
            return;
        }
        var entry = translated.Objects[0];
        var id = entry.ObjectID?.ObjectID ?? entry.ObjectID?.SpeechObject?.BasicSpeechObjectID;
        var speech = id is null ? null : _aoe.GetSpeech(id);
        if (speech is null || entry.TargetLanguage is not { Length: > 0 } target)
        {
            Console.WriteLine($"[CAE-ASE-V1.0] cannot translate {id ?? "an unnamed object"}: {(speech is null ? "no such Speech Object" : "no target language")}.");
            return;
        }
        var source = speech.SpeechQualifier?.Attributes?.Metadata?.Language?.LanguageCode;
        outputs[_toTranslatePort] = MpaiJson.ToJson(speech);
        outputs[_languagePort]    = MpaiJson.ToJson(BasicSelectorObject.Languages(source, target));
        Console.WriteLine($"[CAE-ASE-V1.0] translating {id} from {source ?? "its language"} into {target}");
        if (translated.Objects.Count > 1)
            Console.WriteLine($"[CAE-ASE-V1.0] {translated.Objects.Count - 1} more Speech Object(s) named: one at a time.");
    }

    // The Basic Audio Object inside a composed one. Materialize expands the
    // children, so the identifier is there to be read rather than tracked.
    private string? BasicOf(string? audioObjectId)
    {
        if (audioObjectId is null) return null;

        try
        {
            var expanded = _aoe.Materialize(audioObjectId);
            return expanded.BasicAudioObjects?.Count > 0
                ? expanded.BasicAudioObjects[0].BAObjectIDOrBAObject?.BasicAudioObjectID
                : null;
        }
        catch (Exception failure)
        {
            Console.WriteLine($"[CAE-ASE-V1.0] could not read the basic component: {failure.Message}");
            return null;
        }
    }

    // A SpatialAttitude from a Command becomes the T0 attitude of a SpaceTime,
    // which is what the engine takes. The schema and the engine were evidently
    // drawn from the same picture: SpaceTime holds exactly this.
    private static SpaceTime? Placement(SpatialAttitude? attitude) =>
        attitude is null ? null : new SpaceTime { SpatialAttitude1 = attitude };

    // ObjectOrID: usually the identifier alone, the content coming from Shared
    // Storage.
    private static string? IdOf(ManagedObject? managed) =>
        managed is null ? null
        : !string.IsNullOrWhiteSpace(managed.ObjectID)               ? managed.ObjectID
        : !string.IsNullOrWhiteSpace(managed.AudioObject?.AudioObjectID) ? managed.AudioObject!.AudioObjectID
        : null;

    private static string? HeaderOf(string json)
    {
        try { return System.Text.Json.Nodes.JsonNode.Parse(json)?["Header"]?.GetValue<string>(); }
        catch { return null; }
    }

    private Message Nothing(Message message) => new()
    {
        MessageId   = Guid.NewGuid().ToString(),
        MessageType = "NoOpenObject",
        Ports       = { }
    };
}