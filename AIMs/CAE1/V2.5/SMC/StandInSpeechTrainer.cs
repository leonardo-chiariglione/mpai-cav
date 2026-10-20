using System;
using System.Collections.Generic;

namespace Mpai.Cae.Usc.Smc;

// The stand-in trainer: it does not train a neural network. It measures the segments - the mean level (RMS), the mean zero-crossing rate and the mean duration - and the
// "model" is those three numbers. It exists so that the AIM, its Ports, its model object and what depends on them can be run and tested end to end; a real trainer
// implements ISpeechModelTrainer and replaces it.
public sealed class StandInSpeechTrainer : ISpeechModelTrainer
{
    public const string MethodName = "stand-in: mean RMS, zero-crossing rate and duration of the segments";

    public TrainedSpeechModel Train(IReadOnlyList<Audio> segments)
    {
        if (segments.Count == 0) throw new ArgumentException("There are no Speech Segments for Modelling.");
        double rms = 0, zcr = 0, seconds = 0;
        foreach (var segment in segments)
        {
            var s = segment.Samples;
            if (s.Length == 0) throw new FormatException("A Speech Segment for Modelling has no samples.");
            double sum = 0; var crossings = 0;
            for (var i = 0; i < s.Length; i++)
            {
                var v = s[i] / 32768.0; sum += v * v;
                if (i > 0 && (s[i - 1] < 0) != (s[i] < 0)) crossings++;
            }
            rms += Math.Sqrt(sum / s.Length); zcr += crossings / (double)s.Length; seconds += segment.Seconds;
        }
        var n = segments.Count;
        return new TrainedSpeechModel([(float)(rms / n), (float)(zcr / n), (float)(seconds / n)], MethodName, n, seconds);
    }
}
