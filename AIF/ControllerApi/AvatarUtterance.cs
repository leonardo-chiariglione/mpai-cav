using System.Linq;
using System.Text.Json.Nodes;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Aif.Api;

// One utterance of the avatar, as the UA presents it: the machine speech (WAV bytes),
// the machine Face Descriptors and Body Descriptors that drive the 3-D avatar;
// TranslatedText carries a spoken translation's text when relevant. Defined here in
// AIF/ControllerApi (alongside ControllerApi) so every User Agent and UaKit shares one
// type: a first-class Controller API type - not an MPAI data type. What a Module gives it
// to present is a Portable Avatar (PAF-PAV-V1.6).
public sealed record AvatarUtterance(
    byte[] MachineSpeechWav,
    FaceDescriptorsObject? FaceDescriptors,
    string? TranslatedText = null,
    BodyDescriptorsObject? BodyDescriptors = null)
{
    // THE AVATAR MODEL THE CLIENT HOLDS, as the datum (OSD-B3O-V1.5, a Basic 3D Model Object) a Module that renders
    // with Response and Scene Rendering takes: the Model by its identifier. The client
    // holds the model itself and draws it; the Module animates it and gives it a voice.
    public static string AvatarDatum(string modelId) =>
        new JsonObject { ["Header"] = "OSD-B3O-V1.5", ["Basic3DModelObjectID"] = modelId }.ToJsonString();

    // THE WORDS OF A PORTABLE AVATAR THAT CARRIES NO SPEECH: its Text Object, as Answer to Multimodal
    // Question gives its answer - the machine's Portable Avatar, saying it in text. A client that cannot
    // play speech from it shows the words and has them spoken (it asks the Controller: it renders no
    // AIM's work itself). Null when the Portable Avatar holds no Text Object, or one with no words.
    // The language (ISO 639-1) its Text Object says it is in, in its Text Qualifier; null when it says none.
    public static string? LanguageOfPortableAvatar(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || JsonNode.Parse(json) is not JsonObject pav) return null;
        if (pav["TextObject"] is not JsonObject text) return null;
        try { return MpaiJson.FromJson<BasicTextObject>(text.ToJsonString())?.TextQualifier?.Attributes?.Language?.LanguageCode; }
        catch { return null; }
    }

    public static string? TextOfPortableAvatar(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || JsonNode.Parse(json) is not JsonObject pav) return null;
        if (pav["TextObject"] is not JsonObject text) return null;
        try
        {
            var words = MpaiJson.FromJson<BasicTextObject>(text.ToJsonString())?.GetText();
            return string.IsNullOrWhiteSpace(words) ? null : words;
        }
        catch { return null; }
    }

    // A PORTABLE AVATAR (PAF-PAV-V1.6), as Response and Scene Rendering - or Portable Avatar Multiplexing, the Virtual Meeting Secretary's - outputs it,
    // unpacked for presentation: the first Speech Object it carries, and its Face and Body Descriptors Objects. Null when it carries no speech.
    public static AvatarUtterance? FromPortableAvatar(string? json, string? text = null)
    {
        if (string.IsNullOrWhiteSpace(json) || JsonNode.Parse(json) is not JsonObject pav) return null;
        T? Part<T>(JsonNode? node) where T : class
        {
            try { return node is JsonObject o ? MpaiJson.FromJson<T>(o.ToJsonString()) : null; }
            catch { return null; }
        }
        var speech = (pav["SPOOrSPOID"] as JsonArray)?.Select(Part<BasicSpeechObject>).FirstOrDefault(s => s is not null);
        if (speech?.Data is not { Length: > 0 } wav) return null;
        return new AvatarUtterance(wav, Part<FaceDescriptorsObject>(pav["FaceDescriptorsObject"]), text, Part<BodyDescriptorsObject>(pav["BodyDescriptorsObject"]));
    }
}
