using System;
using System.Collections.Generic;

using AIF.Controller;
using AIF.Store;

namespace Mpai.Pgm;

// The AIMs of the first slice of PGM-AUA, as the Controller asks for them by AIM name. The language model, the speech recogniser and the speech synthesiser are given (or left to the
// defaults: Ollama with the model of the setting Llm.Model; a recogniser that recognises nothing; a synthesiser of silence).
public sealed class PgmProvider : IAimProvider
{
    public static readonly string[] Aims =
    [
        "1PGM-AUC-V1.0-I01", "1PGM-CXT-V1.0-I01", "1PGM-PRC-V1.0-I01", "1PGM-BKN-V1.0-I01", "1PGM-DAC-V1.0-I01", "1PGM-USR-V1.0-I01", "1PGM-PAL-V1.0-I01", "1PGM-AUF-V1.0-I01",
        "1PGM-PSD-V1.0-I01", "1PGM-MOG-V1.0-I01", "1PGM-EFD-V1.0-I01", "1PGM-EBD-V1.0-I01"
    ];

    private readonly AmdStore _store;
    private readonly ILanguageModel? _model;
    private readonly ISpeechRecogniser? _recogniser;
    private readonly ISpeechSynthesiser _voice;

    public PgmProvider(AmdStore store, ILanguageModel? model = null, ISpeechRecogniser? recogniser = null, ISpeechSynthesiser? voice = null)
    {
        _store = store; _model = model; _recogniser = recogniser; _voice = voice ?? new SilentSynthesiser();
    }

    public bool CanCreate(string aimName) => Array.IndexOf(Aims, aimName) >= 0;

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage) => Make(aimName, settings);

    public PgmAim Make(string aimName, IReadOnlyDictionary<string, string> settings)
    {
        AimPortReader Ports(string name) => AimPortReader.Load(_store, name);
        var persona = settings.TryGetValue(BknAim.PersonaSetting, out var p) && p.Length > 0 ? p : "a-user-persona";
        return aimName switch
        {
            "1PGM-AUC-V1.0-I01" => new AucAim(aimName, Ports(aimName)),
            "1PGM-CXT-V1.0-I01" => new CxtAim(aimName, Ports(aimName), _recogniser),
            "1PGM-PRC-V1.0-I01" => new PrcAim(aimName, Ports(aimName)),
            "1PGM-BKN-V1.0-I01" => new BknAim(aimName, Ports(aimName), _model ?? OllamaModel.From(settings), persona),
            "1PGM-DAC-V1.0-I01" => new DacAim(aimName, Ports(aimName)),
            "1PGM-USR-V1.0-I01" => new UsrAim(aimName, Ports(aimName)),
            "1PGM-PAL-V1.0-I01" => new PalAim(aimName, Ports(aimName)),
            "1PGM-PSD-V1.0-I01" => new PsdAim(aimName, Ports(aimName)),
            "1PGM-MOG-V1.0-I01" => new MogAim(aimName, Ports(aimName), persona),
            "1PGM-EFD-V1.0-I01" => new EfdAim(aimName, Ports(aimName)),
            "1PGM-EBD-V1.0-I01" => new EbdAim(aimName, Ports(aimName)),
            // The composite builds its Sub-AIMs from their own Metadata.
            "1PGM-AUF-V1.0-I01" => new AufAim(aimName, Ports(aimName),
                (PsdAim)Make("1PGM-PSD-V1.0-I01", settings), (MogAim)Make("1PGM-MOG-V1.0-I01", settings), (EfdAim)Make("1PGM-EFD-V1.0-I01", settings),
                (EbdAim)Make("1PGM-EBD-V1.0-I01", settings), _voice, settings.TryGetValue("Avatar.Id", out var a) && a.Length > 0 ? a : "a-user-avatar"),
            _ => throw new NotSupportedException($"PgmProvider does not provide '{aimName}'.")
        };
    }
}
