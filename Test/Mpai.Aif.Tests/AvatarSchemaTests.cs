using System.Text.Json;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Aif.Tests;

// THE AVATAR AND THE SPEAKING AVATAR, IN C# AND IN THEIR SCHEMAS (the author,
// 2026/10/03). Response and Scene Rendering takes an Avatar - the Model to
// animate - and outputs a Speaking Avatar (XRV-SAV), ready to play: the Avatar
// with its Speech and the Face and Body Descriptors that animate it. What the C#
// classes write is checked against the schemas; what is not valid says why.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class AvatarSchemaTests
{
    private static readonly IReadOnlyDictionary<string, Json.Schema.JsonSchema> All = AIF.Metadata.PublishedSchemas.At(Repository.Schemas);

    private static string Check(string schema, object value)
    {
        var s = All[Path.GetFullPath(Path.Combine(Repository.Schemas, schema))];
        using var doc = JsonDocument.Parse(MpaiJson.ToJson(value));
        Json.Schema.EvaluationResults r;
        lock (AIF.Metadata.PublishedSchemas.Lock)
            r = s.Evaluate(doc.RootElement, new Json.Schema.EvaluationOptions { OutputFormat = Json.Schema.OutputFormat.List });
        if (r.IsValid) return "valid";
        var first = r.Details.FirstOrDefault(d => d.Errors is { Count: > 0 });
        return first is null ? "not valid" : $"not valid: {first.InstanceLocation} {first.Errors!.First().Value}";
    }

    [Fact]
    public void AvatarsAsTheSchemasSayThem()
    {
        var model = Avatar.OfModel("avatars/thalia.glb", "thalia");
        var speech = BasicSpeechObject.FromData(new byte[3200], new SpeechQualifier
        {
            SpeechQualifierID = "q",
            Format = new SpeechFormat
            {
                ContentFormats = new SpeechContentFormats { RawData = new Pcm { SamplingFrequency = 16000, Precision = 16 } },
                TransportFormats = new SpeechTransportFormats { FileFormat = SpeechFileFormat.Wav }
            }
        });
        var face = new FaceDescriptorsObject { FaceDescriptorsObjectID = "face" };
        var body = BodyDescriptorsObject.FromContent("HIERARCHY\nROOT Hips\n", "BVH");
        var speaking = new SpeakingAvatar
        {
            SpeakingAvatarID = "thalia-says-hello",
            SpeakingAvatarData = new SpeakingAvatarData { Avatar = model.Speaking(speech, face, body), SpeechObject = speech }
        };

        Expected.Match("avatar-schemas.json", new Dictionary<string, string>
        {
            ["an Avatar that is only a Model, by ID"] = Check("PAF/V1.6/data/Avatar.json", model),
            ["a Speaking Avatar: the Avatar with its Speech, Face and Body Descriptors"] = Check("XRV1/V1.0/data/SpeakingAvatar.json", speaking),
            ["the Speaking Avatar keeps the Model"] = speaking.Avatar()?.ModelId() ?? "none",
            ["the Speaking Avatar's face and body"] = $"{(speaking.Face() is null ? "no face" : "face")}, {speaking.Body()?.GetContentFormat() ?? "no body"}"
        });
    }
}
