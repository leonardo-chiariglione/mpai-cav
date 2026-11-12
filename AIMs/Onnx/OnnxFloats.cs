using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Mpai.Onnx;

// THE WEIGHTS OF AN ONNX FILE, read and written in place. Only the initializers of the graph are looked at, and only those that are 32-bit floats held as raw data.
// Writing replaces the bytes of a weight by bytes of the same length, so nothing else of the file changes. The nodes of the graph can be read too (Nodes), so that a
// network can be run and trained outside ONNX Runtime (the HSP federated learning). Used by the NNW watermarking (NoConstraint) and by AIH-HFL.
public static class OnnxFloats
{
    public sealed record Tensor(string Name, int[] Dims, int Offset, int Length)
    {
        public int Count => Length / 4;
    }

    // The float initializers, in the order of the file.
    public static List<Tensor> Weights(byte[] onnx)
    {
        var found = new List<Tensor>();
        Walk(onnx, 0, onnx.Length, (f, wire, start, len) =>
        {
            if (f == 7 && wire == 2) Walk(onnx, start, start + len, (g, w2, s2, l2) =>                    // ModelProto.graph
            {
                if (g == 5 && w2 == 2) found.Add(ReadTensor(onnx, s2, s2 + l2));                          // GraphProto.initializer
            });
        });
        return found.Where(t => t.Length > 0).ToList();
    }

    // A node of the graph: its operator, inputs, outputs and attributes (integers, lists of integers, floats, tensors).
    public sealed record Node(string Op, string[] Inputs, string[] Outputs, Dictionary<string, long[]> Ints, Dictionary<string, float> Floats, Dictionary<string, float[]> Tensors);

    // The graph's nodes in the order of the file (an exported network lists them in the order they run), and the names of its inputs and outputs.
    public static (List<Node> Nodes, List<string> Inputs, List<string> Outputs) Graph(byte[] onnx)
    {
        var nodes = new List<Node>(); var inputs = new List<string>(); var outputs = new List<string>();
        Walk(onnx, 0, onnx.Length, (f, wire, start, len) =>
        {
            if (f == 7 && wire == 2) Walk(onnx, start, start + len, (g, w2, s2, l2) =>
            {
                if (g == 1 && w2 == 2) nodes.Add(ReadNode(onnx, s2, s2 + l2));                            // GraphProto.node
                else if (g == 11 && w2 == 2) inputs.Add(NameOf(onnx, s2, s2 + l2));                       // GraphProto.input (ValueInfoProto)
                else if (g == 12 && w2 == 2) outputs.Add(NameOf(onnx, s2, s2 + l2));                      // GraphProto.output
            });
        });
        return (nodes, inputs, outputs);
    }

    private static string NameOf(byte[] b, int start, int end)
    {
        var name = ""; Walk(b, start, end, (f, wire, s, l) => { if (f == 1 && wire == 2) name = Encoding.UTF8.GetString(b, s, l); }); return name;
    }

    private static Node ReadNode(byte[] b, int start, int end)
    {
        var ins = new List<string>(); var outs = new List<string>(); var op = "";
        var ints = new Dictionary<string, long[]>(); var floats = new Dictionary<string, float>(); var tensors = new Dictionary<string, float[]>();
        Walk(b, start, end, (f, wire, s, l) =>
        {
            if (wire != 2) return;
            switch (f)
            {
                case 1: ins.Add(Encoding.UTF8.GetString(b, s, l)); break;
                case 2: outs.Add(Encoding.UTF8.GetString(b, s, l)); break;
                case 4: op = Encoding.UTF8.GetString(b, s, l); break;
                case 5:                                                                                   // AttributeProto
                {
                    var name = ""; var list = new List<long>(); long? one = null; float? fl = null; float[]? t = null;
                    Walk(b, s, s + l, (a, aw, as_, al) =>
                    {
                        switch (a)
                        {
                            case 1 when aw == 2: name = Encoding.UTF8.GetString(b, as_, al); break;
                            case 2 when aw == 5: fl = BitConverter.ToSingle(b, as_); break;
                            case 3 when aw == 0: one = al; break;
                            case 5 when aw == 2: { var tt = ReadTensor(b, as_, as_ + al); t = new float[tt.Count]; for (var i = 0; i < tt.Count; i++) t[i] = BitConverter.ToSingle(b, tt.Offset + 4 * i); break; }
                            case 8 when aw == 0: list.Add(al); break;
                            case 8 when aw == 2: { var p = as_; while (p < as_ + al) list.Add((long)ReadVarint(b, ref p)); break; }
                        }
                    });
                    if (list.Count > 0) ints[name] = list.ToArray(); else if (one is long v) ints[name] = new[] { v };
                    if (fl is float x) floats[name] = x;
                    if (t is not null) tensors[name] = t;
                    break;
                }
            }
        });
        return new Node(op, ins.ToArray(), outs.ToArray(), ints, floats, tensors);
    }

    // True where the bytes are an ONNX file with at least one float initializer.
    public static bool IsOnnx(byte[] bytes)
    {
        try { return Weights(bytes).Count > 0; } catch { return false; }
    }

    public static float[] Read(byte[] onnx, IReadOnlyList<Tensor> tensors)
    {
        var w = new float[tensors.Sum(t => t.Count)]; var k = 0;
        foreach (var t in tensors) for (var i = 0; i < t.Count; i++) w[k++] = BitConverter.ToSingle(onnx, t.Offset + 4 * i);
        return w;
    }

    // A copy of the file with the weights of the tensors replaced, in the same order as Read gives them.
    public static byte[] Write(byte[] onnx, IReadOnlyList<Tensor> tensors, float[] w)
    {
        var copy = (byte[])onnx.Clone(); var k = 0;
        foreach (var t in tensors) for (var i = 0; i < t.Count; i++) BitConverter.GetBytes(w[k++]).CopyTo(copy, t.Offset + 4 * i);
        return copy;
    }

    private static Tensor ReadTensor(byte[] b, int start, int end)
    {
        string name = ""; var dims = new List<int>(); int type = 0, off = 0, len = 0;
        Walk(b, start, end, (f, wire, s, l) =>
        {
            switch (f)
            {
                case 1 when wire == 0: dims.Add(l); break;                                                // dims, not packed (the value is in l)
                case 1 when wire == 2: { var p = s; while (p < s + l) dims.Add((int)ReadVarint(b, ref p)); break; }
                case 2 when wire == 0: type = l; break;
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
                case 2: { var l = (int)ReadVarint(b, ref p); if (l < 0 || p + l > end) throw new FormatException("not an ONNX file."); field(f, 2, p, l); p += l; break; }
                case 5: field(f, 5, p, 4); p += 4; break;
                default: throw new FormatException("not an ONNX file.");
            }
        }
    }

    private static ulong ReadVarint(byte[] b, ref int p)
    {
        ulong v = 0; var shift = 0;
        while (true)
        {
            if (p >= b.Length || shift > 63) throw new FormatException("not an ONNX file.");
            var x = b[p++]; v |= (ulong)(x & 0x7F) << shift; if ((x & 0x80) == 0) return v; shift += 7;
        }
    }
}
