using System;
using System.Collections.Generic;
using System.Linq;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.SpatialAudio;

// FROM AN AUDIO SCENE TO WHAT THE RENDERER TAKES. A Basic Audio Scene places Basic
// Audio Objects; an Audio Scene places Basic and full Audio Objects and sub-scenes,
// each with its Space-Time relative to what holds it. Every Basic Audio Object is a
// source: its sound (the WAV it carries, as mono at the renderer's rate), where it
// is in the scene (the placements composed: positions added, yaws added), its
// directional pattern (its Acoustic Profile), and the user it is heard by - the
// member's UserPoV, else the scene's, else the origin facing ahead.
public static class SceneAudio
{
    // StartSeconds: when the source begins to sound, in the scene's own time (0 where nothing is said).
    // EndSeconds: when it stops, in the scene's own time; null where it plays to the end of its sound.
    public sealed record Member(BasicAudioObject Object, double[] Position, double YawDeg, double PitchDeg, PointOfView? User, double StartSeconds = 0, double? EndSeconds = null);

    // The end a Space-Time states: the end of its first relative time segment, when that is later than its start (an instant states no end).
    public static double? EndOf(SpaceTime? st)
    {
        var segment = st?.Time?.SimpleTimeData?.FirstOrDefault(s => s.TimeType == false);
        if (segment is null || segment.EndTime <= segment.StartTime) return null;
        return segment.TimeUnit switch { "01" => segment.EndTime / 1e3, "10" => segment.EndTime / 1e6, "11" => segment.EndTime / 1e9, _ => segment.EndTime };
    }

    // A sound as its member states it: cut where it ends, then begun late.
    public static float[] Shape(float[] sound, double start, double? end, int rate)
    {
        if (end is { } e && e > start && (int)Math.Round((e - start) * rate) is var n && n < sound.Length) sound = sound[..Math.Max(0, n)];
        return Delay(sound, start, rate);
    }

    // The start a Space-Time states, in seconds: the first relative time segment of its Time (an absolute stamp says when something was made, not when it sounds).
    public static double StartOf(SpaceTime? st)
    {
        var segment = st?.Time?.SimpleTimeData?.FirstOrDefault(s => s.TimeType == false);
        if (segment is null) return 0;
        return segment.TimeUnit switch { "01" => segment.StartTime / 1e3, "10" => segment.StartTime / 1e6, "11" => segment.StartTime / 1e9, _ => segment.StartTime };
    }

    // How loud a sound is, in LUFS - the mean power of its samples (the K-weighting left out): the unit a Level and a Loudness are given in.
    public static double Lufs(float[] mono)
    {
        if (mono.Length == 0) return -70;
        double s = 0; foreach (var x in mono) s += (double)x * x;
        s /= mono.Length;
        return s < 1e-12 ? -70 : -0.691 + 10 * Math.Log10(s);
    }

    // A SOUND AS ITS ACOUSTIC PROFILE SAYS IT IS: as loud as its Loudness (in LUFS; the object's Level where the profile gives none), and within its Frequency Range.
    // The gain is set from the sound as it is, and the range then takes away what is outside it.
    public static float[] WithLevel(BasicAudioObject bao, float[] mono, int rate = 48000)
    {
        if (mono.Length == 0) return mono;
        var properties = bao.BasicAudioObjectProperties; var profile = properties?.AcousticProfile;
        var sound = mono;
        if ((profile?.Loudness ?? properties?.Level) is { } target)
        {
            var gain = (float)Math.Clamp(Math.Pow(10, (target - Lufs(mono)) / 20), 0.03, 30);
            sound = new float[mono.Length];
            for (var i = 0; i < mono.Length; i++) sound[i] = mono[i] * gain;
        }
        if (profile?.FrequencyRange is { } range)
        {
            if (range.MinFrequencyHz > 25) sound = Biquad(sound, rate, range.MinFrequencyHz, high: true);
            if (range.MaxFrequencyHz > 0 && range.MaxFrequencyHz < Math.Min(19000, rate * 0.45)) sound = Biquad(sound, rate, range.MaxFrequencyHz, high: false);
        }
        return sound;
    }

