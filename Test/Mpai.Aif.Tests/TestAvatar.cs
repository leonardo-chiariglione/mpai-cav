using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Aif.Tests;

// THE AVATAR A TEST GIVES a Module that renders with Response and Scene Rendering,
// which takes an Avatar to animate (the author, 2026/10/03), and the speech read
// back from the Portable Avatar (PAF-PAV-V1.6) it answers with.
public static class TestAvatar
{
    public const string Model = "cav-avatar.glb";

    public static ControllerApi.Datum Datum => new("OSD-B3O-V1.5", 1, AvatarUtterance.AvatarDatum(Model));

    // The speech of the Portable Avatar a run produced, or null.
    public static BasicSpeechObject? Speech(ControllerApi.Result r) =>
        r.ByType("PAF-PAV-V1.6") is { } json ? MpaiJson.FromJson<PortableAvatar>(json)?.Speech() : null;

    // A Portable Avatar that speaks: the Model by ID, the Speech (WAV data) and, if given, where it is.
    public static PortableAvatar Speaking(string id, byte[] speech, string model = Model, string? mInstance = "M1", SpaceTime? at = null) => new()
    {
        MInstanceID = mInstance, PortableAvatarID = id + "-says", AvatarSpaceTime = at,
        AvatarOrAvatarID = PortableAvatar.OfModel(model, id).AvatarOrAvatarID,
        SPOOrSPOID = new System.Text.Json.Nodes.JsonArray(System.Text.Json.Nodes.JsonNode.Parse(MpaiJson.ToJson(new BasicSpeechObject { BasicSpeechObjectID = id + "-speech", Data = speech })))
    };
}
