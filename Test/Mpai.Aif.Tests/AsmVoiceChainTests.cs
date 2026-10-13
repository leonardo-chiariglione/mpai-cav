using System.Text.Json.Nodes;

using Mpai.Core;
using Mpai.Mmc.Edp;

namespace Mpai.Aif.Tests;

// A VOICE COMMAND, END TO END (the author, 2026/10/11: put a Whisper that feeds EDP). The person's voice is a Speech Object; Speech Recognition (Whisper, MMC-ASR) makes it
// text; Entity Dialogue Processing (MMC-EDP), given the Scene being edited, makes the User Command; the ASM AIM that executes it - Audio Scene Editing - does what it says.
// The AIMs do not talk to one another: the User Agent, here the test, gives each what the one before gave out, as it does through the Controller. The voice is made by
// Text-To-Speech, so that the test needs no microphone.
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
public class AsmVoiceChainTests
{
    private const string Model = "llama3.2:3b";

    private static bool Ollama()
    {
        try { using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(3) }; return h.GetAsync("http://127.0.0.1:11434/api/tags").Result.IsSuccessStatusCode; }
        catch { return false; }
    }

    private static Dictionary<string, string> Settings(string aim)
    {
        var all = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Root, "AIMs", "aim-settings.json")))!;
        return all[aim]!.AsObject().ToDictionary(p => p.Key, p =>
        {
            var v = (string)p.Value!;
            return File.Exists(Path.Combine(Repository.Root, v)) ? Path.Combine(Repository.Root, v) : v;
        });
    }

    private static SpatialAttitude At(double x, double y) => new()
    {
        ObjectSpatialAttitudeID = Guid.NewGuid().ToString(),
        Position = new Position { PositionID = Guid.NewGuid().ToString(), CartPosition = [x, y, 0] },
        Orientation = new Orientation { OrientationID = Guid.NewGuid().ToString(), EulerAngles = [0, 0, 180] }
    };

    private static BasicAudioObject Sound(string name) => new()
    {
        BasicAudioObjectID = name, BasicAudioObjectData = [new InlineAudioData(Convert.ToBase64String(new byte[64]))], DescrMetadata = name
    };

    // Where the member of the Scene named so is, along x: the Scene holds its Objects, which carry their names in their description.
    private static double? XOf(JsonNode? scene, string name) =>
        scene?["BasicAudioSceneDescriptorsData"]?.AsArray()
            .Where(e => (string?)e?["AObjectIDOrAObject"]?[0]?["DescrMetadata"] == name)
            .Select(e => e?["AudioObjectSpaceTime"]?["SpatialAttitude1"]?["Position"]?["CartPosition"]?[0] is { } x ? (double?)x.GetValue<double>() : null)
            .FirstOrDefault();

    [SkippableFact]
    [Trait("Duration", "Long")]
    public async Task WhatThePersonSaysMovesTheObjectInTheScene()
    {
        Skip.IfNot(Ollama(), "Ollama is not running.");
        var tts = Settings("1MMC-TTS-V2.5-I01"); var asr = Settings("1MMC-ASR-V2.5-I01");
        Skip.IfNot(File.Exists(tts["PiperExecutable"]) && File.Exists(asr["ModelPath"]) && File.Exists(asr["ExecutablePath"]), "Piper or the Whisper model is absent: the model files are obtained separately.");

        var root = Path.Combine(Path.GetTempPath(), "asm-voice-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new AIF.SharedStorage.FileSharedStorage(root, "CAE-ASM", "ASM-Voice");
            var store = new AIF.Store.AmdStore(Repository.Amds); store.Scan();

            // The AIMs: Object and Scene Editing of ASM, Speech Recognition, Dialogue Processing.
            var aoePorts = AIF.Controller.AimPortReader.Load(store, "1CAE-AOE-V1.0-I01");
            var asePorts = AIF.Controller.AimPortReader.Load(store, "1CAE-ASE-V1.0-I01");
            var edpPorts = AIF.Controller.AimPortReader.Load(store, "1MMC-EDP-V2.5-I01");
            var aoe = new Mpai.Cae.Aoe.AoeAimProcessor("1CAE-AOE-V1.0-I01", new Mpai.Cae.Aoe.AoeAim(storage), aoePorts);
            var ase = new Mpai.Cae.Ase.AseAimProcessor("1CAE-ASE-V1.0-I01", new Mpai.Cae.Ase.AseAim(storage), asePorts);
            using var llm = new OllamaClient(Model, context: 4096);
            var edp = new EdpAimProcessor("1MMC-EDP-V2.5-I01", llm, edpPorts);
            var synthesizer = Mpai.Aims.Tts.TtsFactory.Create(tts);
            var recogniser = Mpai.Aims.Asr.AsrFactory.Create(asr);

            async Task<AIF.Controller.Message> Give(AIF.Controller.IAimProcessor aim, params (string Port, string Json)[] inputs) =>
                await aim.ProcessAsync(new AIF.Controller.Message { MessageId = Guid.NewGuid().ToString(), Ports = inputs.ToDictionary(i => i.Port, i => i.Json) });

            // The scene being edited: a violin at x = 1 and drums at x = 3, two metres ahead, built as the User Agent would.
            string objectIn = aoePorts.Input("OSD-BAO-V1.5"), objectOut = aoePorts.Output("OSD-BAO-V1.5"),
                   command = asePorts.Input("CAE-UCM-V1.0"), sceneOut = asePorts.Output("OSD-BAS-V1.5"), sceneReport = asePorts.Output("CAE-UCM-V1.0");
            var ids = new List<string>();
            foreach (var name in new[] { "violin", "drums" })
                ids.Add((string)JsonNode.Parse((await Give(aoe, (objectIn, MpaiJson.ToJson(Sound(name))))).Ports[objectOut])!["BasicAudioObjectID"]!);
            var build = new UserCommand
            {
                UserCommandID = "build", UserCommandTime = SimpleTime.At(DateTimeOffset.UtcNow),
                UserCommandData = new UserCommandData
                {
                    AddedObjects = new ObjectPlacements { Objects = [ new ObjectPlacement { ObjectID = new ManagedObject { ObjectID = ids[0] }, SpatialAttitude = At(1, 2) },
                                                                      new ObjectPlacement { ObjectID = new ManagedObject { ObjectID = ids[1] }, SpatialAttitude = At(3, 2) } ] }
                }
            };
            var scene = (await Give(ase, (command, MpaiJson.ToJson(build)))).Ports[sceneOut];
            Assert.True(XOf(JsonNode.Parse(scene), "violin") is not null, "the scene: " + scene[..Math.Min(1500, scene.Length)]);
            var violinAt = XOf(JsonNode.Parse(scene), "violin")!.Value;

            // 1. The voice: what the person says, as a sound.
            const string said = "move the violin to the left";
            var voice = await synthesizer.ProcessAsync(BasicTextObject.FromText(said));
            // 2. Whisper hears it.
            var heard = (await recogniser.ProcessAsync(voice)).GetText();
            Assert.True(Alike(said, heard) >= 0.7, $"Whisper heard \"{heard}\"");
            // 3. EDP, given the Scene and the words, makes the User Command.
            var understood = await Give(edp, (edpPorts.Input("OSD-BTO-V1.5"), MpaiJson.ToJson(BasicTextObject.FromText(heard))), (edpPorts.Input("OSD-BAS-V1.5"), scene));
            Assert.True(understood.Ports.TryGetValue(edpPorts.Output("CAE-UCM-V1.0"), out var ucm), "EDP gave no User Command for \"" + heard + "\"");
            Assert.NotNull(JsonNode.Parse(ucm!)!["UserCommandData"]!["MovedObjects"]);
            // 4. Audio Scene Editing executes it, and says so.
            var edited = await Give(ase, (command, ucm!));
            var report = MpaiJson.FromJson<UserCommand>(edited.Ports[sceneReport]).UserCommandReport!;
            Assert.Equal("Done", report.Outcome);
            Assert.True(XOf(JsonNode.Parse(edited.Ports[sceneOut]), "violin")!.Value < violinAt, "the violin moved to the left");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // Letters only, from 0 to 1.
    private static double Alike(string a, string b)
    {
        static string Plain(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        a = Plain(a); b = Plain(b);
        if (a.Length == 0 || b.Length == 0) return 0;
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return 1 - (double)d[a.Length, b.Length] / Math.Max(a.Length, b.Length);
    }
}
