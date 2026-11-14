using System.Collections.Generic;

using AIF.Controller;

namespace Mpai.Xrv.Pti;

// THE PLUG-IN OF XRV-PTI-V1.0: what a Controller that obtains this AIM as a package (its L3 names the package; the Store approved its fingerprint) finds in
// Mpai.Xrv.Pti.dll and builds the AIM with (PackageAimProvider). The Controller names the AIM by its Instance.
public sealed class PtiPlugin : IAimPlugin
{
    public string AimName => "XRV-PTI-V1.0";

    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings) => new PtiAimProcessor(AimName);
}