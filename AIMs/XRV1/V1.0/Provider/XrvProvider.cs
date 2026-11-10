using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;
using AIF.SharedStorage;

using Mpai.Xrv.Acg;
using Mpai.Xrv.Osi;
using Mpai.Xrv.Pfi;
using Mpai.Xrv.Pti;

namespace Mpai.Xrv;

// THE AIMs OF XRV-LTP V1.0 that are software so far, as the Controller asks for them by their AIM Instance: Action Generation and the Performance, Participant and Operator Status
// Interpretation AIMs. (Environment Description and the two Experience Generation AIMs, and the Live Theatrical Performance Composite, are for later stages.)
public sealed class XrvProvider : IAimProvider
{
    public const string Acg = "1XRV-ACG-V1.0-I01", Pfi = "1XRV-PFI-V1.0-I01", Pti = "1XRV-PTI-V1.0-I01", Osi = "1XRV-OSI-V1.0-I01";

    private readonly Task? gate;
    // gate: a task the AIMs wait for before they begin to read their Ports (a test lets Messages pile up on a Port, then releases it). Null: they begin at once.
    public XrvProvider(Task? gate = null) => this.gate = gate;

    public bool CanCreate(string aimName) => aimName is Acg or Pfi or Pti or Osi;

    public string? ImplementationOf(string aimName) => aimName switch
    {
        Acg => typeof(AcgAimProcessor).Assembly.Location,
        Pfi => typeof(PfiAimProcessor).Assembly.Location,
        Pti => typeof(PtiAimProcessor).Assembly.Location,
        Osi => typeof(OsiAimProcessor).Assembly.Location,
        _ => null
    };

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, ISharedStorage? storage) => aimName switch
    {
        Acg => new AcgAimProcessor(aimName, gate),
        Pfi => new PfiAimProcessor(aimName, gate),
        Pti => new PtiAimProcessor(aimName, gate),
        Osi => new OsiAimProcessor(aimName, gate),
        _ => throw new ArgumentException($"{aimName} is not an AIM this provider builds.")
    };
}
