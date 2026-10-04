using System.Text.Json;
using System.Text.Json.Nodes;
using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.SpatialAudio;
using Mpai.Cae.Asm;
using Mpai.Providers;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Aif.Tests;

// AUDIO SCENE MANAGEMENT AS ONE MODULE, as the author's Reference Model draws it
// (2026/10/04): the audio chain - Audio Object Editing, Audio Scene Editing - and the
// speech chain - Speech Object Editing, Speech Scene Editing - with Text and Speech
// Translation, a session a sequence of runs, the Objects and Scenes in the Module's
// Shared Storage. Capture and hearing are the User Agent's. Judged: a sound the User
// Agent captured enters as a Basic Audio Object and comes back as the open Object;
// given again by its identifier, it is opened from Shared Storage; an Audio Scene
// Command places it, with a second sound, in a Basic Audio Scene heard from the User's
// Point of View - the Scene leaves the Module, and the User Agent's spatial audio
// renders it in stereo; a Speech Object is placed by a Speech Scene Command in a Basic
// Speech Scene, and Speech Scene Editing reports the command; every output valid
// against its schema.
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
public class AsmModuleTests
{
    private const string Asm = AsmProvider.Module;
    private static readonly string Steam = Path.Combine(Repository.Root, "Models", "SteamAudio");

    private static float[] Tone(double hz) => Enumerable.Range(0, 48000).Select(i => (float)(0.3 * Math.Sin(2 * Math.PI * hz * i / 48000.0))).ToArray();
    private static BasicAudioObject Captured(string name, double hz) => new()
    {
        BasicAudioObjectID = name,
        BasicAudioObjectData = [new InlineAudioData(Convert.ToBase64String(SceneAudio.Wav(Tone(hz), 1, 48000)))],
        DescrMetadata = name
    };
    private static SpatialAttitude At(double x, double y) => new()
    {
        ObjectSpatialAttitudeID = Guid.NewGuid().ToString(),
        Position = new Position { PositionID = Guid.NewGuid().ToString(), CartPosition = [x, y, 1.6] },
        Orientation = new Orientation { OrientationID = Guid.NewGuid().ToString(), EulerAngles = [0, 0, 180] }
    };
    private static string Command(UserCommandData data) => MpaiJson.ToJson(new UserCommand
    {
        UserCommandID = Guid.NewGuid().ToString(), UserCommandTime = SimpleTime.At(DateTimeOffset.UtcNow), UserCommandData = data
    });

    private static string Valid(string schema, string json)
    {
        var all = AIF.Metadata.PublishedSchemas.At(Repository.Schemas);
        var s = all[Path.GetFullPath(Path.Combine(Repository.Schemas, schema))];
        using var doc = JsonDocument.Parse(json);
        lock (AIF.Metadata.PublishedSchemas.Lock) return s.Evaluate(doc.RootElement).IsValid ? "valid" : "not valid";
    }

