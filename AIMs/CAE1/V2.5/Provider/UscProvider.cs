using System;
using System.Collections.Generic;

using AIF.Controller;
using AIF.SharedStorage;
using AIF.Store;

using Mpai.Aims.Tts;
using Mpai.Cae.Usc.Arp;
using Mpai.Cae.Usc.Eae;
using Mpai.Cae.Usc.Ees;
using Mpai.Cae.Usc.Smc;
using Mpai.Cae.Usc.Sra;
using Mpai.Cae.Usc.Voice;

namespace Mpai.Cae.Usc;

// THE AIMs OF CAE-USC V2.5 that are software so far - the Speech Restoration System (Speech Model Creation, Speech Restoration Assembly, the Text To Speech it uses), Emotion
// Enhanced Speech (its five SubAIMs), Audio Recording Preservation (its five SubAIMs) and Enhanced Audioconference Experience (its six SubAIMs) - as the Controller asks for them by their AIM Instance. An AIM host builds this provider from the AIM Metadata it
// holds (AIF.AimHost --provider <assembly>:Mpai.Cae.Usc.UscProvider).
//
// Text To Speech is MPAI-MMC's. The setting "Tts" of its AIM Instance: "stand-in" (the default here) gives the stand-in that makes a tone from the Speech Model; "piper" gives
// the Piper AIM of MPAI-MMC (a voice file: PiperExecutable, VoiceModel, VoiceConfig), which does not use the Speech Model yet.
public sealed class UscProvider : IAimProvider
{
    public const string Smc = "1CAE-SMC-V2.5-I01", Sra = "1CAE-SRA-V2.5-I01", Tts = "1MMC-TTS-V2.5-I01",
                        Sfa = "1CAE-SFA-V2.5-I01", Sfb = "1CAE-SFB-V2.5-I01", Efp = "1CAE-EFP-V2.5-I01", Pei = "1CAE-PEI-V2.5-I01", Nei = "1CAE-NEI-V2.5-I01",
                        Aap = "1CAE-AAP-V2.5-I01", Vap = "1CAE-VAP-V2.5-I01", Tic = "1CAE-TIC-V2.5-I01", Tar = "1CAE-TAR-V2.5-I01", Pap = "1CAE-PAP-V2.5-I01",
                        Aat = "1CAE-AAT-V2.5-I01", Sfd = "1CAE-SFD-V2.5-I01", Sds = "1CAE-SDS-V2.5-I01", Ncm = "1CAE-NCM-V2.5-I01", Ast = "1CAE-AST-V2.5-I01", Amx = "1CAE-AMX-V2.5-I01";

    private readonly AmdStore _amds;
    public UscProvider(AmdStore amds) => _amds = amds;

    // The setting "SpeechModel": "stand-in" (the default) is the three numbers of the stand-in; "openvoice" is the tone-colour embedding of OpenVoice V2, made by Speech Model Creation and spoken in by the Piper Text To Speech.
    private static bool OpenVoiceModel(IReadOnlyDictionary<string, string> settings) => settings.GetValueOrDefault("SpeechModel", "stand-in") == "openvoice";

    public bool CanCreate(string aimName) => aimName is Smc or Sra or Tts or Sfa or Sfb or Efp or Pei or Nei or Aap or Vap or Tic or Tar or Pap or Aat or Sfd or Sds or Ncm or Ast or Amx;

