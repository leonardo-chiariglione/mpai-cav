using System.Text.Json;
using System.Text.Json.Nodes;
using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Aims.Audio.Spatial;
using Mpai.Cae.Asm;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Aif.Tests;

// CAE-ASM STEP 5: AUDIO SCENE MANAGEMENT AS ONE MODULE (the author's goal,
// 2026/10/02). The Controller composes CAE-ASM from its L3 - Audio Object
// Acquisition, Audio Object Editing, Audio Scene Editing, Audio Object Delivery -
// and a session is a sequence of runs, one per User action, the Objects and Scenes
// in the Module's Shared Storage. Judged: a sound captured from the device is a
// Basic Audio Object, played as it is; a User Command places it, with a second
// sound, in a Basic Audio Scene heard from the User's Point of View - the Scene
// leaves the Module and is played, rendered for the User, in stereo; every output
// valid against its schema.
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
        all[AsmProvider.Aod] = new JsonObject { ["SteamAudio"] = Steam, ["OutputFolder"] = Path.Combine(work, "played"), ["Layout"] = "Binaural" };
        File.WriteAllText(settings, all.ToJsonString());

        var r = new Dictionary<string, string>();
        using var api = new ControllerApi(Repository.Amds, settings, _ => new AsmProvider(Repository.Amds));
        try
        {
            r["the Module starts"] = api.StartFlow(Asm).ToString();
            api.SharedStorageInit(Asm, Path.Combine(work, "assets"));

            // 1. Two sounds from the device: each a Basic Audio Object, played as it is.
            string Played()
            {
                var read = api.OutputRead(Asm, "OSD-BAO-V1.5", 1, 60_000);
                return read.Error == AifError.OK ? read.Json! : $"nothing ({read.Error})";
            }
            api.InputWrite(Asm, "OSD-BAO-V1.5", 1, MpaiJson.ToJson(Captured("low", 440)));
            var first = Played();
            api.InputWrite(Asm, "OSD-BAO-V1.5", 1, MpaiJson.ToJson(Captured("high", 880)));
            var second = Played();
            r["a sound from the device, played"] = first.StartsWith('{') ? $"{JsonNode.Parse(first)!["Header"]}, {Valid("OSD/V1.5/data/BasicAudioObject.json", first)}" : first;
            r["a second sound, played"] = second.StartsWith('{') ? $"{JsonNode.Parse(second)!["Header"]}, {Valid("OSD/V1.5/data/BasicAudioObject.json", second)}" : second;

            // 2. One User Command: both in a Basic Audio Scene, heard from the User.
            var user = new PointOfView { PointOfViewID = "user", CartPosition = [0, 0, 1.6], Orientation = [0, 0, 0] };
            api.InputWrite(Asm, "CAE-UCM-V1.0", 3, Command(new UserCommandData
            {
                UserPoV = user,
                AddedObjects = new ObjectPlacements { Objects = [new ObjectPlacement { ObjectID = new ManagedObject { ObjectID = "BAO000001" }, SpatialAttitude = At(-1.5, 1.5) },
                                                                 new ObjectPlacement { ObjectID = new ManagedObject { ObjectID = "BAO000002" }, SpatialAttitude = At(1.5, 1.5) }] }
            }));
            var scene = api.OutputRead(Asm, "OSD-BAS-V1.5", 1, 60_000);
            r["the Scene leaving the Module"] = scene.Error == AifError.OK
                ? $"{JsonNode.Parse(scene.Json!)!["BasicAudioSceneDescriptorsData"]!.AsArray().Count} members, {Valid("OSD/V1.5/data/BasicAudioSceneDescriptors.json", scene.Json!)}"
                : $"nothing ({scene.Error})";
            var rendered = Played();
            if (rendered.StartsWith('{'))
            {
                var wav = MpaiJson.FromJson<BasicAudioObject>(rendered)!.Data;
                r["the Scene played"] = $"{BitConverter.ToInt16(wav, 22)} channels at {BitConverter.ToInt32(wav, 24)} Hz, {Valid("OSD/V1.5/data/BasicAudioObject.json", rendered)}";
            }
            else r["the Scene played"] = rendered;
        }
        finally
        {
            api.StopFlow(Asm);
            try { Directory.Delete(work, true); } catch { }
        }
        Expected.Match("asm-module.json", r);
    }
}
