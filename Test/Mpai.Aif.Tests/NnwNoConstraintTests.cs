using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.Store;
using Mpai.Nnw.NoConstraint;
using Mpai.Onnx;

namespace Mpai.Aif.Tests;

// THE NoConstraint WATERMARKING METHOD (IMT, M3304) and the two NNW V2.0 AIMs that use it: Post-Training Watermark Insertion and Watermark Extraction.
// The method: weights at secret positions are replaced by the watermark; it is found again by the Pearson correlation of the weights read there.
[Trait("Group", "Fast")]
public class NnwNoConstraintTests
{
    private static readonly byte[] Key = NoConstraint.Seed([0.11, 0.42, 0.73, 0.94]);
    private static readonly bool[] Payload = NoConstraint.Bits("1010011100101101");

    // A layer of weights as a trained network has them: normal, small.
    private static float[] Weights(int n, int seed)
    {
        var r = new Random(seed); var w = new float[n];
        for (var i = 0; i < n; i++) w[i] = (float)(0.05 * Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble()));
        return w;
    }

    [Fact]
    public void ThePayloadIsReadBackWithTheKeyAndNotWithoutIt()
    {
        var original = Weights(20000, 1);
        var marked = NoConstraint.Insert(original, Key, Payload);
        Assert.Equal(16 * NoConstraint.ValuesPerBit, original.Zip(marked).Count(p => p.First != p.Second));   // only the selected weights change
        var d = NoConstraint.Extract(marked, Key, 16);
        Assert.True(d.Carries(Payload));
        Assert.True(d.Correlation > 0.999, "correlation " + d.Correlation);

        Assert.False(NoConstraint.Extract(original, Key, 16).Carries(Payload));                                 // the model before the mark
        Assert.False(NoConstraint.Extract(marked, NoConstraint.Seed([0.5]), 16).Carries(Payload));               // another key
        Assert.False(NoConstraint.Extract(Weights(20000, 2), Key, 16).Carries(Payload));                         // another model
        var other = NoConstraint.Insert(original, Key, NoConstraint.Bits("0101100011010010"));
        Assert.False(NoConstraint.Extract(other, Key, 16).Carries(Payload));                                     // the same key, another payload
    }

    [Fact]
    public void TheMarkSurvivesTheChangesTrainingPruningAndQuantisationMake()
    {
        var marked = NoConstraint.Insert(Weights(20000, 3), Key, Payload);

        // training moves every weight; the thesis finds the moves roughly Gaussian (Figure 55): a move of the size of the weights themselves
        var r = new Random(7); var trained = marked.Select(w => w + (float)(0.05 * Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble()))).ToArray();
        Assert.True(NoConstraint.Extract(trained, Key, 16).Carries(Payload));

        // magnitude pruning: the 80% smallest weights set to zero (the mark's values are at least half a standard deviation)
        var cut = marked.Select(Math.Abs).OrderBy(v => v).ElementAt((int)(0.8 * marked.Length));
        var pruned = marked.Select(w => Math.Abs(w) < cut ? 0f : w).ToArray();
        Assert.True(NoConstraint.Extract(pruned, Key, 16).Carries(Payload));

        // quantisation to 4 bits, uniform over the range of the weights
        float lo = marked.Min(), hi = marked.Max(), step = (hi - lo) / 15;
        var quantised = marked.Select(w => lo + step * MathF.Round((w - lo) / step)).ToArray();
        Assert.True(NoConstraint.Extract(quantised, Key, 16).Carries(Payload));
    }

    // A model with no mark: the test of presence says "present" about 5 times in 100 (its level), and the payload is never matched.
    [Fact]
    public void ChanceGivesAFalsePresenceAboutFiveTimesInAHundredAndNeverThePayload()
    {
        int present = 0, carried = 0;
        for (var m = 0; m < 200; m++)
        {
            var d = NoConstraint.Extract(Weights(5000, 1000 + m), Key, 16);
            if (d.Present) present++;
            if (d.Carries(Payload)) carried++;
        }
        Assert.InRange(present, 2, 25);
        Assert.Equal(0, carried);
    }

    // Fewer values a bit (a small network): the mark is smaller and still read back, and the extraction must use the same number.
    [Fact]
    public void AMarkOfFewerValuesABitIsReadBackWithThatNumber()
    {
        var original = Weights(6000, 8);
        var marked = NoConstraint.Insert(original, Key, Payload, valuesPerBit: 16);
        Assert.Equal(16 * 16, original.Zip(marked).Count(p => p.First != p.Second));
        Assert.True(NoConstraint.Extract(marked, Key, 16, valuesPerBit: 16).Carries(Payload));
        Assert.False(NoConstraint.Extract(original, Key, 16, valuesPerBit: 16).Carries(Payload));
        Assert.False(NoConstraint.Extract(marked, Key, 16).Carries(Payload));                                     // read as 64 values a bit: not found
    }

    [Fact]
    public void FingerprintingReadsWithoutChanging()
    {
        var w = Weights(20000, 4);
        var fp = NoConstraint.Fingerprint(w, Key, 512);
        Assert.Equal(1.0, NoConstraint.MatchFingerprint(w, Key, fp), 6);
        Assert.True(NoConstraint.MatchFingerprint(Weights(20000, 5), Key, fp) < NoConstraint.Threshold(512));
    }

    [Fact]
    public void TheMethodNameCarriesTheNumberOfBits()
    {
        Assert.Equal(16, NoConstraint.BitsOf(NoConstraint.MethodName(16)));
        Assert.Equal((16, 8), (NoConstraint.BitsOf(NoConstraint.MethodName(16, 8)), NoConstraint.ValuesPerBitOf(NoConstraint.MethodName(16, 8))));
        Assert.Null(NoConstraint.BitsOf("another method"));
        Assert.Null(NoConstraint.BitsOf(null));
    }

    // ---- the AIMs, on the frozen network of the HSP federated learning (an ONNX file) ----

    private static string Network => Path.Combine(Repository.Root, "Test", "Data", "Fl", "frozen-backbone.onnx");
    private static AmdStore Amds() { var s = new AmdStore(Path.Combine(Repository.Root, "AIMs", "AMDs")); s.Scan(); return s; }

    internal static JsonObject ModelWithNetwork(byte[] onnx) => new()
    {
        ["Header"] = "AIP-MLO-V1.0", ["MLModelObjectID"] = "model-1",
        ["MLModelData"] = new JsonArray(new JsonObject { ["Data"] = Convert.ToBase64String(new byte[16]) }, new JsonObject { ["Data"] = "1" }, new JsonObject { ["Data"] = Convert.ToBase64String(onnx) })
    };

    private static Message Msg(params (string Port, JsonObject Value)[] ports) =>
        new() { MessageId = Guid.NewGuid().ToString("N"), Ports = ports.ToDictionary(p => p.Port, p => p.Value.ToJsonString()) };

    [Fact]
    public async Task TheTwoAimsInsertAndFindTheMarkInAnOnnxModel()
    {
        var amds = Amds();
        var provider = new NnwProvider(amds);
        Assert.True(provider.CanCreate(NnwProvider.Pwi) && provider.CanCreate(NnwProvider.Wex));
        var pwiPorts = AimPortReader.Load(amds, NnwProvider.Pwi); var wexPorts = AimPortReader.Load(amds, NnwProvider.Wex);
        var pwi = provider.Create(NnwProvider.Pwi, new Dictionary<string, string>(), null);
        var wex = provider.Create(NnwProvider.Wex, new Dictionary<string, string>(), null);

        var key = NnwJson.NewKey("key-1"); var locator = NnwJson.LocatorObject("wlc-1", 16, "key:backend-1");
        var original = File.ReadAllBytes(Network);
        var model = ModelWithNetwork(original);

        var marked = await pwi.ProcessAsync(Msg((pwiPorts.Input(NnwJson.Model), model), (pwiPorts.Input(NnwJson.Payload), NnwJson.PayloadObject("p", "1010011100101101")),
                                                (pwiPorts.Input(NnwJson.Key), key), (pwiPorts.Input(NnwJson.Locator), locator)));
        Assert.False(marked.IsError, marked.Payload);
        var markedModel = JsonNode.Parse(marked.Payload)!.AsObject();
        var markedOnnx = Convert.FromBase64String(markedModel["MLModelData"]![2]!["Data"]!.GetValue<string>());
        Assert.Equal(original.Length, markedOnnx.Length);                                                      // edited in place
        Assert.Equal(model["MLModelData"]![0]!.ToJsonString(), markedModel["MLModelData"]![0]!.ToJsonString());  // the final layer is not an ONNX file: left as it is

        var found = await wex.ProcessAsync(Msg((wexPorts.Input(NnwJson.Model), markedModel), (wexPorts.Input(NnwJson.Key), key), (wexPorts.Input(NnwJson.Locator), locator)));
        Assert.False(found.IsError, found.Payload);
        var payload = JsonNode.Parse(found.Payload)!.AsObject();
        Assert.Equal("NNW-PLD-V2.0", (string?)payload["Header"]);
        Assert.Equal("1010011100101101", (string?)payload["PayloadData"]);
        Assert.StartsWith("mark present", (string?)payload["DescrMetadata"]);

        var none = await wex.ProcessAsync(Msg((wexPorts.Input(NnwJson.Model), model), (wexPorts.Input(NnwJson.Key), key), (wexPorts.Input(NnwJson.Locator), locator)));
        Assert.StartsWith("mark NOT present", (string?)JsonNode.Parse(none.Payload)!["DescrMetadata"]);

        // what the AIMs refuse: another method, no Locator for the extraction, a model with no ONNX file
        var otherMethod = new JsonObject { ["Header"] = "NNW-WLC-V2.0", ["LocatorID"] = "x", ["Method"] = "another method" };
        Assert.True((await pwi.ProcessAsync(Msg((pwiPorts.Input(NnwJson.Model), model), (pwiPorts.Input(NnwJson.Payload), NnwJson.PayloadObject("p", "10")),
                                                 (pwiPorts.Input(NnwJson.Key), key), (pwiPorts.Input(NnwJson.Locator), otherMethod)))).IsError);
        Assert.True((await wex.ProcessAsync(Msg((wexPorts.Input(NnwJson.Model), markedModel), (wexPorts.Input(NnwJson.Key), key)))).IsError);
        var noOnnx = new JsonObject { ["Header"] = "AIP-MLO-V1.0", ["MLModelObjectID"] = "m", ["MLModelData"] = new JsonArray(new JsonObject { ["Data"] = Convert.ToBase64String(new byte[64]) }) };
        Assert.True((await pwi.ProcessAsync(Msg((pwiPorts.Input(NnwJson.Model), noOnnx), (pwiPorts.Input(NnwJson.Payload), NnwJson.PayloadObject("p", "10")), (pwiPorts.Input(NnwJson.Key), key)))).IsError);
    }

    [Fact]
    public void TheMarkAvoidsTheFirstAndLastLayersOfTheNetwork()
    {
        var onnx = File.ReadAllBytes(Network);
        var layers = OnnxFloats.Weights(onnx).Where(t => t.Dims.Length >= 2).ToList();
        var targets = NoConstraint.Targets(onnx);
        if (layers.Count >= 3)
        {
            Assert.DoesNotContain(targets, t => t.Name == layers[0].Name);
            Assert.DoesNotContain(targets, t => t.Name == layers[^1].Name);
        }
        Assert.True(targets.Sum(t => t.Count) >= 2 * 16 * NoConstraint.ValuesPerBit, "room for a 16-bit mark: " + targets.Sum(t => t.Count));
    }
}
