// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using System.Collections.Generic;

using AIF.Controller;

using Mpai.Aims.Tts;
using Mpai.Cae.Usc.Voice;

namespace Mpai.Mmc.Tsd;

// Plug-ins of MMC-TSD-V2.5 and of the OpenVoice instance of MMC-ESD-V2.5. The engines are built from the settings the Controller gives from the AccessSources of the L3
// (the Piper voices; the code, the configuration and the checkpoint of OpenVoice), as the plug-ins of Text-To-Speech and of Speech Model Creation do.
public sealed class TsdPlugin : IAimPlugin
{
    public string AimName => "MMC-TSD-V2.5";
    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new TsdAimProcessor(AimName, TtsFactory.Create(settings), new OpenVoiceWorker(settings), ports);
}

public sealed class EsdOpenVoicePlugin : IAimPlugin
{
    public string AimName => "MMC-ESD-V2.5";
    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new EsdOpenVoiceAimProcessor(AimName, new OpenVoiceWorker(settings), ports);
}
