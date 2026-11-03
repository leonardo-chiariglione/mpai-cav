// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using System;
using System.Collections.Generic;

using Mpai.Core;

namespace Mpai.Mmc.Tiq;

// Builds the BLIP-backed TIQ AIM from deployment settings.
//
// Settings: VisionModel, EncoderModel, DecoderModel, VocabFile
public static class TiqFactory
{
    public static ITiqAim Create(
        IReadOnlyDictionary<string, string> settings)
    {
        return new BlipTiqAim(
            new BlipTiqConfiguration
            {
                VisionModel = Setting(settings, "VisionModel"),
                EncoderModel = Setting(settings, "EncoderModel"),
                DecoderModel = Setting(settings, "DecoderModel"),
                VocabFile = Setting(settings, "VocabFile")
            });
    }

    private static string Setting(
        IReadOnlyDictionary<string, string> settings,
        string key)
    {
        if (!settings.TryGetValue(key, out var value) ||
            string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"MMC-TIQ-V2.5 setting '{key}' is missing.");
        }

        // A RELATIVE SETTING IS RESOLVED AGAINST THE APPLICATION'S OWN ROOT.
        // Every setting here names a file - a model, or the program that reads
        // one - and naming it absolutely binds the installation to one machine.
        // A user must be able to put the folder where they like and run it.
        return Mpai.Core.MpaiPaths.Resolve(value);
    }
}
