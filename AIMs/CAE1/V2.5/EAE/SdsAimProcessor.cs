using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Cae.Usc.Eae;

// CAE-SDS-V2.5, Speech Detection and Separation: the Audio Object of the Spherical Harmonic Decomposition Coefficients (and, if there is one, the Audio Scene Reference) in; the Audio Object
// in the transform domain with a channel for each source found, and the Audio Scene Geometry (Basic Audio Scene Descriptors) with where each is and when it is active, out.
//
// The page says the sources are "either a speech or a non-speech signal": the AIM finds the SOURCES, it does not tell speech from noise.
//   Detection of the directions: at each time-frequency point (100 Hz to 4 kHz) of enough energy a plane wave from the direction u gives the coefficients of the order one as sqrt(3) u times
//   that of the order zero, so Re(conj(a0) a1) / (sqrt(3) |a0|^2) is u - the direction of the intensity. The directions, weighted by the energy, are gathered in a histogram of 5 degrees; the
//   heaviest cell and the points near it are a source; they are taken away and the next is looked for, until what is left is less than 5 % of the whole or MaxSources sources are found.
//   Separation: a beam for each source, the weights found so that a plane wave from the direction of a source gives itself and, to the extent the order allows, nothing of the others (the
//   inverse of the harmonics of the directions, regularised); the beams are of the amplitude of the wave, and their number is at most the number of coefficients minus one.
//   Detection of the activity: a source is active in a block if its energy is within 20 dB of its loudest block and well above the noise (the median energy of the points), and one block more
//   on each side (the blocks overlap).
// The Audio Scene Reference (a polynomial in the sine or cosine of an angle) is read and given on, not used: the page does not say what its terms are the model OF.
public sealed class SdsAimProcessor(string instanceId, string shdKey, string asrKey, string transformKey, string geometryKey, string asrOutKey) : IAimProcessor
{
    public string InstanceId { get; } = instanceId;
    public int MaxSources { get; init; } = 4;

    private const double LowHertz = 100, HighHertz = 4000, Cell = 5, Cluster = 20, Share = 0.05, SourceShareOfBand = 0.15, EnergyOverNoise = 10, ActivityWindow = 0.01;

