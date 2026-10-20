using System;
using System.Collections.Generic;
using System.Linq;

using Mpai.Cae.Usc.Smc;

namespace Mpai.Cae.Usc.Voice;

// The real trainer of Speech Model Creation: the Neural Network Speech Model is the TONE-COLOUR EMBEDDING of the voice in the Speech Segments (OpenVoice V2: 256 numbers from the reference
// encoder of its converter, the mean over the segments). It is "training" in the sense the Basic API leaves to the Implementer: a model of the voice is made from the segments, no
// weights of a generator are fitted; the voice is applied by the Text To Speech that uses the model (VoiceCloningTtsAimProcessor).
public sealed class OpenVoiceSpeechTrainer(OpenVoiceWorker worker) : ISpeechModelTrainer
{
    public const string MethodName = "OpenVoice V2 tone-colour embedding (256 numbers) of the voice of the segments";

    public TrainedSpeechModel Train(IReadOnlyList<Audio> segments)
    {
        if (segments.Count == 0) throw new ArgumentException("There are no Speech Segments for Modelling.");
        try { return new TrainedSpeechModel(worker.Extract(segments), MethodName, segments.Count, segments.Sum(s => s.Seconds)); }
        catch (InvalidOperationException ex) { throw new ArgumentException(ex.Message, ex); }        // the AIM reports an ArgumentException as an error of the AIM
    }
}