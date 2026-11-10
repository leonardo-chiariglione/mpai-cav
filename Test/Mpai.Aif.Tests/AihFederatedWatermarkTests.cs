using System.Text.Json;
using System.Text.Json.Nodes;
using AIF.SharedStorage;
using Mpai.Aih.Hfl;

namespace Mpai.Aif.Tests;

// FEDERATED LEARNING, STEP 5 (night N-5): the Back End puts a watermark in the frozen network of the model it uploads (the same for every End User) and looks for it in the models
// that come back. The method is the demo's own (the NNW schemas leave the method open: ModelWatermark.cs says which and why).
[Trait("Group", "Fast")]
public class AihFederatedWatermarkTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "aih-fl-wm-" + Guid.NewGuid().ToString("N"));
    public AihFederatedWatermarkTests() => Directory.CreateDirectory(folder);
    public void Dispose() { try { Directory.Delete(folder, true); } catch { } }

    private static string Network => Path.Combine(Repository.Root, "Test", "Data", "Fl", "frozen-backbone.onnx");
    private static readonly bool[] Payload = ModelWatermark.Bits(0xA5C3, 16);

    [Fact]
    public void TheMarkIsInsertedInTheConvolutionWeightsAndFoundWithoutTheOriginal()
    {
        var original = File.ReadAllBytes(Network);
        var targets = ModelWatermark.Targets(original);
        Assert.True(targets.Count >= 3);
        Assert.All(targets, t => Assert.True(t.Dims.Length >= 4));

        var (marked, locator) = ModelWatermark.Insert(original, "back-end-secret-1", "key:backend-1", Payload);
        Assert.Equal(original.Length, marked.Length);                                       // edited in place: the file has the same size
        Assert.True(ModelWatermark.Extract(marked, "back-end-secret-1", Payload).Found);
        Assert.Equal(16, ModelWatermark.Extract(marked, "back-end-secret-1", Payload).Matching);

        // not found in a model that is not the Back End's: the original, a model marked with another key, the right key with another payload
        Assert.False(ModelWatermark.Extract(original, "back-end-secret-1", Payload).Found);
        var (other, _) = ModelWatermark.Insert(original, "another-secret", "key:other", Payload);
        Assert.False(ModelWatermark.Extract(other, "back-end-secret-1", Payload).Found);
        Assert.False(ModelWatermark.Extract(marked, "back-end-secret-1", ModelWatermark.Bits(0x5A3C, 16)).Found);
        Assert.False(ModelWatermark.Extract(marked, "wrong-key", Payload).Found);

        // the Locator names the method and the tensors, and carries a reference to the key, never the key (no schema defines it: it is what this AIH implementation writes)
        Assert.False(string.IsNullOrEmpty((string?)locator["Method"]));
        Assert.DoesNotContain("back-end-secret-1", locator.ToJsonString());
        Assert.Equal(targets.Select(t => t.Name), locator["Targets"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void TheMarkedNetworkStillRunsAndGivesNearlyTheSameFeatures()
    {
        var original = File.ReadAllBytes(Network);
        var (marked, _) = ModelWatermark.Insert(original, "k", "key:k", Payload);
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
        var (marked, _) = ModelWatermark.Insert(original, "round-1-secret", "key:round-1", Payload);
        var markedPath = Path.Combine(folder, "marked.onnx"); File.WriteAllBytes(markedPath, marked);
        for (var u = 0; u < 3; u++)
        {
            // an End User downloads the marked model, trains the final layer on its own images, and returns the whole model
            using var fe = new FrontEndTrainer(markedPath, new FileSharedStorage(Path.Combine(folder, "fe" + u), "fe", "fe"));
            var k = 0; foreach (var (img, f) in AihFederatedTrainingTests.Cases(AihFederatedTrainingTests.EndUsers[u], 40, 50 + u)) fe.KeepCase($"c{k++}", img, f);
            fe.Train(null);
            var back = fe.ExportWithNetwork();
            var network = FrontEndTrainer.NetworkOf(back)!;
            Assert.True(ModelWatermark.Extract(network, "round-1-secret", Payload).Found, "the mark is found in the model of End User " + u);
            Assert.Equal(40, HflAimProcessor.Cases(back));
        }
        // a model that is not the Back End's (an End User's own, unmarked network): not found
        using var own = new FrontEndTrainer(Network, new FileSharedStorage(Path.Combine(folder, "own"), "o", "o"));
        var j = 0; foreach (var (img, f) in AihFederatedTrainingTests.Cases(AihFederatedTrainingTests.EndUsers[0], 20, 77)) own.KeepCase($"c{j++}", img, f);
        own.Train(null);
        Assert.False(ModelWatermark.Extract(FrontEndTrainer.NetworkOf(own.ExportWithNetwork())!, "round-1-secret", Payload).Found);
        // a second round, a new watermark: the first is not found in it
        var (second, _) = ModelWatermark.Insert(original, "round-2-secret", "key:round-2", ModelWatermark.Bits(0x1234, 16));
        Assert.True(ModelWatermark.Extract(second, "round-2-secret", ModelWatermark.Bits(0x1234, 16)).Found);
        Assert.False(ModelWatermark.Extract(second, "round-1-secret", Payload).Found);
    }
}
