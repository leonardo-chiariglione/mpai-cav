// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using System;
using System.Collections.Generic;

using AIF.Controller;
using AIF.Store;

using Mpai.Core;
using Mpai.Aims.Asr;   // AsrAimProcessor, AsrFactory
using Mpai.Mmc.Edp;    // EdpAimProcessor, OllamaClient
using Mpai.Aims.Ttt;   // TttAimProcessor, TttFactory
using Mpai.Paf.Psd;    // PsdAimProcessor
using Mpai.Aims.Tts;   // TtsAimProcessor, TtsFactory
using Mpai.Paf.Gfd;    // GfdAimProcessor
using Mpai.Paf.Gbd;         // GbdAimProcessor
using Mpai.Mmc.Tsd;         // TsdAimProcessor, EsdOpenVoiceAimProcessor
using Mpai.Cae.Usc.Voice;   // OpenVoiceWorker

namespace Mpai.Providers;

// Leaf provider of the AIMs that an L3 topology (the Audio Scene Management Module's, for one) names:
//   MMC-EDP - dialogue (local LLM via Ollama), using the running Summary as memory
//   MMC-ASR - speech to text (Whisper)
//   MMC-TTT - text-to-text translation (M2M100), to the Language Selector's target
//   MMC-PDX, MMC-TTS, PAF-GFD, PAF-GBD, PAF-PMX - Response and Scene Rendering (speaks in the target voice)
//   MMC-TSD, MMC-ESD (the OpenVoice instance) - Speech Translation with Descriptors (speaks the translation in the voice of the speaker)
public sealed class LeafAimsProvider : IAimProvider, IDisposable
{
    private readonly AmdStore _store;
    private OllamaClient? _llm;
    private Mpai.Mmc.Etd.GoEmotionsClassifier? _goEmotions;
    private Mpai.Mmc.Spe.Wav2Vec2EmotionEstimator? _w2v2;
    private Mpai.Paf.Fpe.HSEmotionEstimator? _hse;
    private Mpai.Paf.Ebd.PoseLandmarker? _pose;
    public LeafAimsProvider(AmdStore store) => _store = store;

    // WHAT THIS PROVIDER CAN MAKE. A Service composed of several providers asks
    // before it builds, and says at startup which Apps it cannot run.
    // The binary that implements it: measured under Zero Trust (M3223 3.2).
    public string? ImplementationOf(string aimName) => CanCreate(aimName) ? AimBinaries.Of(aimName) : null;

