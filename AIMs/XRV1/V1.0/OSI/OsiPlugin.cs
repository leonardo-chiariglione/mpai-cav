using System.Collections.Generic;

using AIF.Controller;

namespace Mpai.Xrv.Osi;

// THE PLUG-IN OF XRV-OSI-V1.0: what a Controller that obtains this AIM as a package (its L3 names the package; the Store approved its fingerprint) finds in
// Mpai.Xrv.Osi.dll and builds the AIM with (PackageAimProvider). The Controller names the AIM by its Instance.
public sealed class OsiPlugin : IAimPlugin
{
    public string AimName => "XRV-OSI-V1.0";

    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings) => new OsiAimProcessor(AimName);
}