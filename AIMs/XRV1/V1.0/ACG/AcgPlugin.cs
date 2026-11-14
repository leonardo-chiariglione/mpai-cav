using System.Collections.Generic;

using AIF.Controller;

namespace Mpai.Xrv.Acg;

// THE PLUG-IN OF XRV-ACG-V1.0: what a Controller that obtains this AIM as a package (its L3 names the package; the Store approved its fingerprint) finds in
// Mpai.Xrv.Acg.dll and builds the AIM with (PackageAimProvider). The Controller names the AIM by its Instance.
public sealed class AcgPlugin : IAimPlugin
{
    public string AimName => "XRV-ACG-V1.0";

    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings) => new AcgAimProcessor(AimName);
}