using System.Collections.Generic;

using AIF.Controller;

namespace Mpai.Mmc.Cts;

// Plug-in for Column Text Selection: no model, no setting. Discovered dynamically (IAimPlugin).
public sealed class CtsPlugin : IAimPlugin
{
    public string AimName => "MMC-CTS-V2.5";

    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new CtsAimProcessor(AimName, ports);
}
