// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using System.Collections.Generic;

using AIF.Controller;
using Mpai.Mmc.Sir;

namespace Mpai.Osd.Aus;

// Plug-in for Audio Segmentation. Builds and caches the ECAPA speaker embedder (the model
// path from settings, defaulting to Models\ecapa-tdnn.onnx). Discovered dynamically (IAimPlugin).
public sealed class AusPlugin : IAimPlugin
{
    private SpeakerEmbedder? _ecapa;

    public string AimName => "OSD-AUS-V1.5";

    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new AusAimProcessor(
            AimName,
            _ecapa ??= new SpeakerEmbedder(Mpai.Core.MpaiPaths.Given(settings, "EcapaModel")),
            ports);

    private static string Get(IReadOnlyDictionary<string, string> s, string k, string d) =>
        s.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : d;
}
