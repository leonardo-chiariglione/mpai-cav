using System;
using System.Collections.Generic;

using AIF.Controller;
using AIF.Store;

using Mpai.Aims.Asr;   // AsrAimProcessor, AsrFactory
using Mpai.Aims.Tts;   // TtsAimProcessor, TtsFactory
using Mpai.Mmc.Edp;    // EdpAimProcessor, OllamaClient
using Mpai.Mmc.Nlu;    // NluAimProcessor
using Mpai.Mmc.Scm;    // ScmAimProcessor
using Mpai.Paf.Gbd;    // GbdAimProcessor
using Mpai.Paf.Gfd;    // GfdAimProcessor
using Mpai.Paf.Pdx;    // PdxAimProcessor (Portable Avatar Demultiplexing)
using Mpai.Paf.Pmx;    // PmxAimProcessor (Portable Avatar Multiplexing)
using Mpai.Paf.Psd;    // PsdAimProcessor (MMC-PDX, Personal Status Demultiplexing)

namespace Mpai.Providers;

// Leaf provider for the PAF-VMS-V1.6 Module (Virtual Meeting Secretary).
// The Controller builds the VMS composite - and the Personal Status Display composite
// inside it - from their L3s; this provider supplies ONLY the leaf AIMs they name:
//   PAF-PDX - the participant's Portable Avatar, demultiplexed
//   MMC-ASR, MMC-NLU - what the participant said, recognised and understood
//   MMC-SCM - the Summary of the meeting
//   MMC-EDP (I02) - the secretary: keeps the Summary, answers when addressed (Ollama)
//   MMC-PDX, MMC-TTS, PAF-GFD, PAF-GBD, PAF-PMX - Personal Status Display, which forms
//     the VMS Portable Avatar
// No orchestration here; the User Agent drives the Module through the Controller.
public sealed class VmsProvider : IAimProvider, IDisposable
{
    private readonly AmdStore _store;
    private OllamaClient? _llm;

    public VmsProvider(AmdStore store) => _store = store;

    // The binary that implements it: measured under Zero Trust (M3223 3.2).
    public string? ImplementationOf(string aimName) => CanCreate(aimName) ? AimBinaries.Of(aimName) : null;

    public bool CanCreate(string aimName) =>
        aimName is "1PAF-PDX-V1.6-I01" or "1MMC-ASR-V2.5-I01" or "1MMC-NLU-V2.5-I01" or "1MMC-SCM-V2.5-I01"
                or "1MMC-EDP-V2.5-I02" or "1MMC-PDX-V2.5-I01" or "1MMC-TTS-V2.5-I01" or "1PAF-GFD-V1.6-I01"
                or "1PAF-GBD-V1.6-I01" or "1PAF-PMX-V1.6-I02";

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage)
        => aimName switch
        {
            "1PAF-PDX-V1.6-I01" => new PdxAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1MMC-ASR-V2.5-I01" => new AsrAimProcessor(aimName, AsrFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            "1MMC-NLU-V2.5-I01" => new NluAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1MMC-SCM-V2.5-I01" => new ScmAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1MMC-EDP-V2.5-I02" => new EdpAimProcessor(aimName, Llm(settings), AimPortReader.Load(_store, aimName),
                                                       persona: "the Secretary", avatarModel: Setting(settings, "VmsAvatarModel", "cav-avatar.glb")),
            "1MMC-PDX-V2.5-I01" => new PsdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1MMC-TTS-V2.5-I01" => new TtsAimProcessor(aimName, TtsFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            "1PAF-GFD-V1.6-I01" => new GfdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1PAF-GBD-V1.6-I01" => new GbdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1PAF-PMX-V1.6-I02" => new ContinuousPmx(aimName, AimPortReader.Load(_store, aimName)),
            _ => throw new NotSupportedException($"VmsProvider does not provide '{aimName}'.")
        };

    private static string Setting(IReadOnlyDictionary<string, string> s, string key, string fallback) =>
        s.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    // One Ollama client, model from settings (default llama3.2:3b), as LeafAimsProvider.
    private OllamaClient Llm(IReadOnlyDictionary<string, string> s)
    {
        if (_llm is not null) return _llm;
        string model = Setting(s, "OllamaModel", "llama3.2:3b");
        return _llm = s.TryGetValue("OllamaUrl", out var url) && !string.IsNullOrWhiteSpace(url)
            ? new OllamaClient(model, url)
            : new OllamaClient(model);
    }

    public void Dispose() { _llm?.Dispose(); }
}