    public bool CanCreate(string aimName) =>
        aimName is "1MMC-ASR-V2.5-I01" or "1MMC-EDP-V2.5-I01" or "1MMC-TTT-V2.5-I01" or "1MMC-TTS-V2.5-I01" or "1MMC-PDX-V2.5-I01" or "1PAF-GFD-V1.6-I01"
                or "1PAF-GBD-V1.6-I01" or "1PAF-PMX-V1.6-I01" or "1MMC-TSD-V2.5-I01" or "1MMC-ESD-V2.5-I02"
                or "1MMC-ETD-V2.5-I01" or "1MMC-PTI-V2.5-I01" or "1MMC-ESD-V2.5-I03" or "1MMC-PSI-V2.5-I01" or "1PAF-EFD-V1.6-I02" or "1PAF-PFI-V1.6-I01"
                or "1PAF-EBD-V1.6-I01" or "1PAF-PBI-V1.6-I01" or "1MMC-PMX-V2.5-I01" or "1MMC-NLU-V2.5-I01";
    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage) =>
        Create(aimName, settings, storage, null, null, null);

    // With the Access the AIM declared: the language model, whose weights Ollama is given from Access.
    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage,
        AIF.SharedStorage.ISharedStorage? privateStorage, AIF.SharedStorage.IRuledStorage? moduleStorage, IAccessReader? access)
        => aimName switch
        {
            "1MMC-ASR-V2.5-I01" => new AsrAimProcessor(aimName, AsrFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            "1MMC-EDP-V2.5-I01" => new EdpAimProcessor(aimName, Llm(settings, access), AimPortReader.Load(_store, aimName)),
            "1MMC-TTT-V2.5-I01" => new TttAimProcessor(aimName, TttFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            "1MMC-PDX-V2.5-I01" => new PsdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1MMC-TTS-V2.5-I01" => new TtsAimProcessor(aimName, TtsFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            "1PAF-GFD-V1.6-I01" => new GfdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1PAF-GBD-V1.6-I01" => new GbdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1PAF-PMX-V1.6-I01" => new Mpai.Paf.Pmx.PmxAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1MMC-TSD-V2.5-I01" => new TsdAimProcessor(aimName, TtsFactory.Create(settings), new OpenVoiceWorker(settings), AimPortReader.Load(_store, aimName)),
            "1MMC-ESD-V2.5-I02" => new EsdOpenVoiceAimProcessor(aimName, new OpenVoiceWorker(settings), AimPortReader.Load(_store, aimName)),
            "1MMC-ETD-V2.5-I01" => new Mpai.Mmc.Etd.EtdAimProcessor(aimName, _goEmotions ??= Mpai.Mmc.Etd.EtdFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            "1MMC-PTI-V2.5-I01" => new Mpai.Mmc.Pti.PtiAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1MMC-ESD-V2.5-I03" => new Mpai.Mmc.Spe.EsdAffectAimProcessor(aimName, _w2v2 ??= new Mpai.Mmc.Spe.Wav2Vec2EmotionEstimator(MpaiPaths.Given(settings, "W2v2Model")), AimPortReader.Load(_store, aimName)),
            "1MMC-PSI-V2.5-I01" => new Mpai.Mmc.Psi.PsiAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1PAF-EFD-V1.6-I02" => new Mpai.Paf.Fpe.EfdAffectAimProcessor(aimName, _hse ??= new Mpai.Paf.Fpe.HSEmotionEstimator(MpaiPaths.Given(settings, "HseModel")), AimPortReader.Load(_store, aimName)),
            "1PAF-PFI-V1.6-I01" => new Mpai.Paf.Pfi.PfiAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1PAF-EBD-V1.6-I01" => new Mpai.Paf.Ebd.EbdAimProcessor(aimName, _pose ??= new Mpai.Paf.Ebd.PoseLandmarker(MpaiPaths.Given(settings, "PoseModel")), AimPortReader.Load(_store, aimName)),
            "1PAF-PBI-V1.6-I01" => new Mpai.Paf.Pbi.PbiAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1MMC-PMX-V2.5-I01" => new Mpai.Mmc.Pmx.PmxAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1MMC-NLU-V2.5-I01" => new Mpai.Mmc.Nlu.NluAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            _ => throw new NotSupportedException($"LeafAimsProvider does not provide '{aimName}'.")
        };

    // One Ollama client, model from settings (default llama3.2:3b).
    private OllamaClient Llm(IReadOnlyDictionary<string, string> s, IAccessReader? access)
    {
        var client = LlmFor(s);
        client.UseAccess(access);
        return client;
    }

    private OllamaClient LlmFor(IReadOnlyDictionary<string, string> s)
    {
        if (_llm is not null) return _llm;
        string model = s.TryGetValue("OllamaModel", out var m) && !string.IsNullOrWhiteSpace(m) ? m : "llama3.2:3b";
        // Where Ollama is: OllamaUrl, or this machine's.
        return _llm = s.TryGetValue("OllamaUrl", out var url) && !string.IsNullOrWhiteSpace(url)
            ? new OllamaClient(model, url)
            : new OllamaClient(model);
    }

    public void Dispose() { _llm?.Dispose(); _goEmotions?.Dispose(); _w2v2?.Dispose(); _hse?.Dispose(); _pose?.Dispose(); }
}
