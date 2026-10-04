using System;
using System.Linq;
using System.Threading.Tasks;

using AIF.Controller;

using Mpai.Cae.Aoe;

namespace Mpai.Cae.Ase;

// CAE-ASE-V1.0 - Audio Scene Editing: every User Command of Audio Scene Management,
// on Objects and on Scenes (the author, 2026/10/04: "ASE should basically implement
// all User Commands ... ASE IS ASM"). What was Audio Object Editing (CAE-AOE) is its
// Object half; Audio Object Delivery (CAE-AOD) stays a separate AIM, and capture and
// playback are the User Agent's, through the Units of its Physical Layer.
//
//   AudioObject      a Basic Audio Object or an Audio Object to open; a new Basic
//                    Audio Object - a sound the User Agent captured - is created
//   SpeechObject     a Speech Object, kept
//   ObjectCommand    a User Command on the open Object: AddedObjects (compose),
//                    ChangedObjects (where it is), ModifiedObjects (what it is like),
//                    TranslatedObjects (a Speech Object to Text and Speech Translation)
//   SceneCommand     a User Command on the open Scene: AddedObjects (place),
//                    MovedObjects, ChangedObjects, UserPoV
//   AudioScene       a Basic Audio Scene or an Audio Scene to open
//   UserPoV          where the Scene is heard from
//   TranslatedSpeech the translation, which replaces the Speech Object it translates
//
// The two halves are joined here, as the Topology joined the two AIMs: the Object as
// Object Editing leaves it goes on to Scene Editing, which places it.
public sealed class AseAimProcessor : IAimProcessor
{
    private const string Edited = "#edited";   // the Object passed from one half to the other

    private readonly ObjectEditing _objects;
    private readonly SceneEditing  _scenes;

    public string InstanceId { get; }

    public AseAimProcessor(string instanceId, AoeAim aoe, AseAim ase, AimPortReader ports)
    {
        InstanceId = instanceId;
        var objectOut = ports.Output("OSD-AUO-V1.5");   // or OSD-BAO-V1.5: one Port
        _objects = new ObjectEditing(aoe,
            commandPort:     ports.Input("CAE-UCM-V1.0", 1),
            objectPort:      ports.Input("OSD-AUO-V1.5"),
            outputPort:      objectOut,
            speechPort:      ports.InputOrDefault("OSD-BSO-V1.5", 1, ""),
            toTranslatePort: ports.OutputOrDefault("OSD-BSO-V1.5", ""),
            languagePort:    ports.OutputOrDefault("OSD-SEL-V1.5", ""));
        _scenes = new SceneEditing(ase,
            commandPort:       ports.Input("CAE-UCM-V1.0", 2),
            objectPort:        Edited,
            scenePort:         ports.Input("OSD-ASD-V1.5"),
            povPort:           ports.Input("OSD-OPV-V1.5"),
            outputPort:        ports.Output("OSD-ASD-V1.5"),
            objectCommandPort: ports.Input("CAE-UCM-V1.0", 1),
            translatedPort:    ports.InputOrDefault("OSD-BSO-V1.5", 2, ""));
        _objectOut = objectOut;
    }

    private readonly string _objectOut;

    public async Task<Message> ProcessAsync(Message message)
    {
        // 1. the Object half, when this run brings it something.
        var objects = _objects.Concerned(message) ? await _objects.ProcessAsync(message) : null;

        // 2. the Scene half, given the Object as the Object half left it.
        var toScenes = new Message { MessageId = message.MessageId, Context = message.Context, Ports = new(message.Ports) };
        toScenes.Ports.Remove(Edited);
        if (objects?.Ports.TryGetValue(_objectOut, out var edited) == true) toScenes.Ports[Edited] = edited;
        var scenes = await _scenes.ProcessAsync(toScenes);

        // 3. both, on the Ports of this AIM.
        var ports = (objects?.Ports ?? new()).Concat(scenes.Ports).ToDictionary(p => p.Key, p => p.Value);
        var main = scenes.Ports.Count > 0 ? scenes : objects ?? scenes;
        return new Message
        {
            MessageId   = Guid.NewGuid().ToString(),
            MessageType = main.MessageType,
            DataType    = main.DataType,
            Ports       = ports
        };
    }
}
