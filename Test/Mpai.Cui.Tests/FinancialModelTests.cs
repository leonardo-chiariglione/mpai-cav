using System.Text.Json.Nodes;

using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Cui;

namespace Mpai.Cui.Tests;

// THE TRAINED FINANCIAL MODEL (AIMs/CUI/V2.1/Models/FinancialDefault-Polish5y.json), a demonstration model trained on the Polish Companies Bankruptcy dataset (CC BY 4.0) and delivered as Access data: the
// AIMs of Primary Default, Organisation Model and Primary Discontinuity Prediction declare it in their AccessSources (optional, pinned by SHA-256, with its Terms); the Controller obtains it and gives the AIM
// its path as the setting FinancialModel; without it the AIM uses its built-in reference model.
[Collection("cui")]
public class FinancialModelTests
{
    private static string ModelFile => Path.Combine(Repo.Root, "AIMs", "CUI", "V2.1", "Models", "FinancialDefault-Polish5y.json");

    private static IReadOnlyDictionary<string, JsonNode> Inputs(bool healthy) => new Dictionary<string, JsonNode>
    {
        [CuiTypes.PredictionHorizon] = JsonValue.Create(12)!,
        [CuiTypes.FinancialDescriptors] = Companies.Financial(healthy)
    };

    private static (double Probability, List<string> Descriptors, double Impacts) Primary(PrimaryDefaultPrediction aim, bool healthy)
    {
        var (_, json) = aim.Compute(Inputs(healthy)).Single();
        var d = json["PrimaryDefaultDescriptors"]![0]!;
        var impacts = (d["PrimaryDefaultDescriptorImpacts"] as JsonArray)!;
        return ((double)d["PrimaryDefaultProbability"]!, impacts.Select(i => (string)i!["PrimaryDefaultDescriptorID"]!).ToList(), impacts.Sum(i => (double)i!["PrimaryDefaultDescriptorImpact"]!));
    }

    [Fact]
    public void TheModelFileIsOneAndSaysWhatItWasTrainedOn()
    {
        var model = PdModel.Load(ModelFile);
        Assert.NotEmpty(model.Features);
        Assert.All(model.Features, f => Assert.Contains(f.Name, ReferenceModels.RatioNames));
        // more of what brings risk down never brings the risk up
        var direction = ReferenceModels.RatioNames.Zip(new[] { -1, -1, -1, -1, -1, -1, 1, 1, -1, 1 }).ToDictionary(p => p.First, p => p.Second);
        Assert.All(model.Features, f => Assert.True(f.Weight * direction[f.Name] >= 0, $"{f.Name} has a weight the wrong way: {f.Weight}"));
        var training = model.Training!;
        Assert.Equal("CC-BY-4.0", (string?)training["Licence"]);
        Assert.Contains("Tomczak", (string?)training["Attribution"]);                      // CC BY: the model carries the attribution
        Assert.True((double)training["HeldOut"]!["TrainedAuc"]! > 0.7);
        Assert.Contains("not for real companies", (string?)training["Caution"]);
    }

