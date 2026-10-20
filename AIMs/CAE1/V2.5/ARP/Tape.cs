using System;
using System.Collections.Generic;
using System.Numerics;

namespace Mpai.Cae.Usc.Arp;

// What the standards of an open-reel tape mean for the audio digitised from it: the speed (inches a second: 0.9375 to 30) and the playback equalisation (IEC, IEC1, IEC2). A tape
// written at one speed and with one equalisation and read (digitised) at another speed or with another equalisation has to be corrected: the sampling frequency to apply, and the
// equalisation to correct.
public static class Tape
{
    public static readonly double[] Speeds = [0.9375, 1.875, 3.75, 7.5, 15, 30];

    // The time constants of the playback equalisation, in microseconds: the low-frequency one (0 where there is none) and the high-frequency one. IEC1 is the CCIR curve, IEC2 the
    // NAB curve; "IEC" here is IEC1. THESE VALUES ARE FROM MEMORY OF IEC 60094 AND ARE TO BE CHECKED AGAINST IT BY SOMEONE WHO HAS IT: nothing else in the code depends on them.
    private static readonly Dictionary<(string, double), (double Lf, double Hf)> Constants = new()
    {
        [("IEC1", 30)] = (0, 17.5), [("IEC1", 15)] = (0, 35), [("IEC1", 7.5)] = (0, 70), [("IEC1", 3.75)] = (3180, 90), [("IEC1", 1.875)] = (3180, 90), [("IEC1", 0.9375)] = (3180, 120),
        [("IEC2", 30)] = (0, 17.5), [("IEC2", 15)] = (3180, 50), [("IEC2", 7.5)] = (3180, 50), [("IEC2", 3.75)] = (3180, 90), [("IEC2", 1.875)] = (3180, 90), [("IEC2", 0.9375)] = (3180, 120)
    };

    public static (double Lf, double Hf) TimeConstants(string standard, double speed) =>
        Constants.TryGetValue((standard == "IEC" ? "IEC1" : standard, speed), out var t) ? t : throw new ArgumentException($"There is no equalisation {standard} at {speed} ips.");

    // The sampling frequency to apply to audio digitised from a tape read at one speed that was written at another: a tape read twice as fast as it was written plays at half the rate.
    public static int AppliedSamplingFrequency(int rate, double readingSpeed, double writingSpeed) => (int)Math.Round(rate * writingSpeed / readingSpeed);

    // The response of the playback equalisation at a frequency: (1 + jwTlf) / (jwTlf) / (1 + jwThf), the first factor only where there is a low-frequency time constant.
    private static Complex Response((double Lf, double Hf) t, double hertz)
    {
        var w = 2 * Math.PI * Math.Max(hertz, 1.0);
        var jw = new Complex(0, w);
        var hf = 1 / (1 + jw * t.Hf * 1e-6);
        return t.Lf > 0 ? (1 + jw * t.Lf * 1e-6) / (jw * t.Lf * 1e-6) * hf : hf;
    }

    // The audio corrected for a tape that was read with the equalisation of one standard at one speed and written with the equalisation of another at another: the spectrum is
    // multiplied by the response it should have had over the one it had. The audio is then to be given the sampling frequency of AppliedSamplingFrequency, so its frequencies are those
    // of the tape as written: the response it should have had is taken at the frequency the sound then has.
    public static Audio CorrectEqualisation(Audio audio, string readingStandard, double readingSpeed, string writingStandard, double writingSpeed)
    {
        var read = TimeConstants(readingStandard, readingSpeed); var write = TimeConstants(writingStandard, writingSpeed);
        var n = Fft.NextPowerOfTwo(audio.Samples.Length + 1);
        var re = new double[n]; var im = new double[n];
        for (var i = 0; i < audio.Samples.Length; i++) re[i] = audio.Samples[i];
        Fft.Transform(re, im, false);
        for (var k = 0; k <= n / 2; k++)
        {
            var hertz = k * (double)audio.Rate / n;
            var c = Response(write, hertz * writingSpeed / readingSpeed) / Response(read, hertz);
            if (k == 0 || k == n / 2) { re[k] *= c.Real; im[k] = 0; continue; }
            var x = new Complex(re[k], im[k]) * c;
            re[k] = x.Real; im[k] = x.Imaginary; re[n - k] = x.Real; im[n - k] = -x.Imaginary;
        }
        Fft.Transform(re, im, true);
        return new Audio(Enumerable_Clamp(re, audio.Samples.Length), audio.Rate);
    }

    private static short[] Enumerable_Clamp(double[] x, int length)
    {
        var s = new short[length];
        for (var i = 0; i < length; i++) s[i] = (short)Math.Clamp(Math.Round(x[i]), short.MinValue, short.MaxValue);
        return s;
    }
}
