using System;
using System.Collections.Generic;
using System.Linq;

namespace Mpai.SpatialAudio;

// THE ROOM A SCENE IS HEARD IN: the box its walls make and what the walls do to sound - the Closed Space of the Scene (its walls and their Materials) and the Scene part of the Acoustic Profile
// (CAE-ACP, as M3282 states it: the units, the scales, the relations).
//
//   FaceAbsorption  the share of the sound each wall takes (0: none, 1: all), in the order of the images (x0, x1, y0, y1, z0, z1 = Left, Right, Back, Front, Floor, Ceiling): a wall with a Material
//                   of its own takes that Material's Absorption; one without takes the Absorption of the Acoustic Profile (as if it were the DefaultMaterial), else the DefaultMaterial of
//                   the Closed Space, else the usual of a furnished room (0.2). A wall gives back the rest: the reflection has sqrt(1 - absorption) of the amplitude.
//   Rt60            the time the late sound takes to fall by 60 dB, in seconds (the RT60 of the profile, at 1000 Hz or the pair nearest to it; one pair applies at all frequencies); absent, Sabine's:
//                   0.161 V / A, with A the sum of the area of each wall multiplied by its absorption.
//   Diffusion       how scattered the late sound is (0..1): the denser the late sound, the smoother
//   EarlyTime       the time from the direct sound to the first reflection (EarlyReflectionTime, seconds)
//   LateTime        the time from the first reflection to the onset of the late reverberation (LateReflectionTime, seconds); the early part ends EarlyTime + LateTime after the direct sound
//
// Heard: the sound itself, the first reflection off each wall that arrives within the early part (as a source in the mirror image of the room), and the late reverberation of a Schroeder
// reverberator whose time is the Rt60. THE LEVEL OF THE LATE SOUND IS NOT A PARAMETER: it follows from the volume V and the time T60 - the direct and the reverberant levels are equal at the critical
// distance 0.057 sqrt(V/T60) metres (omnidirectional source), so the late sound of a source that is 1 m away at unit amplitude has the energy 1/dc^2.
public sealed record RoomAcoustics(double X0, double X1, double Y0, double Y1, double Z0, double Z1, double[] FaceAbsorption, double? Rt60, double Diffusion, double EarlyTime, double LateTime)
{
    public const double SpeedOfSound = 343.0;

    public double Volume => Math.Abs((X1 - X0) * (Y1 - Y0) * (Z1 - Z0));

    // The area of each face, in the order of the images.
    public double[] FaceArea
    {
        get
        {
            double w = Math.Abs(X1 - X0), d = Math.Abs(Y1 - Y0), h = Math.Abs(Z1 - Z0);
            return [d * h, d * h, w * h, w * h, w * d, w * d];
        }
    }

    public double Surface => FaceArea.Sum();

    // The absorption of the space: Sabine's sum of area times absorption, and its mean over the surface.
    public double TotalAbsorption => FaceArea.Select((area, i) => area * Math.Clamp(FaceAbsorption[i], 0, 1)).Sum();
    public double Absorption => Surface > 0 ? TotalAbsorption / Surface : 0.2;

    // What one wall gives back of the amplitude.
    public double Reflection(int face) => Math.Sqrt(1 - Math.Clamp(FaceAbsorption[face], 0, 0.99));

    // The time of the late sound: the one the profile gives, or Sabine's (0.161 V / A).
    public double Time => Math.Clamp(Rt60 ?? (TotalAbsorption > 0.01 ? 0.161 * Volume / TotalAbsorption : 6.0), 0.1, 6.0);

    // The early part ends, and the late sound begins, this long after the direct sound.
    public double Onset => Math.Max(0, EarlyTime) + Math.Max(0, LateTime);

    // The critical distance, in metres.
    public double CriticalDistance => Math.Max(0.1, 0.057 * Math.Sqrt(Math.Max(Volume, 1e-3) / Time));

    // The six images of a position, one in the mirror of each wall.
    public IEnumerable<double[]> Images(double[] p)
    {
        yield return [2 * X0 - p[0], p[1], p[2]]; yield return [2 * X1 - p[0], p[1], p[2]];
        yield return [p[0], 2 * Y0 - p[1], p[2]]; yield return [p[0], 2 * Y1 - p[1], p[2]];
        yield return [p[0], p[1], 2 * Z0 - p[2]]; yield return [p[0], p[1], 2 * Z1 - p[2]];
    }

    // The gain that gives the late sound of a feed the level the room says: the energy of the late sound of an impulse is 1/dc^2, in each ear.
    private double? _lateGain;
    public double LateGain(int rate, int ears)
    {
        if (_lateGain is { } known) return known;
        var impulse = new float[(int)(Onset * rate) + (int)(Time * rate * 2.5) + 1]; impulse[0] = 1f;
        var response = Late(impulse, rate, ears, normalised: false);
        var energy = response.Average(ear => ear.Sum(x => (double)x * x));
        _lateGain = energy > 0 ? 1.0 / CriticalDistance / Math.Sqrt(energy) : 0;
        return _lateGain.Value;
    }

    // The late sound of a mono feed: Schroeder's four combs and two all-passes, one set per ear, at the level the room says (LateGain). Returns `ears` buffers of the feed's length.
    public float[][] Late(float[] feed, int rate, int ears, bool normalised = true)
    {
        var length = feed.Length;
        var result = new float[ears][];
        var rt = Time; var scale = Math.Clamp(Math.Cbrt(Math.Max(Volume, 1)) / 3.0, 0.5, 2.0);
        var damp = (float)Math.Clamp(Absorption, 0, 0.9) * 0.5f;
        var allpassGain = (float)(0.3 + 0.4 * Math.Clamp(Diffusion, 0, 1));
        var onset = (int)Math.Round(Onset * rate);
        var level = normalised ? (float)LateGain(rate, ears) : 1f;
        for (var ear = 0; ear < ears; ear++)
        {
            var stretch = 1.0 + 0.07 * ear;                                                   // the other ear has other delays: the late sound is not the same in both
            var output = new float[length];
            foreach (var ms in new[] { 29.7, 37.1, 41.1, 43.7 })
            {
                var delay = Math.Max(2, (int)Math.Round(ms * 1e-3 * scale * stretch * rate));
                var g = (float)Math.Pow(10, -3.0 * delay / rate / rt);
                var line = new float[delay]; var at = 0; var low = 0f;
                for (var i = 0; i < length; i++)
                {
                    var x = i >= onset ? feed[i - onset] : 0f;
                    var echo = line[at];
                    low = echo * (1 - damp) + low * damp;
                    line[at] = x + g * low;
                    at = (at + 1) % delay;
                    output[i] += echo * 0.25f;
                }
            }
            foreach (var ms in new[] { 5.0, 1.7 })
            {
                var delay = Math.Max(2, (int)Math.Round(ms * 1e-3 * stretch * rate));
                var line = new float[delay]; var at = 0;
                for (var i = 0; i < length; i++)
                {
                    var buffered = line[at]; var x = output[i];
                    var y = -allpassGain * x + buffered;
                    line[at] = x + allpassGain * y;
                    at = (at + 1) % delay;
                    output[i] = y;
                }
            }
            if (level != 1f) for (var i = 0; i < length; i++) output[i] *= level;
            result[ear] = output;
        }
        return result;
    }
}
