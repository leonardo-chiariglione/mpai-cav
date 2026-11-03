using System.Collections.Generic;

namespace Mpai.Cae.Usc.Smc;

// What creates a speech model from Speech Segments for Modelling. The Basic API of AIF has no function to train a model: how a model is created is for the
// Implementer. This is the seam at which a real trainer (a voice model fitted to the segments) replaces the stand-in without touching what the AIM does with its Ports.
public interface ISpeechModelTrainer
{
    TrainedSpeechModel Train(IReadOnlyList<Audio> segments);
}

// A trained model: its weights (32-bit floats), how it was made, and from how many segments and how many seconds of speech.
public sealed record TrainedSpeechModel(float[] Weights, string Method, int SegmentCount, double Seconds);
