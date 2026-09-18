using System;
using System.Collections.Generic;

using AIF.Controller;
using AIF.Store;

using Mpai.Core;
using Mpai.Aims.Asr;   // AsrAimProcessor, AsrFactory
using Mpai.Aims.Ttt;   // TttAimProcessor, TttFactory
using Mpai.Paf.Psd;    // PsdAimProcessor
using Mpai.Aims.Tts;   // TtsAimProcessor, TtsFactory
using Mpai.Paf.Gfd;    // GfdAimProcessor

namespace Mpai.Providers;

// Leaf provider for the MMC-MAT-V2.5 Module (Multimodal Anonymous Translation).
// Supplies the leaf AIMs the L3 topology names:
//   MMC-ASR - speech to text (Whisper)
//   MMC-TTT - text-to-text translation (M2M100), to the Language Selector's target
//   PAF-PSD, MMC-TTS, PAF-GFD - Response and Scene Rendering (speaks in the target voice)
public sealed class MatProvider : IAimProvider, IDisposable
{
    private readonly AmdStore _store;
    public MatProvider(AmdStore store) => _store = store;

    // WHAT THIS PROVIDER CAN MAKE. A Service composed of several providers asks
    // before it builds, and says at startup which Apps it cannot run.
    public bool CanCreate(string aimName) =>
        aimName is "MMC-ASR-V2.5" or "MMC-TTT-V2.5" or "MMC-TTS-V2.5" or "PAF-PSD-V1.6" or "PAF-GFD-V1.6";
    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage)
        => aimName switch
        {
            "MMC-ASR-V2.5" => new AsrAimProcessor(aimName, AsrFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            "MMC-TTT-V2.5" => new TttAimProcessor(aimName, TttFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            "PAF-PSD-V1.6" => new PsdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "MMC-TTS-V2.5" => new TtsAimProcessor(aimName, TtsFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            "PAF-GFD-V1.6" => new GfdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            _ => throw new NotSupportedException($"MatProvider does not provide '{aimName}'.")
        };

    public void Dispose() { }
}
