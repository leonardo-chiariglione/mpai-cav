using System.Collections.Generic;
using AIF.Controller;

namespace Mpai.Paf.Pdx;

// Plug-in for PAF-PDX-V1.6. No model dependencies: Create builds the processor with its
// ports. Discovered dynamically by the middleware provider (IAimPlugin).
public sealed class PdxPlugin : IAimPlugin
{
    public string AimName => "PAF-PDX-V1.6";

    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new PdxAimProcessor(AimName, ports);
}
