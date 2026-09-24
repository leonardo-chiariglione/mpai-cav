using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Aif.Api;

// The Speaking Avatar payload the UA renders: the machine speech (WAV bytes) and
// the machine Face Descriptors that drive the 3-D avatar; TranslatedText carries a
// spoken translation's text when relevant. Defined here in AIF/ControllerApi (alongside
// ControllerApi) so every User Agent and UaKit shares one type: a first-class
// Controller API type.
public sealed record SpeakingAvatar(
    byte[] MachineSpeechWav,
    FaceDescriptorsObject? FaceDescriptors,
    string? TranslatedText = null);