    [Fact]
    public void AFileThatIsNotAModelIsRefusedWithTheReason()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cui-model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string Write(string name, string text) { var p = Path.Combine(dir, name); File.WriteAllText(p, text); return p; }
            Assert.Contains("not a financial model", Assert.Throws<CuiDataException>(() => PdModel.Load(Write("a.json", """{"Format":"other"}"""))).Message);
            Assert.Contains("does not compute", Assert.Throws<CuiDataException>(() => PdModel.Load(Write("b.json", """{"Format":"MPAI-CUI-LOGISTIC-1","Intercept":-3,"Features":[{"Name":"Luck","Mean":0,"Scale":1,"Weight":1}]}"""))).Message);
            Assert.Contains("unusable", Assert.Throws<CuiDataException>(() => PdModel.Load(Write("c.json", """{"Format":"MPAI-CUI-LOGISTIC-1","Intercept":-3,"Features":[{"Name":"ROA","Mean":0,"Scale":0,"Weight":1}]}"""))).Message);
            Assert.Contains("cannot be read", Assert.Throws<CuiDataException>(() => PdModel.Load(Write("d.json", "{ not json"))).Message);
            Assert.Null(PdModel.FromSettings(new Dictionary<string, string>()));
            Assert.Null(PdModel.FromSettings(new Dictionary<string, string> { [PdModel.Setting] = Path.Combine(dir, "missing.json") }));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void TheTrainedModelGivesAnotherProbabilityAndExplainsItWithTheRatiosItKept()
    {
        var model = PdModel.Load(ModelFile);
        var reference = Primary(new PrimaryDefaultPrediction("ref"), false);
        var trained = Primary(new PrimaryDefaultPrediction("trained", fixedModel: model), false);
        var healthy = Primary(new PrimaryDefaultPrediction("trained", fixedModel: model), true);

        Assert.NotEqual(reference.Probability, trained.Probability);
        Assert.True(trained.Probability > healthy.Probability, $"a company in difficulty {trained.Probability:F3} against a healthy one {healthy.Probability:F3}");
        Assert.All(trained.Descriptors, d => Assert.Contains(d, model.Features.Select(f => f.Name)));
        Assert.True(Math.Abs(trained.Impacts - 100) < 0.2, $"the importances add up to {trained.Impacts}");
        Assert.True(trained.Descriptors.Count <= model.Features.Count);
    }

    // THE MODULE: the Controller obtains the model as Access data - from where the Metadata says, checked against its SHA-256 - and the AIM reads it. A model that is not what the Metadata pins is refused, and
    // the AIM then goes on with its own.
    [Fact]
    public void TheModuleReadsTheModelItsControllerObtainedAsAccessData()
    {
        const string module = "1CUI-CPP-V2.1-I01";
        ReferenceModels.AsOf = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var work = Path.Combine(Path.GetTempPath(), "cui-access-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        double Run(string model)
        {
            var name = Path.GetFileNameWithoutExtension(model);
            Environment.SetEnvironmentVariable("MPAI_ACCESS", Path.Combine(work, "access-" + name));
            using var api = new ControllerApi(Repo.AmdsWithModel(Path.Combine(work, "amds-" + name), model), Path.Combine(Repo.Root, "AIMs", "aim-settings.json"), new CuiProvider());
            Assert.Equal(AifError.OK, api.StartFlow(module));
            try
            {
                void Write(string type, JsonNode json) => Assert.Equal(AifError.OK, api.InputWrite(module, type, 1, json.ToJsonString(), 2000));
                Write(CuiTypes.PredictionHorizon, JsonValue.Create(12)!);
                Write(CuiTypes.FinancialDescriptors, Companies.Financial(false));
                Write(CuiTypes.GovernanceDescriptors, Companies.Governance(false));
                var read = api.OutputRead(module, CuiTypes.PrimaryDefault, 1, 5000);
                Assert.True(read is { Ok: true, Json: { } }, "the Primary Default Descriptors");
                return (double)JsonNode.Parse(read.Json!)!["PrimaryDefaultDescriptors"]![0]!["PrimaryDefaultProbability"]!;
            }
            finally { api.StopFlow(module); Environment.SetEnvironmentVariable("MPAI_ACCESS", null); }
        }
        try
        {
            var copy = Path.Combine(work, "model-ok.json");
            File.Copy(ModelFile, copy);
            var damaged = Path.Combine(work, "model-damaged.json");
            File.WriteAllText(damaged, File.ReadAllText(ModelFile).Replace("\"Intercept\": -", "\"Intercept\": -9"));      // not the bytes the Metadata pins

            var expected = Primary(new PrimaryDefaultPrediction("direct", fixedModel: PdModel.Load(ModelFile)), false).Probability;
            var reference = Primary(new PrimaryDefaultPrediction("ref"), false).Probability;
            Assert.Equal(expected, Run(copy), 4);                       // the AIM read the model that Access gave it
            Assert.Equal(reference, Run(damaged), 4);                   // refused as not what the Metadata pins: the AIM goes on with its reference model
        }
        finally { ReferenceModels.AsOf = null; try { Directory.Delete(work, true); } catch { } }
    }
}
