// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using AIF.Controller;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Mmc.Sir;

// MMC-SIR-V2.5 - Speaker Identity Recognition. Self-contained IAimProcessor.
// Reads its own port names from 1MMC-SIR-V2.5-I01.json at startup.
//
// Consumes a Basic Speech Object (OSD-BSO-V1.5) - the mirror of what the Microphone
// Unit captures as speech - which carries the time it was acquired. Embeds the speech with ECAPA, matches it against the shared
// SubjectGallery, and emits the speaker identity as an Instance Identifier
// (OSD-IID-V1.5): a ranked candidate list at the speaker layer, or the coarse
// "speech" layer when no subject matches.
//
// The recognition logic is the verified SpeakerIdentityRecognitionAim unchanged;
// this processor only bridges it to the AIF - reading the port, decoding the
// speech bytes to samples, and packaging the identity as a Message.
public sealed class SirAimProcessor : IAimProcessor
{
    private readonly string                        _speechPort;
    private readonly string                        _outputPort;
    private readonly SpeakerIdentityRecognitionAim _sir;
    // Where the gallery is kept, where it changes while the AIM runs (a Service,
    // M3245): read again before each match.
    private readonly SubjectGallery                _gallery;
    private readonly AIF.SharedStorage.ISharedStorage? _galleryStore;

    // TELEVISION MEDIA ANALYSIS (OSD-TMA). Optional Inputs of an instance that declares them: the Auxiliary Text (the names in it raise the candidates of those names, by TextBonus)
    // and the Speech Overlap (noted in the log: speech that overlaps another is not told apart by this AIM yet). An instance that declares neither is as before.
    private readonly string _textPort, _overlapPort;
    private string? _text;
    public const double TextBonus = 0.1;

    public string InstanceId { get; }

    public SirAimProcessor(
        string instanceId,
        SpeakerEmbedder embedder,
        SubjectGallery gallery,
        AimPortReader ports,
        AIF.SharedStorage.ISharedStorage? galleryStore = null,
        IAccessReader? access = null)
    {
        InstanceId      = instanceId;
        // THE REFERENCE POPULATION is read from Access, where the Metadata declares it (AccessSources, "reference-voices"): the
        // features of people already computed, pinned by SHA-256, never written by this AIM. The people enrolled by
        // ACR are in the gallery (Shared Storage).
        if (access is not null)
            foreach (var uri in access.SourcesOf("reference-voices"))
                if (access.Open(uri, "", out var stream) == AccessOutcome.OK)
                    using (stream) gallery.LoadReference(stream);

        _sir            = new SpeakerIdentityRecognitionAim(embedder, gallery);
        _gallery        = gallery;
        _galleryStore   = galleryStore;
        _speechPort     = ports.Input("OSD-BSO-V1.5");
        _outputPort     = ports.Output("OSD-IID-V1.5");
        _textPort       = ports.InputOrDefault("OSD-TXO-V1.5", 1, "");
        _overlapPort    = ports.InputOrDefault("MMC-SOL-V2.5", 1, "");
    }

