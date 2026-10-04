using System.Text.Json;
using AIF.SharedStorage;
using Mpai.Cae.Aoe;
using Mpai.Cae.Ase;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Aif.Tests;

// AUDIO SCENE EDITING, OBJECTS AND SCENES (the author's goal, 2026/10/02: edit BAOs;
// compose BAOs and AUOs into AUOs; compose Basic Audio Scenes of BAOs and Audio
// Scenes; 2026/10/04: "ASE is ASM" - one AIM, every User Command). CAE-ASE runs
// through its L3, one User Command per action - on its Object Command Port or its
// Scene Command Port - the Assets versioned in Shared Storage. Judged: a BAO
// arriving is created and goes out as a BAO, not as an Audio Object of one; its
// Acoustic Profile (the Object part) is modified in a new version; two BAOs compose
// an Audio Object, which has no Acoustic Profile of its own; BAOs make a Basic Audio
// Scene with its UserPoV, an AUO an Audio Scene; every output, and every Command,
// valid against its schema.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class AsmEditingTests
{
    private static readonly IReadOnlyDictionary<string, Json.Schema.JsonSchema> All = AIF.Metadata.PublishedSchemas.At(Repository.Schemas);

    private static string Valid(string schema, string json)
    {
        var s = All[Path.GetFullPath(Path.Combine(Repository.Schemas, schema))];
        using var doc = JsonDocument.Parse(json);
        Json.Schema.EvaluationResults r;
        lock (AIF.Metadata.PublishedSchemas.Lock)
            r = s.Evaluate(doc.RootElement, new Json.Schema.EvaluationOptions { OutputFormat = Json.Schema.OutputFormat.List });
        if (r.IsValid) return "valid";
        var first = r.Details.FirstOrDefault(d => d.Errors is { Count: > 0 });
        return first is null ? "not valid" : $"not valid: {first.InstanceLocation} {first.Errors!.First().Value}";
    }

    private static string? Header(string json) => System.Text.Json.Nodes.JsonNode.Parse(json)?["Header"]?.GetValue<string>();
    private static string? Id(string json, string field) => System.Text.Json.Nodes.JsonNode.Parse(json)?[field]?.GetValue<string>();

    private static BasicAudioObject Sound(string name) => new()
    {
        BasicAudioObjectID = name,
        BasicAudioObjectData = [new InlineAudioData(Convert.ToBase64String(new byte[64]))],
        DescrMetadata = name
    };

    private static UserCommand Command(UserCommandData data) => new() { UserCommandID = Guid.NewGuid().ToString(), UserCommandTime = SimpleTime.At(DateTimeOffset.UtcNow), UserCommandData = data };
    private static ManagedObject Ref(string id) => new() { ObjectID = id };
    private static SpatialAttitude At(double x) => new()
    {
        ObjectSpatialAttitudeID = Guid.NewGuid().ToString(),
        Position = new Position { PositionID = Guid.NewGuid().ToString(), CartPosition = [x, 2, 0] },
        Orientation = new Orientation { OrientationID = Guid.NewGuid().ToString(), EulerAngles = [0, 0, 180] }
    };

    [Fact]
    public async Task EditComposePlace()
    {
        var root = Path.Combine(Path.GetTempPath(), "asm-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new FileSharedStorage(root, "CAE-ASM", "ASM-Test");
            var store = new AIF.Store.AmdStore(Repository.Amds);
            store.Scan();
            var asePorts = AIF.Controller.AimPortReader.Load(store, "1CAE-ASE-V1.0-I01");
            var ase = new AseAimProcessor("1CAE-ASE-V1.0-I01", new AoeAim(storage), new AseAim(storage), asePorts);
            var commands = new List<string>();
            string objectIn = asePorts.Input("OSD-BAO-V1.5"), objectCommand = asePorts.Input("CAE-UCM-V1.0", 1), sceneCommand = asePorts.Input("CAE-UCM-V1.0", 2),
                   objectOut = asePorts.Output("OSD-BAO-V1.5"), sceneOut = asePorts.Output("OSD-BAS-V1.5");

            async Task<string> Run(IAimLike aim, string output, params (string Port, string Json)[] inputs)
            {
                foreach (var (port, json) in inputs) if (Header(json) == "CAE-UCM-V1.0") commands.Add(json);
                var m = new AIF.Controller.Message { MessageId = Guid.NewGuid().ToString(), Ports = inputs.ToDictionary(i => i.Port, i => i.Json) };
                var o = await aim.ProcessAsync(m);
                return o.Ports.TryGetValue(output, out var json2) ? json2 : "";
            }
            var ASE = new Aim(ase);

            var r = new Dictionary<string, string>();

            // 1. A BAO arrives (a capture): created, and out as a BAO.
            var music = await Run(ASE, objectOut, (objectIn, MpaiJson.ToJson(Sound("music"))));
            r["1 a BAO arriving: what ASE gives back"] = $"{Header(music)} {Id(music, "BasicAudioObjectID")}, {Valid("OSD/V1.5/data/BasicAudioObject.json", music)}";

            // 2. Its acoustics, the Object part: a new version.
            var profile = new AcousticProfile { AcousticProfileID = "music-acp", FrequencyRange = new FrequencyRange { MinFrequencyHz = 40, MaxFrequencyHz = 16000 }, Loudness = -18 };
            var modified = await Run(ASE, objectOut, (objectCommand, MpaiJson.ToJson(Command(new UserCommandData
            {
                ModifiedObjects = new ObjectChanges { Objects = [new ObjectChange { ObjectID = Ref(Id(music, "BasicAudioObjectID")!), AcousticProfile = profile }] }
            }))));
            var acp = System.Text.Json.Nodes.JsonNode.Parse(modified)?["BasicAudioObjectProperties"]?["AcousticProfile"];
            r["2 its Acoustic Profile modified"] = $"{Id(modified, "BasicAudioObjectID")}, loudness {acp?["Loudness"]}, {Valid("OSD/V1.5/data/BasicAudioObject.json", modified)}";

            // 3. A second BAO, and the first composed into it: an Audio Object.
            var voice = await Run(ASE, objectOut, (objectIn, MpaiJson.ToJson(Sound("voice"))));
            var composed = await Run(ASE, objectOut, (objectCommand, MpaiJson.ToJson(Command(new UserCommandData
            {
                AddedObjects = new ObjectPlacements { Objects = [new ObjectPlacement { ObjectID = Ref(Id(modified, "BasicAudioObjectID")!), SpatialAttitude = At(1) }] }
            }))));
            var auo = System.Text.Json.Nodes.JsonNode.Parse(composed);
            r["3 two BAOs composed"] = $"{Header(composed)} {Id(composed, "AudioObjectID")} of {auo?["BasicAudioObjects"]?.AsArray().Count ?? 0} Basic and {auo?["SubAudioObjects"]?.AsArray().Count ?? 0} sub-Objects, Acoustic Profile of its own: {(auo?["AudioObjectProperties"] is null ? "none" : "yes")}, {Valid("OSD/V1.5/data/AudioObject.json", composed)}";

            // 4. BAOs into a Basic Audio Scene, heard from a UserPoV.
            var user = new PointOfView { PointOfViewID = "user", CartPosition = [0, 0, 1.6], Orientation = [0, 0, 0] };
            var bas = await Run(ASE, sceneOut, (sceneCommand, MpaiJson.ToJson(Command(new UserCommandData
            {
                UserPoV = user,
                AddedObjects = new ObjectPlacements { Objects = [new ObjectPlacement { ObjectID = Ref(Id(modified, "BasicAudioObjectID")!), SpatialAttitude = At(-1) }, new ObjectPlacement { ObjectID = Ref(Id(voice, "BasicAudioObjectID")!), SpatialAttitude = At(1) }] }
            }))));
            var basNode = System.Text.Json.Nodes.JsonNode.Parse(bas);
            r["4 two BAOs placed in a scene"] = $"{Header(bas)} of {basNode?["BasicAudioSceneDescriptorsData"]?.AsArray().Count ?? 0} members, UserPoV {(basNode?["UserPoV"] is null ? "absent" : "set")}, {Valid("OSD/V1.5/data/BasicAudioSceneDescriptors.json", bas)}";

            // 5. An Audio Object into an Audio Scene, in a fresh session.
            var ase2 = new Aim(new AseAimProcessor("1CAE-ASE-V1.0-I01", new AoeAim(storage), new AseAim(storage), asePorts));
            await Run(ase2, objectOut, (objectIn, composed));
            var asd = await Run(ase2, sceneOut, (sceneCommand, MpaiJson.ToJson(Command(new UserCommandData
            {
                AddedObjects = new ObjectPlacements { Objects = [new ObjectPlacement { ObjectID = Ref(Id(composed, "AudioObjectID")!), SpatialAttitude = At(0) }] }
            }))));
            r["5 an Audio Object placed in a scene"] = $"{Header(asd)}, {Valid("OSD/V1.5/data/AudioSceneDescriptors.json", asd)}";

            r["6 the User Commands"] = string.Join("; ", commands.Select(c => Valid("CAE3/V1.0/data/UserCommand.json", c)).Distinct());
            r["7 versions kept in Shared Storage"] = string.Join(", ", new[] { "BAO", "AUO", "BAS", "ASD" }.Select(t => $"{t} {storage.MPAI_AIFM_SharedStorage_List(t).Count}"));
            Expected.Match("asm-editing.json", r);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private interface IAimLike { Task<AIF.Controller.Message> ProcessAsync(AIF.Controller.Message m); }
    private sealed class Aim(AIF.Controller.IAimProcessor p) : IAimLike { public Task<AIF.Controller.Message> ProcessAsync(AIF.Controller.Message m) => p.ProcessAsync(m); }
}
