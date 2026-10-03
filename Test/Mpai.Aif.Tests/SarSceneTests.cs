using System.Text.Json;
using AIF.Controller;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Paf.Sar;

namespace Mpai.Aif.Tests;

// RSR STEP 3: SCENE AND AVATAR RENDERING, THE 3D ALTERNATIVE (the author, 2026/10/03).
// Scene and Avatar Rendering places the Speaking Avatar in a Scene seen and heard
// from a Point of View and produces it as data: the 3D Model Scene (OSD-B3S) with the
// Avatar placed, for the User Agent's renderer to draw, and the Multimodal Scene
// (OSD-BMS) of the Audio, Speech and 3D Model Scenes. Judged, through SAR's L3:
// where the Avatar goes - in front of the Point of View facing it, or where the Scene
// already has it; the Scene kept between utterances; nothing without a Scene; and
// what it writes valid against the schemas.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class SarSceneTests
{
    private static readonly IReadOnlyDictionary<string, Json.Schema.JsonSchema> All = AIF.Metadata.PublishedSchemas.At(Repository.Schemas);

    private static string Check(string schema, string json)
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

    private static string Where(SpaceTime? at)
    {
        var a = at?.SpatialAttitude1;
        var p = a?.Position.CartPosition ?? [];
        return $"({string.Join(", ", p.Select(v => Math.Round(v, 2).ToString(System.Globalization.CultureInfo.InvariantCulture)))}) yaw {Math.Round(a?.Orientation.EulerAngles[2] ?? 0)}";
    }

    private static PointOfView Viewer(double x, double y, double yaw) =>
        new() { PointOfViewID = $"pov-{yaw}", CartPosition = [x, y, 1.6], Orientation = [0, 0, yaw] };

    private static Basic3DModelSceneDescriptors Room(params (string Model, double[] At, double Yaw)[] members) => new()
    {
        MInstanceID = "M1", Basic3DModelSceneDescriptorsID = "room", UserPoV = Viewer(0, 0, 0),
        Basic3DModelSceneDescriptorsSpaceTime = SarAimProcessor.At([0, 0, 0], 0),
        ModelObjectCount = members.Length,
        Basic3DModelSceneItems = members.Select(m => new Basic3DModelSceneItem
        {
            ModelObjectSpaceTime = SarAimProcessor.At(m.At, m.Yaw),
            ObjectIDOrObject = [System.Text.Json.Nodes.JsonValue.Create(m.Model)]
        }).ToList()
    };

    [Fact]
    public async Task TheAvatarPlacedInTheScene()
    {
        var store = new AIF.Store.AmdStore(Repository.Amds);
        store.Scan();
        var ports = AimPortReader.Load(store, "1PAF-SAR-V1.6-I01");
        var sar = new SarAimProcessor("1PAF-SAR-V1.6-I01", ports);
        var speech = new BasicSpeechObject { BasicSpeechObjectID = "hello", Data = new byte[3200] };
        var speaking = MpaiJson.ToJson(new SpeakingAvatar
        {
            MInstanceID = "M1", SpeakingAvatarID = "thalia-says-hello",
            SpeakingAvatarData = new SpeakingAvatarData { Avatar = Avatar.OfModel(TestAvatar.Model, "thalia"), SpeechObject = speech }
        });
        string sav = ports.Input("XRV-SAV-V1.0"), model = ports.Input("OSD-B3S-V1.5"), pov = ports.Input("OSD-OPV-V1.5"),
               audio = ports.Input("OSD-BAS-V1.5"), b3sOut = ports.Output("OSD-B3S-V1.5"), bmsOut = ports.Output("OSD-BMS-V1.5");
        async Task<Message> Run(Dictionary<string, string> inputs) => await sar.ProcessAsync(new Message { MessageId = "m", Ports = inputs });

        var r = new Dictionary<string, string>();
        var none = await Run(new() { [sav] = speaking });
        r["an utterance with no Scene"] = $"{none.MessageType}, {none.Ports.Count} output(s)";

        var kept = await Run(new() { [model] = MpaiJson.ToJson(Room(("table.glb", [3, 1, 0], 90))), [pov] = MpaiJson.ToJson(Viewer(0, 0, 0)) });
        r["a Scene alone"] = $"{kept.MessageType}, {kept.Ports.Count} output(s)";

        var first = await Run(new() { [sav] = speaking });
        var b3s = MpaiJson.FromJson<Basic3DModelSceneDescriptors>(first.Ports[b3sOut]);
        var bms = MpaiJson.FromJson<BasicAudioVisualSceneDescriptors>(first.Ports[bmsOut]);
        r["the 3D Model Scene"] = string.Join("; ", b3s.Basic3DModelSceneItems.Select(i => $"{i.Id} at {Where(i.ModelObjectSpaceTime)}"));
        r["the 3D Model Scene is valid"] = Check("OSD/V1.5/data/Basic3DModelSceneDescriptors.json", first.Ports[b3sOut]);
        r["the Multimodal Scene holds"] = string.Join(", ", JsonDocument.Parse(first.Ports[bmsOut]).RootElement.GetProperty("BasicAVSceneDescriptorsData")
            .EnumerateArray().Select(e => e.GetProperty("BXSOrBXSID").GetProperty("Header").GetString()));
        var speechScene = JsonDocument.Parse(first.Ports[bmsOut]).RootElement.GetProperty("BasicAVSceneDescriptorsData").EnumerateArray()
            .First(e => e.GetProperty("BXSOrBXSID").GetProperty("Header").GetString() == "OSD-BSS-V1.5").GetProperty("BXSOrBXSID").GetRawText();
        r["the speech is at the Avatar's mouth"] = Where(MpaiJson.FromJson<BasicSpeechSceneDescriptors>(speechScene).BasicSpeechSceneItems[0].ObjectSpaceTime);
        r["the Speech Scene is valid"] = Check("OSD/V1.5/data/BasicSpeechSceneDescriptors.json", speechScene);
        r["the Multimodal Scene is valid"] = Check("OSD/V1.5/data/BasicAudioVisualSceneDescriptors.json", first.Ports[bmsOut]);

        var turned = await Run(new() { [pov] = MpaiJson.ToJson(Viewer(0, 0, 90)), [sav] = speaking });
        r["the viewer turned left, the room kept"] = string.Join("; ", MpaiJson.FromJson<Basic3DModelSceneDescriptors>(turned.Ports[b3sOut])
            .Basic3DModelSceneItems.Select(i => $"{i.Id} at {Where(i.ModelObjectSpaceTime)}"));

        var seated = await Run(new() { [model] = MpaiJson.ToJson(Room(("table.glb", [3, 1, 0], 90), (TestAvatar.Model, [2, -1, 0], 180))), [sav] = speaking });
        r["a Scene that has the Avatar"] = string.Join("; ", MpaiJson.FromJson<Basic3DModelSceneDescriptors>(seated.Ports[b3sOut])
            .Basic3DModelSceneItems.Select(i => $"{i.Id} at {Where(i.ModelObjectSpaceTime)}"));

        var heard = await Run(new() { [audio] = MpaiJson.ToJson(new BasicAudioObject { BasicAudioObjectID = "rain" }), [sav] = speaking });
        r["with Audio, the Multimodal Scene holds"] = string.Join(", ", JsonDocument.Parse(heard.Ports[bmsOut]).RootElement.GetProperty("BasicAVSceneDescriptorsData")
            .EnumerateArray().Select(e => e.GetProperty("BXSOrBXSID").GetProperty("Header").GetString()));

        Expected.Match("sar-scene.json", r);
    }

    // A MEETING (the Avatar-Based Videoconference's Client Receiver, PDX -> SAS -> SAR):
    // each participant's Avatar placed by the Server at a seat, speaking in turn; all
    // stay in the Scene, and only the one speaking is heard.
    [Fact]
    public async Task AvatarsOfAMeeting()
    {
        var store = new AIF.Store.AmdStore(Repository.Amds);
        store.Scan();
        var ports = AimPortReader.Load(store, "1PAF-SAR-V1.6-I01");
        var sar = new SarAimProcessor("1PAF-SAR-V1.6-I01", ports);
        string sav = ports.Input("XRV-SAV-V1.0"), model = ports.Input("OSD-B3S-V1.5"), pov = ports.Input("OSD-OPV-V1.5"),
               b3sOut = ports.Output("OSD-B3S-V1.5"), bmsOut = ports.Output("OSD-BMS-V1.5");
        string Says(string id, string glb, double[] seat, double yaw) => MpaiJson.ToJson(new SpeakingAvatar
        {
            MInstanceID = "M1", SpeakingAvatarID = $"{id}-says",
            SpeakingAvatarData = new SpeakingAvatarData
            {
                Avatar = new Avatar
                {
                    AvatarID = id, AvatarSpaceTime = SarAimProcessor.At(seat, yaw),
                    AvatarData = new AvatarData { ModelOrModelID = [new AvatarModel { ModelID = glb }] }
                },
                SpeechObject = new BasicSpeechObject { BasicSpeechObjectID = $"{id}-speech", Data = new byte[3200] }
            }
        });
        async Task<Message> Run(Dictionary<string, string> inputs) => await sar.ProcessAsync(new Message { MessageId = "m", Ports = inputs });
        string Seen(Message m) => string.Join("; ", MpaiJson.FromJson<Basic3DModelSceneDescriptors>(m.Ports[b3sOut])
            .Basic3DModelSceneItems.Select(i => $"{i.Id} at {Where(i.ModelObjectSpaceTime)}"));
        string Heard(Message m)
        {
            var bss = JsonDocument.Parse(m.Ports[bmsOut]).RootElement.GetProperty("BasicAVSceneDescriptorsData").EnumerateArray()
                .First(e => e.GetProperty("BXSOrBXSID").GetProperty("Header").GetString() == "OSD-BSS-V1.5").GetProperty("BXSOrBXSID").GetRawText();
            var item = MpaiJson.FromJson<BasicSpeechSceneDescriptors>(bss).BasicSpeechSceneItems.Single();
            return $"{item.SpeechObject?.BasicSpeechObjectID} at {Where(item.ObjectSpaceTime)}";
        }

        var r = new Dictionary<string, string>();
        await Run(new() { [model] = MpaiJson.ToJson(Room(("table.glb", [2, 0, 0], 0))), [pov] = MpaiJson.ToJson(Viewer(0, 0, 0)) });
        var anna = await Run(new() { [sav] = Says("anna", "anna.glb", [2, 1, 0], -90) });
        r["Anna speaks"] = Seen(anna);
        r["Anna is heard"] = Heard(anna);
        var bob = await Run(new() { [sav] = Says("bob", "bob.glb", [2, -1, 0], 90) });
        r["Bob speaks"] = Seen(bob);
        r["Bob is heard"] = Heard(bob);
        var again = await Run(new() { [sav] = Says("anna", "anna.glb", [2.5, 1, 0], -90) });
        r["Anna, moved by the Server, speaks again"] = Seen(again);
        Expected.Match("sar-meeting.json", r);
    }
}