    public async Task<Message> ProcessAsync(Message message)
    {
        if (_galleryStore is not null) _gallery.Refresh(_galleryStore);
        if (_textPort.Length > 0 && message.Ports.TryGetValue(_textPort, out var textJson)) _text = AuxiliaryText.Words(textJson);
        if (_overlapPort.Length > 0 && message.Ports.ContainsKey(_overlapPort)) Sir("overlap information received");
        // Read the Basic Speech Object delivered on the speech input port.
        if (!message.Ports.TryGetValue(_speechPort, out var speechJson) ||
            string.IsNullOrWhiteSpace(speechJson))
        {
            return Message.Error(message.MessageId, InstanceId,
                $"MMC-SIR-V2.5: no speech on input port '{_speechPort}'.");
        }

        var speech = SpeechInputs.Basic(speechJson);
        if (speech is null || speech.Data.Length == 0)
        {
            return Message.Error(message.MessageId, InstanceId,
                "MMC-SIR-V2.5: speech object carried no audio data.");
        }

        // Decode the in-memory WAV to 16 kHz mono samples and identify the speaker.
        // (The Speech Object carries its time, which could window the samples
        // here; the first version analyses the whole clip.)
        float[] samples = WavReader.ReadMono16k(speech);

        // --- inner trace: input + VAD metrics ---
        int _vf = 0;
        for (int i = 0; i + FrameSamples <= samples.Length; i += FrameSamples)
        {
            double sum = 0; for (int j = 0; j < FrameSamples; j++) { float v = samples[i + j]; sum += v * v; }
            if (System.Math.Sqrt(sum / FrameSamples) >= EnergyFloor) _vf++;
        }
        int _voicedMs = _vf * 20;
        bool _activated = _voicedMs >= MinVoicedMs;
        Sir($"in: wavBytes={speech.Data.Length} samples={samples.Length} ({samples.Length/16000.0:F2}s) voicedMs={_voicedMs} floor={EnergyFloor} minVoiced={MinVoicedMs} ACTIVATED={_activated}");

        InstanceIdentifier identity = _activated ? _sir.Identify(samples) : CoarseSpeech();

        // --- inner trace: what the recogniser returned ---
        var _top = identity.InstanceIdentifierData is { Count: > 0 } _d ? _d[0] : null;
        Sir($"out: path={(_activated ? "Identify" : "CoarseSpeech-VADgate")} label=<{_top?.InstanceLabel ?? "<none>"}> conf={(_top?.LabelConfidenceLevel ?? 0f):F3} candidates={identity.InstanceIdentifierData.Count}");

        if (_text is { Length: > 0 }) identity = AuxiliaryText.RaiseNamed(identity, _text, TextBonus);
        var json = MpaiJson.ToJson(identity);
        await Task.CompletedTask;

        return new Message
        {
            MessageId   = message.MessageId,
            MessageType = "InstanceIdentifier",
            DataType    = "OSD-IID-V1.5",
            Payload     = json,
            Ports       = new Dictionary<string, string> { [_outputPort] = json }
        };
    }

    // Simple, dependency-free voice-activity detector on 16 kHz mono samples:
    // require at least MinVoicedMs of audio whose short-frame RMS energy exceeds a
    // floor. Enough to reject silence and one-syllable fragments, cheap and robust.
    private const int   FrameSamples = 320;      // 20 ms at 16 kHz
    private const float EnergyFloor  = 0.015f;   // RMS threshold for a "voiced" frame
    private const int   MinVoicedMs  = 350;      // a real utterance, not a fragment

    private static void Sir(string m)
    {
        try { Mpai.Core.MpaiDiag.Append("sir-trace.log",
            System.DateTime.Now.ToString("HH:mm:ss.fff") + "  SIR " + m + "\n"); } catch {}
    }

    private static bool VoiceActivated(float[] s)
    {
        if (s.Length < FrameSamples) return false;
        int voicedFrames = 0;
        for (int i = 0; i + FrameSamples <= s.Length; i += FrameSamples)
        {
            double sum = 0;
            for (int j = 0; j < FrameSamples; j++) { float v = s[i + j]; sum += v * v; }
            double rms = System.Math.Sqrt(sum / FrameSamples);
            if (rms >= EnergyFloor) voicedFrames++;
        }
        int voicedMs = voicedFrames * 20;   // each frame is 20 ms
        return voicedMs >= MinVoicedMs;
    }

    // The coarse "speech" identity: a non-subject the reconciler will not grant on.
    private static InstanceIdentifier CoarseSpeech() => new()
    {
        Header = "OSD-IID-V1.5",
        InstanceIdentifier_ = "speech",
        InstanceIdentifierData =
        {
            new InstanceCandidate
            {
                InstanceLabel = "speech",
                LabelConfidenceLevel = 0f,
                Taxonomy = new InstanceTaxonomy { TaxonomyLevelIDs = { "sound", "speech" } }
            }
        }
    };
}
