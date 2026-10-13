using System.Collections.Generic;
using AIF.Controller;

namespace Mpai.Mmc.Scm;

// Plug-in for MMC-SCM-V2.5. No model dependencies: Create builds the processor with its
// ports. Discovered dynamically by the middleware provider (IAimPlugin).
public sealed class ScmPlugin : IAimPlugin
{
    public string AimName => "MMC-SCM-V2.5";

    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new ScmAimProcessor(AimName, ports);
}
