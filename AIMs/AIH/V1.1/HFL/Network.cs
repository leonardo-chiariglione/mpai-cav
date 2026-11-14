using System;
using System.Collections.Generic;
using System.Linq;

using Mpai.Onnx;

namespace Mpai.Aih.Hfl;

// THE NETWORK OF THE NN MODEL, TRAINABLE (federated learning: the Front Ends train the network itself, not only the final layer).
//
// The network is an ONNX file. It is read here, run forward and backward, and written back into the same file (the weights in place, nothing else changes), so that
// the model that travels is always an ONNX file that ONNX Runtime runs as it is. Only a chain of nodes is understood, each one taking the output of the one before,
// and only the operators of the HSP demo network: Conv (one group), Relu, AveragePool (no padding), Flatten (axis 1), Gemm (transB = 1) and Mul by a constant.
// A graph with anything else is refused when it is read. The input is one grey image, 1 x side x side, or (N-22, the head on the frozen features of a chest network)
// a vector of features, given as channels x 1 x 1.
public sealed class Network
{
    private abstract class Layer
    {
        public int InC, InH, InW, OutC, OutH, OutW;
        public int InSize => InC * InH * InW;
        public int OutSize => OutC * OutH * OutW;
        public abstract void Forward(float[] x, float[] y);
        // dy is the gradient at the output; dx receives the gradient at the input; the gradients of the weights are ADDED to their buffers.
        public abstract void Backward(float[] x, float[] y, float[] dy, float[] dx);
    }

    private sealed class Conv : Layer
    {
        public float[] W = null!, B = null!, GW = null!, GB = null!;
        public int K, S, P;
        public override void Forward(float[] x, float[] y)
        {
            for (var co = 0; co < OutC; co++)
                for (var oy = 0; oy < OutH; oy++)
                    for (var ox = 0; ox < OutW; ox++)
                    {
                        double s = B[co];
                        for (var ci = 0; ci < InC; ci++)
                            for (var ky = 0; ky < K; ky++)
                            {
                                var iy = oy * S - P + ky; if (iy < 0 || iy >= InH) continue;
                                var wRow = ((co * InC + ci) * K + ky) * K; var xRow = (ci * InH + iy) * InW;
                                for (var kx = 0; kx < K; kx++)
                                {
                                    var ix = ox * S - P + kx; if (ix < 0 || ix >= InW) continue;
                                    s += W[wRow + kx] * x[xRow + ix];
                                }
                            }
                        y[(co * OutH + oy) * OutW + ox] = (float)s;
                    }
        }
        public override void Backward(float[] x, float[] y, float[] dy, float[] dx)
        {
            Array.Clear(dx);
            for (var co = 0; co < OutC; co++)
                for (var oy = 0; oy < OutH; oy++)
                    for (var ox = 0; ox < OutW; ox++)
                    {
                        var g = dy[(co * OutH + oy) * OutW + ox]; if (g == 0) continue;
                        GB[co] += g;
                        for (var ci = 0; ci < InC; ci++)
                            for (var ky = 0; ky < K; ky++)
                            {
                                var iy = oy * S - P + ky; if (iy < 0 || iy >= InH) continue;
                                var wRow = ((co * InC + ci) * K + ky) * K; var xRow = (ci * InH + iy) * InW;
                                for (var kx = 0; kx < K; kx++)
                                {
                                    var ix = ox * S - P + kx; if (ix < 0 || ix >= InW) continue;
                                    GW[wRow + kx] += g * x[xRow + ix];
                                    dx[xRow + ix] += g * W[wRow + kx];
                                }
                            }
                    }
        }
    }

    private sealed class Relu : Layer
    {
        public override void Forward(float[] x, float[] y) { for (var i = 0; i < x.Length; i++) y[i] = x[i] > 0 ? x[i] : 0; }
        public override void Backward(float[] x, float[] y, float[] dy, float[] dx) { for (var i = 0; i < x.Length; i++) dx[i] = x[i] > 0 ? dy[i] : 0; }
    }

    private sealed class Scale : Layer
    {
        public float G;
        public override void Forward(float[] x, float[] y) { for (var i = 0; i < x.Length; i++) y[i] = x[i] * G; }
        public override void Backward(float[] x, float[] y, float[] dy, float[] dx) { for (var i = 0; i < x.Length; i++) dx[i] = dy[i] * G; }
    }

