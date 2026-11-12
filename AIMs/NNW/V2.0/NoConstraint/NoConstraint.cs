using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Mpai.Onnx;

namespace Mpai.Nnw.NoConstraint;

// THE NoConstraint WATERMARKING METHOD (IMT: patent application "Procédé pour protéger par traçage un réseau artificiel de neurones", filed 13 May 2025; C. De Sousa Trias,
// "Robust Watermarking for Neural Networks in Multimedia Tasks", Chapter VII; analysed in M3304). The use of the method in this software awaits the licensing declaration of IMT.
//
//   Insertion: a subset of the weights is selected BY POSITION, and their values are REPLACED by the watermark W_X, values in [-0.2, 0.2]. The positions are secret.
//              Nothing else is done: the training (if any) goes on as the task requires, with no term added to its loss.
//   Detection: the weights at the same positions are read and their Pearson correlation with W_X is compared with a threshold.
//   Fingerprinting (the variant): the selected weights are read and kept, not replaced; detection is the same.
//
// WHAT THE DOCUMENTS LEAVE OPEN, AND WHAT IS DONE HERE (choices of this implementation, listed in M3304 section 8):
//   - The key. NNW-KEY carries a sequence of reals. They are hashed (SHA-256) into a seed, from which a stream of pseudo-random numbers gives the positions and W_X.
//     Whoever holds the key can find the positions; the key is therefore given only to the party that inserts and recognises the mark.
//   - The payload. The documents detect the presence of a KNOWN W_X; they carry no bits. The patent says that 7,500 values carry 100 bits, i.e. 75 values a bit; here a bit
//     is carried by a number of values chosen by whoever inserts the mark (ValuesPerBit, 64, unless the Locator's method name says otherwise: the name carries both
//     numbers, so that the extraction knows them; a small network needs fewer values a bit, see the HSP demo): W_X = s_k * r, with r the key's pseudo-random values (random sign) and s_k = +1 for a 1 bit, -1 for a 0 bit.
//   - The values. The patent's example draws W_X in [-0.2, 0.2] (ResNet18). Here the magnitudes are drawn between half and twice the standard deviation of the weights
//     of the tensors that carry the mark, so that the replaced weights look like the others (in the small network of the HSP demo, whose weights have a spread of 0.05,
//     values up to 0.2 cost 10 points of accuracy).
//     A bit is read back as the sign of the correlation of its values with r. The whole W_X, rebuilt from the bits read, is then correlated with what was read (Pearson).
//   - The threshold. The thesis compares the correlation with a KNOWN W_X with the critical value of a correlation test at the 95% level (Threshold, used by fingerprints).
//     Where the bits are read first, the presence is tested on the strength of the bits instead (see Detection). A model that does not carry the mark passes it 5 times
//     in 100, but its bits then agree with a payload of B bits only once in 2^B.
//   - The positions. Among the float tensors of two or more dimensions; the first and the last of them (the input and output layers) are avoided, as the patent advises,
//     where at least three are left. A Locator may name the tensors instead (Targets).
public static class NoConstraint
{
    public const int ValuesPerBit = 64;
    public const double Smallest = 0.5, Largest = 2.0;            // the magnitudes of W_X, in standard deviations of the weights that carry it
    public const double Confidence95 = 1.6449;

    public const string MethodPrefix = "NoConstraint";
    public static string MethodName(int bits, int valuesPerBit = ValuesPerBit) => $"{MethodPrefix} (IMT): {bits} bits, {valuesPerBit} values a bit, Pearson detection";

    // The number of bits, and of values a bit, of a method named by MethodName; null where it is not this method.
    public static int? BitsOf(string? method) => Numbers(method)?.Bits;
    public static int? ValuesPerBitOf(string? method) => Numbers(method)?.ValuesPerBit;

