using System.Net;
using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.Store;
using Mpai.Aih;
using Mpai.Aih.Nnm;

namespace Mpai.Aif.Tests;

// THE HEALTH BACK END IS AN IMPLEMENTER OF THE MPAI STORE: a human (the manager of the Back End) registers it as a machine; MPAI approves; the Back End signs what it submits. It publishes
// the AIM that wraps its model, and a Front End that has it, through the Store Service - not through the Store library - and a Controller obtains them over HTTP and verifies them with the Store.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class AihStoreServiceTests
{
    private static JsonObject Model(string id, float mean, float count) => new()
    {
        ["Header"] = "AIP-MLO-V1.0", ["MLModelObjectID"] = id,
        ["MLModelData"] = new JsonArray(new JsonObject { ["Data"] = Convert.ToBase64String(new[] { mean, count }.SelectMany(BitConverter.GetBytes).ToArray()) }),
        ["DescrMetadata"] = "test model"
    };

    // MPAI submits the L3s of the Sub-AIMs of the Front End, which the Front End cannot be published without (the Store refuses a Composite AIM whose Sub-AIMs it does not have).
    private static async Task PublishSubAims(StoreTests.RunningStore store, JsonObject hfe)
    {
        var package = new Uri(Path.Combine(store.Packages, "mpai") + Path.DirectorySeparatorChar).AbsoluteUri;
        foreach (var sub in hfe["SubAIMs"]!.AsArray())
        {
            var name = (string)sub!["Identifier"]!["AIMName"]!;
            var l3 = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Amds, name + ".json")))!;
            foreach (var impl in l3["Implementations"]!.AsArray()) impl!["ImplementationURI"] = package;
            var answer = await StoreTests.Submit(store.Http, l3, null, StoreTests.Admin);
            Assert.True(answer.Status == HttpStatusCode.Created, $"{name}: {answer.Status} {answer.Body}");
        }
    }

    [SkippableFact]
    public async Task TheBackEndRegistersAsAMachinePublishesThroughTheServiceAndAControllerObtainsWhatItPublished()
    {
        using var store = new StoreTests.RunningStore();
        var http = store.Http;
        var hfe = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Amds, "1AIH-HFE-V1.1-I01.json")))!.AsObject();
        await PublishSubAims(store, hfe);

        // A human registers the Back End, a machine; MPAI approves it.
        var backEnd = await StoreTests.Implementer.Approved(http, "Machine", "Health Back End");
        var publisher = new ModelAimPublisher(store.Url, backEnd.Id, backEnd.Key, Path.Combine(store.Packages, "health-back-end"));

        // It publishes the AIM that wraps its model: signed, through the Service, which reads the model where the Back End keeps it and records its fingerprint.
        var aim = publisher.Upload(Model("model-1", 0.9f, 3));
        Assert.Equal(backEnd.Id + "AIH-NNM-V1.1-I01", aim);
        Assert.True(publisher.Has(aim));
        Assert.True(File.Exists(Path.Combine(store.StoreFolder, "approved", aim + ".json")));
        // The model stays with the Back End: the Store folder holds no copy of it.
        Assert.False(Directory.EnumerateFiles(store.StoreFolder, "*.model.json", SearchOption.AllDirectories).Any());

        // A Controller of a Front End, with a folder of its own, obtains the AIM over HTTP and builds it: the model is the one the Store approved.
        using var source = new HttpAimSource(store.Url);
        var controller = Path.Combine(store.Root, "controller");
        var repository = new AimRepository(controller);
        Assert.True(repository.Obtain(aim, source));
        var amds = new AmdStore(controller); amds.Scan();
        var built = Assert.IsType<NnmAimProcessor>(new AihProvider(amds, repository).Create(aim, new Dictionary<string, string>(), null));
        Assert.Equal("model-1", built.ModelId);

        // A model changed where the Back End keeps it, after the Store approved it, is not kept by another Controller.
        File.WriteAllText(publisher.ModelFile(aim), Model("model-1", 0.1f, 3).ToJsonString());
        var second = new AimRepository(Path.Combine(store.Root, "controller-2"));
        Assert.Contains("NOT_TRUSTED", Assert.Throws<InvalidOperationException>(() => second.Obtain(aim, source)).Message);
        File.WriteAllText(publisher.ModelFile(aim), Model("model-1", 0.9f, 3).ToJsonString());

        // It publishes a Front End that has the AIM, as its own Implementation, and each new version after it; the Store keeps every version.
        var baseL3 = (JsonObject)hfe.DeepClone();
        foreach (var impl in baseL3["Implementations"]!.AsArray()) impl!["ImplementationURI"] = new Uri(Path.Combine(store.Packages, "mpai") + Path.DirectorySeparatorChar).AbsoluteUri;
        var version = publisher.PublishFrontEnd(baseL3.ToJsonString(), aim);
        Assert.Equal("1.2", version);
        var again = publisher.Upload(Model("model-2", 0.2f, 1));
        Assert.Equal(backEnd.Id + "AIH-NNM-V1.1-I02", again);
        Assert.Equal("1.3", publisher.PublishFrontEnd(baseL3.ToJsonString(), again));
        var frontEnd = backEnd.Id + "AIH-HFE-V1.1-I01";
        using (var versions = await http.GetAsync($"/MPAI/Store/L3/{frontEnd}/versions"))
            Assert.Equal(2, JsonNode.Parse(await versions.Content.ReadAsStringAsync())!.AsArray().Count);
        Assert.Equal("1.3", source.CurrentVersion(frontEnd));

        // Only the registered Back End: another key submits nothing under its Implementer ID.
        var impostor = new ModelAimPublisher(store.Url, backEnd.Id, ImplementerKey.Create(), Path.Combine(store.Packages, "impostor"));
        Assert.Contains("signature", Assert.Throws<InvalidOperationException>(() => impostor.Upload(Model("model-x", 0.5f, 1))).Message);
    }
}