    [SkippableFact]
    public void OneModuleOneSession()
    {
        Skip.IfNot(SpatialRenderer.Available(Steam), "Models/SteamAudio is absent: Steam Audio is obtained separately (docs/models-provenance.md).");
        var work = Path.Combine(Path.GetTempPath(), "asm-module-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var settings = Path.Combine(work, "aim-settings.json");
        var all = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Root, "AIMs", "aim-settings.json")))!.AsObject();
        File.WriteAllText(settings, all.ToJsonString());

        var r = new Dictionary<string, string>();
        using var api = new ControllerApi(Repository.Amds, settings, store => new CompositeProvider(new AsmProvider(Repository.Amds), new MatProvider(store)));
        try
        {
            r["the Module starts"] = api.StartFlow(Asm).ToString();
            api.SharedStorageInit(Asm, Path.Combine(work, "assets"));

            // 1. Two sounds the User Agent captured: each a Basic Audio Object, back as the open Object.
            string Played()
            {
                var read = api.OutputRead(Asm, "OSD-BAO-V1.5", 1, 60_000);
                return read.Error == AifError.OK ? read.Json! : $"nothing ({read.Error})";
            }
            api.InputWrite(Asm, "OSD-BAO-V1.5", 1, MpaiJson.ToJson(Captured("low", 440)));
            var first = Played();
            api.InputWrite(Asm, "OSD-BAO-V1.5", 1, MpaiJson.ToJson(Captured("high", 880)));
            var second = Played();
            r["a sound the User Agent captured, the open Object"] = first.StartsWith('{') ? $"{JsonNode.Parse(first)!["Header"]}, {Valid("OSD/V1.5/data/BasicAudioObject.json", first)}" : first;
            r["a second sound, the open Object"] = second.StartsWith('{') ? $"{JsonNode.Parse(second)!["Header"]}, {Valid("OSD/V1.5/data/BasicAudioObject.json", second)}" : second;

            // 1b. An Object of Shared Storage, given by its identifier: opened.
            api.InputWrite(Asm, "OSD-BAO-V1.5", 1, first);
            var stored = Played();
            r["an Object of Shared Storage, opened"] = stored.StartsWith('{') ? $"{JsonNode.Parse(stored)!["Header"]} {JsonNode.Parse(stored)!["BasicAudioObjectID"]}, {Valid("OSD/V1.5/data/BasicAudioObject.json", stored)}" : stored;

            // 2. One User Command: both in a Basic Audio Scene, heard from the User.
            var user = new PointOfView { PointOfViewID = "user", CartPosition = [0, 0, 1.6], Orientation = [0, 0, 0] };
            api.InputWrite(Asm, "CAE-UCM-V1.0", 2, Command(new UserCommandData
            {
                UserPoV = user,
                AddedObjects = new ObjectPlacements { Objects = [new ObjectPlacement { ObjectID = new ManagedObject { ObjectID = "BAO000001" }, SpatialAttitude = At(-1.5, 1.5) },
                                                                 new ObjectPlacement { ObjectID = new ManagedObject { ObjectID = "BAO000002" }, SpatialAttitude = At(1.5, 1.5) }] }
            }));
            var scene = api.OutputRead(Asm, "OSD-BAS-V1.5", 1, 60_000);
            r["the Scene leaving the Module"] = scene.Error == AifError.OK
                ? $"{JsonNode.Parse(scene.Json!)!["BasicAudioSceneDescriptorsData"]!.AsArray().Count} members, {Valid("OSD/V1.5/data/BasicAudioSceneDescriptors.json", scene.Json!)}"
                : $"nothing ({scene.Error})";
            if (scene.Error == AifError.OK)
            {
                // What the User Agent's Loudspeaker Unit would play.
                var heard = MpaiJson.ToJson(new AudioRendering(Steam).Render(scene.Json!));
                var wav = MpaiJson.FromJson<BasicAudioObject>(heard)!.Data;
                r["the Scene heard by the User"] = $"{BitConverter.ToInt16(wav, 22)} channels at {BitConverter.ToInt32(wav, 24)} Hz, {Valid("OSD/V1.5/data/BasicAudioObject.json", heard)}";
            }

            // 3. The speech chain: a Speech Object the User Agent captured, placed by a
            // Speech Scene Command in a Basic Speech Scene; Speech Scene Editing reports it.
            var hello = new BasicSpeechObject { BasicSpeechObjectID = "hello", Data = SceneAudio.Wav(Tone(300), 1, 48000) };
            api.InputWrite(Asm, "OSD-BSO-V1.5", 1, MpaiJson.ToJson(hello));
            var kept = api.OutputRead(Asm, "OSD-BSO-V1.5", 1, 60_000);
            r["a Speech Object, the open Object"] = kept.Error == AifError.OK ? $"{JsonNode.Parse(kept.Json!)!["BasicSpeechObjectID"]}, {Valid("OSD/V1.5/data/BasicSpeechObject.json", kept.Json!)}" : $"nothing ({kept.Error})";
            api.InputWrite(Asm, "CAE-UCM-V1.0", 4, Command(new UserCommandData
            {
                UserPoV = user,
                AddedObjects = new ObjectPlacements { Objects = [new ObjectPlacement { ObjectID = new ManagedObject { ObjectID = "hello" }, SpatialAttitude = At(0, 2) }] }
            }));
            var speechScene = api.OutputRead(Asm, "OSD-BSS-V1.5", 1, 60_000);
            r["the Speech Scene leaving the Module"] = speechScene.Error == AifError.OK
                ? $"{JsonNode.Parse(speechScene.Json!)!["BasicSpeechSceneDescriptors"]!.AsArray().Count} member(s), {Valid("OSD/V1.5/data/BasicSpeechSceneDescriptors.json", speechScene.Json!)}"
                : $"nothing ({speechScene.Error})";
            var sseReport = api.OutputRead(Asm, "CAE-UCM-V1.0", 4, 60_000);
            r["the report of Speech Scene Editing"] = sseReport.Error == AifError.OK && MpaiJson.FromJson<UserCommand>(sseReport.Json!).UserCommandReport is { } done
                ? $"{done.Outcome}; " + string.Join("; ", done.Actions.Select(a => $"{a.Action} {a.Outcome}")) + $", {Valid("CAE3/V1.0/data/UserCommand.json", sseReport.Json!)}"
                : $"nothing ({sseReport.Error})";
        }
        finally
        {
            api.StopFlow(Asm);
            try { Directory.Delete(work, true); } catch { }
        }
        Expected.Match("asm-module.json", r);
    }
}
