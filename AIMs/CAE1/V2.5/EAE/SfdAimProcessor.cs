using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Cae.Usc.Eae;

// CAE-SFD-V2.5, Sound Field Description: the Microphone Array Geometry and the Audio Object in the transform domain (the blocks of the array's channels) in; the Audio Object of the
// Spherical Harmonic Decomposition Coefficients out - the sound field as the coefficients of the spherical harmonics up to the order the array can resolve (an array of M microphones:
// (N + 1)^2 <= M).
//
// For each frequency the pressures at the microphones are those of the sound field on an OPEN SPHERE of the microphones' radii: p_m = sum a_nm b_n(k r_m) Y_nm(direction of m), b_n the
// radial function 4 pi i^n j_n(kr). The coefficients are the regularised least-squares solution, found once for each frequency. A rigid sphere (the Eigenmike) has other radial
// functions, and a microphone is taken as a point and an omnidirectional one: the specification does not say what the Implementation shall assume, and the arrays differ.
// The decomposition is right while the wave number times the radius of the array is below about one: above it an array with few microphones cannot tell an order from a higher one
// (spatial aliasing), whatever the Implementation.
public sealed class SfdAimProcessor(string instanceId, string geometryKey, string transformKey, string outputKey) : IAimProcessor
{
    public const double SpeedOfSound = 343.0;
    public string InstanceId { get; } = instanceId;

    public Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(geometryKey, out var geometryJson) || string.IsNullOrWhiteSpace(geometryJson) ||
            !message.Ports.TryGetValue(transformKey, out var transformJson) || string.IsNullOrWhiteSpace(transformJson))
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "CAE-SFD-V2.5: it needs the Microphone Array Geometry and the Audio Object in the transform domain."));
        try
        {
            var geometry = ArrayGeometry.FromJson(JsonNode.Parse(geometryJson));
            var (t, _) = TransformObject.Read(transformJson);
            if (t.Channels != geometry.Count) throw new FormatException($"The array has {geometry.Count} microphones and the transform {t.Channels} channels.");
            var order = SphericalHarmonics.OrderOf(geometry.Count);
            var decoder = Decoder(geometry, order, t.Rate, t.BlockSize);                                   // for each bin, the matrix from the microphones to the coefficients
            var k = SphericalHarmonics.Count(order);
            var blocks = t.Blocks.Select(block =>
            {
                var result = Enumerable.Range(0, k).Select(_ => new Complex[t.Bins]).ToArray();
                for (var b = 0; b < t.Bins; b++)
                    for (var c = 0; c < k; c++) { Complex s = 0; for (var m = 0; m < geometry.Count; m++) s += decoder[b][c, m] * block[m][b]; result[c][b] = s; }
                return result;
            }).ToList();
            var text = TransformObject.Make("sfd-" + Guid.NewGuid().ToString("N")[..12], new TransformBlocks(t.Rate, t.BlockSize, t.Samples, blocks), "SHD", JsonNode.Parse(geometryJson));
            return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = "BasicAudioObject", DataType = AudioObject.Header, Payload = text, Ports = new Dictionary<string, string> { [outputKey] = text } });
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "CAE-SFD-V2.5: " + ex.Message));
        }
    }

    // The matrix, for each bin, that gives the coefficients from the microphones: (B^H B + lambda I)^-1 B^H, B the matrix of the radial functions and the harmonics of the microphones.
    public static Complex[][,] Decoder(ArrayGeometry geometry, int order, int rate, int blockSize)
    {
        var m = geometry.Count; var k = SphericalHarmonics.Count(order); var bins = blockSize / 2 + 1;
        var directions = geometry.Positions.Select(p =>
        {
            var r = Math.Sqrt(p[0] * p[0] + p[1] * p[1] + p[2] * p[2]);
            return (Radius: r, Y: SphericalHarmonics.Real(order, Math.Atan2(p[1], p[0]), r > 0 ? Math.Asin(Math.Clamp(p[2] / r, -1, 1)) : 0));
        }).ToArray();
        var decoder = new Complex[bins][,];
        for (var bin = 0; bin < bins; bin++)
        {
            var wavenumber = 2 * Math.PI * bin * rate / blockSize / SpeedOfSound;
            var b = new Complex[m, k];
            for (var i = 0; i < m; i++)
                for (var n = 0; n <= order; n++)
                    for (var mm = -n; mm <= n; mm++) b[i, n * n + n + mm] = SphericalHarmonics.Radial(n, wavenumber * directions[i].Radius) * directions[i].Y[n * n + n + mm];
            var normal = new Complex[k, k]; double trace = 0;
            for (var p = 0; p < k; p++) for (var q = 0; q < k; q++) { Complex s = 0; for (var i = 0; i < m; i++) s += Complex.Conjugate(b[i, p]) * b[i, q]; normal[p, q] = s; if (p == q) trace += s.Real; }
            var lambda = 1e-4 * trace / k + 1e-12;                                                         // the regularisation: it bounds the gain where a coefficient is hardly in the pressures
            for (var p = 0; p < k; p++) normal[p, p] += lambda;
            var w = new Complex[k, m];
            for (var i = 0; i < m; i++)
            {
                var rhs = new Complex[k]; for (var p = 0; p < k; p++) rhs[p] = Complex.Conjugate(b[i, p]);
                var column = SphericalHarmonics.Solve(normal, rhs);
                for (var p = 0; p < k; p++) w[p, i] = column[p];
            }
            decoder[bin] = w;
        }
        return decoder;
    }
}
