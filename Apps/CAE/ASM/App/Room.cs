using System.Text.Json.Nodes;

using Mpai.Core;

namespace Mpai.Cae.AsmApp;

// A ROOM: the box of a Closed Space (OSD-CSP) - one Right Parallelepiped - and how much its walls absorb. Height is the distance between floor and ceiling.
public sealed record Room(double X0, double Y0, double X1, double Y1, double Height, double Absorption)
{
    public double Width => X1 - X0;
    public double Depth => Y1 - Y0;
    public double Volume => Width * Depth * Height;
    public double Surface => 2 * (Width * Depth + Width * Height + Depth * Height);

    // How long the sound takes to die away (60 dB), by Sabine's formula.
    public double Rt60 => Math.Clamp(0.161 * Volume / Math.Max(1e-6, Absorption * Surface), 0.1, 6);

    // The Closed Space of a scene, if it holds one in full: its first Right Parallelepiped and the mean absorption of its default material.
    public static Room? From(JsonNode? scene)
    {
        try
        {
            var closed = scene?["ClosedSpace"]?[0];
            var p = closed?["Shape"]?[0]?["SimpleLocationData"]?[0]?["RightParallelepiped"]?["CartPositions"]?.AsArray();
            if (p is null || p.Count < 4) return null;
            double V(int i, int j) => p[i]![j]!.GetValue<double>();
            var absorption = closed?["DefaultMaterial"]?[0]?["Acoustic"]?["Absorption"]?.AsArray() is { Count: > 0 } a ? a.Average(b => b!["Fraction"]!.GetValue<double>()) : 0.15;
            return new Room(V(0, 0), V(0, 1), V(1, 0), V(2, 1), V(3, 2) - V(0, 2), absorption);
        }
        catch { return null; }
    }

    // The same room with walls that absorb more or less (the mean fraction of the sound energy they take): the tail gets shorter or longer.
    public Room WithAbsorption(double absorption) => this with { Absorption = Math.Clamp(absorption, 0.02, 0.9) };

    // The absorption that gives the tail asked for, by Sabine's formula.
    public Room WithTail(double seconds) => WithAbsorption(0.161 * Volume / (Math.Max(0.1, seconds) * Surface));

    // The Closed Space of a room as the standard writes it: the box as a Right Parallelepiped (a vertex, then its Right, Front and Up neighbours), walls that
    // absorb little at low frequencies and more at high ones, around the mean absorption.
    public JsonObject ToClosedSpace()
    {
        JsonArray V(double x, double y, double z) => new(x, y, z);
        JsonArray Band(double f, params double[] fractions) => new(fractions.Select((fr, i) => (JsonNode)new JsonObject { ["FrequencyHz"] = new[] { 400.0, 2500.0, 15000.0 }[i], ["Fraction"] = Math.Round(Math.Clamp(fr, 0, 1), 3) }).ToArray());
        var a = Absorption;
        return new JsonObject
        {
            ["Header"] = "OSD-CSP-V1.5", ["ClosedSpaceID"] = "CSP-" + Guid.NewGuid().ToString("N")[..8],
            ["Shape"] = new JsonArray(new JsonObject
            {
                ["Header"] = "OSD-SLC-V1.5", ["SimpleLocationID"] = "SLC-" + Guid.NewGuid().ToString("N")[..8],
                ["SimpleLocationData"] = new JsonArray(new JsonObject
                {
                    ["RightParallelepiped"] = new JsonObject
                    {
                        ["Header"] = "OSD-RPP-V1.5", ["RightParallelepipedID"] = "RPP-1", ["Dimensions"] = "3D",
                        ["CartPositions"] = new JsonArray(V(X0, Y0, 0), V(X1, Y0, 0), V(X0, Y1, 0), V(X0, Y0, Height))
                    }
                })
            }),
            ["DefaultMaterial"] = new JsonArray(new JsonObject
            {
                ["Header"] = "OSD-MTR-V1.5", ["MaterialID"] = "plaster",
                ["Acoustic"] = new JsonObject
                {
                    ["Absorption"] = Band(0, a * 0.67, a, a * 1.33), ["Scattering"] = 0.3, ["Transmission"] = Band(0, 0.0, 0.0, 0.0),
                    ["BandScheme"] = "Other", ["Incidence"] = "Random"
                }
            })
        };
    }
}

// WHAT THE SCENE DOES TO SOUND, from its Acoustic Profile (the Scene part) and its room: the reverberation time (RT60; or twice RT30, three times RT20, or the EDT; or,
// with a room, Sabine's from its walls), when the early and the late reflections come, how dense the tail is (diffusion) and how much the walls take from it
// (absorption). Where nothing says there is reverberation, there is none. The Doppler effect is kept in the profile and not rendered: nothing in the scene moves.
public sealed record Acoustics(double Rt60, double? Early, double? Late, double Diffusion, double Absorption, double Volume, double? Edt)
{
    // a plot of rows [frequency, seconds]: the mean of the seconds
    private static double? Mean(Plot? plot) => plot?.Data?.TwoD is { Count: > 0 } rows && rows.Where(r => r.Count >= 2).Select(r => r[1]).ToList() is { Count: > 0 } v ? v.Average() : null;

