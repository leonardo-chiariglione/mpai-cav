using System.Text.Json;
using AIF.SharedStorage;
using Mpai.Cae.Aoe;
using Mpai.Cae.Ase;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Aif.Tests;

// AUDIO OBJECT EDITING AND AUDIO SCENE EDITING, as the author's Reference Model of
// Audio Scene Management draws them (2026/10/04): each AIM through its L3, one User
// Command per action, the Assets versioned in Shared Storage, and every command given
// back with its response half. No Port has a Basic Data Type (2026/10/26): the AIMs take
// and give the Audio Object (OSD-AUO) and the Audio Scene Descriptors (OSD-ASD), the one
// Basic Object or Scene being edited the one the Audio Object or Audio Scene holds alone.
// Judged: an Audio Object holding one Basic Audio Object arriving is created; its
// Acoustic Profile is modified in a new version, and heard from a User PoV; composing
// in Object Editing is reported as not supported (an Object is composed in a Scene);
// Basic Audio Objects make a Basic Audio Scene with its User PoV, given as an Audio
// Scene; a member is moved and another removed; a command naming a member the Scene
// does not have is reported as failed; every output, command and response valid against
// its schema. An Object and a Scene are identified by the name the User gave them
// ("music", "stage"), never by a storage key (the author, 2026/10/26): the Audio Object
// and the Audio Scene a command makes are given their names by the command.
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

    private static UserCommand Command(UserCommandRQ data) => new() { UserCommandID = Guid.NewGuid().ToString(), UserCommandTime = SimpleTime.At(DateTimeOffset.UtcNow), UserCommandRQ = data };
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
            var priv = AsmStorage.PrivateAt(Path.Combine(root, "private"));
            var store = new AIF.Store.AmdStore(Repository.Amds);
            store.Scan();
            var aoePorts = AIF.Controller.AimPortReader.Load(store, "1CAE-AOE-V1.0-I01");
            var asePorts = AIF.Controller.AimPortReader.Load(store, "1CAE-ASE-V1.0-I01");
            var AOE = new Aim(new AoeAimProcessor("1CAE-AOE-V1.0-I01", new AoeAim(AsmStorage.Keep(priv, "1CAE-AOE-V1.0-I01", storage)), aoePorts));
            var ASE = new Aim(new AseAimProcessor("1CAE-ASE-V1.0-I01", new AseAim(AsmStorage.Keep(priv, "1CAE-ASE-V1.0-I01", storage)), asePorts));
            var commands = new List<string>();
            var reports = new List<string>();
            string objectCommand = aoePorts.Input("CAE-UCM-V1.0"), objectOut = aoePorts.Output("OSD-AUO-V1.5"),
                   objectReport = aoePorts.Output("CAE-UCM-V1.0"),
                   sceneCommand = asePorts.Input("CAE-UCM-V1.0"), sceneOut = asePorts.Output("OSD-ASD-V1.5"), sceneReport = asePorts.Output("CAE-UCM-V1.0");

            async Task<(string Out, string Report)> Run(IAimLike aim, string output, string report, params (string Port, string Json)[] inputs)
            {
                foreach (var (port, json) in inputs) if (Header(json) == "CAE-UCM-V1.0") commands.Add(json);
                var m = new AIF.Controller.Message { MessageId = Guid.NewGuid().ToString(), Ports = inputs.ToDictionary(i => i.Port, i => i.Json) };
                var o = await aim.ProcessAsync(m);
                var rep = o.Ports.TryGetValue(report, out var rj) ? rj : "";
                if (rep.Length > 0) reports.Add(rep);
                return (o.Ports.TryGetValue(output, out var json2) ? json2 : "", rep);
            }
            string Said(string report)
            {
                if (report.Length == 0) return "no report";
                var done = MpaiJson.FromJson<UserCommand>(report).UserCommandRS!;
                return $"{done.Outcome}: " + string.Join(", ", done.Actions.Select(a => $"{a.Action} {a.ObjectID} {a.Outcome}{(a.ResultID is null ? "" : " -> " + a.ResultID)}"));
            }

            var r = new Dictionary<string, string>();

            // 1. An Audio Object of one BAO arrives (a capture): created, and out as an Audio Object of that BAO.
            var (musicAuo, _) = await Run(AOE, objectOut, objectReport, (objectCommand, Forms.Acquire(Forms.Auo(Sound("music")))));
            var music = Forms.Bao(musicAuo);
            r["1 a BAO arriving: what AOE gives back"] = $"{Header(music)} {Id(music, "BasicAudioObjectID")}, {Valid("OSD/V1.5/data/BasicAudioObject.json", music)}";
            r["1 the Audio Object AOE gives"] = $"{Header(musicAuo)} {Id(musicAuo, "AudioObjectID")}, {Valid("OSD/V1.5/data/AudioObject.json", musicAuo)}";

            // 2. Its acoustics, the Object part, and where it is heard from: new versions.
            var profile = new AcousticProfile { AcousticProfileID = "music-acp", FrequencyRange = new FrequencyRange { MinFrequencyHz = 40, MaxFrequencyHz = 16000 }, Loudness = -18 };
            var (modifiedAuo, said2) = await Run(AOE, objectOut, objectReport, (objectCommand, MpaiJson.ToJson(Command(new UserCommandRQ
            {
                UserPoV = new PointOfView { PointOfViewID = "behind", CartPosition = [0, -2, 1.6], Orientation = [0, 0, 0] },
                ModifiedObjects = new ObjectChanges { Objects = [new ObjectChange { ObjectID = Ref(Id(music, "BasicAudioObjectID")!), AcousticProfile = profile }] }
            }))));
            var modified = Forms.Bao(modifiedAuo);
            var modifiedNode = System.Text.Json.Nodes.JsonNode.Parse(modified);
            var acp = modifiedNode?["BasicAudioObjectProperties"]?["AcousticProfile"];
            r["2 its Acoustic Profile modified, heard from behind"] = $"{Id(modified, "BasicAudioObjectID")}, loudness {acp?["Loudness"]}, UserPoV {modifiedNode?["UserPoV"]?["PointOfViewID"]}, {Valid("OSD/V1.5/data/BasicAudioObject.json", modified)}";
            r["2 the report"] = Said(said2);

            // 3. Composing in Object Editing: an Object is composed in a Scene.
            var (voiceAuo, _) = await Run(AOE, objectOut, objectReport, (objectCommand, Forms.Acquire(Forms.Auo(Sound("voice")))));
            var voice = Forms.Bao(voiceAuo);
            var (_, said3) = await Run(AOE, objectOut, objectReport, (objectCommand, MpaiJson.ToJson(Command(new UserCommandRQ
            {
                AddedObjects = new ObjectPlacements { ObjectID = "duo", Objects = [new ObjectPlacement { ObjectID = Ref(Id(modified, "BasicAudioObjectID")!), SpatialAttitude = At(1) }] }
            }))));
            r["3 composing in Object Editing: the report"] = Said(said3);

            // 4. BAOs into a Basic Audio Scene, heard from a UserPoV.
            var user = new PointOfView { PointOfViewID = "user", CartPosition = [0, 0, 1.6], Orientation = [0, 0, 0] };
            var (asd, said4) = await Run(ASE, sceneOut, sceneReport, (sceneCommand, MpaiJson.ToJson(Command(new UserCommandRQ
            {
                UserPoV = user,
                AddedObjects = new ObjectPlacements { ObjectID = "stage", Objects = [new ObjectPlacement { ObjectID = Ref(Id(modified, "BasicAudioObjectID")!), SpatialAttitude = At(-1) }, new ObjectPlacement { ObjectID = Ref(Id(voice, "BasicAudioObjectID")!), SpatialAttitude = At(1) }] }
            }))));
            var bas = Forms.Bas(asd);
            r["4 the Audio Scene ASE gives"] = $"{Header(asd)} of {System.Text.Json.Nodes.JsonNode.Parse(asd)?["SubAudioScenes"]?.AsArray().Count ?? 0} sub-scene(s), {Valid("OSD/V1.5/data/AudioSceneDescriptors.json", asd)}";
            var basNode = System.Text.Json.Nodes.JsonNode.Parse(bas);
            r["4 two BAOs placed in a scene"] = $"{Header(bas)} of {basNode?["BasicAudioSceneDescriptorsData"]?.AsArray().Count ?? 0} members, UserPoV {(basNode?["UserPoV"] is null ? "absent" : "set")}, {Valid("OSD/V1.5/data/BasicAudioSceneDescriptors.json", bas)}";
            r["4 the report"] = Said(said4);

            // 5. One member moved, the other removed, and a member the Scene does not have.
            var (movedAsd, said5) = await Run(ASE, sceneOut, sceneReport, (sceneCommand, MpaiJson.ToJson(Command(new UserCommandRQ
            {
                MovedObjects = new ObjectMovements { Objects = [new ObjectMovement { ObjectID = Ref(Id(voice, "BasicAudioObjectID")!), NewSpatialAttitude = At(3) }] },
                RemovedObjects = new ObjectPlacements { Objects = [new ObjectPlacement { ObjectID = Ref(Id(modified, "BasicAudioObjectID")!) }, new ObjectPlacement { ObjectID = Ref("nothing") }] }
            }))));
            var moved = Forms.Bas(movedAsd);
            var movedNode = System.Text.Json.Nodes.JsonNode.Parse(moved);
            r["5 moved and removed"] = $"{movedNode?["BasicAudioSceneDescriptorsData"]?.AsArray().Count ?? 0} member(s), {Valid("OSD/V1.5/data/BasicAudioSceneDescriptors.json", moved)}";
            r["5 the report"] = Said(said5);

            r["6 the User Commands"] = string.Join("; ", commands.Select(c => Valid("CAE3/V1.0/data/UserCommand.json", c)).Distinct());
            r["6 the reports"] = string.Join("; ", reports.Select(c => Valid("CAE3/V1.0/data/UserCommand.json", c)).Distinct());
            r["7 versions kept in the Private Storage of the Module"] = string.Join(", ", new[] { "OSD-BAO-V1.5#1", "OSD-ASD-V1.5#1" }.Select(t => $"{t} {AsmStorage.Handle(priv, "1CAE-AOE-V1.0-I01").MPAI_AIFM_RuledStorage_List(null, t + "/").Count}"));
            Expected.Match("asm-editing.json", r);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private interface IAimLike { Task<AIF.Controller.Message> ProcessAsync(AIF.Controller.Message m); }
    private sealed class Aim(AIF.Controller.IAimProcessor p) : IAimLike { public Task<AIF.Controller.Message> ProcessAsync(AIF.Controller.Message m) => p.ProcessAsync(m); }
}