    private sealed class Flatten : Layer
    {
        public override void Forward(float[] x, float[] y) => Array.Copy(x, y, x.Length);
        public override void Backward(float[] x, float[] y, float[] dy, float[] dx) => Array.Copy(dy, dx, dy.Length);
    }

    private sealed class AvgPool : Layer
    {
        public int K, S;
        public override void Forward(float[] x, float[] y)
        {
            for (var c = 0; c < OutC; c++)
                for (var oy = 0; oy < OutH; oy++)
                    for (var ox = 0; ox < OutW; ox++)
                    {
                        double s = 0;
                        for (var ky = 0; ky < K; ky++) for (var kx = 0; kx < K; kx++) s += x[(c * InH + oy * S + ky) * InW + ox * S + kx];
                        y[(c * OutH + oy) * OutW + ox] = (float)(s / (K * K));
                    }
        }
        public override void Backward(float[] x, float[] y, float[] dy, float[] dx)
        {
            Array.Clear(dx);
            for (var c = 0; c < OutC; c++)
                for (var oy = 0; oy < OutH; oy++)
                    for (var ox = 0; ox < OutW; ox++)
                    {
                        var g = dy[(c * OutH + oy) * OutW + ox] / (K * K);
                        for (var ky = 0; ky < K; ky++) for (var kx = 0; kx < K; kx++) dx[(c * InH + oy * S + ky) * InW + ox * S + kx] += g;
                    }
        }
    }

    private sealed class Dense : Layer                                        // Gemm with transB = 1: y = W x + b, W is [out, in]
    {
        public float[] W = null!, B = null!, GW = null!, GB = null!;
        public override void Forward(float[] x, float[] y)
        {
            for (var o = 0; o < OutSize; o++) { double s = B[o]; var row = o * InSize; for (var i = 0; i < InSize; i++) s += W[row + i] * x[i]; y[o] = (float)s; }
        }
        public override void Backward(float[] x, float[] y, float[] dy, float[] dx)
        {
            Array.Clear(dx);
            for (var o = 0; o < OutSize; o++)
            {
                var g = dy[o]; if (g == 0) continue;
                GB[o] += g; var row = o * InSize;
                for (var i = 0; i < InSize; i++) { GW[row + i] += g * x[i]; dx[i] += g * W[row + i]; }
            }
        }
    }

    private readonly byte[] _file;
    private readonly List<OnnxFloats.Tensor> _tensors;
    private readonly Dictionary<string, float[]> _values = new(), _grads = new();
    private readonly List<Layer> _layers = new();

    public int ImageSide { get; }
    public int InputSize { get; }
    public int FeatureCount => _layers[^1].OutSize;
    public int WeightCount => _tensors.Sum(t => t.Count);

    public Network(byte[] onnx, int imageSide) : this(onnx, 1, imageSide, imageSide) { }

