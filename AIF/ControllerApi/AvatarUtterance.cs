using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Aif.Api;

// One utterance of the avatar, as the UA presents it: the machine speech (WAV bytes)
// and the machine Face Descriptors that drive the 3-D avatar; TranslatedText carries a
// spoken translation's text when relevant. Defined here in AIF/ControllerApi (alongside
// ControllerApi) so every User Agent and UaKit shares one type: a first-class
// Controller API type - not an MPAI data type. (Named SpeakingAvatar until 2026/10/03;
// the name is the MPAI Speaking Avatar's, XRV-SAV-V1.0, Mpai.Core.OSD.SpeakingAvatar.)
public sealed record AvatarUtterance(
    byte[] MachineSpeechWav,
    FaceDescriptorsObject? FaceDescriptors,
    string? TranslatedText = null);
