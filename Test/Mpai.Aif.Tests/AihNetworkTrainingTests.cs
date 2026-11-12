using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Mpai.Aih.Hfl;

namespace Mpai.Aif.Tests;

// FEDERATED LEARNING: THE NETWORK IS TRAINED (since 2026-11-12; before, it was frozen). The network of the NN Model is an ONNX file that Network runs and trains in C#:
// it must give what ONNX Runtime gives, its gradients must be right, and what it writes back must be an ONNX file that ONNX Runtime runs.
[Trait("Group", "Fast")]
public class AihNetworkTrainingTests
{
    private static string NetworkFile => Path.Combine(Repository.Root, "Test", "Data", "Fl", "frozen-backbone.onnx");

    private static float[] Image(int seed)
    {
        var r = new Random(seed); var side = FrontEndTrainer.ImageSide;
        return Enumerable.Range(0, side * side).Select(i => (float)(0.3 + 0.4 * Math.Sin(i * 0.01 + seed) * Math.Cos(i / side * 0.2) + 0.1 * r.NextDouble())).ToArray();
    }

    private static float[] Runtime(byte[] onnx, float[] image)
    {
        using var session = new InferenceSession(onnx);
        var input = new DenseTensor<float>(image, new[] { 1, 1, FrontEndTrainer.ImageSide, FrontEndTrainer.ImageSide });
        using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(session.InputMetadata.First().Key, input) });
        return results.First().AsEnumerable<float>().ToArray();
    }

    [Fact]
    public void TheNetworkGivesWhatOnnxRuntimeGives()
    {
        var onnx = File.ReadAllBytes(NetworkFile);
        var net = new Network(onnx, FrontEndTrainer.ImageSide);
        Assert.Equal(64, net.FeatureCount);
        for (var s = 0; s < 3; s++)
        {
            var a = net.Features(Image(s)); var b = Runtime(onnx, Image(s));
            Assert.Equal(b.Length, a.Length);
            Assert.True(a.Zip(b).Max(p => Math.Abs(p.First - p.Second)) < 1e-4 * (1 + b.Max(Math.Abs)), "the features differ from ONNX Runtime's");
        }
    }

    // The gradient of a loss (the sum of the features times fixed numbers) against the weights, compared with the change of the loss when a weight is moved a little.
    [Fact]
    public void TheGradientsAreThoseOfTheLoss()
    {
        var onnx = File.ReadAllBytes(NetworkFile);
        var image = Image(7);
        var net = new Network(onnx, FrontEndTrainer.ImageSide);
        var c = Enumerable.Range(0, net.FeatureCount).Select(i => (float)Math.Sin(i + 1)).ToArray();
        double Loss(Network n) => n.Features(image).Zip(c, (f, k) => (double)f * k).Sum();
        net.ClearGradients(); net.Backward(net.Run(image), c);
        var parameters = net.Parameters().ToList();
        var r = new Random(3); var checkedCount = 0;
        foreach (var (values, grads) in parameters)
        {
            // the scale of the gradients of this tensor: a small gradient among large ones is checked against it (a step of the weight may cross the bend of a Relu)
            var scale = grads.Average(g => Math.Abs((double)g));
            for (var t = 0; t < 4; t++)
            {
                var i = r.Next(values.Length); var keep = values[i]; const float h = 1e-3f;
                values[i] = keep + h; var up = Loss(net); values[i] = keep - h; var down = Loss(net); values[i] = keep;
                var numeric = (up - down) / (2 * h);
                if (Math.Abs(numeric) < 1e-3 && Math.Abs(grads[i]) < 1e-3) continue;      // a weight behind a closed Relu: both nothing
                Assert.True(Math.Abs(numeric - grads[i]) <= 0.05 * Math.Max(Math.Abs(numeric), Math.Abs(grads[i])) + 0.02 * scale, $"weight {i}: numeric {numeric}, computed {grads[i]}");
                checkedCount++;
            }
        }
        Assert.True(checkedCount >= 10, "too few weights checked: " + checkedCount);
    }

    [Fact]
    public void WhatTheTrainingWritesIsAnOnnxFileThatRunsAsTrained()
    {
        var onnx = File.ReadAllBytes(NetworkFile);
        var net = new Network(onnx, FrontEndTrainer.ImageSide);
        foreach (var (values, _) in net.Parameters()) for (var i = 0; i < values.Length; i++) values[i] *= 1.1f;
        var written = net.ToOnnx();
        Assert.Equal(onnx.Length, written.Length);
        var a = net.Features(Image(1)); var b = Runtime(written, Image(1));
        Assert.True(a.Zip(b).Max(p => Math.Abs(p.First - p.Second)) < 1e-4 * (1 + b.Max(Math.Abs)));
        Assert.NotEqual(Runtime(onnx, Image(1)), b);
    }
}
