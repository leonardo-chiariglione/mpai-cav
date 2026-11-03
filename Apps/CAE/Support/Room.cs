using System;
using System.Linq;

using Mpai.Cae.Usc;

namespace Mpai.Cae.Apps;

// A ROOM FOR A DEMONSTRATION when there is no microphone array to hand: sources of sound in directions, heard by an array of microphones at points. A source is a tone of a frequency and a
// level that arrives from an azimuth and elevation (degrees; azimuth anticlockwise from the x axis) as a plane wave, so that a microphone nearer to it hears it earlier, within the interval
// of seconds it is active; every microphone has noise of its own. It is what the Enhanced Audioconference Experience is tested with, not a model of a room (no reverberation).
public static class Room
{
    public const double SpeedOfSound = 343.0;

    // Six microphones, one on each axis, `radius` metres from the centre: the smallest array that gives the order one.
    public static ArrayGeometry Octahedron(double radius = 0.012, int blockSize = 512, int rate = 16000) =>
        new("demo-array", "Spherical", rate, blockSize, [[radius, 0, 0], [-radius, 0, 0], [0, radius, 0], [0, -radius, 0], [0, 0, radius], [0, 0, -radius]]);

    public sealed record Source(double Hertz, double Level, double Azimuth, double Elevation, double From, double To);

    public static MultiAudio Capture(ArrayGeometry array, double seconds, int noise, params Source[] sources)
    {
        var length = (int)(seconds * array.Rate); var state = 12345u; var channels = new short[array.Count][];
        for (var m = 0; m < array.Count; m++)
        {
            channels[m] = new short[length]; var p = array.Positions[m];
            for (var i = 0; i < length; i++)
            {
                double s = 0;
                foreach (var src in sources)
                {
                    var t = i / (double)array.Rate; if (t < src.From || t >= src.To) continue;
                    double a = src.Azimuth * Math.PI / 180, e = src.Elevation * Math.PI / 180;
                    var delay = (p[0] * Math.Cos(e) * Math.Cos(a) + p[1] * Math.Cos(e) * Math.Sin(a) + p[2] * Math.Sin(e)) / SpeedOfSound;
                    s += src.Level * Math.Sin(2 * Math.PI * src.Hertz * (t + delay));
                }
                state = state * 1664525u + 1013904223u;
                channels[m][i] = (short)Math.Clamp(Math.Round(s) + ((int)(state >> 8) % (2 * noise + 1) - noise), short.MinValue, short.MaxValue);
            }
        }
        return new MultiAudio(channels, array.Rate);
    }

    // Two people, one at 60 degrees to the left and a little above, the other at 90 degrees to the right, speaking in turn and for a time together, in a noisy room.
    public static MultiAudio Demo(ArrayGeometry array) =>
        Capture(array, 6.0, 200, new Source(1000, 8000, 60, 20, 0.0, 3.5), new Source(2000, 6000, -90, 0, 2.5, 6.0));

    // A voice in the room: a recording of speech (at the array's rate) that arrives as a plane wave from a direction, from the second `Start`, as loud as `Gain` says; it may be repeated every `Every` seconds.
    public sealed record Voice(Audio Speech, double Azimuth, double Elevation, double Gain = 1.0, double Start = 0, double Every = 0);

    // The array hears real speech: each microphone hears the voice with its own delay (a fraction of a sample at these radii, made by an interpolation with a windowed sinc), and noise of its own.
    public static MultiAudio Speech(ArrayGeometry array, double seconds, int noise, params Voice[] voices)
    {
        var length = (int)(seconds * array.Rate); var mix = Enumerable.Range(0, array.Count).Select(_ => new double[length]).ToArray();
        foreach (var v in voices)
        {
            var s = v.Speech.At(array.Rate).Samples; double a = v.Azimuth * Math.PI / 180, e = v.Elevation * Math.PI / 180;
            var u = new[] { Math.Cos(e) * Math.Cos(a), Math.Cos(e) * Math.Sin(a), Math.Sin(e) };
            for (var m = 0; m < array.Count; m++)
            {
                var p = array.Positions[m]; var delay = (p[0] * u[0] + p[1] * u[1] + p[2] * u[2]) / SpeedOfSound * array.Rate;       // in samples: a microphone nearer the source hears it earlier, as in Capture
                for (var start = v.Start; start < seconds; start += v.Every > 0 ? v.Every : double.MaxValue)
                {
                    var from = (int)Math.Round(start * array.Rate);
                    for (var i = 0; i < s.Length && from + i < length; i++) if (from + i >= 0) mix[m][from + i] += v.Gain * Fractional(s, i, delay);
                }
            }
        }
        var state = 12345u;
        return new MultiAudio(mix.Select(c => c.Select(v => { state = state * 1664525u + 1013904223u; return (short)Math.Clamp(Math.Round(v) + ((int)(state >> 8) % (2 * noise + 1) - noise), short.MinValue, short.MaxValue); }).ToArray()).ToArray(), array.Rate);
    }

    // The sample of `s` at the (fractional) position i + shift: the windowed sinc of 16 taps.
    private static double Fractional(short[] s, int i, double shift)
    {
        var n = (int)Math.Floor(i + shift); var frac = i + shift - n; double sum = 0;
        for (var k = -7; k <= 8; k++)
        {
            var at = n + k; if (at < 0 || at >= s.Length) continue;
            var x = k - frac; var sinc = Math.Abs(x) < 1e-9 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
            sum += s[at] * sinc * (0.5 + 0.5 * Math.Cos(Math.PI * x / 8.5));
        }
        return sum;
    }}
