using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Mpai.Aih.Hfl;

// THE WATERMARK OF THE BACK END'S MODEL (federated learning, step 5).
//
// WHAT THE STANDARD ALLOWS. The Neural Network Watermarking schemas (NNW V1.2: Payload NNW-PLD, Locator NNW-WLC; the Post-Training Watermark Insertion and Watermark
// Extraction AIMs) leave the METHOD OPEN: the Locator names "the watermarking method that inserted the watermark, as its Implementer names it" and an extraction AIM can retrieve
// the Payload only if its Implementation implements that method; Targets name the tensors that carry it (as named in the model file); the secret key is never carried, only a
// KeyReference. So a method of my own is allowed, if it is named in the Locator. This is the SIMPLEST one (a choice, listed in the report): a spread-spectrum, additive,
// blind-detection mark on the convolution weights of the FROZEN network (the float tensors of four or more dimensions of the ONNX file).
//
//   Payload: K bits (the same for every End User of a round).  Key: a secret string.  For each bit k a pseudo-random sequence p_k of +1/-1, one value for every target weight,
//   made from SHA-256(key, k).  Embedding: w_i += a * sum_k s_k p_k,i  with s_k = +1 for a 1 bit and -1 for a 0 bit and a = Strength * (standard deviation of the target weights).
//   Extraction (without the original model): c_k = sum_i w_i p_k,i; the bit is 1 if c_k > 0. The mark is FOUND if all but at most one bit are those of the Payload (a model
//   that is not marked agrees on about half of them).
//
// The ONNX file is edited in place: the bytes of the raw data of the target tensors are replaced by bytes of the same length, so nothing else of the file changes.
public static class ModelWatermark
{
    public const string Method = "AIH-demo spread-spectrum additive mark on the convolution weights, blind detection";
    public const double Strength = 0.07;

    public sealed record Tensor(string Name, int[] Dims, int Offset, int Length);
    public sealed record Detection(int Matching, int Bits, bool Found);

    // ---- the ONNX file: only the initializers are looked at ----

    public static List<Tensor> Weights(byte[] onnx)
    {
        var found = new List<Tensor>();
        Walk(onnx, 0, onnx.Length, field: (f, wire, start, len) =>
        {
            if (f == 7 && wire == 2) Walk(onnx, start, start + len, (g, w2, s2, l2) =>                    // ModelProto.graph
            {
                if (g == 5 && w2 == 2) found.Add(ReadTensor(onnx, s2, s2 + l2));                          // GraphProto.initializer
            });
        });
        return found.Where(t => t.Length > 0).ToList();
    }

    private static Tensor ReadTensor(byte[] b, int start, int end)
    {
        string name = ""; var dims = new List<int>(); int type = 0, off = 0, len = 0;
        Walk(b, start, end, (f, wire, s, l) =>
        {
            switch (f)
            {
                case 1 when wire == 0: dims.Add((int)l); break;                                           // dims, not packed (value in l)
                case 1 when wire == 2: { var p = s; while (p < s + l) dims.Add((int)ReadVarint(b, ref p)); break; }
                case 2 when wire == 0: type = (int)l; break;
                case 8 when wire == 2: name = Encoding.UTF8.GetString(b, s, l); break;
                case 9 when wire == 2: off = s; len = l; break;                                           // raw_data
            }
        });
        return new Tensor(name, dims.ToArray(), off, type == 1 ? len : 0);                                // only float32
    }

    // Walks the fields of a message: for a varint the value is passed in the last argument, for a length-delimited field the start and the length.
    private static void Walk(byte[] b, int start, int end, Action<int, int, int, int> field)
    {
        var p = start;
        while (p < end)
        {
            var key = ReadVarint(b, ref p); var f = (int)(key >> 3); var wire = (int)(key & 7);
            switch (wire)
            {
                case 0: { var v = ReadVarint(b, ref p); field(f, 0, 0, (int)v); break; }
                case 1: p += 8; break;
                case 2: { var l = (int)ReadVarint(b, ref p); field(f, 2, p, l); p += l; break; }
                case 5: p += 4; break;
                default: throw new FormatException("not an ONNX file.");
            }
        }
    }

