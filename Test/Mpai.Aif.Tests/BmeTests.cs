using System.Text.Json;
using System.Text.Json.Nodes;
using AIF.SharedStorage;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Osd.Bme;

namespace Mpai.Aif.Tests;

// BASIC MULTIMODAL SCENE EDITING (OSD-BME; the author, 2026/10/04: "a new aim that take
// basic scenes from at least two different media scenes and create a BMS"), through its
// L3, its Port 1 taking an Audio Scene, its Port 2 another Scene. Judged: two Scenes of
// one medium make no Basic Multimodal Scene; one taken out and a Speech Scene added,
// the Scene is multimodal and output, valid against its schema; a new version of the
// Speech Scene arriving on its Port is followed; a member taken out leaves one medium,
// and nothing is output; a Scene it does not have is reported as failed; every report
// valid against the User Command schema.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class BmeTests
{
    private static string Valid(string schema, string json)
    {
        var s = AIF.Metadata.PublishedSchemas.At(Repository.Schemas)[Path.GetFullPath(Path.Combine(Repository.Schemas, schema))];
        using var doc = JsonDocument.Parse(json);
        lock (AIF.Metadata.PublishedSchemas.Lock) return s.Evaluate(doc.RootElement).IsValid ? "valid" : "not valid";
    }

    private static string Audio(string id) => MpaiJson.ToJson(new BasicAudioSceneDescriptors
    {
        MInstanceID = "M1", BasicAudioSceneDescriptorsID = id, AudioObjectCount = 0, BASSpaceTime = new SpaceTime(),
        UserPoV = new PointOfView { PointOfViewID = "u", CartPosition = [0, 0, 0], Orientation = [0, 0, 0] }
    });
    private static string Speech(string id) => MpaiJson.ToJson(new BasicSpeechSceneDescriptors { MInstanceID = "M1", BasicSpeechSceneDescriptorsID = id });
    private static string Command(UserCommandData data) => MpaiJson.ToJson(new UserCommand
    {
        UserCommandID = Guid.NewGuid().ToString(), UserCommandTime = SimpleTime.At(DateTimeOffset.UtcNow), UserCommandData = data
    });
    private static ObjectPlacement Place(string id) => new() { ObjectID = new ManagedObject { ObjectID = id } };

    [Fact]
    public async Task TwoMediaMakeABasicMultimodalScene()
    {
        var root = Path.Combine(Path.GetTempPath(), "bme-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AIF.Store.AmdStore(Repository.Amds);
            store.Scan();
            var ports = AIF.Controller.AimPortReader.Load(store, "1OSD-BME-V1.5-I01");
            var bme = new BmeAimProcessor("1OSD-BME-V1.5-I01", new FileSharedStorage(root, "BME", "BME-Test"), ports, "M1");
            string first = ports.Input("OSD-BAS-V1.5", 1), second = ports.Input("OSD-BAS-V1.5", 2), command = ports.Input("CAE-UCM-V1.0"),
                   sceneOut = ports.Output("OSD-BMS-V1.5"), reportOut = ports.Output("CAE-UCM-V1.0");
            var reports = new List<string>();
            async Task<(string? Scene, string Said)> Run(params (string Port, string Json)[] inputs)
            {
                var o = await bme.ProcessAsync(new AIF.Controller.Message { MessageId = "m", Ports = inputs.ToDictionary(i => i.Port, i => i.Json) });
                var said = "";
                if (o.Ports.TryGetValue(reportOut, out var rep))
                {
                    reports.Add(rep);
                    var done = MpaiJson.FromJson<UserCommand>(rep).UserCommandReport!;
                    said = $"{done.Outcome}: " + string.Join(", ", done.Actions.Select(a => $"{a.Action} {a.ObjectID} {a.Outcome}"));
                }
                return (o.Ports.TryGetValue(sceneOut, out var s) ? s : null, said);
            }
            string Members(string? json) => json is null ? "no Basic Multimodal Scene"
                : string.Join(" + ", JsonNode.Parse(json)!["BasicAVSceneDescriptorsData"]!.AsArray().Select(m =>
                    (string?)m!["BXSOrBXSID"]!["BasicAudioSceneDescriptorsID"] ?? (string?)m!["BXSOrBXSID"]!["BasicSpeechSceneDescriptorsID"] ?? "?"));

            var r = new Dictionary<string, string>();
            await Run((first, Audio("music")), (second, Audio("rain")));
            var (one, said1) = await Run((command, Command(new UserCommandData { AddedObjects = new ObjectPlacements { Objects = [Place("music"), Place("rain")] } })));
            r["1 two Audio Scenes"] = $"{Members(one)}; {said1}";

            await Run((command, Command(new UserCommandData { RemovedObjects = new ObjectPlacements { Objects = [Place("rain")] } })));
            await Run((second, Speech("voice")));
            var (two, said2) = await Run((command, Command(new UserCommandData { AddedObjects = new ObjectPlacements { Objects = [Place("voice")] } })));
            r["2 rain out, a Speech Scene in"] = $"{Members(two)}, {(two is null ? "-" : Valid("OSD/V1.5/data/BasicAudioVisualSceneDescriptors.json", two))}; {said2}";

            var (followed, _) = await Run((second, Speech("voice-2")));
            r["3 a new version of the Speech Scene, followed"] = Members(followed);

            var (removed, said4) = await Run((command, Command(new UserCommandData { RemovedObjects = new ObjectPlacements { Objects = [Place("music"), Place("nowhere")] } })));
            r["4 music out, and a Scene not there"] = $"{Members(removed)}; {said4}";

            r["5 the reports"] = string.Join("; ", reports.Select(j => Valid("CAE3/V1.0/data/UserCommand.json", j)).Distinct());
            Expected.Match("bme.json", r);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
