using System.Threading.Tasks;

namespace Mpai.Xrv.Osi;

// XRV-OSI-V1.0, Operator Status Interpretation: the Operator Descriptors of the Real and of the Virtual Environment in, the Operator Status the current Cue Point asks for out (Common: StatusInterpretationAim).
public sealed class OsiAimProcessor(string instanceId, Task? startGate = null) : StatusInterpretationAim(instanceId, Family.Operator, startGate)
{
}
