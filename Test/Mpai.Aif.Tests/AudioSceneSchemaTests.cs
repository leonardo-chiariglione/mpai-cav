using System.Text.Json;
using System.Text.Json.Nodes;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Aif.Tests;

// THE AUDIO AND SPEECH SCENES OF CAE-ASM, IN C# AND IN THEIR SCHEMAS (the author,
// 2026/10/02). The Acoustic Profile has an Object part and a Scene part; a Basic
// Audio Scene is made of Basic Audio Objects, with a UserPoV, a member's UserPoV,
// a Closed Space and the Scene's acoustics; a Basic Audio-Visual Scene (BMS) holds
// Basic Scenes only, of any medium, at least one (the author, 2026/10/02): a voice
// over music is the Basic Audio Scene of the music and the Basic Speech Scene of
// the voice; a Basic Object is not a member, nor is another BMS. A Basic Visual
// Scene holds Basic Visual Objects and is seen from its UserPoV. What the C#
// classes write is checked against the schemas; what is not valid says why.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class AudioSceneSchemaTests
{
    private static readonly IReadOnlyDictionary<string, Json.Schema.JsonSchema> All = AIF.Metadata.PublishedSchemas.At(Repository.Schemas);

    private static string Check(string schema, object value, string pointer = "")
    {
        var s = All[Path.GetFullPath(Path.Combine(Repository.Schemas, schema))];
        var json = MpaiJson.ToJson(value);
        using var doc = JsonDocument.Parse(json);
        Json.Schema.EvaluationResults r;
        lock (AIF.Metadata.PublishedSchemas.Lock)
            r = s.Evaluate(doc.RootElement, new Json.Schema.EvaluationOptions { OutputFormat = Json.Schema.OutputFormat.List });
        if (r.IsValid) return "valid";
        var first = r.Details.FirstOrDefault(d => d.Errors is { Count: > 0 });
        return first is null ? "not valid" : $"not valid: {first.InstanceLocation} {first.Errors!.First().Value}";
    }

    private static PointOfView User(double x) => new() { PointOfViewID = "user", CartPosition = [x, 0, 1.6], Orientation = [0, 0, 0] };
    private static SpaceTime At(string id, double x) => new()
    {
        SpaceTimeID = id,
        SpatialAttitude1 = new SpatialAttitude { ObjectSpatialAttitudeID = id + "-SA", Position = new Position { PositionID = id + "-P", CartPosition = [x, 2, 0] }, Orientation = new Orientation { OrientationID = id + "-O" } }
    };

    [Fact]
    public void ScenesAsTheSchemasSayThem()
    {
        var voice = new AcousticProfile
        {
            AcousticProfileID = "voice", FrequencyRange = new FrequencyRange { MinFrequencyHz = 80, MaxFrequencyHz = 8000 }, Loudness = -23,
            DirectionalPatterns = new Plot()
        };
        var room = new AcousticProfile { AcousticProfileID = "room", Absorption = 0.2, Diffusion = 0.4 };
        var music = new BasicAudioObject { BasicAudioObjectID = "music", UserPoV = User(0) };
        var bas = new BasicAudioSceneDescriptors
        {
            MInstanceID = "M", BasicAudioSceneDescriptorsID = "living-room", BASSpaceTime = At("bas", 0), AudioObjectCount = 1,
            UserPoV = User(0), ClosedSpace = new JsonArray("room-1"), AcousticProfile = room,
            BasicAudioSceneDescriptorsEntries = [new BasicAudioSceneEntry { AudioObjectSpaceTime = At("music", 1), UserPoV = User(0.5), AudioObjectIDOrAudioObject = music }]
        };
        var bms = new BasicAudioVisualSceneDescriptors
        {
            MInstanceID = "M", BasicAVSceneDescriptorsID = "voice-over-music", BAVSDescriptorsSpaceTime = At("bms", 0), AVObjectCount = 2,
            UserPoV = User(0), ClosedSpace = new JsonArray("room-1"), AcousticProfile = room,
            BasicAVSceneDescriptorsData =
            [
                new BasicAVSceneEntry { BXSSpaceTime = At("m", 1), BXSOrBXSID = bas },
                new BasicAVSceneEntry { BXSSpaceTime = At("v", -1), UserPoV = User(0.5), BXSOrBXSID = "BSS-voice" }
            ]
        };
        var objects = new BasicAudioVisualSceneDescriptors
        {
            MInstanceID = "M", BasicAVSceneDescriptorsID = "of-objects", BAVSDescriptorsSpaceTime = At("obj", 0), AVObjectCount = 1,
            BasicAVSceneDescriptorsData = [new BasicAVSceneEntry { BXSSpaceTime = At("m", 1), BXSOrBXSID = music }]
        };
        var bvs = new BasicVisualSceneDescriptors
        {
            MInstanceID = "M", BasicVisualSceneDescriptorsID = "camera", BVSDescriptorsSpaceTime = At("bvs", 0), VisualObjectCount = 1,
            UserPoV = User(0),
            BasicVisualSceneDescriptorsEntries = [new BasicVisualSceneEntry { VisualObjectSpaceTime = At("car", 8), VObjectIDOrVObject = new BasicVisualObject { BasicVisualObjectID = "car" } }]
        };
        var cav = new BasicAudioVisualSceneDescriptors
        {
            MInstanceID = "M", BasicAVSceneDescriptorsID = "as-CAV-builds-it", BAVSDescriptorsSpaceTime = At("cav", 0), AVObjectCount = 2,
            BasicAVSceneDescriptorsData = [new BasicAVSceneEntry { BXSSpaceTime = At("a", 0), BXSOrBXSID = "BAS-1" }, new BasicAVSceneEntry { BXSSpaceTime = At("s", 0), BXSOrBXSID = "BSS-1" }]
        };

        Expected.Match("audio-scene-schemas.json", new Dictionary<string, string>
        {
            ["an Object's Acoustic Profile, as the Object part"] = Check("CAE3/V1.0/data/AcousticProfile.json", voice),
            ["a Scene's Acoustic Profile, as the Scene part"] = Check("CAE3/V1.0/data/AcousticProfile.json", room),
            ["a Basic Audio Scene with its UserPoV, a member's UserPoV, a Closed Space and its acoustics"] = Check("OSD/V1.5/data/BasicAudioSceneDescriptors.json", bas),
            ["a voice over music, a BMS of a Basic Audio Scene and a Basic Speech Scene"] = Check("OSD/V1.5/data/BasicAudioVisualSceneDescriptors.json", bms),
            ["a BMS of a Basic Audio Object, refused"] = Check("OSD/V1.5/data/BasicAudioVisualSceneDescriptors.json", objects).StartsWith("not valid") ? "refused" : "accepted",
            ["a Basic Visual Scene of a Basic Visual Object, seen from its UserPoV"] = Check("OSD/V1.5/data/BasicVisualSceneDescriptors.json", bvs),
            ["a BMS as CAV builds it, of Basic Scenes"] = Check("OSD/V1.5/data/BasicAudioVisualSceneDescriptors.json", cav)
        });
    }
}
