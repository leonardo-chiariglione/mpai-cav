using System;
using System.Collections.Generic;
using System.Linq;

namespace Mpai.Cae.Usc.Ees;

// The prosody of a speech segment, as Emotion Enhanced Speech measures it and changes it: the pitch (the median fundamental frequency of the voiced frames), the level (the RMS of
// the active frames, in dB below full scale) and the speaking rate (the syllable-like peaks of the energy envelope in each second of speech). Plain signal processing: no
// neural network is in this file.
public sealed record SpeechFeatures(double MeanF0Hz, double RmsDb, double SpeakingRate);

public static class Prosody
{
    private const int Frame = 400, Hop = 160;                     // 25 ms and 10 ms at 16 kHz

    public static SpeechFeatures Measure(Audio audio)
    {
        var x = audio.Samples; var rate = audio.Rate;
        var frame = Math.Max(64, rate * 25 / 1000); var hop = Math.Max(16, rate * 10 / 1000);
        var count = x.Length >= frame ? (x.Length - frame) / hop + 1 : 0;
        if (count == 0) throw new FormatException("The speech is too short to measure.");
        var rms = new double[count]; var f0 = new double?[count];
        var minLag = rate / 400; var maxLag = Math.Min(frame - 1, rate / 60);
        for (var f = 0; f < count; f++)
        {
            double e = 0; var at = f * hop;
            for (var i = 0; i < frame; i++) { var v = x[at + i] / 32768.0; e += v * v; }
            rms[f] = Math.Sqrt(e / frame);
        }
        var max = rms.Max();
        var active = rms.Select(r => r > 0.1 * max).ToArray();
        for (var f = 0; f < count; f++)
        {
            if (!active[f]) continue;
            var at = f * hop; double r0 = 0;
            for (var i = 0; i < frame; i++) { var v = x[at + i] / 32768.0; r0 += v * v; }
            double best = 0; var bestLag = 0;
            for (var lag = minLag; lag <= maxLag; lag++)
            {
                double r = 0; for (var i = 0; i + lag < frame; i++) r += (x[at + i] / 32768.0) * (x[at + i + lag] / 32768.0);
                r /= r0 * (frame - lag) / frame;
                if (r > best) { best = r; bestLag = lag; }
            }
            if (best > 0.4) f0[f] = rate / (double)bestLag;
        }
        var voiced = f0.Where(v => v.HasValue).Select(v => v!.Value).OrderBy(v => v).ToList();
        var meanF0 = voiced.Count > 0 ? voiced[voiced.Count / 2] : 0;

        double sum = 0; var n = 0;
        for (var f = 0; f < count; f++) if (active[f]) { sum += rms[f] * rms[f]; n++; }
        var level = 20 * Math.Log10(Math.Max(Math.Sqrt(sum / Math.Max(1, n)), 1e-6));

        // Syllable-like peaks: local maxima of the smoothed energy envelope that stand out, at least 80 ms apart, per second of active speech.
        var env = new double[count];
        for (var f = 0; f < count; f++) { double s = 0; var c = 0; for (var d = -2; d <= 2; d++) if (f + d >= 0 && f + d < count) { s += rms[f + d]; c++; } env[f] = s / c; }
        var peaks = 0; var last = -100;
        for (var f = 1; f < count - 1; f++)
            if (env[f] > env[f - 1] && env[f] >= env[f + 1] && env[f] > 0.3 * max && f - last >= 8 && IsPeak(env, f)) { peaks++; last = f; }
        var seconds = n * hop / (double)rate;
        return new SpeechFeatures(meanF0, level, seconds > 0 ? peaks / seconds : 0);
    }

    // A peak stands out when the envelope falls to 85 % of it on both sides within 150 ms.
    private static bool IsPeak(double[] env, int f)
    {
        bool Falls(int step) { for (var d = 1; d <= 15; d++) { var g = f + step * d; if (g < 0 || g >= env.Length) return true; if (env[g] < 0.85 * env[f]) return true; } return false; }
        return Falls(-1) && Falls(1);
    }

    // The speech with its pitch multiplied by pitchRatio, its speaking rate by rateRatio (faster when above 1) and its level by gain.
    public static Audio Modify(Audio audio, double pitchRatio, double rateRatio, double gain)
    {
        pitchRatio = Math.Clamp(pitchRatio, 0.5, 2.0); rateRatio = Math.Clamp(rateRatio, 0.6, 1.6); gain = Math.Clamp(gain, 0.25, 4.0);
        var x = audio.Samples.Select(s => (double)s).ToArray();
        if (Math.Abs(rateRatio - 1) > 1e-3) x = Stretch(x, 1 / rateRatio, audio.Rate);
        if (Math.Abs(pitchRatio - 1) > 1e-3)
        {
            x = Stretch(x, pitchRatio, audio.Rate);                                          // longer by pitchRatio, the pitch unchanged ...
            var y = new double[(int)(x.Length / pitchRatio)];                                 // ... played faster: the pitch multiplied, the length as before
            for (var i = 0; i < y.Length; i++) { var p = i * pitchRatio; var at = (int)p; var fr = p - at; y[i] = x[at] + ((at + 1 < x.Length ? x[at + 1] : x[at]) - x[at]) * fr; }
            x = y;
        }
        return new Audio(x.Select(v => (short)Math.Clamp(Math.Round(v * gain), short.MinValue, short.MaxValue)).ToArray(), audio.Rate);
    }

    // Waveform-similarity overlap-add: the speech as long as `ratio` times it was (ratio above 1 slower), its pitch unchanged.
    private static double[] Stretch(double[] x, double ratio, int rate)
    {
        var frame = Math.Max(256, rate * 40 / 1000); var synHop = frame / 2; var search = frame / 4;
        var anaHop = synHop / ratio;
        var outLength = (int)(x.Length * ratio);
        var y = new double[outLength + frame]; var norm = new double[outLength + frame];
        var window = Enumerable.Range(0, frame).Select(i => 0.5 - 0.5 * Math.Cos(2 * Math.PI * (i + 0.5) / frame)).ToArray();
        var prev = 0;
        for (var m = 0; ; m++)
        {
            var outAt = m * synHop; if (outAt >= outLength) break;
            var nominal = (int)Math.Round(m * anaHop); var at = nominal;
            if (m > 0)
            {
                // the frame of the speech that best continues the previous one
                var targetAt = prev + synHop; double bestScore = double.NegativeInfinity;
                for (var d = -search; d <= search; d++)
                {
                    var a = nominal + d; if (a < 0 || a + frame > x.Length || targetAt + frame > x.Length) continue;
                    double score = 0; for (var i = 0; i < frame; i += 4) score += x[a + i] * x[targetAt + i];
                    if (score > bestScore) { bestScore = score; at = a; }
                }
            }
            at = Math.Clamp(at, 0, Math.Max(0, x.Length - frame));
            for (var i = 0; i < frame && at + i < x.Length; i++) { y[outAt + i] += x[at + i] * window[i]; norm[outAt + i] += window[i]; }
            prev = at;
        }
        var result = new double[outLength];
        for (var i = 0; i < outLength; i++) result[i] = norm[i] > 1e-6 ? y[i] / norm[i] : 0;
        return result;
    }
}
