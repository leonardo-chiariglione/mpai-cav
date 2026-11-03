// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using System.Collections.Generic;
using AIF.Controller;
using Mpai.Osd.VisualScene;

namespace Mpai.Cve.Vsi;

// Plug-in for CVE-VSI-V1.0. Builds and caches its own model instance (loaded once, reused),
// with the model path taken from settings and defaulting to the current location -
// so behaviour is unchanged now and the path becomes portable via settings later.
public sealed class VsiPlugin : IAimPlugin
{
    private ScrfdFaceDetector? _dep;
    public string AimName => "CVE-VSI-V1.0";
    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new CveVsiAimProcessor(AimName, _dep ??= new ScrfdFaceDetector(Mpai.Core.MpaiPaths.Given(settings, "ScrfdModel")), ports);
    private static string Get(IReadOnlyDictionary<string,string> s, string k, string d) => s.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : d;
}
