using System.Text.Json;
using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.SharedStorage;
using Mpai.Cae.Asm;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.SpatialAudio;

namespace Mpai.Aif.Tests;

// THE AUDIO SCENE MANAGEMENT AIMs OVER ONE SHARED STORAGE, and a Controller's routing between them (CAE-ASM, the author, 2026/10/27): a User Command is given to every editing AIM,
// as the Voice Command Interface gives it; what the editing AIMs give is delivered to the AIM that takes it (the Audio Object to Audio Scene Editing, the Speech Object to Speech Scene
// Editing, the Scenes to Multimodal Scene Editing), until nothing more is given. The response of each is kept, in the order of the AIMs. Used by the tests of the editing of an Object
// and of the Module as a whole.
internal sealed class AsmRig : IDisposable
{
    public const string Ucm = "CAE-UCM-V1.0", Opv = "OSD-OPV-V1.5", Auo = "OSD-AUO-V1.5", Asd = "OSD-ASD-V1.5", Spo = "OSD-SPO-V1.5", Ssd = "OSD-SSD-V1.5", Msd = "OSD-MSD-V1.5";

    public readonly string Root = Path.Combine(Path.GetTempPath(), "asm-rig-" + Guid.NewGuid().ToString("N"));
    public readonly FileSharedStorage Storage;
    public readonly AimPortReader AoePorts, AsePorts, SoePorts, SsePorts, MsePorts;
    public readonly IAimProcessor Aoe, Ase, Soe, Sse, Mse;
    public readonly List<string> Said = new();           // everything the AIMs gave, to be searched
    public readonly List<string> Delivered = new();      // every User Command request given to the editing AIMs

    private sealed record Editor(IAimProcessor Aim, string Command, string Response, string? DataOut, string? DataIn, int To);
    private readonly Editor[] _editors;

    public AsmRig(string? schemasRoot = null)
    {
        Storage = new FileSharedStorage(Root, "CAE-ASM", "ASM-Rig");
        var store = new AIF.Store.AmdStore(Repository.Amds); store.Scan();
        AoePorts = AimPortReader.Load(store, "1CAE-AOE-V1.0-I01"); AsePorts = AimPortReader.Load(store, "1CAE-ASE-V1.0-I01");
        SoePorts = AimPortReader.Load(store, "1MMC-SOE-V2.5-I01"); SsePorts = AimPortReader.Load(store, "1MMC-SSE-V2.5-I01");
        MsePorts = AimPortReader.Load(store, "1OSD-MSE-V1.5-I01");

        // The editing AIMs as the provider of the Module makes them, over the one Shared Storage.
        var provider = new AsmProvider(Repository.Amds);
        var settings = new Dictionary<string, string> { ["SchemasRoot"] = schemasRoot ?? Repository.Schemas };
        Aoe = provider.Create(AsmProvider.Aoe, settings, Storage); Ase = provider.Create(AsmProvider.Ase, settings, Storage);
        Soe = provider.Create(AsmProvider.Soe, settings, Storage); Sse = provider.Create(AsmProvider.Sse, settings, Storage);
        Mse = provider.Create(AsmProvider.Mse, settings, Storage);

        // Where the data an editing AIM gives goes: to the AIM that takes it (its index, 0 to 4).
        _editors =
        [
            new(Aoe, AoePorts.Input(Ucm), AoePorts.Output(Ucm), AoePorts.Output(Auo), AsePorts.Input(Auo), 1),
            new(Ase, AsePorts.Input(Ucm), AsePorts.Output(Ucm), AsePorts.Output(Asd), MsePorts.Input(Asd, 1), 4),
            new(Soe, SoePorts.Input(Ucm), SoePorts.Output(Ucm), SoePorts.Output(Spo, 1), SsePorts.Input(Spo), 3),
            new(Sse, SsePorts.Input(Ucm), SsePorts.Output(Ucm), SsePorts.Output(Ssd), MsePorts.Input(Ssd, 2), 4),
            new(Mse, MsePorts.Input(Ucm), MsePorts.Output(Ucm), null, null, 0)
        ];
    }

    public async Task<Message> Give(IAimProcessor aim, params (string Port, string Json)[] inputs)
    {
        var o = await aim.ProcessAsync(new Message { MessageId = Guid.NewGuid().ToString(), Ports = inputs.ToDictionary(i => i.Port, i => i.Json) });
        foreach (var p in o.Ports.Values) if (p.Length > 0) Said.Add(p);
        return o;
    }

