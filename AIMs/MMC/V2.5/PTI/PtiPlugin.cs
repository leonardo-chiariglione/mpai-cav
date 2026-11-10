using System.Collections.Generic;

using AIF.Controller;

namespace Mpai.Mmc.Pti;

// Plug-in of MMC-PTI-V2.5: it reads no file; the mapping is code.
public sealed class PtiPlugin : IAimPlugin
{
    public string AimName => "MMC-PTI-V2.5";
    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings) => new PtiAimProcessor(AimName, ports);
}
