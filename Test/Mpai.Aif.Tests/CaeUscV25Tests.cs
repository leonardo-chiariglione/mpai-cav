using System.Text.Json;

namespace Mpai.Aif.Tests;

// CAE-USC V2.5: the four AI Workflows of V2.4 are Composite AIMs, and two AIMs are added beside Emotion Enhanced Speech, which stays (Add Emotion to Speech, CAE-AES; Speak Model with Emotion, CAE-SME: what dividing it into two Modules gives), so there are 31 AIMs
// (schemas/CAE1/V2.5/AIMs; Audio Analysis Transform, CAE-AAT, is now Short-Time Fourier Transform, CAE-SFT). Each is valid against the AIM Metadata schema (MetadataTests.Schema checks
// every L2); here the Composite AIMs are checked as a graph: every SubAIM has an L2, every connection joins an Output Port to an Input Port of the Data Type and Port Number it names, and the
// Topology resolves.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class CaeUscV25Tests
{
    private static readonly string Folder = Path.Combine(Repository.Schemas, "CAE1", "V2.5", "AIMs");

    private static Dictionary<string, JsonElement> AllL2s()
    {
        var all = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Repository.Schemas, "*.json", SearchOption.AllDirectories).Where(f => Path.GetFileName(Path.GetDirectoryName(f)) == "AIMs"))
        {
            try
            {
                var l2 = JsonDocument.Parse(File.ReadAllText(file)).RootElement.Clone();
                if (l2.TryGetProperty("Header", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } name) all[name] = l2;   // an L2 is known by its Header
            }
            catch (JsonException) { }
        }
        return all;
    }

    private record Port(string Direction, string[] Types, int Number);

    private static List<Port> Ports(JsonElement l2)
    {
        var ports = new List<Port>(); var seen = new Dictionary<(string, string), int>();
        var all = l2.GetProperty("ExternalPorts").EnumerateArray().ToList();
        foreach (var p in all)
        {
            var types = p.GetProperty("DataType").ValueKind == JsonValueKind.Array ? p.GetProperty("DataType").EnumerateArray().Select(t => t.GetString()!).ToArray() : [p.GetProperty("DataType").GetString()!];
            var dir = p.GetProperty("Direction").GetString()!;
            var number = p.TryGetProperty("PortNumber", out var n) ? n.GetInt32() : 1;
            ports.Add(new Port(dir, types, number));
        }
        return ports;
    }

    [Fact]
    public void ThereAreThirtyOneAimsAndTheFourWorkflowsAreCompositeAims()
    {
        var files = Directory.EnumerateFiles(Folder, "*.json").ToList();
        Assert.Equal(31, files.Count);
        foreach (var name in new[] { "AudioRecordingPreservation", "EmotionEnhancedSpeech", "EnhancedAudioconferenceExperience", "SpeechRestorationSystem" })
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, name + ".json")));
            Assert.True(doc.RootElement.GetProperty("SubAIMs").GetArrayLength() >= 3, name);
            Assert.True(doc.RootElement.TryGetProperty("Execution", out _), name);
        }
        // the Module with a loop (the Audio and the Video Analysis exchange Irregularity Files) is executed continuously
        using var arp = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "AudioRecordingPreservation.json")));
        Assert.Equal("Continuous", arp.RootElement.GetProperty("Execution").GetString());
    }

    [Fact]
    public void EveryAimIsNamedByItsFileItsHeaderAndItsIdentifier()
    {
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file)); var r = doc.RootElement;
            var name = r.GetProperty("Header").GetString()!;                                    // the AIM Type; the Identifier is that of L1
            Assert.Matches(@"^[A-Z0-9]{3}-[A-Z0-9]{3}-V[0-9]+\.[0-9]+$", name);               // the canonical form
            Assert.StartsWith("/*", r.GetProperty("Identifier").GetProperty("AIMName").GetString());   // not filled in: the Implementer's
            Assert.EndsWith("-V2.5", name);
            Assert.Equal($"https://schemas.mpai.community/CAE1/V2.5/AIMs/{Path.GetFileName(file)}", r.GetProperty("$id").GetString());
        }
    }

    [Fact]
    public void TheCompositeAimsAreGraphsWhoseConnectionsJoinPortsThatExist()
    {
        var all = AllL2s(); var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file)); var r = doc.RootElement; var me = r.GetProperty("Header").GetString()!;
            if (r.GetProperty("SubAIMs").GetArrayLength() == 0) continue;
            AIF.Store.TopologyNormaliser.Normalise(r).Dispose();                                 // the Topology resolves into Data Types and Port Numbers
            var subs = r.GetProperty("SubAIMs").EnumerateArray().Select(s => s.GetProperty("Header").GetString()!).ToList();
            foreach (var s in subs) if (!all.ContainsKey(s)) problems.Add($"{me}: SubAIM {s} has no L2");
            var own = Ports(r);
            foreach (var c in r.GetProperty("Topology").EnumerateArray())
            {
                bool Has(JsonElement end, string direction)
                {
                    var aim = end.GetProperty("AIMName").GetString()!; var type = end.GetProperty("DataType").GetString()!; var number = end.TryGetProperty("PortNumber", out var n) ? n.GetInt32() : 1;
                    // the boundary of this AIM: its Input Ports feed the Topology (they are the Output of a connection), its Output Ports take from it
                    var ports = aim.Length == 0 ? own : all.TryGetValue(aim, out var l2) ? Ports(l2) : [];
                    var wanted = aim.Length == 0 ? (direction == "Output" ? "Input" : "Output") : direction;
                    return ports.Any(p => p.Direction == wanted && p.Types.Contains(type) && p.Number == number);
                }
                if (!Has(c.GetProperty("Output"), "Output")) problems.Add($"{me}: no Output Port for {c.GetProperty("Output")}");
                if (!Has(c.GetProperty("Input"), "Input")) problems.Add($"{me}: no Input Port for {c.GetProperty("Input")}");
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