    public static Acoustics? From(AcousticProfile? scene, Room? room)
    {
        var reverberation = scene?.Reverberation?.FirstOrDefault();
        var walls = room is null ? null : scene?.Absorption is { } a ? room.WithAbsorption(a) : room;
        double? rt = Mean(reverberation?.RT60) ?? Mean(reverberation?.RT30) * 2 ?? Mean(reverberation?.RT20) * 3 ?? reverberation?.EDT ?? walls?.Rt60;
        if (rt is not { } seconds) return null;
        var reflections = scene?.Reflectivity?.FirstOrDefault();
        return new Acoustics(Math.Clamp(seconds, 0.1, 8), reflections?.EarlyReflectionTime, reflections?.LateReflectionTime, scene?.Diffusion ?? 0.5,
                             scene?.Absorption ?? room?.Absorption ?? 0.15, room?.Volume ?? 60, reverberation?.EDT);
    }
}

// THE ROOM HEARD: what a room does to sound is to keep it going - the tail. Schroeder's reverberator (four combs in parallel, two all-passes in series) per
// channel, its delays a little apart from one ear to the other, its feedback set so that the tail dies away in the reverberation time.
public static class RoomReverb
{
    public static byte[] ApplyToWav(byte[] wav, Acoustics acoustics)
    {
        var (rate, bits, channels) = Studio.Format(wav);
        if (bits != 16 || channels < 1 || wav.Length <= 44) return wav;
        var n = (wav.Length - 44) / 2; var samples = new float[n];
        for (var i = 0; i < n; i++) samples[i] = BitConverter.ToInt16(wav, 44 + 2 * i) / 32768f;
        return Mpai.SpatialAudio.SceneAudio.Wav(Apply(samples, channels, rate, acoustics), channels, rate);
    }

    public static float[] Apply(float[] interleaved, int channels, int rate, Acoustics a, double? wetLevel = null)
    {
        var rt60 = a.Rt60; var frames = interleaved.Length / channels;
        var late = (int)(Math.Clamp(a.Late ?? 0, 0, 0.5) * rate);                 // the tail begins this long after the sound
        var wet = (wetLevel ?? Math.Clamp(0.5 + 0.35 * rt60, 0.5, 1.2)) * (a.Edt is { } edt ? Math.Clamp(edt / rt60, 0.6, 1.4) : 1.0);   // a short early decay: a drier start
        var total = frames + late + (int)(Math.Min(rt60, 3.0) * rate);
        var output = new float[total * channels];
        Array.Copy(interleaved, output, interleaved.Length);
        var feed = new float[total];
        for (var f = 0; f < frames; f++) { double s = 0; for (var c = 0; c < channels; c++) s += interleaved[f * channels + c]; feed[f + late] = (float)(s / channels); }

        var scale = Math.Clamp(Math.Cbrt(a.Volume) / 5.0, 0.6, 2.0);              // a bigger room, longer delays
        var damp = (float)Math.Clamp(0.1 + a.Absorption * 1.5, 0.1, 0.8);          // the walls take the high frequencies first, the more the more they absorb
        var allpass = (float)Math.Clamp(0.3 + 0.4 * a.Diffusion, 0.2, 0.85);       // more diffusion, a denser tail
        double[] combMs = [29.7, 37.1, 41.1, 43.7];
        for (var c = 0; c < channels; c++)
        {
            var tail = new float[total];
            foreach (var ms in combMs)
            {
                var d = Math.Max(1, (int)((ms * scale + 0.9 * c) / 1000 * rate));
                var g = (float)Math.Pow(10, -3.0 * d / rate / rt60);               // feedback for the tail to fall 60 dB in rt60
                var buffer = new float[d]; var at = 0; float lowpass = 0;
                for (var i = 0; i < total; i++)
                {
                    var y = buffer[at];
                    lowpass = y * (1 - damp) + lowpass * damp;
                    buffer[at] = feed[i] + lowpass * g;
                    tail[i] += y / 2f;
                    if (++at == d) at = 0;
                }
            }
            foreach (var ms in new[] { 5.0, 1.7 })                                   // all-passes: density without colour
            {
                var d = Math.Max(1, (int)((ms + 0.3 * c) / 1000 * rate)); var buffer = new float[d]; var at = 0;
                for (var i = 0; i < total; i++)
                {
                    var delayed = buffer[at]; var x = tail[i];
                    buffer[at] = x + delayed * allpass; tail[i] = delayed - x * allpass;
                    if (++at == d) at = 0;
                }
            }
            for (var i = 0; i < total; i++) output[i * channels + c] += (float)(wet * tail[i]);

            // the early reflections: three echoes of the sound, the first at the early reflection time
            if (a.Early is { } early && early > 0)
                foreach (var (k, gain) in new[] { (1.0, 0.5), (1.4, 0.35), (1.9, 0.25) })
                {
                    var d = (int)((early * k + 0.0007 * c) * rate);
                    for (var i = 0; i < frames && i + d < total; i++) output[(i + d) * channels + c] += (float)(gain * 0.6 * interleaved[i * channels + c]);
                }
        }
        var peak = output.Length == 0 ? 1 : output.Max(Math.Abs);
        if (peak > 0.95f) for (var i = 0; i < output.Length; i++) output[i] *= 0.95f / peak;
        return output;
    }
}
