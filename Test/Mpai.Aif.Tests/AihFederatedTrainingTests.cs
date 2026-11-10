using System.Text.Json.Nodes;
using AIF.SharedStorage;
using Mpai.Aih.Hfl;

namespace Mpai.Aif.Tests;

// FEDERATED LEARNING, STEP 1 (night N-1): the training in the End User's Health Front End.
// The images are SYNTHETIC (chest-like pictures with or without a bright finding); the frozen network is a fixed-random-weight ONNX file made by Test\Tools\FlLab\make_frozen_backbone.py.
// Each End User's device has its own look (brightness, noise, where the findings sit), so that what a Front End learns from its own cases is better for its own cases.
[Trait("Group", "Fast")]
public class AihFederatedTrainingTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "aih-fl-train-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(folder, true); } catch { } }

    private static string Network => Path.Combine(Repository.Root, "Test", "Data", "Fl", "frozen-backbone.onnx");

    // The look of an End User's device: brightness offset, noise, contrast, and where the finding sits (centre x, y; spread).
    internal sealed record Device(string Name, float Offset, float Noise, float Contrast, float Cx, float Cy, float Spread);
    internal static readonly Device Neutral = new("neutral", 0f, 0.05f, 1f, 32f, 32f, 14f);
    internal static readonly Device[] EndUsers =
    {
        new("A", +0.03f, 0.05f, 1.0f, 20f, 30f, 4f),     // findings in the left lung
        new("B", -0.03f, 0.08f, 1.0f, 44f, 30f, 4f),     // findings in the right lung
        new("C", 0.00f, 0.05f, 0.8f, 32f, 46f, 5f),      // findings low in the chest
    };

    // One synthetic image: body, two darker lungs, noise, and (if finding) a bright blob.
    internal static float[] Image(Device d, bool finding, Random rng)
    {
        const int n = FrontEndTrainer.ImageSide;
        var img = new float[n * n];
        float bx = 0, by = 0;
        if (finding) { bx = Clamp(d.Cx + Gauss(rng) * d.Spread, 12, 52); by = Clamp(d.Cy + Gauss(rng) * d.Spread, 14, 52); }
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
            {
                var v = 0.6f - 0.05f * (y / (float)n);
                foreach (var lx in new[] { 20f, 44f })
                    if (Math.Pow((x - lx) / 11.0, 2) + Math.Pow((y - 32) / 20.0, 2) < 1) v -= 0.25f;
                if (finding) v += 0.35f * (float)Math.Exp(-((x - bx) * (x - bx) + (y - by) * (y - by)) / (2 * 3.5 * 3.5));
                v = 0.5f + (v - 0.5f) * d.Contrast + d.Offset + Gauss(rng) * d.Noise;
                img[y * n + x] = Math.Clamp(v, 0f, 1f);
            }
        return img;
    }

    internal static List<(float[] Image, bool Finding)> Cases(Device d, int count, int seed)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, count).Select(i => { var f = i % 2 == 0; return (Image(d, f, rng), f); }).ToList();
    }

    private static float Gauss(Random r) => (float)(Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble()));
    private static float Clamp(float v, float lo, float hi) => Math.Clamp(v, lo, hi);

    private FrontEndTrainer Front(string name, out FileSharedStorage vault)
    {
        vault = new FileSharedStorage(Path.Combine(folder, name), name, name);
        return new FrontEndTrainer(Network, vault);
    }

    // The fixed check set: cases of every device, always the same.
    internal static List<(float[] Image, bool Finding)> CheckSet() =>
        new[] { Neutral }.Concat(EndUsers).SelectMany((d, i) => Cases(d, 50, 9000 + i)).ToList();

    // The model the Back End sends first: trained centrally on a few neutral cases.
    internal static JsonObject StartModel(FrontEndTrainer any, string folder)
    {
        using var back = new FrontEndTrainer(Network, new FileSharedStorage(Path.Combine(folder, "back-end-start"), "be", "be"));
        var k = 0;
        foreach (var (img, f) in Cases(Neutral, 200, 1)) back.KeepCase("n" + k++, img, f);
        return back.Train(null, epochs: 80);
    }

    [Fact]
    public void ThreeFrontEndsEachTrainOnTheirOwnImagesAndTheImagesStay()
    {
        var results = new List<string>();
        var checkSet = CheckSet();
        using var probe = Front("probe", out _);
        var start = StartModel(probe, folder);
        var startCheck = probe.Score(start, checkSet);

        var exported = new List<JsonObject>();
        for (var u = 0; u < EndUsers.Length; u++)
        {
            var d = EndUsers[u];
            using var fe = Front("fe-" + d.Name, out var vault);
            var own = Cases(d, 100, 100 + u);                    // the 100 past cases of this End User
            var k = 0; foreach (var (img, f) in own) fe.KeepCase($"case-{k++:D3}", img, f);
            Assert.Equal(100, fe.Cases);

            var heldOwn = Cases(d, 200, 500 + u);                // this End User's later cases, never used for training
            var before = fe.Score(start, heldOwn);
            var trained = fe.Train(start);
            var after = fe.Score(trained, heldOwn);
            var check = fe.Score(trained, checkSet);
            results.Add($"{d.Name}: own cases {before.Accuracy:P0} -> {after.Accuracy:P0} (loss {before.LogLoss:F3} -> {after.LogLoss:F3}); check set {startCheck.Accuracy:P0} -> {check.Accuracy:P0}");

            Assert.True(after.LogLoss < before.LogLoss, "better on its own cases (loss): " + results[^1]);
            Assert.True(after.Accuracy >= before.Accuracy, "not worse on its own cases: " + results[^1]);
            Assert.True(check.Accuracy >= startCheck.Accuracy - 0.05, "still holds on the fixed check set: " + results[^1]);

            // Each version is kept in the End User's storage; only the model leaves.
            Assert.Single(fe.Versions());
            var model = fe.Export();
            Assert.Equal("AIP-MLO-V1.0", model["Header"]!.GetValue<string>());
            Assert.Equal(fe.FeatureCount + 1, FrontEndTrainer.Weights(model).Length);
            Assert.True(model.ToJsonString().Length < 2000, "what leaves is a small model, not images");
            Assert.Equal(100, fe.Cases);                         // the images are still there, and nowhere else
            exported.Add(model);
        }
        Assert.Equal(3, exported.Select(m => m["MLModelData"]![0]!["Data"]!.GetValue<string>()).Distinct().Count());
        foreach (var r in results) Console.WriteLine(r);
    }

    [Fact]
    public void EachNewTrainingKeepsANewVersionInTheEndUsersStorage()
    {
        using var fe = Front("fe-versions", out _);
        var k = 0; foreach (var (img, f) in Cases(EndUsers[0], 40, 7)) fe.KeepCase($"c{k++}", img, f);
        var v1 = fe.Train(null);
        var v2 = fe.Train(v1, epochs: 50);
        Assert.Equal(2, fe.Versions().Count());
        Assert.NotEqual(v1["MLModelObjectID"]!.GetValue<string>(), v2["MLModelObjectID"]!.GetValue<string>());
        Assert.Equal(v2.ToJsonString(), fe.Export().ToJsonString());
        // a model of the wrong size is refused
        var wrong = FrontEndTrainer.ModelObject("x", new float[3], "wrong");
        Assert.Throws<FormatException>(() => fe.Train(wrong));
    }
}
