// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using System.Collections.Generic;
using AIF.Controller;
using Mpai.Core;
using Mpai.Core.OSD;          // SubjectGallery

namespace Mpai.Mmc.Sir;

// Plug-in for Speaker Instance Recognition. Builds and caches its own ECAPA speaker
// embedder and the SubjectGallery it matches against - each path from settings,
// defaulting to the current location. Discovered dynamically (IAimPlugin).
public sealed class SirPlugin : IAimPlugin
{
    private SpeakerEmbedder? _ecapa;
    private SubjectGallery?  _gallery;

    public string AimName => "MMC-SIR-V2.5";

    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings) => Create(ports, settings, null);

    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings, IAccessReader? access)
        => new SirAimProcessor(
            AimName,
            _ecapa   ??= new SpeakerEmbedder(Mpai.Core.MpaiPaths.Given(settings, "EcapaModel")),
            _gallery ??= SubjectGallery.Load(new AIF.SharedStorage.FileSharedStorage(Mpai.Core.MpaiPaths.SharedStorage, AimName, "local")),
            ports, null, access);
    private static string Get(IReadOnlyDictionary<string,string> s, string k, string d) => s.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : d;
}
