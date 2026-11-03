// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using System;
using System.Collections.Generic;
using System.Linq;

using AIF.Controller;
using AIF.Store;

using Mpai.Core;
using Mpai.Aims.Asr;   // AsrAimProcessor, AsrFactory
using Mpai.Mmc.Edp;    // EdpAimProcessor, OllamaClient
using Mpai.Mmc.Jsi;    // JsiAimProcessor, JsiConfiguration
using Mpai.Aims.Tts;   // TtsAimProcessor, TtsFactory

namespace Mpai.Providers;

// Leaf provider for the MMC-VCI-V2.5 Module (Voice Command Interface). The Controller builds the composite from its L3 (1MMC-VCI-V2.5-I01); this provider supplies ONLY the
// leaf AIMs its topology names:
//   MMC-ASR - speech to text (Whisper)
//   MMC-JSI - the JSON Interface: the request to a command, the response to the Response Hint. The same AIM for every application: which application is the Access of its L3
//             (1MMC-JSI-V2.5-I02 is Audio Scene Management's; I01 has no application and sends no command)
//   MMC-EDP - the dialogue, as the L3 I03 has it: it says the Response Hint (local LLM via Ollama)
//   MMC-TTS - text to speech (Piper)
// Acquisition (the microphone) and delivery (the loudspeaker) are the User Agent's, not sub-AIMs.
public sealed class VciProvider : IAimProvider, IDisposable
{
    private readonly AmdStore _store;
    private readonly StructuredModel? _model;
    private OllamaClient? _llm;

    public VciProvider(AmdStore store) => _store = store;

    // With a language model of its own, instead of Ollama's: for a test, and for whoever has another.
    public VciProvider(AmdStore store, StructuredModel model) { _store = store; _model = model; }

    public string? ImplementationOf(string aimName) => CanCreate(aimName) ? AimBinaries.Of(aimName) : null;

    public bool CanCreate(string aimName) =>
        aimName is "1MMC-ASR-V2.5-I01" or "1MMC-JSI-V2.5-I01" or "1MMC-JSI-V2.5-I02" or "1MMC-EDP-V2.5-I03" or "1MMC-TTS-V2.5-I01";

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage) =>
        Create(aimName, settings, storage, null, null, null);

    // With the Access the AIM declared: the language model, whose weights Ollama is given from Access; the schemas and the guide of the application.
    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage,
        AIF.SharedStorage.ISharedStorage? privateStorage, AIF.SharedStorage.IRuledStorage? moduleStorage, IAccessReader? access)
        => aimName switch
        {
            "1MMC-ASR-V2.5-I01" => new AsrAimProcessor(aimName, AsrFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            "1MMC-JSI-V2.5-I01" or "1MMC-JSI-V2.5-I02" => new JsiAimProcessor(aimName, AimPortReader.Load(_store, aimName), Configuration(aimName, settings), Model(settings, access)),
            "1MMC-EDP-V2.5-I03" => new EdpAimProcessor(aimName, Llm(settings, access), AimPortReader.Load(_store, aimName)),
            "1MMC-TTS-V2.5-I01" => new TtsAimProcessor(aimName, TtsFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            _ => throw new NotSupportedException($"VciProvider does not provide '{aimName}'.")
        };

    // The application of a JSON Interface: the addresses its Metadata gives the schemas, and the files Access holds for them (the Settings).
    private JsiConfiguration Configuration(string aimName, IReadOnlyDictionary<string, string> settings)
    {
        var resolved = _store.FindByAimName(aimName);
        var entries = resolved is null ? [] : AccessEntry.ReadFrom(_store.GetAMD(resolved).RootElement);
        string UriOf(string setting) => entries.FirstOrDefault(e => e.Setting == setting && !e.IsPlaceholder)?.Uri ?? "";
        return JsiConfiguration.From(settings, UriOf("CommandSchema"), UriOf("ResponseSchema"));
    }

    private StructuredModel Model(IReadOnlyDictionary<string, string> settings, IAccessReader? access)
    {
        if (_model is not null) return _model;
        var client = Llm(settings, access);
        return (system, turns, schema) => client.ChatSchemaAsync(system, turns, schema);
    }

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
        return _llm = s.TryGetValue("OllamaUrl", out var url) && !string.IsNullOrWhiteSpace(url)
            ? new OllamaClient(model, url)
            : new OllamaClient(model);
    }

    public void Dispose() { _llm?.Dispose(); }
}