    // A network whose input is channels x height x width (a vector of n features: n x 1 x 1).
    public Network(byte[] onnx, int channels, int height, int width)
    {
        _file = (byte[])onnx.Clone(); ImageSide = channels == 1 && height == width ? height : 0; InputSize = channels * height * width;
        _tensors = OnnxFloats.Weights(_file);
        foreach (var t in _tensors) { _values[t.Name] = OnnxFloats.Read(_file, new[] { t }); _grads[t.Name] = new float[t.Count]; }
        var (nodes, inputs, _) = OnnxFloats.Graph(_file);
        var constants = nodes.Where(n => n.Op == "Constant" && n.Tensors.ContainsKey("value")).ToDictionary(n => n.Outputs[0], n => n.Tensors["value"]);
        int c = channels, h = height, w = width; var current = inputs.FirstOrDefault(i => !_values.ContainsKey(i)) ?? throw new FormatException("the network has no input.");
        foreach (var n in nodes.Where(n => n.Op != "Constant"))
        {
            if (n.Inputs.Length == 0 || n.Inputs[0] != current) throw new NotSupportedException($"the network is not a chain: {n.Op} does not take '{current}'.");
            Layer layer;
            switch (n.Op)
            {
                case "Conv":
                {
                    if (n.Ints.TryGetValue("group", out var g) && g[0] != 1) throw new NotSupportedException("a Conv of more than one group.");
                    var dims = _tensors.First(t => t.Name == n.Inputs[1]).Dims;
                    var k = dims[2]; var s = (int)(n.Ints.GetValueOrDefault("strides")?[0] ?? 1); var p = (int)(n.Ints.GetValueOrDefault("pads")?[0] ?? 0);
                    var conv = new Conv { K = k, S = s, P = p, W = _values[n.Inputs[1]], GW = _grads[n.Inputs[1]], OutC = dims[0] };
                    if (n.Inputs.Length > 2) { conv.B = _values[n.Inputs[2]]; conv.GB = _grads[n.Inputs[2]]; } else { conv.B = new float[dims[0]]; conv.GB = new float[dims[0]]; }
                    conv.OutH = (h + 2 * p - k) / s + 1; conv.OutW = (w + 2 * p - k) / s + 1;
                    layer = conv; break;
                }
                case "Relu": layer = new Relu { OutC = c, OutH = h, OutW = w }; break;
                case "Flatten": layer = new Flatten { OutC = c * h * w, OutH = 1, OutW = 1 }; break;
                case "AveragePool":
                {
                    if (n.Ints.TryGetValue("pads", out var pads) && pads.Any(v => v != 0)) throw new NotSupportedException("an AveragePool with padding.");
                    var k = (int)n.Ints["kernel_shape"][0]; var s = (int)(n.Ints.GetValueOrDefault("strides")?[0] ?? 1);
                    layer = new AvgPool { K = k, S = s, OutC = c, OutH = (h - k) / s + 1, OutW = (w - k) / s + 1 }; break;
                }
                case "Gemm":
                {
                    if (n.Ints.GetValueOrDefault("transB")?[0] != 1 || n.Ints.GetValueOrDefault("transA")?[0] is long ta && ta != 0) throw new NotSupportedException("a Gemm other than x times W transposed.");
                    if ((n.Floats.TryGetValue("alpha", out var a) && a != 1) || (n.Floats.TryGetValue("beta", out var b) && b != 1)) throw new NotSupportedException("a Gemm with alpha or beta not 1.");
                    var dims = _tensors.First(t => t.Name == n.Inputs[1]).Dims;
                    layer = new Dense { W = _values[n.Inputs[1]], GW = _grads[n.Inputs[1]], B = _values[n.Inputs[2]], GB = _grads[n.Inputs[2]], OutC = dims[0], OutH = 1, OutW = 1 }; break;
                }
                case "Mul":
                {
                    var other = n.Inputs[1];
                    var g = constants.TryGetValue(other, out var cv) ? cv : _values.TryGetValue(other, out var iv) ? iv : null;
                    if (g is not { Length: 1 }) throw new NotSupportedException("a Mul by something other than a constant.");
                    layer = new Scale { G = g[0], OutC = c, OutH = h, OutW = w }; break;
                }
                default: throw new NotSupportedException($"the operator {n.Op} is not understood by the trainer.");
            }
            layer.InC = c; layer.InH = h; layer.InW = w;
            c = layer.OutC; h = layer.OutH; w = layer.OutW;
            _layers.Add(layer); current = n.Outputs[0];
        }
    }

    // The features of one image, and what the backward pass needs (the output of each layer).
    public float[][] Run(float[] image)
    {
        var acts = new float[_layers.Count + 1][]; acts[0] = image;
        for (var i = 0; i < _layers.Count; i++) { acts[i + 1] = new float[_layers[i].OutSize]; _layers[i].Forward(acts[i], acts[i + 1]); }
        return acts;
    }

    public float[] Features(float[] image) => Run(image)[^1];

    // Adds to the gradients of the weights those of one image, given the gradient at the features.
    public void Backward(float[][] acts, float[] dFeatures)
    {
        var dy = dFeatures;
        for (var i = _layers.Count - 1; i >= 0; i--)
        {
            var dx = new float[_layers[i].InSize];
            _layers[i].Backward(acts[i], acts[i + 1], dy, dx);
            dy = dx;
        }
    }

    public void ClearGradients() { foreach (var g in _grads.Values) Array.Clear(g); }

    // All the weights, and their gradients, in the order of the file (the order OnnxFloats.Read gives).
    public IEnumerable<(float[] Values, float[] Grads)> Parameters() => _tensors.Select(t => (_values[t.Name], _grads[t.Name]));

    public float[] Weights() => _tensors.SelectMany(t => _values[t.Name]).ToArray();

    // The ONNX file with the current weights (the original file, the weights replaced in place).
    public byte[] ToOnnx() => OnnxFloats.Write(_file, _tensors, Weights());
}
