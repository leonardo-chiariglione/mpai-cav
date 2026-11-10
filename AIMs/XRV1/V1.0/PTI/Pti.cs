using System.Threading.Tasks;

namespace Mpai.Xrv.Pti;

// XRV-PTI-V1.0, Participant Status Interpretation: the Participant Descriptors of the Real and of the Virtual Environment in, the Participant Status the current Cue Point asks for out (Common: StatusInterpretationAim).
public sealed class PtiAimProcessor(string instanceId, Task? startGate = null) : StatusInterpretationAim(instanceId, Family.Participant, startGate)
{
}