    private static ulong ReadVarint(byte[] b, ref int p)
    {
        ulong v = 0; var shift = 0;
        while (true) { var x = b[p++]; v |= (ulong)(x & 0x7F) << shift; if ((x & 0x80) == 0) return v; shift += 7; }
    }

    // The tensors that carry the mark: float, four or more dimensions (convolution weights).
    public static List<Tensor> Targets(byte[] onnx) => Weights(onnx).Where(t => t.Dims.Length >= 4).ToList();

    // ---- the mark ----

    private static IEnumerable<sbyte> Sequence(string key, int bit, int count)
    {
        var seed = SHA256.HashData(Encoding.UTF8.GetBytes(key + "#" + bit));
        var block = 0; var left = 0; byte[] hash = seed; var i = 0;
        while (count-- > 0)
        {
            if (left == 0) { hash = SHA256.HashData(seed.Concat(BitConverter.GetBytes(block++)).ToArray()); left = 256; i = 0; }
            yield return (hash[i / 8] >> (i % 8) & 1) == 1 ? (sbyte)1 : (sbyte)-1; i++; left--;
        }
    }

    private static float[] Read(byte[] onnx, List<Tensor> targets)
    {
        var n = targets.Sum(t => t.Length / 4); var w = new float[n]; var k = 0;
        foreach (var t in targets) for (var i = 0; i < t.Length / 4; i++) w[k++] = BitConverter.ToSingle(onnx, t.Offset + 4 * i);
        return w;
    }

    // Puts the payload (bits, true = 1) in the weights; returns the marked file and the Locator (NNW-WLC-V1.2) that says where and how, never the key.
    public static (byte[] Onnx, JsonObject Locator) Insert(byte[] onnx, string key, string keyReference, bool[] payload)
    {
        var targets = Targets(onnx);
        if (targets.Count == 0) throw new InvalidOperationException("the model has no convolution weights to carry a mark.");
        var w = Read(onnx, targets);
        var mean = w.Average(); var sd = Math.Sqrt(w.Sum(v => (v - mean) * (v - mean)) / w.Length);
        var a = (float)(Strength * sd);
        var delta = new float[w.Length];
        for (var k = 0; k < payload.Length; k++)
        {
            var s = payload[k] ? 1f : -1f; var i = 0;
            foreach (var p in Sequence(key, k, w.Length)) delta[i++] += s * p;
        }
        var marked = (byte[])onnx.Clone(); var at = 0;
        foreach (var t in targets) for (var i = 0; i < t.Length / 4; i++) BitConverter.GetBytes(w[at] + a * delta[at++]).CopyTo(marked, t.Offset + 4 * i);
        var locator = new JsonObject
        {
            ["Header"] = "NNW-WLC-V1.2", ["LocatorID"] = "wlc-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyReference)))[..8].ToLowerInvariant(),
            ["Method"] = Method, ["Targets"] = new JsonArray(targets.Select(t => (JsonNode)t.Name).ToArray()), ["KeyReference"] = keyReference
        };
        return (marked, locator);
    }

    // Looks for the payload in a model, without the original.
    public static Detection Extract(byte[] onnx, string key, bool[] payload)
    {
        var targets = Targets(onnx);
        if (targets.Count == 0) return new Detection(0, payload.Length, false);
        var w = Read(onnx, targets); var match = 0;
        for (var k = 0; k < payload.Length; k++)
        {
            double c = 0; var i = 0;
            foreach (var p in Sequence(key, k, w.Length)) c += w[i++] * p;
            if ((c > 0) == payload[k]) match++;
        }
        return new Detection(match, payload.Length, match >= payload.Length - 1);
    }

    // The payload of a round as bits.
    public static bool[] Bits(ulong value, int count) => Enumerable.Range(0, count).Select(i => (value >> i & 1) == 1).ToArray();
}
