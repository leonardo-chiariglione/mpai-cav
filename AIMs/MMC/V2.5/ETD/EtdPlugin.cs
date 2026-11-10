// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using System.Collections.Generic;

using AIF.Controller;

using Mpai.Core;

namespace Mpai.Mmc.Etd;

// Plug-in of MMC-ETD-V2.5: the classifier is built from the files that the Controller gives from the AccessSources of the L3.
public sealed class EtdPlugin : IAimPlugin
{
    private GoEmotionsClassifier? _model;
    public string AimName => "MMC-ETD-V2.5";
    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new EtdAimProcessor(AimName, _model ??= EtdFactory.Create(settings), ports);
}

public static class EtdFactory
{
    public static GoEmotionsClassifier Create(IReadOnlyDictionary<string, string> s)
        => new(MpaiPaths.Given(s, "GoEmotionsModel"), MpaiPaths.Given(s, "GoEmotionsVocab"), MpaiPaths.Given(s, "GoEmotionsMerges"));
}
