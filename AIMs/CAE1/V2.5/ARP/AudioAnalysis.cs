using System;
using System.Collections.Generic;
using System.Linq;

namespace Mpai.Cae.Usc.Arp;

// What Audio Analysis for Preservation measures: where two recordings of the same tape are in step (the offset), and where the audio of the tape is irregular. Plain signal processing.
public static class AudioAnalysis
{
    // THE OFFSET of the audio of the Preservation Audio-Visual File from the Preservation Audio File, in samples (at the rate of the Preservation Audio File): av[i] = paf[i + offset],
    // so a positive offset is an Audio-Visual File that starts that much after the Preservation Audio File. Found by cross-correlation of the first two minutes: coarse, on the
    // signals reduced to about 4 kHz, then fine, at the full rate. The standard tolerates a misalignment of 10 s.
    public static int Offset(Audio paf, Audio av, double toleratedSeconds = 10)
    {
        if (av.Rate != paf.Rate) av = av.At(paf.Rate);
        var factor = Math.Max(1, paf.Rate / 4000); var span = (int)Math.Min(120.0 * paf.Rate, Math.Min(paf.Samples.Length, av.Samples.Length + toleratedSeconds * paf.Rate));
        var a = Reduce(paf.Samples, span, factor); var b = Reduce(av.Samples, (int)Math.Min(av.Samples.Length, span), factor);
        var n = Fft.NextPowerOfTwo(a.Length + b.Length);
        double[] ar = new double[n], ai = new double[n], br = new double[n], bi = new double[n];
        Array.Copy(a, ar, a.Length); Array.Copy(b, br, b.Length);
        Fft.Transform(ar, ai, false); Fft.Transform(br, bi, false);
        for (var k = 0; k < n; k++) { var re = ar[k] * br[k] + ai[k] * bi[k]; var im = ai[k] * br[k] - ar[k] * bi[k]; ar[k] = re; ai[k] = im; }
        Fft.Transform(ar, ai, true);                                                          // ar[k] = sum paf[i + k] * av[i]; the negative lags are at the end
        var limit = (int)(toleratedSeconds * paf.Rate / factor);
        double best = double.NegativeInfinity; var coarse = 0;
        for (var lag = -limit; lag <= limit; lag++)
        {
            var v = ar[lag >= 0 ? lag : n + lag];
            if (v > best) { best = v; coarse = lag; }
        }
        // fine: the lag around the coarse one that makes the full-rate signals most alike over two seconds
        var center = coarse * factor; var window = Math.Min(2 * paf.Rate, av.Samples.Length); double bestFine = double.NegativeInfinity; var result = center;
        for (var lag = center - 2 * factor; lag <= center + 2 * factor; lag++)
        {
            double sum = 0; var from = Math.Max(0, -lag); var to = Math.Min(window, paf.Samples.Length - lag);
            for (var i = from; i < to; i++) sum += (double)av.Samples[i] * paf.Samples[i + lag];
            if (sum > bestFine) { bestFine = sum; result = lag; }
        }
        return result;
    }

    private static double[] Reduce(short[] x, int length, int factor)
    {
        var r = new double[length / factor];
        for (var i = 0; i < r.Length; i++) { double s = 0; for (var k = 0; k < factor; k++) s += x[i * factor + k]; r[i] = s / factor; }
        return r;
    }

    // THE IRREGULARITIES OF THE AUDIO: a dropout (a stretch much quieter than its surroundings), a click (a sample far from its neighbours), a step in level (the level after is much higher
    // or lower than the level before). Found by the energy of 10 ms frames and by the differences of the samples; the ones close to one another are one.
    public static IReadOnlyList<(double Start, double End, string Kind)> Detect(Audio audio)
    {
        var found = new List<(double Start, double End, string Kind)>();
        var rate = audio.Rate; var x = audio.Samples; var frame = Math.Max(1, rate / 100);
        var frames = x.Length / frame; if (frames < 10) return found;
        var rms = new double[frames];
        for (var f = 0; f < frames; f++) { double s = 0; for (var i = 0; i < frame; i++) { var v = x[f * frame + i] / 32768.0; s += v * v; } rms[f] = Math.Sqrt(s / frame); }

        // dropouts: frames below 5 % of the median of their two seconds, for at least two frames
        var block = 200;
        for (var b = 0; b * block < frames; b++)
        {
            var from = b * block; var to = Math.Min(frames, from + block);
            var median = rms[from..to].OrderBy(v => v).ElementAt((to - from) / 2);
            if (median < 0.003) continue;                                                      // the tape is silent here: not a dropout
            var run = -1;
            for (var f = from; f <= to; f++)
            {
                var low = f < to && rms[f] < 0.05 * median;
                if (low && run < 0) run = f;
                else if (!low && run >= 0) { if (f - run >= 2) found.Add((run * frame / (double)rate, f * frame / (double)rate, "dropout")); run = -1; }
            }
        }

        // clicks: a difference of samples far above the typical one of its neighbourhood
        var window = Math.Max(1, rate / 20);
        for (var start = 0; start + window <= x.Length; start += window)
        {
            var diffs = new int[window - 1];
            for (var i = 1; i < window; i++) diffs[i - 1] = Math.Abs(x[start + i] - x[start + i - 1]);
            var median = diffs.OrderBy(v => v).ElementAt(diffs.Length / 2);
            var threshold = Math.Max(12.0 * Math.Max(median, 1), 4000);
            for (var i = 1; i < window; i++)
                if (diffs[i - 1] > threshold) { var t = (start + i) / (double)rate; found.Add((t - 0.002, t + 0.002, "click")); i += rate / 100; }
        }

        // steps in level: the mean energy of the second after a frame against that of the second before, more than 6 dB apart, taken where the difference is greatest
        var second = 100;
        if (frames > 2 * second)
        {
            var cumulative = new double[frames + 1];
            for (var f = 0; f < frames; f++) cumulative[f + 1] = cumulative[f] + rms[f] * rms[f];
            var delta = new double[frames];
            for (var f = second; f + second <= frames; f++)
            {
                var before = (cumulative[f] - cumulative[f - second]) / second; var after = (cumulative[f + second] - cumulative[f]) / second;
                if (before > 1e-8 && after > 1e-8) delta[f] = 10 * Math.Log10(after / before);
            }
            for (var f = second; f + second <= frames; f++)
            {
                if (Math.Abs(delta[f]) < 6) continue;
                var peak = f; while (f + 1 + second <= frames && Math.Abs(delta[f + 1]) >= 6) { f++; if (Math.Abs(delta[f]) > Math.Abs(delta[peak])) peak = f; }
                found.Add(((peak - 10) * frame / (double)rate, (peak + 10) * frame / (double)rate, "step"));
            }
        }

        // a dropout (or a click) changes the level of the second around it: the steps it makes at its edges are not steps of the tape
        var causes = found.Where(d => d.Kind != "step").ToList();
        found.RemoveAll(d => d.Kind == "step" && causes.Any(c => d.Start < c.End + 1.0 && d.End > c.Start - 1.0));

        // those that are one: within 50 ms of one another
        var merged = new List<(double Start, double End, string Kind)>();
        foreach (var d in found.OrderBy(d => d.Start))
        {
            if (merged.Count > 0 && d.Start <= merged[^1].End + 0.05) merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, d.End), merged[^1].Kind);
            else merged.Add(d);
        }
        return merged;
    }
}
