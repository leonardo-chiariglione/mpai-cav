// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
namespace Mmc.Tts.Piper;

public interface IPiperProcessRunner
{
    Task<byte[]> SynthesizeAsync(
        PiperSynthesisRequest request,
        CancellationToken cancellationToken = default);
}