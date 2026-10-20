using System.Text.Json;
using AIF.Metadata;
using Json.Schema;

namespace Mpai.Aif.Tests;

// NNW-NNT V1.2: where a watermark sits (the Locator, NNW-WLC) - neither the model (AIP-MLO) nor the Payload.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class NnwV12Tests
{
    private static bool Valid(string json)
    {
        var root = Path.GetFullPath(Repository.Schemas);
        var compiled = PublishedSchemas.At(root)[Path.Combine(root, "NNW1", "V1.2", "data", "Locator.json")];
        using var document = JsonDocument.Parse(json);
        lock (PublishedSchemas.Lock) return compiled.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;
    }

    [Fact]
    public void ALocatorNamesTheMethodAndMayNameTheTensorsAndAKeyReferenceNeverAKey()
    {
        Assert.True(Valid("""{ "Header": "NNW-WLC-V1.2", "LocatorID": "l-1", "Method": "spread-spectrum-v1" }"""));
        Assert.True(Valid("""{ "Header": "NNW-WLC-V1.2", "LocatorID": "l-1", "Method": "m", "Targets": ["conv1.weight", "fc.weight"], "KeyReference": "key-7" }"""));
        Assert.False(Valid("""{ "Header": "NNW-WLC-V1.2", "LocatorID": "l-1" }"""));                                  // the method is required
        Assert.False(Valid("""{ "Header": "NNW-WLC-V1.2", "LocatorID": "l-1", "Method": "m", "Targets": [] }"""));       // omitted, or at least one
        Assert.False(Valid("""{ "Header": "NNW-WLC-V1.2", "LocatorID": "l-1", "Method": "m", "SecretKey": "abc" }"""));  // a key is never carried
    }

    [Fact]
    public void TheInsertionAimsGiveTheLocatorAndTheExtractionAimMayTakeIt()
    {
        var folder = Path.Combine(Repository.Schemas, "NNW1", "V1.2", "AIMs");
        (string File, string Direction, bool Optional)[] expected = [("PostTrainingWatermarkInsertion", "Output", false), ("DuringTrainingWatermarkInsertion", "Output", true), ("WatermarkExtraction", "Input", true)];     // Post-Training Watermark Insertion always gives it; the others are still to be reviewed
        foreach (var (file, direction, optional) in expected)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, file + ".json")));
            var port = doc.RootElement.GetProperty("ExternalPorts").EnumerateArray().Single(p => p.GetProperty("DataType").GetString() == "NNW-WLC-V1.2");
            Assert.Equal(direction, port.GetProperty("Direction").GetString());
            Assert.Equal(optional, port.TryGetProperty("IsOptional", out var o) && o.GetBoolean());
        }
    }

    // The complete Ports of the three AIMs (Direction, Data Type, optional): a lost or an extra Port is a failure.
    [Fact]
    public void TheThreeAimsHaveExactlyTheirPorts()
    {
        var folder = Path.Combine(Repository.Schemas, "NNW1", "V1.2", "AIMs");
        var expected = new Dictionary<string, string[]>
        {
            ["PostTrainingWatermarkInsertion"] = ["Input AIP-MLO-V1.0", "Input NNW-PLD-V1.2", "Output AIP-MLO-V1.0", "Output NNW-WLC-V1.2"],
            ["DuringTrainingWatermarkInsertion"] = ["Input AIP-MLO-V1.0 optional", "Input NNW-DST-V1.2", "Input NNW-PLD-V1.2", "Output AIP-MLO-V1.0", "Output NNW-WLC-V1.2 optional"],
            ["WatermarkExtraction"] = ["Input AIP-MLO-V1.0", "Input NNW-WLC-V1.2 optional", "Output NNW-PLD-V1.2"]
        };
        foreach (var (file, ports) in expected)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, file + ".json")));
            var actual = doc.RootElement.GetProperty("ExternalPorts").EnumerateArray()
                .Select(p => $"{p.GetProperty("Direction").GetString()} {p.GetProperty("DataType").GetString()}" + (p.TryGetProperty("IsOptional", out var o) && o.GetBoolean() ? " optional" : "")).ToArray();
            Assert.Equal(ports, actual);
        }
    }
}