    // A 4th-order Butterworth high-pass or low-pass at hz: two biquads in series (the audio EQ cookbook's).
    private static float[] Biquad(float[] x, int rate, double hz, bool high)
    {
        var w0 = 2 * Math.PI * Math.Min(hz, rate * 0.45) / rate; var cos = Math.Cos(w0); var sin = Math.Sin(w0);
        var y = x;
        foreach (var q in new[] { 0.5412, 1.3066 })
        {
            var alpha = sin / (2 * q);
            double b0, b1, b2; var a0 = 1 + alpha; var a1 = -2 * cos; var a2 = 1 - alpha;
            if (high) { b0 = (1 + cos) / 2; b1 = -(1 + cos); b2 = (1 + cos) / 2; } else { b0 = (1 - cos) / 2; b1 = 1 - cos; b2 = (1 - cos) / 2; }
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0; var o = new float[y.Length];
            for (var i = 0; i < y.Length; i++)
            {
                var v = (b0 * y[i] + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2) / a0;
                x2 = x1; x1 = y[i]; y2 = y1; y1 = v; o[i] = (float)v;
            }
            y = o;
        }
        return y;
    }

    // A sound begun late: silence first.
    public static float[] Delay(float[] sound, double seconds, int rate)
    {
        var lead = (int)Math.Round(Math.Max(0, seconds) * rate);
        if (lead == 0 || sound.Length == 0) return sound;
        var delayed = new float[lead + sound.Length];
        sound.CopyTo(delayed, lead);
        return delayed;
    }

    private static (double[] Position, double Yaw, double Pitch) Of(SpaceTime? st)
    {
        var sa = st?.SpatialAttitude1;
        var p = sa?.Position?.CartPosition is { Length: >= 3 } c ? new[] { c[0], c[1], c[2] } : new double[3];
        var e = sa?.Orientation?.EulerAngles is { Length: >= 3 } a ? a : new double[3];   // roll, pitch, yaw
        return (p, e[2], e[1]);
    }

    private static (double[] Position, double Yaw, double Pitch) Compose((double[] P, double Yaw, double Pitch) outer, (double[] P, double Yaw, double Pitch) inner)
    {
        // The inner position turned by the outer yaw, then moved by the outer position.
        var y = outer.Yaw * Math.PI / 180;
        double x = inner.P[0] * Math.Cos(y) - inner.P[1] * Math.Sin(y), yy = inner.P[0] * Math.Sin(y) + inner.P[1] * Math.Cos(y);
        return ([outer.P[0] + x, outer.P[1] + yy, outer.P[2] + inner.P[2]], outer.Yaw + inner.Yaw, outer.Pitch + inner.Pitch);
    }

    private static readonly (double[] P, double Yaw, double Pitch) Here = (new double[3], 0, 0);

    public static IEnumerable<Member> Members(BasicAudioSceneDescriptors scene) => Members(scene, Here, null);

    private static IEnumerable<Member> Members(BasicAudioSceneDescriptors scene, (double[] P, double Yaw, double Pitch) frame, PointOfView? outerUser)
    {
        var user = scene.UserPoV ?? outerUser;
        foreach (var e in scene.BasicAudioSceneDescriptorsEntries)
            if (e.AudioObjectIDOrAudioObject is { } bao)
            {
                var (p, yaw, pitch) = Compose(frame, Of(e.AudioObjectSpaceTime));
                yield return new Member(bao, p, yaw, pitch, e.UserPoV ?? user, StartOf(e.AudioObjectSpaceTime), EndOf(e.AudioObjectSpaceTime));
            }
    }

    public static IEnumerable<Member> Members(AudioSceneDescriptors scene) => Members(scene, Here, null, 0);

    private static IEnumerable<Member> Members(AudioSceneDescriptors scene, (double[] P, double Yaw, double Pitch) frame, PointOfView? outerUser, double start)
    {
        var user = scene.UserPoV ?? outerUser;
        foreach (var e in scene.AudioObjects ?? [])
            if (e.ObjectIDOrObject is { } auo)
                foreach (var m in Members(auo, Compose(frame, Of(e.AudioObjectSpaceTime)), user, start + StartOf(e.AudioObjectSpaceTime))) yield return m;
        foreach (var s in scene.SubAudioScenes ?? [])
            if (s.SubAudioSceneIDOrSubAudioScene is { } sub)
                foreach (var m in Members(sub, Compose(frame, Of(s.SubAudioSceneSpaceTime)), user, start + StartOf(s.SubAudioSceneSpaceTime))) yield return m;
    }

    // An Audio Object heard on its own, from its UserPoV.
    public static IEnumerable<Member> Members(AudioObject o) => Members(o, Here, o.UserPoV, 0);