    public Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(shdKey, out var shdJson) || string.IsNullOrWhiteSpace(shdJson))
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, $"CAE-SDS-V2.5: no Spherical Harmonic Decomposition Coefficients on input port '{shdKey}'."));
        try
        {
            string? asr = null;
            if (message.Ports.TryGetValue(asrKey, out var a) && !string.IsNullOrWhiteSpace(a))
            {
                if ((JsonNode.Parse(a) as JsonObject)?["Header"]?.GetValue<string>() != "CAE-ASR-V1.0") throw new FormatException("The Audio Scene Reference is not a CAE-ASR-V1.0.");
                asr = a;
            }
            var (t, geometry) = TransformObject.Read(shdJson);
            var k = t.Channels; var order = (int)Math.Round(Math.Sqrt(k)) - 1;
            if (k < 4 || (order + 1) * (order + 1) != k) throw new FormatException($"The coefficients are {k}: it needs (N + 1)^2 of them, N at least one.");

            var (found, medianEnergy) = FindDirections(t, Math.Min(MaxSources, k - 1));
            if (found.Count == 0)                                                                                  // a quiet room is not a fault: nothing is given (no source, no scene)
                return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = "NoSoundSource", DataType = AudioObject.Header, Payload = "", Ports = new Dictionary<string, string>() });
            var weights = BeamWeights(found, order);
            var s = found.Count; var hop = t.BlockSize / 2;
            var beams = t.Blocks.Select(block =>
            {
                var channels = Enumerable.Range(0, s).Select(_ => new Complex[t.Bins]).ToArray();
                for (var i = 0; i < s; i++)
                    for (var b = 0; b < t.Bins; b++) { Complex sum = 0; for (var c = 0; c < k; c++) sum += weights[c, i] * block[c][b]; channels[i][b] = sum; }
                return channels;
            }).ToList();

            var lo = Math.Max(1, (int)Math.Ceiling(LowHertz * t.BlockSize / t.Rate)); var hi = Math.Min(t.Bins - 1, (int)(HighHertz * t.BlockSize / t.Rate));
            var sources = new List<SceneSource>();
            for (var i = 0; i < s; i++)
            {
                var energy = beams.Select(blk => Enumerable.Range(lo, hi - lo + 1).Sum(b => blk[i][b].Real * blk[i][b].Real + blk[i][b].Imaginary * blk[i][b].Imaginary)).ToArray();
                var peak = energy.Max(); var floor = EnergyOverNoise * medianEnergy * (hi - lo + 1);
                var active = energy.Select(e => e >= ActivityWindow * peak && e > floor).ToArray();
                var wide = active.Select((x, n) => x || (n > 0 && active[n - 1]) || (n + 1 < active.Length && active[n + 1])).ToArray();
                var spans = new List<(double, double)>();
                for (var n = 0; n < wide.Length; n++)
                {
                    if (!wide[n]) { continue; }
                    var m = n; while (m + 1 < wide.Length && wide[m + 1]) m++;
                    spans.Add((Math.Max(0, (n * hop - hop / 2.0) / t.Rate), Math.Min(t.Samples / (double)t.Rate, (m * hop + hop / 2.0) / t.Rate)));
                    n = m;
                }
                for (var n = 0; n < beams.Count; n++) if (!wide[n]) beams[n][i] = new Complex[t.Bins];            // what is not active is silence
                sources.Add(new SceneSource($"source-{i + 1}", found[i].Azimuth, found[i].Elevation, spans.ToArray()));
            }

            var transform = TransformObject.Make("sds-" + Guid.NewGuid().ToString("N")[..12], new TransformBlocks(t.Rate, t.BlockSize, t.Samples, beams), "Transform", geometry);
            var scene = SceneDescriptors.Make("sds-scene-" + Guid.NewGuid().ToString("N")[..8], sources);
            var ports = new Dictionary<string, string> { [transformKey] = transform, [geometryKey] = scene };
            if (asr is not null) ports[asrOutKey] = asr;
            return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = "BasicAudioObject", DataType = AudioObject.Header, Payload = transform, Ports = ports });
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "CAE-SDS-V2.5: " + ex.Message));
        }
    }

    // The directions the sound comes from (the loudest first) and the median energy of the points of the band.
    private static (List<(double Azimuth, double Elevation)>, double) FindDirections(TransformBlocks t, int most)
    {
        var lo = Math.Max(1, (int)Math.Ceiling(LowHertz * t.BlockSize / t.Rate)); var hi = Math.Min(t.Bins - 1, (int)(HighHertz * t.BlockSize / t.Rate));
        var energies = new List<double>(); var points = new List<(double X, double Y, double Z, double E)>();
        foreach (var block in t.Blocks)
            for (var b = lo; b <= hi; b++)
                energies.Add(block[0][b].Real * block[0][b].Real + block[0][b].Imaginary * block[0][b].Imaginary);
        if (energies.Count == 0) return ([], 0);
        var sorted = energies.OrderBy(e => e).ToArray(); var median = sorted[sorted.Length / 2];
        foreach (var block in t.Blocks)
            for (var b = lo; b <= hi; b++)
            {
                var w = block[0][b]; var e = w.Real * w.Real + w.Imaginary * w.Imaginary;
                if (e <= EnergyOverNoise * median || e <= 0) continue;
                double Along(int c) => (Complex.Conjugate(w) * block[c][b]).Real / (Math.Sqrt(3) * e);          // the coefficients of the order one: y, z, x
                double y = Along(1), z = Along(2), x = Along(3); var length = Math.Sqrt(x * x + y * y + z * z);
                if (length < 0.7 || length > 1.3) continue;                                                // not a plane wave: diffuse sound
                points.Add((x / length, y / length, z / length, e));
            }
        var total = points.Sum(p => p.E); var result = new List<(double, double)>();
        var band = energies.Sum();                                                                            // all the energy of the band: a source is a good part of it, not a few loud points of noise
        var azimuthCells = (int)(360 / Cell); var elevationCells = (int)(180 / Cell);
        while (result.Count < most && points.Count > 0)
        {
            var histogram = new double[azimuthCells, elevationCells];
            foreach (var p in points)
            {
                var az = Math.Atan2(p.Y, p.X) * 180 / Math.PI; var el = Math.Asin(Math.Clamp(p.Z, -1, 1)) * 180 / Math.PI;
                histogram[Math.Min(azimuthCells - 1, (int)((az + 180) / Cell)), Math.Min(elevationCells - 1, (int)((el + 90) / Cell))] += p.E;
            }
            double best = -1; int ba = 0, be = 0;
            for (var a = 0; a < azimuthCells; a++)
                for (var e = 0; e < elevationCells; e++)
                {
                    double sum = 0;
                    for (var da = -1; da <= 1; da++) for (var de = -1; de <= 1; de++) { var ee = e + de; if (ee < 0 || ee >= elevationCells) continue; sum += histogram[(a + da + azimuthCells) % azimuthCells, ee]; }
                    if (sum > best) { best = sum; ba = a; be = e; }
                }
            double Azimuth(int cell) => (cell + 0.5) * Cell - 180; double Elevation(int cell) => (cell + 0.5) * Cell - 90;
            (double X, double Y, double Z) Unit(double az, double el) => (Math.Cos(el * Math.PI / 180) * Math.Cos(az * Math.PI / 180), Math.Cos(el * Math.PI / 180) * Math.Sin(az * Math.PI / 180), Math.Sin(el * Math.PI / 180));
            var centre = Unit(Azimuth(ba), Elevation(be)); var limit = Math.Cos(Cluster * Math.PI / 180);
            var near = points.Where(p => p.X * centre.X + p.Y * centre.Y + p.Z * centre.Z >= limit).ToList();
            var weight = near.Sum(p => p.E);
            if (weight < Share * total || weight < SourceShareOfBand * band) break;
            double mx = near.Sum(p => p.X * p.E), my = near.Sum(p => p.Y * p.E), mz = near.Sum(p => p.Z * p.E); var norm = Math.Sqrt(mx * mx + my * my + mz * mz);
            result.Add((Math.Atan2(my, mx) * 180 / Math.PI, Math.Asin(Math.Clamp(mz / norm, -1, 1)) * 180 / Math.PI));
            points = points.Where(p => p.X * centre.X + p.Y * centre.Y + p.Z * centre.Z < limit).ToList();
        }
        return (result, median);
    }

    // The weights, coefficient by source: Y (Y^T Y + eps I)^-1, Y the harmonics at the directions (azimuth and elevation in degrees).
    public static Complex[,] BeamWeights(IReadOnlyList<(double Azimuth, double Elevation)> directions, int order)
    {
        var k = SphericalHarmonics.Count(order); var s = directions.Count;
        var y = directions.Select(d => SphericalHarmonics.Real(order, d.Azimuth * Math.PI / 180, d.Elevation * Math.PI / 180)).ToArray();     // y[source][coefficient]
        var gram = new Complex[s, s]; double trace = 0;
        for (var i = 0; i < s; i++) for (var j = 0; j < s; j++) { double sum = 0; for (var c = 0; c < k; c++) sum += y[i][c] * y[j][c]; gram[i, j] = sum; if (i == j) trace += sum; }
        for (var i = 0; i < s; i++) gram[i, i] += 1e-3 * trace / s;
        var weights = new Complex[k, s];
        for (var i = 0; i < s; i++)
        {
            var e = new Complex[s]; e[i] = 1; var z = SphericalHarmonics.Solve(gram, e);
            for (var c = 0; c < k; c++) { Complex sum = 0; for (var j = 0; j < s; j++) sum += y[j][c] * z[j]; weights[c, i] = sum; }
        }
        return weights;
    }
}
