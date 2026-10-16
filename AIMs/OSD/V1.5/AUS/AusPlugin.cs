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
            _ecapa ??= new SpeakerEmbedder(Get(settings, "EcapaModel", Mpai.Core.MpaiPaths.Model("ecapa-tdnn.onnx"))),
            ports);

    private static string Get(IReadOnlyDictionary<string, string> s, string k, string d) =>
        s.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : d;
}