    // WHAT THE EDITING AIMS ANSWER TO ONE USER COMMAND, as the Voice Command Interface issues it: given to each of the five, in the order of the Module; what they give is delivered on,
    // until nothing more is given. The response of each AIM, in that order; the Objects and the Scenes that left each AIM are in Said.
    public async Task<Answers> Ask(UserCommand command) => await Ask(Json(command));

    public async Task<Answers> Ask(string commandJson)
    {
        Delivered.Add(commandJson);
        var responses = new string?[5];
        var waiting = new Queue<(int Editor, string Port, string Json)>();
        for (var k = 0; k < 5; k++) waiting.Enqueue((k, _editors[k].Command, commandJson));
        for (var round = 0; round < 60 && waiting.Count > 0; round++)
        {
            var (k, port, json) = waiting.Dequeue();
            var editor = _editors[k];
            var answered = await Give(editor.Aim, (port, json));
            if (answered.Ports.TryGetValue(editor.Response, out var response)) responses[k] = response;
            if (editor.DataOut is not null && answered.Ports.TryGetValue(editor.DataOut, out var data) && editor.DataIn is { } to)
                waiting.Enqueue((editor.To, to, data));
        }
        return new Answers(responses);
    }

    public sealed class Answers(string?[] responses)
    {
        // The response of each editing AIM (Audio Object, Audio Scene, Speech Object, Speech Scene, Multimodal Scene Editing), as it gave it.
        public IReadOnlyList<string?> Responses { get; } = responses;
        public UserCommand? Of(int editor) => Responses[editor] is { } json ? MpaiJson.FromJson<UserCommand>(json) : null;

        // The one response the answers make, as the JSON Interface makes it.
        public UserCommand Merged(string id) => UserCommandMerge.Merge(id, Responses.Where(r => r is not null).Select(r => MpaiJson.FromJson<UserCommand>(r!)).ToList());
    }

    // A sound brought in by a User Command (AcquiredObject), to Audio Object Editing.
    public Task<Message> OpenAudio(BasicAudioObject sound) =>
        Give(Aoe, (AoePorts.Input(Ucm), Json(Command(new UserCommandRQ { AcquiredObject = new ManagedObject { BasicAudioObject = sound } }))));

    // A speech brought in by a User Command (AcquiredObject), to Speech Object Editing.
    public Task<Message> OpenSpeech(BasicSpeechObject speech) =>
        Give(Soe, (SoePorts.Input(Ucm), Json(Command(new UserCommandRQ { AcquiredObject = new ManagedObject { SpeechObject = speech } }))));

    // ---- the data -------------------------------------------------------------------------------------------------------------------

    public static UserCommand Command(UserCommandRQ data) => new() { UserCommandID = Guid.NewGuid().ToString("N")[..8], UserCommandTime = SimpleTime.At(DateTimeOffset.UtcNow), UserCommandRQ = data };
    public static string Json(UserCommand c) => MpaiJson.ToJson(c);
    public static ManagedObject Ref(string name) => new() { ObjectID = name };
    public static SpatialAttitude At(double x, double y = 2) => new()
    {
        ObjectSpatialAttitudeID = Guid.NewGuid().ToString(),
        Position = new Position { PositionID = Guid.NewGuid().ToString(), CartPosition = [x, y, 0] },
        Orientation = new Orientation { OrientationID = Guid.NewGuid().ToString(), EulerAngles = [0, 0, 180] }
    };
    public static ObjectPlacement Put(string name, double x, double y = 2) => new() { ObjectID = Ref(name), SpatialAttitude = At(x, y) };

    // A sound of the seconds given: a WAV of silence at 8 kHz, so that its Duration is known.
    public static BasicAudioObject Sound(string name, double seconds = 1, byte seed = 1) => new()
    {
        BasicAudioObjectID = name, DescrMetadata = name,
        BasicAudioObjectData = [new InlineAudioData(Convert.ToBase64String(SceneAudio.Wav(Enumerable.Range(0, (int)(8000 * seconds)).Select(i => seed * 0.001f * (i % 7)).ToArray(), 1, 8000)))]
    };
    public static BasicSpeechObject Speech(string name, double seconds = 0.5) =>
        new() { BasicSpeechObjectID = name, Data = SceneAudio.Wav(Enumerable.Range(0, (int)(8000 * seconds)).Select(i => 0.002f * (i % 5)).ToArray(), 1, 8000) };

    public static UserCommandRS Response(string json) => MpaiJson.FromJson<UserCommand>(json).UserCommandRS!;

    public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
}
