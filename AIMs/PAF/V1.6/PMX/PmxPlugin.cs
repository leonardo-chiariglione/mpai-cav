using System.Collections.Generic;
using AIF.Controller;

namespace Mpai.Paf.Pmx;

// Plug-in for PAF-PMX-V1.6. No model dependencies: Create builds the processor with its
// ports. Discovered dynamically by the middleware provider (IAimPlugin).
public sealed class PmxPlugin : IAimPlugin
{
    public string AimName => "PAF-PMX-V1.6";

    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new PmxAimProcessor(AimName, ports);
}