    private static (int Bits, int ValuesPerBit)? Numbers(string? method)
    {
        if (method is null || !method.StartsWith(MethodPrefix, StringComparison.Ordinal)) return null;
        var m = Regex.Match(method, @"(\d+) bits, (\d+) values a bit");
        if (!m.Success) return null;
        int bits = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), vpb = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return bits > 0 && vpb > 1 ? (bits, vpb) : null;
    }

    // What a model says of a mark: the bits read; the Pearson correlation of the values read with the W_X those bits give (the measure of the documents); and the test of
    // presence. The documents correlate with a KNOWN W_X. Here the bits are read from the weights first, so that correlation is pushed upwards by the reading itself (a model
    // with no mark gives about 0.08 for 64 values a bit): the presence is therefore tested on the strength of each bit, z_k (its values against r, in standard deviations),
    // whose mean |z_k| is about 0.80 by chance; the mark is present where the mean is above that at the 95% level.
    public sealed record Detection(bool[] Bits, double Correlation, double Strength, double Threshold)
    {
        public bool Present => Strength >= Threshold;
        public string Payload => new(Bits.Select(b => b ? '1' : '0').ToArray());
        public bool Carries(bool[] payload) => Present && payload.SequenceEqual(Bits);
    }

    // ---- the key ----

    public static byte[] Seed(IEnumerable<double> keyData) => SHA256.HashData(keyData.SelectMany(BitConverter.GetBytes).ToArray());

    private sealed class Stream
    {
        private readonly byte[] _seed; private byte[] _block = Array.Empty<byte>(); private int _at = 32; private long _counter;
        public Stream(byte[] seed, string purpose) => _seed = SHA256.HashData(seed.Concat(System.Text.Encoding.UTF8.GetBytes(purpose)).ToArray());
        public uint Next()
        {
            if (_at + 4 > _block.Length) { _block = SHA256.HashData(_seed.Concat(BitConverter.GetBytes(_counter++)).ToArray()); _at = 0; }
            var v = BitConverter.ToUInt32(_block, _at); _at += 4; return v;
        }
        public double Unit() => Next() / 4294967296.0;                                                   // [0, 1)
    }

    // The positions (among `available` weights) and the values r, from the key; the same key and sizes give the same plan.
    public static (int[] Positions, float[] R) Plan(byte[] seed, int available, int count, double scale = 1)
    {
        if (count > available / 2) throw new InvalidOperationException($"the mark needs {count} weights, and only {available} are available: at most half of them are used.");
        var positions = new int[count]; var taken = new HashSet<int>(); var s = new Stream(seed, "positions");
        for (var i = 0; i < count; i++)
        {
            int p; do p = (int)(s.Next() % (uint)available); while (!taken.Add(p));
            positions[i] = p;
        }
        var v = new Stream(seed, "values"); var r = new float[count];
        for (var i = 0; i < count; i++)
        {
            var magnitude = scale * (Smallest + (Largest - Smallest) * v.Unit());
            r[i] = (float)((v.Next() & 1) == 0 ? magnitude : -magnitude);
        }
        return (positions, r);
    }

    // ---- watermarking ----

    // The weights with the mark of the payload in them (a copy).
    public static float[] Insert(float[] weights, byte[] seed, bool[] payload, int valuesPerBit = ValuesPerBit)
    {
        if (payload.Length == 0) throw new ArgumentException("the payload has no bits.");
        if (valuesPerBit < 2) throw new ArgumentException("a bit needs at least two values.");
        var (pos, r) = Plan(seed, weights.Length, payload.Length * valuesPerBit, Spread(weights));
        var w = (float[])weights.Clone();
        for (var j = 0; j < pos.Length; j++) w[pos[j]] = (payload[j / valuesPerBit] ? 1f : -1f) * r[j];
        return w;
    }

    // What a model says of a mark of `bits` bits made with this key: the bits read, and the correlation of the values read with the W_X those bits give.
    public static Detection Extract(float[] weights, byte[] seed, int bits, int valuesPerBit = ValuesPerBit)
    {
        var n = bits * valuesPerBit;
        if (n > weights.Length / 2) return new Detection(new bool[bits], 0, 0, StrengthThreshold(bits));
        var (pos, r) = Plan(seed, weights.Length, n);
        var read = pos.Select(p => weights[p]).ToArray();
        var mean = read.Average(x => (double)x); var sd = Math.Sqrt(read.Sum(x => (x - mean) * (x - mean)) / n);
        var b = new bool[bits]; double strength = 0;
        for (var k = 0; k < bits; k++)
        {
            double c = 0, rr = 0;
            for (var j = k * valuesPerBit; j < (k + 1) * valuesPerBit; j++) { c += (read[j] - mean) * r[j]; rr += r[j] * r[j]; }
            b[k] = c > 0;
            strength += sd == 0 ? 0 : Math.Abs(c) / (sd * Math.Sqrt(rr));
        }
        var wx = new float[n];
        for (var j = 0; j < n; j++) wx[j] = (b[j / valuesPerBit] ? 1f : -1f) * r[j];
        return new Detection(b, Pearson(wx, read), strength / bits, StrengthThreshold(bits));
    }

    // The mean of |z_k| over B bits where there is no mark: about sqrt(2/pi) = 0.798, with a standard deviation of sqrt(1 - 2/pi) / sqrt(B); the 95% level above it.
    public static double StrengthThreshold(int bits) => Math.Sqrt(2 / Math.PI) + Confidence95 * Math.Sqrt(1 - 2 / Math.PI) / Math.Sqrt(bits);

    // The standard deviation of the weights (the scale of W_X; 0.1 where the weights are all equal).
    public static double Spread(float[] w)
    {
        var mean = w.Average(x => (double)x); var sd = Math.Sqrt(w.Sum(x => (x - mean) * (x - mean)) / w.Length);
        return sd > 0 ? sd : 0.1;
    }

    // ---- fingerprinting (the variant: nothing is replaced) ----

    public static float[] Fingerprint(float[] weights, byte[] seed, int count)
    {
        var (pos, _) = Plan(seed, weights.Length, count);
        return pos.Select(p => weights[p]).ToArray();
    }

    public static double MatchFingerprint(float[] weights, byte[] seed, float[] fingerprint)
    {
        if (fingerprint.Length > weights.Length / 2) return 0;
        var (pos, _) = Plan(seed, weights.Length, fingerprint.Length);
        return Pearson(fingerprint, pos.Select(p => weights[p]).ToArray());
    }

    // ---- statistics ----

    public static double Pearson(float[] a, float[] b)
    {
        double ma = a.Average(x => (double)x), mb = b.Average(x => (double)x), sab = 0, saa = 0, sbb = 0;
        for (var i = 0; i < a.Length; i++) { var x = a[i] - ma; var y = b[i] - mb; sab += x * y; saa += x * x; sbb += y * y; }
        return saa == 0 || sbb == 0 ? 0 : sab / Math.Sqrt(saa * sbb);
    }

    // The critical correlation of n pairs at the 95% level (one-sided): r = t / sqrt(n - 2 + t^2).
    public static double Threshold(int n) => Confidence95 / Math.Sqrt(n - 2 + Confidence95 * Confidence95);

    // ---- the tensors of an ONNX model that carry the mark ----

    public static List<OnnxFloats.Tensor> Targets(byte[] onnx, IReadOnlyCollection<string>? names = null)
    {
        var all = OnnxFloats.Weights(onnx);
        if (names is { Count: > 0 }) return all.Where(t => names.Contains(t.Name)).ToList();
        var layers = all.Where(t => t.Dims.Length >= 2).ToList();
        return layers.Count >= 3 ? layers.Skip(1).Take(layers.Count - 2).ToList() : layers;
    }

    public static byte[] InsertOnnx(byte[] onnx, byte[] seed, bool[] payload, IReadOnlyCollection<string>? names = null, int valuesPerBit = ValuesPerBit)
    {
        var t = Targets(onnx, names);
        if (t.Count == 0) throw new InvalidOperationException("the model has no float tensor of two or more dimensions to carry a mark.");
        return OnnxFloats.Write(onnx, t, Insert(OnnxFloats.Read(onnx, t), seed, payload, valuesPerBit));
    }

    public static Detection ExtractOnnx(byte[] onnx, byte[] seed, int bits, IReadOnlyCollection<string>? names = null, int valuesPerBit = ValuesPerBit)
    {
        var t = Targets(onnx, names);
        return Extract(t.Count == 0 ? Array.Empty<float>() : OnnxFloats.Read(onnx, t), seed, bits, valuesPerBit);
    }

    public static bool[] Bits(string payload) =>
        payload.All(c => c is '0' or '1') && payload.Length > 0 ? payload.Select(c => c == '1').ToArray() : throw new FormatException("a payload is a string of 0 and 1.");
}
