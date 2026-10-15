using System.Text.Json;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Aif.Tests;

// THE AVATAR AND THE SPEAKING AVATAR, IN C# AND IN THEIR SCHEMAS (the author,
// 2026/10/03). Response and Scene Rendering takes an Avatar - the Model to
// animate - and outputs a Portable Avatar (PAF-PAV), ready to play: the Avatar
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
        var model = PortableAvatar.OfModel("avatars/thalia.glb", "thalia");
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
        var speaking = model.Speaking(speech, face, body);

        Expected.Match("avatar-schemas.json", new Dictionary<string, string>
        {
            ["a Portable Avatar that is only a Model, by ID"] = Check("PAF/V1.6/data/PortableAvatar.json", model),
            ["a Portable Avatar with its Speech, Face and Body Descriptors"] = Check("PAF/V1.6/data/PortableAvatar.json", speaking),
            ["the Portable Avatar keeps the Model"] = speaking.ModelId() ?? "none",
            ["the Portable Avatar's face and body"] = $"{(speaking.FaceDescriptorsObject is null ? "no face" : "face")}, {speaking.BodyDescriptorsObject?.GetContentFormat() ?? "no body"}"
        });
    }
}