    public string? ImplementationOf(string aimName) => CanCreate(aimName) ? typeof(UscProvider).Assembly.Location : null;

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, ISharedStorage? storage)
    {
        var ports = AimPortReader.Load(_amds, aimName);
        return aimName switch
        {
            // The input is the Speech Segments for Modelling (the first Basic Speech Object Port), the output the Neural Network Speech Model.
            Smc => new SmcAimProcessor(aimName, ports.Input("OSD-BSO-V1.5"), ports.Output("AIP-MLO-V1.0"),
                       OpenVoiceModel(settings) ? new OpenVoiceSpeechTrainer(new OpenVoiceWorker(settings)) : new StandInSpeechTrainer()),
            // The Damaged Segment is the first Basic Speech Object input, the Synthesised Speech the second.
            Sra => new SraAimProcessor(aimName, ports.Input("OSD-BSO-V1.5", 1), ports.Input("CAE-DLS-V2.5"), ports.Input("OSD-BSO-V1.5", 2), ports.Output("OSD-BSO-V1.5")),
            Tts => settings.GetValueOrDefault("Tts", "stand-in") == "piper"
                ? OpenVoiceModel(settings)
                    ? new VoiceCloningTtsAimProcessor(aimName, new TextByTextTtsAimProcessor(new TtsAimProcessor(aimName, TtsFactory.Create(settings), ports), ports.Input("OSD-BTO-V1.5"), ports.Output("OSD-BSO-V1.5")), ports.InputOrDefault("AIP-MLO-V1.0", ""), ports.Output("OSD-BSO-V1.5"), new OpenVoiceWorker(settings))
                                        : new TextByTextTtsAimProcessor(new TtsAimProcessor(aimName, TtsFactory.Create(settings), ports), ports.Input("OSD-BTO-V1.5"), ports.Output("OSD-BSO-V1.5"))
                : new StandInTtsAimProcessor(aimName, ports),
            // Emotion Enhanced Speech: each Port is found by its Data Type (and its ordinal, where an AIM has several of the same).
            Sfa => new SfaAimProcessor(aimName, ports.Input("OSD-SEL-V1.5"), ports.Input("OSD-BSO-V1.5"), ports.Output("MMC-SDO-V2.5")),
            Sfb => new SfbAimProcessor(aimName, ports.Input("OSD-SEL-V1.5"), ports.Input("OSD-BSO-V1.5"), ports.Output("MMC-SDO-V2.5")),
            Efp => new EfpAimProcessor(aimName, ports.Input("MMC-SDO-V2.5"), ports.Input("OSD-SEL-V1.5"), ports.Input("MMC-EEM-V2.5"), ports.Output("MMC-SDO-V2.5")),
            Pei => new PeiAimProcessor(aimName, ports.Input("OSD-BSO-V1.5"), ports.Input("MMC-SDO-V2.5"), ports.Input("MMC-EEM-V2.5"), ports.Output("OSD-BSO-V1.5")),
            Nei => new NeiAimProcessor(aimName, ports.Input("OSD-BSO-V1.5"), ports.Input("MMC-SDO-V2.5"), ports.Output("OSD-BSO-V1.5")),
            // Audio Recording Preservation (the Ports of an AIM that has several of a Data Type are told by their number).
            Aap => new AapAimProcessor(aimName, ports.Input("OSD-BAO-V1.5"), ports.Input("OSD-BMO-V1.5"), ports.Input("CAE-IRR-V2.5"), ports.Output("OSD-BAO-V1.5"), ports.Output("CAE-IRR-V2.5")),
            Vap => new VapAimProcessor(aimName, ports.Input("OSD-BMO-V1.5"), ports.Input("CAE-IRR-V2.5"), ports.Output("CAE-IRR-V2.5"), ports.Output("OSD-BVO-V1.5")),
            Tic => new TicAimProcessor(aimName, ports.Input("OSD-BAO-V1.5"), ports.Input("CAE-IRR-V2.5", 1), ports.Input("OSD-BVO-V1.5"), ports.Input("CAE-IRR-V2.5", 2), ports.Output("CAE-IRR-V2.5"), ports.Output("OSD-BVO-V1.5"),
                                       settings.TryGetValue("FilmEndSeconds", out var end) && double.TryParse(end, System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? seconds : null),
            Tar => new TarAimProcessor(aimName, ports.Input("CAE-IRR-V2.5"), ports.Input("OSD-BAO-V1.5"), ports.Output("CAE-ELS-V2.5"), ports.Output("OSD-BAO-V1.5")),
            Pap => new PapAimProcessor(aimName, ports.Input("OSD-BAO-V1.5", 1), ports.Input("OSD-BAO-V1.5", 2), ports.Input("CAE-ELS-V2.5"), ports.Input("CAE-IRR-V2.5"), ports.Input("OSD-BVO-V1.5"), ports.Input("OSD-BMO-V1.5"),
                                       ports.Output("OSD-BAO-V1.5", 1), ports.Output("OSD-BAO-V1.5", 2)),
            // Enhanced Audioconference Experience.
            Aat => new AatAimProcessor(aimName, ports.Input("OSD-BAO-V1.5"), ports.Output("OSD-BAO-V1.5")),
            Sfd => new SfdAimProcessor(aimName, ports.Input("CAE-MAG-V2.5"), ports.Input("OSD-BAO-V1.5"), ports.Output("OSD-BAO-V1.5")),
            Sds => new SdsAimProcessor(aimName, ports.Input("OSD-BAO-V1.5"), ports.Input("CAE-ASR-V1.0"), ports.Output("OSD-BAO-V1.5"), ports.Output("OSD-BAS-V1.5"), ports.Output("CAE-ASR-V1.0")),
            Ncm => new NcmAimProcessor(aimName, ports.Input("OSD-BAO-V1.5", 1), ports.Input("OSD-BAO-V1.5", 2), ports.Input("OSD-BAS-V1.5"), ports.Input("CAE-ASR-V1.0"), ports.Output("OSD-BAO-V1.5")),
            Ast => new AstAimProcessor(aimName, ports.Input("OSD-BAO-V1.5"), ports.Output("OSD-BAO-V1.5")),
            Amx => new AmxAimProcessor(aimName, ports.Input("OSD-SPT-V1.5"), ports.Input("OSD-BAO-V1.5"), ports.Input("OSD-BAS-V1.5"), ports.Output("OSD-BAS-V1.5")),
            _ => throw new ArgumentException($"{aimName} is not an AIM this provider builds.")
        };
    }
}
