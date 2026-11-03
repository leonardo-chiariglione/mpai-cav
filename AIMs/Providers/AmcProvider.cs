// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using System;
using System.Collections.Generic;

using AIF.Controller;
using AIF.Store;

using Mpai.Core;
using Mpai.Aims.Asr;   // AsrAimProcessor, AsrFactory
using Mpai.Mmc.Nlu;    // NluAimProcessor
using Mpai.Mmc.Spe;    // SpeAimProcessor, Wav2Vec2EmotionEstimator
using Mpai.Paf.Fpe;    // FpeAimProcessor, HSEmotionEstimator
using Mpai.Mmc.Pmx;    // PmxAimProcessor
using Mpai.Mmc.Edp;    // EdpAimProcessor, OllamaClient
using Mpai.Paf.Psd;    // PsdAimProcessor
using Mpai.Aims.Tts;   // TtsAimProcessor, TtsFactory
using Mpai.Paf.Gfd;    // GfdAimProcessor
using Mpai.Paf.Gbd;    // GbdAimProcessor

namespace Mpai.Providers;

// Leaf provider for the MMC-AMC-V2.5 Module (Affective Multimodal Conversation).
// The Controller builds MMC-AMC and the composites inside it (Personal Status Extraction, Response and Scene Rendering)
// from their L3s; this provider supplies ONLY the leaf AIMs the topologies name:
//   MMC-ASR - speech to text (Whisper)
//   MMC-NLU - meaning of the text and its Text Personal Status
//   MMC-SPE - the Personal Status of the speech (wav2vec2);  PAF-FPE - of the face (HSEmotion)
//   MMC-PMX - multiplexes the Personal Statuses into the Entity Personal Status
//   MMC-EDP - affective dialogue (local LLM via Ollama)
//   MMC-PDX, MMC-TTS, PAF-GFD, PAF-GBD, PAF-PMX - Response and Scene Rendering
// Acquisition and delivery are the User Agent's, not sub-AIMs.
public sealed class AmcProvider : IAimProvider, IDisposable
{
    private readonly AmdStore _store;
    private OllamaClient? _llm;
    private Wav2Vec2EmotionEstimator? _w2v2;
    private HSEmotionEstimator? _hse;

    public AmcProvider(AmdStore store) => _store = store;

    // WHAT THIS PROVIDER CAN MAKE. A Service composed of several providers asks
    // before it builds, and says at startup which Apps it cannot run.
    // The binary that implements it: measured under Zero Trust (M3223 3.2).
    public string? ImplementationOf(string aimName) => CanCreate(aimName) ? AimBinaries.Of(aimName) : null;

    public bool CanCreate(string aimName) =>
        aimName is "1MMC-ASR-V2.5-I01" or "1MMC-NLU-V2.5-I01" or "1MMC-SPE-V2.5-I01" or "1PAF-FPE-V1.6-I01" or "1MMC-PMX-V2.5-I01"
                or "1MMC-EDP-V2.5-I01" or "1MMC-PDX-V2.5-I01" or "1MMC-TTS-V2.5-I01" or "1PAF-GFD-V1.6-I01" or "1PAF-GBD-V1.6-I01"
                or "1PAF-PMX-V1.6-I01";

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage) =>
        Create(aimName, settings, storage, null, null, null);

    // With the Access the AIM declared: the language model, whose weights Ollama is given from Access.
    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage,
        AIF.SharedStorage.ISharedStorage? privateStorage, AIF.SharedStorage.IRuledStorage? moduleStorage, IAccessReader? access)
        => aimName switch
        {
            "1MMC-ASR-V2.5-I01" => new AsrAimProcessor(aimName, AsrFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            "1MMC-NLU-V2.5-I01" => new NluAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1MMC-SPE-V2.5-I01" => new SpeAimProcessor(aimName, W2v2(settings), AimPortReader.Load(_store, aimName)),
            "1PAF-FPE-V1.6-I01" => new FpeAimProcessor(aimName, Hse(settings), AimPortReader.Load(_store, aimName)),
            "1MMC-PMX-V2.5-I01" => new PmxAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1MMC-EDP-V2.5-I01" => new EdpAimProcessor(aimName, Llm(settings, access), AimPortReader.Load(_store, aimName)),
            "1MMC-PDX-V2.5-I01" => new PsdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1MMC-TTS-V2.5-I01" => new TtsAimProcessor(aimName, TtsFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            "1PAF-GFD-V1.6-I01" => new GfdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1PAF-GBD-V1.6-I01" => new GbdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1PAF-PMX-V1.6-I01" => new Mpai.Paf.Pmx.PmxAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            _ => throw new NotSupportedException($"AmcProvider does not provide '{aimName}'.")
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

    private Wav2Vec2EmotionEstimator W2v2(IReadOnlyDictionary<string, string> s)
    {
        if (_w2v2 is not null) return _w2v2;
        return _w2v2 = new Wav2Vec2EmotionEstimator(Mpai.Core.MpaiPaths.Given(s, "W2v2Model"));
    }

    private HSEmotionEstimator Hse(IReadOnlyDictionary<string, string> s)
    {
        if (_hse is not null) return _hse;
        return _hse = new HSEmotionEstimator(Mpai.Core.MpaiPaths.Given(s, "HseModel"));
    }

    // A model's path, as the settings give it; a relative one is the repository's.
    private static string Setting(IReadOnlyDictionary<string, string> s, string key, string fallback) =>
        s.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)
            ? (System.IO.Path.IsPathRooted(v) ? v : System.IO.Path.Combine(MpaiPaths.Root, v))
            : fallback;

    public void Dispose() { _llm?.Dispose(); _w2v2?.Dispose(); _hse?.Dispose(); }
}
