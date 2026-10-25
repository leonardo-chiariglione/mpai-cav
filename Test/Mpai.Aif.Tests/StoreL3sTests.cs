using System.Text.Json;

using AIF.Metadata;

namespace Mpai.Aif.Tests;

// THE L3s FOR THE MPAI STORE (MPAIStore/L3s) AGAINST THEIR L2s: the Store validates a submitted L3 against the L2 of the AIM it is an instance of
// (the Header names the L2) and does not use the L1; so what MPAI gives it must conform to the L2s, including a Composite AIM's sub-AIMs
// and their connections. Each L2 was itself checked against the L1 (MetadataTests.Schema).
[Trait("Group", "Fast")]
[Trait("Blocks", "No")]
public class StoreL3sTests
{
    private static readonly string Folder = Path.Combine(Repository.Root, "MPAIStore", "L3s", "AMQ");

    [Fact]
    public void TheL3sOfAmqForTheStoreConformToTheirL2s()
    {
        JsonElement? FindL3(string instance)
        {
            var file = Path.Combine(Folder, instance + ".json");
            return File.Exists(file) ? JsonDocument.Parse(File.ReadAllText(file)).RootElement.Clone() : null;
        }
        var conformance = new L2Conformance(Repository.Schemas, FindL3);
        var files = Directory.GetFiles(Folder, "*.json");
        Assert.Equal(5, files.Length);
        foreach (var file in files)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            Assert.Equal([], conformance.Check(doc.RootElement));
        }
    }
}
