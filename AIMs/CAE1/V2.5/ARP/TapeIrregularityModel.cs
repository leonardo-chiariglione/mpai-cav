// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The model it runs was TRAINED ON SIMULATED IMPAIRMENTS OF PUBLIC-DOMAIN / CC0 MUSIC (Test\Tools\TapeSim), not on real tapes. Read THIRD-PARTY-NOTICES.md and MODELS.md.
using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Mpai.Onnx;
using Mpai.Osd.Speech;

namespace Mpai.Cae.Usc.Arp;

// THE TRAINED CLASSIFIER OF CAE-TIC: an ONNX model (TapeIrregularityClassifier.onnx) the Controller obtained from Access (the AccessSources of the L3 of the AIM, Setting "TicModel")
// and checked against its SHA-256 before giving the AIM its place. The AIM does not look for, read or write that file itself: the model is opened by the library that runs it.
//
// The model takes 4 s of mono 16 kHz audio (-1..1) and gives a logit for each class; the log-mel front end is part of the graph (no signal processing here but the rate and the window).
// What it is shown is what CAE-ARP's Audio Analysis gives for an irregularity: the Audio File, the irregularity with a margin of 1 s on each side (AapAimProcessor.Margin), looked at through
// a window of 4 s centred on the middle of the irregularity, the samples the file lacks taken by reflection about its ends (the training clips were made the same way).
public sealed class TapeIrregularityModel : IDisposable
{
    // The classes, in the order of the output (Test\Tools\TapeSim\tape_sim.py CLASSES). "none": not an irregularity the AIM selects. The others are IrregularityTypes of the Irregularity File.
    public static readonly string[] Classes = ["none", "da", "di", "pps", "ssv", "esv", "sb", "wf"];
    public const int Rate = 16000, WindowSamples = 4 * Rate;

    private readonly InferenceSession _session;
    private readonly string _input, _output;

    public TapeIrregularityModel(string modelPath)
    {
        _session = OnnxSessions.Create(modelPath);
        _input = _session.InputMetadata.Keys.First();
        _output = _session.OutputMetadata.Keys.First();
    }

    public sealed record Result(string Class, double Probability, IReadOnlyList<double> Probabilities);

    // The class of the irregularity [start, end] (seconds in the Preservation Audio File) whose Audio File is clip, which begins at clipStart seconds of that file.
    public Result Classify(Audio clip, double start, double end, double clipStart)
    {
        var at = clip.Rate == Rate ? clip : To16k(clip);
        var centre = (int)Math.Round(((start + end) / 2 - clipStart) * Rate);
        return Classify(Window(at.Samples, centre));
    }

    // The clip at 16 kHz: above that rate it is low-passed first (a Butterworth of order four at 7 kHz, two second-order sections), so that what is above 8 kHz does not fold into the band.
    private static Audio To16k(Audio clip)
    {
        if (clip.Rate <= Rate) return clip.At(Rate);
        var x = clip.Samples.Select(s => (double)s).ToArray();
        foreach (var q in new[] { 0.5411961, 1.3065630 })
        {
            var k = Math.Tan(Math.PI * 7000.0 / clip.Rate); var norm = 1 / (1 + k / q + k * k);
            double b0 = k * k * norm, b1 = 2 * b0, b2 = b0, a1 = 2 * (k * k - 1) * norm, a2 = (1 - k / q + k * k) * norm;
            double z1 = 0, z2 = 0;
            for (var i = 0; i < x.Length; i++) { var o = b0 * x[i] + z1; z1 = b1 * x[i] - a1 * o + z2; z2 = b2 * x[i] - a2 * o; x[i] = o; }
        }
        return new Audio(x.Select(v => (short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue)).ToArray(), clip.Rate).At(Rate);
    }

    // The window of 4 s centred on a sample, by reflection where the clip does not reach.
    public static float[] Window(short[] samples, int centre)
    {
        var w = new float[WindowSamples];
        if (samples.Length == 0) return w;
        for (var i = 0; i < w.Length; i++) w[i] = samples[Reflect(centre - WindowSamples / 2 + i, samples.Length)] / 32768f;
        return w;
    }

    public static int Reflect(int i, int n)
    {
        if (n == 1) return 0;
        var period = 2 * (n - 1);
        i %= period; if (i < 0) i += period;
        return i >= n ? period - i : i;
    }

    public Result Classify(float[] window)
    {
        var tensor = new DenseTensor<float>(window, [1, window.Length]);
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_input, tensor)]);
        var logits = results.First(r => r.Name == _output).AsEnumerable<float>().Select(v => (double)v).ToArray();
        var max = logits.Max();
        var exp = logits.Select(v => Math.Exp(v - max)).ToArray();
        var sum = exp.Sum();
        var p = exp.Select(v => v / sum).ToArray();
        var best = Array.IndexOf(p, p.Max());
        return new Result(Classes[best], p[best], p);
    }

    public void Dispose() => _session.Dispose();
}
