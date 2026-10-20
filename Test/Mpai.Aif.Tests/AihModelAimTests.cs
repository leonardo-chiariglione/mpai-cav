using System.Text.Json;
using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.Store;
using Mpai.Aih;
using Mpai.Aih.Nnm;

namespace Mpai.Aif.Tests;

// THE NEURAL NETWORK MODEL AS AN AIM (AIH-NNM-V1.1), and the Controller's repository of AIMs: the Back End wraps the model it got from federated learning in an AIM and
// uploads it to the MPAI Store, which publishes it only if its L3 conforms to its L2; a Controller obtains it from the Store, keeps it - it has the right to use it, and
// does not obtain it again - and builds it provided its model is the one the Store approved. The Store has a newer version of the Front End when the Back End has published one.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class AihModelAimTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "aih-model-store-" + Guid.NewGuid().ToString("N"));
    private string Repo => Path.Combine(folder, "controller");
    private readonly AIF.Metadata.L2Conformance conformance = new(Repository.Schemas, _ => null);

    public void Dispose() { try { Directory.Delete(folder, true); } catch { } }

    private ModelAimPublisher Store() => new(Path.Combine(folder, "store"), Path.Combine(folder, "back-end"), l3 => conformance.Check(l3));
    private StoreAimSource Source() => new(Path.Combine(folder, "store"), "hfe-1");

    private static JsonObject Model(string id, float mean, float count) => new()
    {
        ["Header"] = "AIP-MLO-V1.0", ["MLModelObjectID"] = id,
        ["MLModelData"] = new JsonArray(new JsonObject { ["Data"] = Convert.ToBase64String(new[] { mean, count }.SelectMany(BitConverter.GetBytes).ToArray()) }),
        ["DescrMetadata"] = "test model"
    };

    // The AIM as the Controller builds it, from the repository.
    private IAimProcessor Build(AimRepository repository, string aim)
    {
        var amds = new AmdStore(repository.Folder); amds.Scan();
        return new AihProvider(amds, repository).Create(aim, new Dictionary<string, string>(), null);
    }

    private static async Task<string> Judge(IAimProcessor aim, byte[] ecg)
    {
        var data = AihSamples.AihData();
        data["AIHData"]!["HealthData"]!["HealthDataData"] = new JsonArray(new JsonObject { ["Data"] = Convert.ToBase64String(ecg) });
        data["AIHData"]!.AsObject().Remove("SmartContractID"); data["AIHData"]!.AsObject().Remove("DeIDAndAnonym");
        var amds = new AmdStore(Repository.Amds); amds.Scan();
        var key = AimPortReader.Load(amds, AihProvider.Ara).Input(AihJson.AihData);
        var reply = await aim.ProcessAsync(new Message { MessageId = "m", Ports = new Dictionary<string, string> { [key] = data.ToJsonString() } });
        Assert.False(reply.IsError, reply.Payload);
        return JsonNode.Parse(reply.Payload)!["ARAData"]!["Risk"]!.ToString();
    }

    [Fact]
    public void TheL2OfTheModelAimIsInTheSchemas() => Assert.True(File.Exists(Path.Combine(Repository.Schemas, "AIH1", "V1.1", "AIMs", "NeuralNetworkModel.json")));

    [Fact]
    public async Task TheBackEndUploadsTheModelAsAnAimAndAControllerObtainsAndKeepsIt()
    {
        var store = Store();
        var aim = store.Upload(Model("model-0-federated-1", 0.9f, 3));
        Assert.Equal("1AIH-NNM-V1.1-I01", aim);
        Assert.True(store.Has(aim));
        Assert.Empty(conformance.Check(JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "store", aim + ".json"))).RootElement));

        // A Controller obtains it: from the Store the first time, from its repository after that - the Store gives, and the Controller pays, once.
        var source = Source(); var repository = new AimRepository(Repo);
        Assert.True(repository.Obtain(aim, source));
        Assert.False(repository.Obtain(aim, source));
        Assert.False(new AimRepository(Repo).Obtain(aim, source));                    // nor does a restarted Controller
        Assert.Equal([aim], source.Obtained);
        Assert.Null(repository.RightOf(aim));                                          // an AIM whose Metadata offers no licence needs none

        var built = Assert.IsType<NnmAimProcessor>(Build(repository, aim));
        Assert.Equal("model-0-federated-1", built.ModelId);
        Assert.Equal(0.9f, built.MeanRiskScore, 3);
        Assert.Equal("Red", await Judge(built, new byte[2000]));                       // nothing to judge in the data: the model's colour
    }

    [Fact]
    public void EachModelIsAnAimOfItsOwn()
    {
        var store = Store();
        Assert.Equal("1AIH-NNM-V1.1-I01", store.Upload(Model("m-1", 0.1f, 1)));
        Assert.Equal("1AIH-NNM-V1.1-I02", store.Upload(Model("m-2", 0.5f, 2)));
    }

    [Fact]
    public void AModelThatIsNotTheOneTheStoreApprovedIsNotBuilt()
    {
        var store = Store();
        var aim = store.Upload(Model("m-1", 0.1f, 1));
        var repository = new AimRepository(Repo);
        repository.Obtain(aim, Source());
        File.WriteAllText(Path.Combine(Repo, "implementations", aim, aim + ".model.json"), Model("m-1", 0.99f, 1).ToJsonString());
        var refused = Assert.Throws<InvalidOperationException>(() => Build(repository, aim));
        Assert.Contains("NOT_TRUSTED", refused.Message);
        Assert.Throws<FileNotFoundException>(() => repository.Obtain("1AIH-NNM-V1.1-I09", Source()));
    }

    [Fact]
    public void AModelThatArrivesOtherThanTheStoreApprovedItIsNotKept()
    {
        var store = Store();
        var aim = store.Upload(Model("m-1", 0.1f, 1));
        // The model is where the Back End keeps it, and it is changed there after the Store approved it: what the Controller obtains is not what was approved.
        File.WriteAllText(store.ModelFile(aim), Model("m-1", 0.99f, 1).ToJsonString());
        var repository = new AimRepository(Repo);
        var refused = Assert.Throws<InvalidOperationException>(() => repository.Obtain(aim, Source()));
        Assert.Contains("NOT_TRUSTED", refused.Message);
        Assert.False(repository.Holds(aim));
        // ... and a model that is not where the L3 says is not obtained at all.
        File.Delete(store.ModelFile(aim));
        Assert.Throws<InvalidOperationException>(() => repository.Obtain(aim, Source()));
        Assert.False(repository.Holds(aim));
    }

    private sealed class Given(AimPackage package, string storeFolder = "") : IAimSource
    {
        public AimPackage? Get(string aimName) => package;
        public VerifyResult Verify(string aimName, string binaryName, string fingerprint) => new ImplementationFingerprints(storeFolder).Verify(aimName, binaryName, fingerprint);
        public string? GetLicence(string aimName) => null;
        public string? CurrentVersion(string aimName) => package.Version;
        public bool? IsRevoked(string aimLicenceId) => null;
    }

    [Fact]
    public void TheControllerAsksWhetherTheStoreHasANewerVersionAndFollowsItOrNot()
    {
        var store = Store();
        var hfe = "1AIH-HFE-V1.1-I01";
        var baseL3 = File.ReadAllText(Path.Combine(Repository.Amds, hfe + ".json"));
        Directory.CreateDirectory(Repo);
        File.WriteAllText(Path.Combine(Repo, hfe + ".json"), baseL3);                  // the L3 the Controller has of its own
        var repository = new AimRepository(Repo); var source = Source();
        Assert.Equal("1.1", repository.VersionOf(hfe));
        Assert.Null(repository.NewerVersion(hfe, source));                             // the Store has none

        var model = store.Upload(Model("m-1", 0.5f, 2));
        Assert.Equal("1.2", store.PublishFrontEnd(baseL3, model));                    // the Back End publishes a Front End that has the AIM
        Assert.Equal("1.2", repository.NewerVersion(hfe, source));
        Assert.Equal("1.1", repository.VersionOf(hfe));                                // asking changed nothing: the Controller decides
        Assert.Empty(source.Obtained);                                                 // and nothing was obtained, nothing paid for

        repository.Update(hfe, source);
        Assert.Equal("1.2", repository.VersionOf(hfe));
        Assert.Null(repository.NewerVersion(hfe, source));
        Assert.Contains(model, File.ReadAllText(Path.Combine(Repo, hfe + ".json")));   // the AIM is a Sub-AIM of it

        var again = store.Upload(Model("m-2", 0.6f, 2));
        Assert.Equal("1.3", store.PublishFrontEnd(baseL3, again));
        Assert.True(File.Exists(Path.Combine(folder, "store", "versions", hfe + ".1.2.json")));      // the Store overwrites no published version
        Assert.Equal("1.3", repository.NewerVersion(hfe, source));
    }

    [Fact]
    public void TheStoreDoesNotPublishAWrappingThatItsL2DoesNotAllow()
    {
        // A Store whose check finds the L3 wrong publishes nothing, and leaves no model behind.
        var store = new ModelAimPublisher(Path.Combine(folder, "store"), Path.Combine(folder, "back-end"), _ => ["a Port the L2 does not have"]);
        var refused = Assert.Throws<InvalidOperationException>(() => store.Upload(Model("m-1", 0.1f, 1)));
        Assert.Contains("a Port the L2 does not have", refused.Message);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(folder, "back-end")));                  // and keeps nothing it did not publish
    }
}
