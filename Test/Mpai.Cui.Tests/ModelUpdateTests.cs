using System.Security.Cryptography;
using System.Text.Json.Nodes;

using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Cui;

namespace Mpai.Cui.Tests;

// MPAI_AIFM_Model_Update and MPAI_AIFM_Model_Drift (Basic API 4.12) for the model of the Prediction AIMs. The model is Access data; its publisher puts a new one at the same URI and the Metadata of the AIM
// states its new SHA-256. The AIM asks the Controller (Model_Update); the Controller reads the Metadata again, obtains what it pins - checked against that SHA-256 before it is kept - and the AIM, which reads
// Access (Access_Version, Access_Path), takes the new model in between two computations. What is not the model its Metadata pins is refused (NOT_TRUSTED) and the AIM goes on with the one it has.
[Collection("cui")]
public class ModelUpdateTests
{
    private const string Module = "1CUI-CPP-V2.1-I01", Cdp = "1CUI-CDP-V2.1-I01";
    private static string ModelFile => Path.Combine(Repo.Root, "AIMs", "CUI", "V2.1", "Models", "FinancialDefault-Polish5y.json");

    private static string Sha(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

    // Metadata whose model is at `uriFile`; the three AIMs that read it.
    private static void Pin(string amds, string oldSha, string newSha)
    {
        foreach (var file in Directory.EnumerateFiles(amds, "1CUI-*.json"))
            File.WriteAllText(file, File.ReadAllText(file).Replace(oldSha, newSha));
    }

    // A model made from the first one by moving its intercept: a model the AIMs give other probabilities with.
    private static void Publish(string at, double shift)
    {
        var model = JsonNode.Parse(File.ReadAllText(ModelFile))!.AsObject();
        model["Intercept"] = (double)model["Intercept"]! + shift;
        File.WriteAllText(at, model.ToJsonString());
    }

    private static JsonNode? Latest(ControllerApi api, int id, string type, int first = 5000)
    {
        JsonNode? last = null;
        for (var wait = first; api.OutputRead(id, type, 1, wait) is { Ok: true, Json: { } j }; wait = 700) last = JsonNode.Parse(j);
        return last;
    }

    private static double Pd(JsonNode? pdd) => (double)pdd!["PrimaryDefaultDescriptors"]![0]!["PrimaryDefaultProbability"]!;

    private static void Give(ControllerApi api, int id, JsonObject company)
    {
        foreach (var (type, json) in new (string, JsonNode)[] { (CuiTypes.PredictionHorizon, JsonValue.Create(12)!), (CuiTypes.FinancialDescriptors, company["FinancialDescriptors"]!.DeepClone()),
                                                               (CuiTypes.GovernanceDescriptors, company["GovernanceDescriptors"]!.DeepClone()) })
            Assert.Equal(AifError.OK, api.InputWrite(id, type, 1, json.ToJsonString(), 2000));
    }

    private static JsonObject Company(string name) => Synthetic.All().Single(e => e.Name == name).Company;

    [Fact]
    public void ANewModelAtTheSameUriIsTakenAfterItsMetadataPinsIt()
    {
        var work = Path.Combine(Path.GetTempPath(), "cui-update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var live = Path.Combine(work, "model.json");
        File.Copy(ModelFile, live);
        var v1 = Sha(live);
        ModelSource.Interval = TimeSpan.Zero;
        Environment.SetEnvironmentVariable("MPAI_ACCESS", Path.Combine(work, "access"));
        var amds = Repo.AmdsWithModel(work, live);
        using var api = new ControllerApi(amds, Path.Combine(Repo.Root, "AIMs", "aim-settings.json"), new CuiProvider());
        Assert.Equal(AifError.OK, api.StartFlow(Module, out var id));
        try
        {
            var company = Company("distressed");
            Give(api, id, company);
            var first = Pd(Latest(api, id, CuiTypes.PrimaryDefault));
            Assert.Equal(Pd(new PrimaryDefaultPrediction("direct", fixedModel: PdModel.Load(live)).Compute(new Dictionary<string, JsonNode>
                { [CuiTypes.PredictionHorizon] = JsonValue.Create(12)!, [CuiTypes.FinancialDescriptors] = company["FinancialDescriptors"]! }).Single().Item2), first, 4);

            // Nothing is new: the Metadata pins what Access holds.
            Assert.Equal(AifError.OK, api.ModelUpdate(id, Cdp, new Uri(live).AbsoluteUri));

            // The publisher puts a new model at the same address, and the Metadata (a new version of it) says what its SHA-256 is.
            Publish(live, -1.0);
            var v2 = Sha(live);
            Pin(amds, v1, v2);
            Assert.Equal(AifError.OK, api.ModelUpdate(id, Cdp, new Uri(live).AbsoluteUri));

            Give(api, id, company);                                                   // the AIM takes the new model between two computations
            var second = Pd(Latest(api, id, CuiTypes.PrimaryDefault));
            Assert.True(second < first, $"the new model lowers the intercept: {first:F4} then {second:F4}");
            Assert.Equal(Pd(new PrimaryDefaultPrediction("direct", fixedModel: PdModel.Load(live)).Compute(new Dictionary<string, JsonNode>
                { [CuiTypes.PredictionHorizon] = JsonValue.Create(12)!, [CuiTypes.FinancialDescriptors] = company["FinancialDescriptors"]! }).Single().Item2), second, 4);

            // What is at the address and is not what the Metadata pins is refused, and the AIM goes on with the model it has.
            Publish(live, +3.0);
            var bogus = new string('A', 64);
            Pin(amds, v2, bogus);                                                  // the Metadata pins a SHA-256 that is not that of what is at the address
            Assert.Equal(AifError.NotTrusted, api.ModelUpdate(id, Cdp, new Uri(live).AbsoluteUri));
            Give(api, id, company);
            Assert.Equal(second, Pd(Latest(api, id, CuiTypes.PrimaryDefault)), 4);

            // A model the Source held before is not brought back (Access only moves forward).
            File.Copy(ModelFile, live, true);
            Pin(amds, bogus, v1);
            Assert.Equal(AifError.NotTrusted, api.ModelUpdate(id, Cdp, new Uri(live).AbsoluteUri));
        }
        finally { api.StopFlow(id); ModelSource.Interval = TimeSpan.FromMinutes(1); Environment.SetEnvironmentVariable("MPAI_ACCESS", null); try { Directory.Delete(work, true); } catch { } }
    }

    [Fact]
    public void AModelThatTheMetadataOfTheAimDoesNotNameIsNotUpdated()
    {
        var work = Path.Combine(Path.GetTempPath(), "cui-update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var live = Path.Combine(work, "model.json");
        File.Copy(ModelFile, live);
        Environment.SetEnvironmentVariable("MPAI_ACCESS", Path.Combine(work, "access"));
        using var api = new ControllerApi(Repo.AmdsWithModel(work, live), Path.Combine(Repo.Root, "AIMs", "aim-settings.json"), new CuiProvider());
        var id = -1;                                                                                    // no MODULE_ID returned yet
        try
        {
            Assert.Equal(AifError.NotStarted, api.ModelUpdate(id, Cdp, new Uri(live).AbsoluteUri));                     // no Module is running
            Assert.Equal(AifError.OK, api.StartFlow(Module, out id));
            Assert.Equal(AifError.NotAuthorised, api.ModelUpdate(id, Cdp, "https://example.org/another-model.json"));      // the Metadata names no model there
            Assert.Equal(AifError.NotFound, api.ModelUpdate(id, "1CUI-NONE-V2.1-I01", new Uri(live).AbsoluteUri));        // no such AIM
        }
        finally { api.StopFlow(id); Environment.SetEnvironmentVariable("MPAI_ACCESS", null); try { Directory.Delete(work, true); } catch { } }
    }

    [Fact]
    public void TheDriftIsHowFarTheInputsHaveBeenFromThoseTheModelWasTrainedOn()
    {
        var work = Path.Combine(Path.GetTempPath(), "cui-update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var live = Path.Combine(work, "model.json");
        File.Copy(ModelFile, live);
        Environment.SetEnvironmentVariable("MPAI_ACCESS", Path.Combine(work, "access"));
        using var api = new ControllerApi(Repo.AmdsWithModel(work, live), Path.Combine(Repo.Root, "AIMs", "aim-settings.json"), new CuiProvider());
        Assert.Equal(AifError.OK, api.StartFlow(Module, out var id));
        try
        {
            Assert.Null(api.ModelDrift(id, Cdp));                                          // nothing given yet: nothing to say
            Give(api, id, Company("healthy-small"));
            Latest(api, id, CuiTypes.PrimaryDefault);
            var usual = api.ModelDrift(id, Cdp);
            Assert.NotNull(usual);
            Assert.InRange(usual!.Value, 0, 1);

            for (var i = 0; i < 5; i++) { Give(api, id, Company("absurd-figures")); Latest(api, id, CuiTypes.PrimaryDefault, 3000); }
            var odd = api.ModelDrift(id, Cdp);
            Assert.True(odd > usual, $"figures far from the training ones drift the model: {usual:F2} then {odd:F2}");
            Assert.Null(api.ModelDrift(id, "1CUI-FNA-V2.1-I01"));                          // an AIM that keeps no measure
        }
        finally { api.StopFlow(id); Environment.SetEnvironmentVariable("MPAI_ACCESS", null); try { Directory.Delete(work, true); } catch { } }
    }
}
