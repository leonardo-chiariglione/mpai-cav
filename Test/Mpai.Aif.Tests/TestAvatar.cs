using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Aif.Tests;

// THE AVATAR A TEST GIVES a Module that renders with Response and Scene Rendering,
// which takes an Avatar to animate (the author, 2026/10/03), and the speech read
// back from the Speaking Avatar (XRV-SAV-V1.0) it answers with.
public static class TestAvatar
{
    public const string Model = "cav-avatar.glb";

    public static ControllerApi.Datum Datum => new("PAF-AVT-V1.6", 1, AvatarUtterance.AvatarDatum(Model));

    // The speech of the Speaking Avatar a run produced, or null.
    public static BasicSpeechObject? Speech(ControllerApi.Result r) =>
        r.ByType("XRV-SAV-V1.0") is { } json ? MpaiJson.FromJson<SpeakingAvatar>(json)?.Speech() : null;
}
