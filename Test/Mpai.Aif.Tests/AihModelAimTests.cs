using System.Text.Json;
using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.Store;
using Mpai.Aih;
using Mpai.Aih.Nnm;

namespace Mpai.Aif.Tests;

// THE NEURAL NETWORK MODEL AS AN AIM (AIH-NNM-V1.1), and the Controller's repository of AIMs: the Back End wraps the model it got from federated learning in an AIM and
// uploads it to the MPAI Store, which publishes it only if its L3 conforms to its L2; a Controller obtains it from the Store, keeps it - it has the right to use it, and
// does not obtain it again - and builds it with its model, which is data of Access (A7, 2026-11-13): obtained from where the L3 says and refused if its SHA-256 is not the one
// the L3 gives. The Store has a newer version of the Front End when the Back End has published one.
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

    // The AIM as the Controller builds it: its L3 from the repository, its model from Access (obtained as the L3 says, checked against its SHA-256, given as ModelPath).
    private IAimProcessor Build(AimRepository repository, string aim) => BuildWithAccess(repository.Folder, aim, Path.Combine(folder, "access"));

    internal static IAimProcessor BuildWithAccess(string amdFolder, string aim, string accessFolder)
    {
        var amds = new AmdStore(amdFolder); amds.Scan();
        using var l3 = JsonDocument.Parse(File.ReadAllText(Directory.EnumerateFiles(amdFolder, aim + ".json", SearchOption.AllDirectories).First()));
        var entries = AccessEntry.ReadFrom(l3.RootElement);
        Assert.Equal("", AccessEntry.Check(entries));
        var store = new AccessStore(accessFolder); var fetcher = new AccessFetcher(store);
        foreach (var e in entries)
            if (fetcher.Ensure(e, aim, out var why) != AccessOutcome.OK) throw new InvalidOperationException($"NOT_OBTAINED: '{e.Name}' of {aim}: {why}");
        var settings = AccessEntry.Overlay(new Dictionary<string, string>(), entries, new BoundAccess(store.As(new AIF.SharedStorage.StorageHolder("M", aim)), entries, fetcher, aim), aim);
        return new AihProvider(amds).Create(aim, settings, null);
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
        // The AIM gives the AIH Data resulting from the processing: a new AIH Data naming the one processed, with what the model found (2026-11-13).
        var result = JsonNode.Parse(reply.Payload)!;
        Assert.Equal(data["AIHDataID"]!.ToString(), result["AIHData"]!["ParentAIHDataID"]!.ToString());
        Assert.NotEqual(data["AIHDataID"]!.ToString(), result["AIHDataID"]!.ToString());
        return result["AIHData"]!["ARAData"]!["ARAData"]!["Risk"]!.ToString();
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

    // THE MODEL IS DATA OF ACCESS: the L3 names it by its URI and pins it with its SHA-256 - not as an Implementation - with Terms (only for the processing of AIH Data).
    [Fact]
    public void TheModelIsInTheAccessOfTheAimPinnedByItsSha256()
    {
        var store = Store();
        var aim = store.Upload(Model("m-1", 0.1f, 1));
        using var l3 = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "store", aim + ".json")));
        var entry = Assert.Single(AccessEntry.ReadFrom(l3.RootElement));
        Assert.Equal("ModelPath", entry.Setting);
        Assert.Equal(new Uri(store.ModelFile(aim)).AbsoluteUri, entry.Uri);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(store.ModelFile(aim)))), entry.Sha256, ignoreCase: true);
        Assert.Equal("Only for the processing of AIH Data (AIH-HSP V1.1)", entry.Terms!.Licence);
        Assert.Single(l3.RootElement.GetProperty("Implementations").EnumerateArray());                   // the code only
        Assert.Throws<FileNotFoundException>(() => new AimRepository(Repo).Obtain("1AIH-NNM-V1.1-I09", Source()));
    }

    [Fact]
    public void AModelThatIsNotTheOneTheL3PinsIsNotGivenToTheAim()
    {
        var store = Store();
        var aim = store.Upload(Model("m-1", 0.1f, 1));
        var repository = new AimRepository(Repo);
        Assert.True(repository.Obtain(aim, Source()));                                                    // the Metadata is kept
        // The model is where the Back End keeps it, and it is changed there after the Store published the L3: its SHA-256 is not the one the L3 gives - it is refused.
        File.WriteAllText(store.ModelFile(aim), Model("m-1", 0.99f, 1).ToJsonString());
        Assert.Contains("NOT_OBTAINED", Assert.Throws<InvalidOperationException>(() => Build(repository, aim)).Message);
        // ... and a model that is not where the L3 says is not obtained at all.
        File.Delete(store.ModelFile(aim));
        Assert.Contains("NOT_OBTAINED", Assert.Throws<InvalidOperationException>(() => Build(repository, aim)).Message);
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
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(folder, "back-end"), "*.model.json"));  // and keeps no model it did not publish (its code stays: it is the code of every NNM AIM it publishes)
    }
}
