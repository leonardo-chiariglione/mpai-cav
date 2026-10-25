// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using System.Collections.Generic;
using AIF.Controller;
using Mpai.Core;
using Mpai.Osd.VisualScene;   // ScrfdFaceDetector
using Mpai.Core.OSD;          // SubjectGallery

namespace Mpai.Paf.Fir;

// Plug-in for Face Instance Recognition. Builds and caches its own SCRFD detector,
// ArcFace recogniser and the SubjectGallery it matches against - each path from
// settings, defaulting to the current location. Discovered dynamically (IAimPlugin).
public sealed class FirPlugin : IAimPlugin
{
    private ScrfdFaceDetector? _scrfd;
    private ArcFaceRecogniser? _arcFace;
    private SubjectGallery?    _gallery;

    public string AimName => "PAF-FIR-V1.6";

    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings) => Create(ports, settings, null);

    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings, IAccessReader? access)
        => new FirAimProcessor(
            AimName,
            _scrfd   ??= new ScrfdFaceDetector(Mpai.Core.MpaiPaths.Given(settings, "ScrfdModel")),
            _arcFace ??= new ArcFaceRecogniser(Mpai.Core.MpaiPaths.Given(settings, "ArcFaceModel")),
            _gallery ??= SubjectGallery.Load(new AIF.SharedStorage.FileSharedStorage(Mpai.Core.MpaiPaths.SharedStorage, AimName, "local")),
            ports, null, access);
    private static string Get(IReadOnlyDictionary<string,string> s, string k, string d) => s.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : d;
}
