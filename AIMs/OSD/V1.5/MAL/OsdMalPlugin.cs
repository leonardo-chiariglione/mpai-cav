using System.Collections.Generic;
using AIF.Controller;

namespace Mpai.Osd.Mal;

// Plug-in for OSD-MAL-V1.5. No model dependencies: Create builds the processor with its
// ports. Discovered dynamically by the middleware provider (IAimPlugin).
public sealed class OsdMalPlugin : IAimPlugin
{
    public string AimName => "OSD-MAL-V1.5";

    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new OsdMalAimProcessor(AimName, ports);
}