    private static IEnumerable<Member> Members(AudioObject o, (double[] P, double Yaw, double Pitch) frame, PointOfView? user, double start)
    {
        var here = Compose(frame, Of(o.AudioObjectSpaceTime));
        var hereStart = start + StartOf(o.AudioObjectSpaceTime);
        foreach (var b in o.BasicAudioObjects ?? [])
            if (b.BAObjectIDOrBAObject is { } bao)
            {
                var (p, yaw, pitch) = Compose(here, Of(b.BasicAudioObjectSpaceTime));
                yield return new Member(bao, p, yaw, pitch, user, hereStart + StartOf(b.BasicAudioObjectSpaceTime), EndOf(b.BasicAudioObjectSpaceTime) is { } end ? hereStart + end : null);
            }
        foreach (var s in o.SubAudioObjects ?? [])
            if (s.SubAObjectIDOrSubAObject is { } sub)
                foreach (var m in Members(sub, Compose(here, Of(s.SubAudioObjectSpaceTime)), user, hereStart + StartOf(s.SubAudioObjectSpaceTime))) yield return m;
    }

    // The user a member is heard by, as the renderer takes it.
    public static SpatialRenderer.User UserOf(PointOfView? pov) =>
        pov is null
            ? new SpatialRenderer.User([0, 0, 0], 0, 0)
            : new SpatialRenderer.User(pov.CartPosition is { Length: >= 3 } c ? c : new double[3],
                                       pov.Orientation is { Length: >= 3 } o ? o[2] : 0,
                                       pov.Orientation is { Length: >= 3 } o2 ? o2[1] : 0);

    // A WAV (PCM 16 or 32 bit, IEEE float 32 bit; any channels) as mono at rate.
    public static float[] Mono(byte[] wav, int rate)
    {
        if (wav.Length < 44 || System.Text.Encoding.ASCII.GetString(wav, 0, 4) != "RIFF") return [];
        int channels = 1, sampleRate = rate, bits = 16, format = 1, pos = 12, offset = -1, length = 0;
        while (pos + 8 <= wav.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
            var len = BitConverter.ToInt32(wav, pos + 4);
            if (id == "fmt ") { format = BitConverter.ToInt16(wav, pos + 8); channels = BitConverter.ToInt16(wav, pos + 10); sampleRate = BitConverter.ToInt32(wav, pos + 12); bits = BitConverter.ToInt16(wav, pos + 22); }
            if (id == "data") { offset = pos + 8; length = Math.Min(len, wav.Length - offset); break; }
            pos += 8 + len + (len & 1);
        }
        if (offset < 0 || channels <= 0) return [];
        var bytes = bits / 8; var frames = length / (bytes * channels);
        var mono = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            double sum = 0;
            for (var c = 0; c < channels; c++)
            {
                var at = offset + (f * channels + c) * bytes;
                sum += format == 3 && bits == 32 ? BitConverter.ToSingle(wav, at)
                     : bits == 32 ? BitConverter.ToInt32(wav, at) / 2147483648.0
                     : BitConverter.ToInt16(wav, at) / 32768.0;
            }
            mono[f] = (float)(sum / channels);
        }
        if (sampleRate == rate) return mono;
        var outLength = (int)((long)mono.Length * rate / sampleRate);
        var resampled = new float[outLength];
        for (var i = 0; i < outLength; i++)
        {
            var x = (double)i * sampleRate / rate; var j = (int)x; var t = x - j;
            resampled[i] = (float)(mono[Math.Min(j, mono.Length - 1)] * (1 - t) + mono[Math.Min(j + 1, mono.Length - 1)] * t);
        }
        return resampled;
    }

    // Interleaved samples as a 16-bit PCM WAV.
    public static byte[] Wav(float[] interleaved, int channels, int rate)
    {
        var data = new byte[interleaved.Length * 2];
        for (var i = 0; i < interleaved.Length; i++)
            BitConverter.GetBytes((short)Math.Clamp(Math.Round(interleaved[i] * 32767.0), short.MinValue, short.MaxValue)).CopyTo(data, i * 2);
        var wav = new byte[44 + data.Length];
        void S(int at, string s) => System.Text.Encoding.ASCII.GetBytes(s).CopyTo(wav, at);
        void I(int at, int v) => BitConverter.GetBytes(v).CopyTo(wav, at);
        void H(int at, short v) => BitConverter.GetBytes(v).CopyTo(wav, at);
        S(0, "RIFF"); I(4, 36 + data.Length); S(8, "WAVE"); S(12, "fmt "); I(16, 16); H(20, 1); H(22, (short)channels);
        I(24, rate); I(28, rate * channels * 2); H(32, (short)(channels * 2)); H(34, 16); S(36, "data"); I(40, data.Length);
        data.CopyTo(wav, 44);
        return wav;
    }
}
