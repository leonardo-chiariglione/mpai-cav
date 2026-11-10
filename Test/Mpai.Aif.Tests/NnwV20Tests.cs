using System.Text.Json;
using AIF.Metadata;
using Json.Schema;

namespace Mpai.Aif.Tests;

// NNW1 V2.0 (Neural Network Watermarking): the five Data Types accept what they describe and refuse the rest, and the AIMs have exactly their Ports and are made of AIMs that exist.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class NnwV20Tests
{
    private static readonly string Folder = Path.Combine(Repository.Schemas, "NNW1", "V2.0");

    private static bool Valid(string dataType, string json)
    {
        var root = Path.GetFullPath(Repository.Schemas);
        var compiled = PublishedSchemas.At(root)[Path.Combine(root, "NNW1", "V2.0", "data", dataType + ".json")];
        using var document = JsonDocument.Parse(json);
        lock (PublishedSchemas.Lock) return compiled.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;
    }

    // (file, Header, the name of the ID and of the Data member, a Data value that is right)
    private static readonly (string File, string Header, string Name, string Value)[] DataTypes =
    [
        ("Payload", "NNW-PLD-V2.0", "Payload", "\"0110\""),
        ("Dataset", "NNW-DST-V2.0", "Dataset", "\"AAEC\""),
        ("Parameters", "NNW-PRM-V2.0", "Parameters", "\"AAEC\""),
        ("Inference", "NNW-INF-V2.0", "Inference", "\"AAEC\""),
        ("CountError", "NNW-CER-V2.0", "CountError", "3")
    ];

    [Fact]
    public void TheDataTypesAcceptWhatTheyDescribe()
    {
        foreach (var (file, header, name, value) in DataTypes)
        {
            Assert.True(Valid(file, $$"""{ "Header": "{{header}}", "{{name}}ID": "id-1", "{{name}}Data": {{value}} }"""), file);
            Assert.True(Valid(file, $$"""{ "Header": "{{header}}", "{{name}}ID": "id-1", "{{name}}Data": {{value}}, "DescrMetadata": "a note" }"""), file + " with a note");
        }
    }

    [Fact]
    public void TheDataTypesRefuseWhatTheyDoNotDescribe()
    {
        foreach (var (file, header, name, value) in DataTypes)
        {
            Assert.False(Valid(file, $$"""{ "Header": "{{header}}", "{{name}}Data": {{value}} }"""), file + " without its ID");
            Assert.False(Valid(file, $$"""{ "Header": "{{header}}", "{{name}}ID": "id-1" }"""), file + " without its Data");
            Assert.False(Valid(file, $$"""{ "Header": "NNW-XXX-V2.0", "{{name}}ID": "id-1", "{{name}}Data": {{value}} }"""), file + " with another Header");
            Assert.False(Valid(file, $$"""{ "Header": "{{header}}", "{{name}}ID": "", "{{name}}Data": {{value}} }"""), file + " with an empty ID");
            Assert.False(Valid(file, $$"""{ "Header": "{{header}}", "{{name}}ID": "id-1", "{{name}}Data": {{value}}, "Extra": 1 }"""), file + " with a member it does not have");
        }
        Assert.False(Valid("Payload", """{ "Header": "NNW-PLD-V2.0", "PayloadID": "p", "PayloadData": "0120" }"""));        // a payload is a bitstring
        Assert.False(Valid("CountError", """{ "Header": "NNW-CER-V2.0", "CountErrorID": "c", "CountErrorData": -1 }"""));   // a count is not negative
    }

    // The Ports of each AIM: Direction, Data Type, Port Number where it has one, "optional" where it is. A lost or an extra Port is a failure.
    private static readonly Dictionary<string, string[]> Ports = new()
    {
        ["Comparator"] = ["Input NNW-PLD-V2.0 #1", "Input NNW-PLD-V2.0 #2", "Output NNW-CER-V2.0"],
        ["ModificationModule"] = ["Input NNW-PRM-V2.0", "Output NNW-PRM-V2.0"],
        ["ModifiedModule"] = ["Input NNW-PRM-V2.0", "Input NNW-DST-V2.0", "Output NNW-INF-V2.0"],
        ["ModuleTrainer"] = ["Input NNW-PRM-V2.0 optional", "Input NNW-DST-V2.0", "Output NNW-PRM-V2.0"],
        ["NIRWatermarkDecoder"] = ["Input NNW-PRM-V2.0", "Output NNW-PLD-V2.0"],
        ["NTIWatermarkEmbedder"] = ["Input NNW-PRM-V2.0", "Input NNW-PLD-V2.0", "Output NNW-PRM-V2.0"],
        ["NoInferenceRobustness"] = ["Input NNW-PLD-V2.0", "Input NNW-PRM-V2.0", "Output NNW-PLD-V2.0", "Output NNW-CER-V2.0"],
        ["NoTrainingImperceptibility"] = ["Input NNW-DST-V2.0 #1", "Input NNW-DST-V2.0 #2", "Input NNW-DST-V2.0 #3", "Input NNW-DST-V2.0 #4", "Input NNW-PLD-V2.0", "Output NNW-INF-V2.0 #1", "Output NNW-INF-V2.0 #2"],
        ["UnwatermarkedModule"] = ["Input NNW-PRM-V2.0", "Input NNW-DST-V2.0", "Output NNW-INF-V2.0"],
        ["WIRWatermarkDecoder"] = ["Input NNW-INF-V2.0", "Output NNW-PLD-V2.0"],
        ["WTIWatermarkEmbedder"] = ["Input NNW-PRM-V2.0 optional", "Input NNW-DST-V2.0", "Input NNW-PLD-V2.0", "Output NNW-PRM-V2.0"],
        ["WatermarkedModule"] = ["Input NNW-PRM-V2.0", "Input NNW-DST-V2.0", "Output NNW-INF-V2.0"],
        ["WithInferenceRobustness"] = ["Input NNW-PLD-V2.0", "Input NNW-DST-V2.0", "Input NNW-PRM-V2.0", "Output NNW-PLD-V2.0", "Output NNW-CER-V2.0"],
        ["WithTrainingImperceptibility"] = ["Input NNW-DST-V2.0 #1", "Input NNW-DST-V2.0 #2", "Input NNW-DST-V2.0 #3", "Input NNW-DST-V2.0 #4", "Input NNW-PLD-V2.0", "Output NNW-INF-V2.0 #1", "Output NNW-INF-V2.0 #2"]
    };

    private static string[] PortsOf(JsonElement l2) =>
        l2.GetProperty("ExternalPorts").EnumerateArray().Select(p =>
            $"{p.GetProperty("Direction").GetString()} {p.GetProperty("DataType").GetString()}"
            + (p.TryGetProperty("PortNumber", out var n) ? $" #{n.GetInt32()}" : "")
            + (p.TryGetProperty("IsOptional", out var o) && o.GetBoolean() ? " optional" : "")).ToArray();

    [Fact]
    public void TheAimsHaveExactlyTheirPorts()
    {
        foreach (var (file, ports) in Ports)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "AIMs", file + ".json")));
            Assert.Equal(ports, PortsOf(doc.RootElement));
        }
    }

    [Fact]
    public void TheAimsAreNamedForV20AndTheCompositesAreMadeOfAimsThatExist()
    {
        var headers = new Dictionary<string, JsonElement>();
        foreach (var file in Ports.Keys)
        {
            var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "AIMs", file + ".json")));
            var header = doc.RootElement.GetProperty("Header").GetString()!;
            Assert.Matches(@"^NNW-[A-Z]{3}-V2\.0$", header);
            headers[header] = doc.RootElement.Clone();
        }
        foreach (var (header, l2) in headers)
            foreach (var sub in l2.GetProperty("SubAIMs").EnumerateArray())
                Assert.True(headers.ContainsKey(sub.GetProperty("Header").GetString()!), header + " names a SubAIM that is not an AIM of NNW1 V2.0: " + sub.GetProperty("Header").GetString());
    }
}
