using AIF.SharedStorage;
using Mpai.Aih.Hfl;
using Mpai.Nnw.NoConstraint;
using Mpai.Onnx;

namespace Mpai.Aif.Tests;

// FEDERATED LEARNING, STEP 5: the Back End puts a watermark in the network of the model it uploads (the same for every End User, who all download the same AIM) and
// looks for it in the trained models that come back: it is how the Back End is sure that a trained AIM was made from its model. Since 2026-11-12 the End Users train the
// network itself (it is no longer frozen), so the mark must survive their training and the averaging. The method is NoConstraint (IMT, M3304), used through Post-Training Watermark Insertion and Watermark Extraction (NNW V2.0); it replaced the demo's own
// spread-spectrum mark on 2026-11-12.
[Trait("Group", "Fast")]
public class AihFederatedWatermarkTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "aih-fl-wm-" + Guid.NewGuid().ToString("N"));
    public AihFederatedWatermarkTests() => Directory.CreateDirectory(folder);
    public void Dispose() { try { Directory.Delete(folder, true); } catch { } }

    private static string Network => Path.Combine(Repository.Root, "Test", "Data", "Fl", "frozen-backbone.onnx");
    private static readonly bool[] Payload = NoConstraint.Bits("1010010111000011");
    private static readonly byte[] Key = NoConstraint.Seed([0.31, 0.27, 0.88]);

    [Fact]
    public void TheMarkIsInsertedInTheNetworkAndFoundWithoutTheOriginal()
    {
        var original = File.ReadAllBytes(Network);
        var marked = NoConstraint.InsertOnnx(original, Key, Payload);
        Assert.Equal(original.Length, marked.Length);                                                        // edited in place: the file has the same size
        Assert.True(NoConstraint.ExtractOnnx(marked, Key, 16).Carries(Payload));

        Assert.False(NoConstraint.ExtractOnnx(original, Key, 16).Carries(Payload));                          // not in a model that is not the Back End's
        Assert.False(NoConstraint.ExtractOnnx(NoConstraint.InsertOnnx(original, NoConstraint.Seed([0.9]), Payload), Key, 16).Carries(Payload));
        Assert.False(NoConstraint.ExtractOnnx(marked, Key, 16).Carries(NoConstraint.Bits("0101101000111100")));
        Assert.False(NoConstraint.ExtractOnnx(marked, NoConstraint.Seed([0.1]), 16).Carries(Payload));
    }

    // The mark replaces weights of the network: the features move (the End Users' training then goes on over the marked weights); here the features are compared.
    [Fact]
    public void TheMarkedNetworkStillRunsAndGivesNearlyTheSameFeatures()
    {
        var marked = NoConstraint.InsertOnnx(File.ReadAllBytes(Network), Key, Payload);
        var markedPath = Path.Combine(folder, "marked.onnx"); File.WriteAllBytes(markedPath, marked);
        using var a = new FrontEndTrainer(Network, new FileSharedStorage(Path.Combine(folder, "a"), "a", "a"));
        using var b = new FrontEndTrainer(markedPath, new FileSharedStorage(Path.Combine(folder, "b"), "b", "b"));
        var image = AihFederatedTrainingTests.Cases(AihFederatedTrainingTests.EndUsers[0], 1, 3)[0].Image;
        var fa = a.Features(image); var fb = b.Features(image);
        var cos = fa.Zip(fb, (x, y) => (double)x * y).Sum() / (Math.Sqrt(fa.Sum(x => (double)x * x)) * Math.Sqrt(fb.Sum(y => (double)y * y)));
        Assert.True(cos > 0.9, "cosine of the features " + cos);
    }

    [Fact]
    public void TheMarkIsFoundInTheModelsThatComeBackAndNotInModelsOfAnotherNetwork()
    {
        var original = File.ReadAllBytes(Network);
        var marked = NoConstraint.InsertOnnx(original, Key, Payload);
        var markedPath = Path.Combine(folder, "marked.onnx"); File.WriteAllBytes(markedPath, marked);
        var returned = new List<byte[]>();
        for (var u = 0; u < 3; u++)
        {
            // an End User downloads the marked model, trains the final layer on its own images, and returns the whole model
            using var fe = new FrontEndTrainer(markedPath, new FileSharedStorage(Path.Combine(folder, "fe" + u), "fe", "fe"));
            var k = 0; foreach (var (img, f) in AihFederatedTrainingTests.Cases(AihFederatedTrainingTests.EndUsers[u], 40, 50 + u)) fe.KeepCase($"c{k++}", img, f);
            fe.Train(null);
            var back = fe.Export();
            var trained = FrontEndTrainer.NetworkOf(back)!;
            Assert.NotEqual(marked, trained);                                                                   // the End User trained the network itself
            Assert.True(NoConstraint.ExtractOnnx(trained, Key, 16).Carries(Payload), "the mark is found in the model of End User " + u);
            Assert.Equal(40, HflAimProcessor.Cases(back));
            returned.Add(trained);
        }
        // the average of the trained networks (what HFL makes) still carries the mark
        var tensors = OnnxFloats.Weights(marked);
        var mean = new float[tensors.Sum(t => t.Count)];
        foreach (var n in returned) { var w = OnnxFloats.Read(n, tensors); for (var i = 0; i < mean.Length; i++) mean[i] += w[i] / returned.Count; }
        Assert.True(NoConstraint.ExtractOnnx(OnnxFloats.Write(marked, tensors, mean), Key, 16).Carries(Payload), "the mark is found in the average");
        // a model that is not the Back End's (an End User's own, unmarked network): not found
        using var own = new FrontEndTrainer(Network, new FileSharedStorage(Path.Combine(folder, "own"), "o", "o"));
        var j = 0; foreach (var (img, f) in AihFederatedTrainingTests.Cases(AihFederatedTrainingTests.EndUsers[0], 20, 77)) own.KeepCase($"c{j++}", img, f);
        own.Train(null);
        Assert.False(NoConstraint.ExtractOnnx(FrontEndTrainer.NetworkOf(own.Export())!, Key, 16).Carries(Payload));
        // a second round, a new key and payload: the first mark is not found in it
        var key2 = NoConstraint.Seed([0.5, 0.6]); var payload2 = NoConstraint.Bits("0001001000110100");
        var second = NoConstraint.InsertOnnx(original, key2, payload2);
        Assert.True(NoConstraint.ExtractOnnx(second, key2, 16).Carries(payload2));
        Assert.False(NoConstraint.ExtractOnnx(second, Key, 16).Carries(Payload));
    }
}
