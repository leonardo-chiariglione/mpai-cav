using System.Text.Json;
using System.Text.Json.Nodes;

using Mpai.Cav.Ams;
using Mpai.Cav.Ess;

namespace Mpai.Aif.Tests;

// THE SCENE THE FED GIVES TO THE PASSENGER (the author, 2026/11/01: the CAV Reference Model, slides 8 and 9).
// What the Full Environment Description describes is also given as Basic Multimodal Scene Descriptors, for
// Human-CAV Interaction to render: each is valid against its schema, holds the CAV and every object of the FED,
// and keeps to the time of the FED.
[Trait("Group", "Fast")]
public class FedSceneTests
{
    [Fact]
    public void TheScenePlacesTheCavAndEveryObjectOfTheFed()
    {
        var sim = AmsStage1Tests.OpenScenario();
        var fed = new FullEnvironmentDescription(AmsProvider.Fed);
        fed.Know(JsonNode.Parse(sim.Map.ToOfflineMapObject(0))!);
        var sensed = sim.Sense(camera: false);
        var described = fed.Describe(JsonNode.Parse(TruthBed.Of(sim, sensed))!);
        var scene = fed.SceneDescriptors(described);

        var schemas = AIF.Metadata.PublishedSchemas.At(Repository.Schemas);
        var schema = schemas[Path.GetFullPath(Path.Combine(Repository.Schemas, "OSD", "V1.5", "data", "BasicMultimodalSceneDescriptors.json"))];
        using var doc = JsonDocument.Parse(scene.ToJsonString());
        bool valid;
        lock (AIF.Metadata.PublishedSchemas.Lock) valid = schema.Evaluate(doc.RootElement).IsValid;

        var objects = described["FullEnvironmentObjects"]!.AsArray();
        Assert.True(valid, "the scene validates against OSD-BMS-V1.5");
        Assert.Equal("OSD-BMS-V1.5", (string?)scene["Header"]);
        Assert.Equal(objects.Count + 1, scene["BasicObjectsData"]!.AsArray().Count);
        Assert.Equal(objects.Count + 1, (int)scene["MultimodalObjectCount"]!);
        Assert.Equal(AmsTypes.Ms(described["FullEnvironmentDescriptorsTime"]), EssJson.Milliseconds(scene["BMSDescriptorsTime"]));
        Assert.True(objects.Count > 0, "the scenario has objects around the CAV");
        foreach (var entry in objects)
            Assert.Contains(scene["BasicObjectsData"]!.AsArray(), m => (string?)m!["BXOOrBXOID"] == (string?)entry!["BasicEnvironmentObject"]!["BasicEnvironmentObjectID"]);
    }
}
