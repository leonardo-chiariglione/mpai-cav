using System.Threading.Tasks;

namespace Mpai.Xrv.Pfi;

// XRV-PFI-V1.0, Performance Status Interpretation: the Performance Descriptors of the Real and of the Virtual Environment in, the Performance Status the current Cue Point asks for out (Common: StatusInterpretationAim).
public sealed class PfiAimProcessor(string instanceId, Task? startGate = null) : StatusInterpretationAim(instanceId, Family.Performance, startGate)
{
}
