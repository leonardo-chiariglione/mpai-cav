using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Mpai.Onnx;

namespace Mpai.Aif.Tests;

// AIH-HSP, NIGHT REQUEST N-22: THE FROZEN PART OF THE NN MODEL FOR REAL CHEST X-RAYS. TorchXRayVision DenseNet-121, CheXpert weights (not trained on NIH ChestX-ray14),
// exported by Test\Tools\FlLab\export_chex_densenet.py: 1 x 224 x 224 grey image in, 1024 features out. ONNX Runtime, as the AIMs run it, gives the features torch gave
// for the same two images. The file is not in git (28 MB): without it the test is skipped.
[Trait("Group", "Fast")]
public class AihChestNetworkTests
{
    private static readonly string Network = Path.Combine(Repository.Root, "Test", "Data", "Fl", "chex-densenet121-features.onnx");

    private static float[] Floats(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var f = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, f, 0, bytes.Length);
        return f;
    }

    [SkippableFact]
    public void OnnxRuntimeGivesTheFeaturesTorchGave()
    {
        Skip.IfNot(File.Exists(Network), "the chest network is not exported: " + Network);
        var x = Floats(Network + ".check-x.bin");
        var expected = Floats(Network + ".check-features.bin");
        using var session = OnnxSessions.Create(Network);
        using var results = session.Run([NamedOnnxValue.CreateFromTensor("image", new DenseTensor<float>(x, [2, 1, 224, 224]))]);
        var got = results.First().AsTensor<float>().ToArray();
        Assert.Equal(2 * 1024, got.Length);
        var worst = got.Zip(expected, (a, b) => Math.Abs(a - b) / (1e-3 + Math.Abs(b))).Max();
        Assert.True(worst < 1e-3, $"largest relative difference {worst}");
    }
}
