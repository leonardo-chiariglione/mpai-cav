using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Cui;

namespace Mpai.Cui.Tests;

// THE CONFORMANCE OF CUI-CPP V2.1 ON SYNTHETIC COMPANIES. A corpus of invented companies (Synthetic) is given to the AIMs and to the Module; what the AIMs give is recorded (Test/Expected/cui-companies.json,
// for the reference model and for the trained one) and what must hold whatever the numbers is checked for every company: the data conform to their schemas, the probabilities are probabilities, the explanations add
// up, a longer horizon is never less likely to see a default, a worse company is never less likely, the Module gives what the AIMs give, and an AIM that cannot compute says so and gives nothing.
// A new result is accepted, as everywhere, with MPAI_UPDATE_EXPECTED=1.
[Collection("cui")]
public class ConformanceTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static string Corpus => Path.Combine(Repo.Root, "Test", "Data", "CUI", "Companies");
    private static string ModelFile => Path.Combine(Repo.Root, "AIMs", "CUI", "V2.1", "Models", "FinancialDefault-Polish5y.json");
    private static bool Updating => Environment.GetEnvironmentVariable("MPAI_UPDATE_EXPECTED") == "1";

    // What the AIMs give for a company and a model, as the AIMs give it (the Module stamps a Header and a time on it).
    private sealed record Result(JsonNode Pdd, JsonNode Org, JsonNode Psd, JsonNode? Sdp)
    {
        public double Pd => (double)Pdd["PrimaryDefaultDescriptors"]![0]!["PrimaryDefaultProbability"]!;
        public double Impact => (double)Org["OrganisationDescriptors"]![0]!["ImpactOnDefault"]!;
        public double Discontinuity => (double)Psd["PrimaryDiscontinuityDescriptors"]![0]!["PrimaryDiscontinuityProbability"]!;
        public double? Secondary => Sdp is null ? null : (double)Sdp;
    }

    private static Dictionary<string, JsonNode> Inputs(JsonObject company, double months, params string[] only)
    {
        var all = new Dictionary<string, JsonNode>
        {
            [CuiTypes.PredictionHorizon] = JsonValue.Create(months)!,
            [CuiTypes.FinancialDescriptors] = company["FinancialDescriptors"]!.DeepClone(),
            [CuiTypes.GovernanceDescriptors] = company["GovernanceDescriptors"]!.DeepClone()
        };
        if (company["ClimateRiskDescriptors"] is { } l) all[CuiTypes.ClimateRisk] = l.DeepClone();
        if (company["CyberRiskDescriptors"] is { } c) all[CuiTypes.CyberRisk] = c.DeepClone();
        if (company["SecondaryRiskStatements"] is { } s) all[CuiTypes.SecondaryStatements] = s.DeepClone();
        return only.Length == 0 ? all : all.Where(p => only.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
    }

    // THE AIMs, each given what it takes: what the Module's graph does inside, done by hand.
    private static Result Direct(JsonObject company, PdModel? model, double months = 12)
    {
        ReferenceModels.AsOf = AsOf;
        try
        {
            var inputs = Inputs(company, months);
            JsonNode One(CuiAim aim, params string[] types) => aim.Compute(inputs.Where(p => types.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value)).First().Item2;
            var pdd = One(new PrimaryDefaultPrediction("cdp", model), CuiTypes.PredictionHorizon, CuiTypes.FinancialDescriptors);
            var org = One(new OrganisationModelAssessment("oma", model), CuiTypes.PredictionHorizon, CuiTypes.GovernanceDescriptors, CuiTypes.FinancialDescriptors);
            var psd = One(new PrimaryDiscontinuityPrediction("pdp", model), CuiTypes.PredictionHorizon, CuiTypes.ClimateRisk, CuiTypes.CyberRisk, CuiTypes.GovernanceDescriptors, CuiTypes.FinancialDescriptors);
            JsonNode? sdp = null;
            if (inputs.ContainsKey(CuiTypes.SecondaryStatements))
            {
                var matrix = One(new RiskMatrixGeneration("rmg"), CuiTypes.SecondaryStatements);
                var step = new Dictionary<string, JsonNode> { [CuiTypes.PrimaryDefault] = pdd, [CuiTypes.SecondaryMatrix] = matrix, [CuiTypes.PredictionHorizon] = inputs[CuiTypes.PredictionHorizon] };
                sdp = new PredictionResultPerturbation("prp").Compute(step).First().Item2;
            }
            return new Result(pdd, org, psd, sdp);
        }
        finally { ReferenceModels.AsOf = null; }
    }

    private static IEnumerable<(string Name, JsonObject Company)> Companies() => Synthetic.All().Select(e => (e.Name, e.Company));
    private static string F(double v) => v.ToString("0.0000", CultureInfo.InvariantCulture);

    // ---- the corpus
    [Fact]
    public void TheCorpusIsWhatTheGeneratorMakes()
    {
        Directory.CreateDirectory(Corpus);
        foreach (var e in Synthetic.All())
        {
            var path = Path.Combine(Corpus, e.Name + ".json");
            var text = e.Company.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
            if (!File.Exists(path) || Updating) File.WriteAllText(path, text);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), JsonNode.Parse(File.ReadAllText(path))), $"{e.Name}.json is not the company the generator makes");
        }
        Assert.Equal(Synthetic.All().Count, Directory.GetFiles(Corpus, "*.json").Length);                  // and nothing else is in the corpus
        Assert.Equal(Synthetic.All().Count, Synthetic.All().Select(e => e.Name).Distinct().Count());
    }

    [Fact]
    public void EveryDatumOfEveryCompanyConformsToItsSchema()
    {
        foreach (var (name, company) in Companies())
        {
            foreach (var (member, schema) in new[] { ("FinancialDescriptors", "FinancialDescriptors"), ("GovernanceDescriptors", "GovernanceDescriptors"), ("ClimateRiskDescriptors", "ClimateRiskDescriptors"),
                                                    ("CyberRiskDescriptors", "CyberRiskDescriptors"), ("SecondaryRiskStatements", "SecondaryRiskStatements") })
                if (company[member] is { } datum) Assert.True(Repo.Validate(schema, datum) == "valid", $"{name}: {member}: {Repo.Validate(schema, datum)}");
        }
    }

    // ---- what the AIMs give, recorded
    [Fact]
    public void WhatTheAimsGiveForEveryCompanyIsWhatWasRecorded()
    {
        var model = PdModel.Load(ModelFile);
        var now = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, company) in Companies())
            foreach (var (label, m) in new[] { ("reference", (PdModel?)null), ("trained", model) })
            {
                var r = Direct(company, m);
                now[$"{name}|{label}|PrimaryDefault"] = F(r.Pd);
                now[$"{name}|{label}|OrganisationImpact"] = F(r.Impact);
                now[$"{name}|{label}|PrimaryDiscontinuity"] = F(r.Discontinuity);
                if (r.Secondary is { } s) now[$"{name}|{label}|SecondaryDiscontinuity"] = F(s);
                now[$"{name}|{label}|DescriptorsOfDefault"] = string.Join(",", ((JsonArray)r.Pdd["PrimaryDefaultDescriptors"]![0]!["PrimaryDefaultDescriptorImpacts"]!).Select(i => (string)i!["PrimaryDefaultDescriptorID"]!));
            }
        Recorded.Match("cui-companies.json", now);
    }

    // ---- what must hold whatever the numbers
    [Fact]
    public void TheProbabilitiesAreProbabilitiesAndTheExplanationsAddUp()
    {
        var model = PdModel.Load(ModelFile);
        foreach (var (name, company) in Companies())
            foreach (var m in new PdModel?[] { null, model })
            {
                var r = Direct(company, m);
                foreach (var (what, p) in new[] { ("default", r.Pd), ("organisation impact", r.Impact), ("discontinuity", r.Discontinuity), ("secondary", r.Secondary ?? 0) })
                    Assert.True(p is >= 0 and <= 1 && double.IsFinite(p), $"{name} ({(m is null ? "reference" : "trained")}): the {what} probability is {p}");
                Assert.True(r.Discontinuity >= r.Pd - 1e-9, $"{name}: the discontinuity {r.Discontinuity} is less than the default {r.Pd}");
                if (r.Secondary is { } s) Assert.True(s >= r.Pd - 1e-9, $"{name}: the secondary risks took the default {r.Pd} down to {s}");
                var shares = ((JsonArray)r.Pdd["PrimaryDefaultDescriptors"]![0]!["PrimaryDefaultDescriptorImpacts"]!).Sum(i => (double)i!["PrimaryDefaultDescriptorImpact"]!);
                Assert.True(shares == 0 || Math.Abs(shares - 100) < 0.2, $"{name}: the importances of the financial descriptors add up to {shares}");
                var governance = ((JsonArray)r.Org["OrganisationDescriptors"]![0]!["GovernanceDescriptorsImpact"]!).Sum(i => (double)i!["GovernanceDescriptorImpact"]!);
                Assert.True(governance == 0 || Math.Abs(governance - 100) < 0.2, $"{name}: the importances of the governance descriptors add up to {governance}");
            }
    }

    [Fact]
    public void ALongerHorizonIsNeverLessLikelyToSeeADefault()
    {
        var model = PdModel.Load(ModelFile);
        foreach (var (name, company) in Companies())
            foreach (var m in new PdModel?[] { null, model })
            {
                double[] pd = new double[] { 6, 12, 24, 60 }.Select(months => Direct(company, m, months).Pd).ToArray();
                double[] psd = new double[] { 6, 12, 24, 60 }.Select(months => Direct(company, m, months).Discontinuity).ToArray();
                for (var i = 1; i < pd.Length; i++)
                {
                    Assert.True(pd[i] >= pd[i - 1] - 1e-9, $"{name}: the default probability {string.Join(", ", pd.Select(F))} does not rise with the horizon");
                    Assert.True(psd[i] >= psd[i - 1] - 1e-9, $"{name}: the discontinuity probability {string.Join(", ", psd.Select(F))} does not rise with the horizon");
                }
            }
    }

    // A worse company is never less likely to default: the same company with the same governance, in worse and worse shape.
    [Fact]
    public void AWorseCompanyIsNeverLessLikelyToDefault()
    {
        var model = PdModel.Load(ModelFile);
        var order = new[] { "blue-chip", "average", "leveraged", "distressed" };
        foreach (var m in new PdModel?[] { null, model })
        {
            var pd = order.Select(n => Direct(Companies().Single(c => c.Name == n).Company, m).Pd).ToArray();
            for (var i = 1; i < pd.Length; i++) Assert.True(pd[i] >= pd[i - 1], $"{(m is null ? "reference" : "trained")}: {string.Join(" <= ", order.Select((n, k) => $"{n} {F(pd[k])}"))}");
        }
        // and the company in difficulty is clearly worse than the healthy one, with either model
        foreach (var m in new PdModel?[] { null, model })
            Assert.True(Direct(Companies().Single(c => c.Name == "distressed").Company, m).Pd > 2 * Direct(Companies().Single(c => c.Name == "healthy-small").Company, m).Pd);
    }

    [Fact]
    public void WhatTheCompanyDoesNotGiveIsLeftOutAndTheRisksThatAreNotGivenAreNotInTheDiscontinuity()
    {
        var partial = Direct(Companies().Single(c => c.Name == "partial-financials").Company, null);
        var kept = ((JsonArray)partial.Pdd["PrimaryDefaultDescriptors"]![0]!["PrimaryDefaultDescriptorImpacts"]!).Select(i => (string)i!["PrimaryDefaultDescriptorID"]!).ToList();
        Assert.DoesNotContain("ROE", kept);                                                                   // no equity, no net profit: nothing to compute it from
        Assert.Contains("CurrentRatio", kept);

        var plain = Direct(Companies().Single(c => c.Name == "healthy-small").Company, null);
        var risks = ((JsonArray)plain.Psd["PrimaryDiscontinuityDescriptors"]!).Select(e => (string)e!["PrimaryRiskID"]!).ToList();
        Assert.Equal(new[] { "Company" }, risks.ToArray());                                                                     // no climate, no cyber: only the company's
        var full = Direct(Companies().Single(c => c.Name == "all-risks").Company, null);
        Assert.Equal(new[] { "Company", "Climate", "Cyber" }, ((JsonArray)full.Psd["PrimaryDiscontinuityDescriptors"]!).Select(e => (string)e!["PrimaryRiskID"]!).ToArray());
        Assert.Null(plain.Secondary);
        Assert.NotNull(full.Secondary);

        // with the equity gone (the liabilities exceed the assets) there is no return on equity: a loss over a negative equity would look like a profit
        var negative = Direct(Companies().Single(c => c.Name == "negative-equity").Company, null);
        Assert.DoesNotContain("ROE", ((JsonArray)negative.Pdd["PrimaryDefaultDescriptors"]![0]!["PrimaryDefaultDescriptorImpacts"]!).Select(i => (string)i!["PrimaryDefaultDescriptorID"]!));

        // a company with no revenue has no ratio that divides by it, and the AIMs still give a probability
        var zero = Direct(Companies().Single(c => c.Name == "zero-revenue").Company, null);
        Assert.InRange(zero.Pd, 0, 1);
    }

    // ---- an AIM that cannot compute says so and gives nothing
    [Fact]
    public void AnAimThatCannotComputeFromWhatItIsGivenSaysSoAndGivesNothing()
    {
        var company = Companies().Single(c => c.Name == "average").Company;
        foreach (var bad in new JsonNode[] { JsonValue.Create(0)!, JsonValue.Create(-6)!, JsonValue.Create("soon")! })
        {
            var inputs = Inputs(company, 12);
            inputs[CuiTypes.PredictionHorizon] = bad;
            var error = Assert.Throws<CuiDataException>(() => new PrimaryDefaultPrediction("cdp").Compute(inputs).ToList());
            Assert.Contains("Prediction Horizon", error.Message);
        }
        var nothing = Inputs(company, 12);
        nothing[CuiTypes.FinancialDescriptors] = JsonNode.Parse("""{ "Header": "CUI-FND-V2.1", "FinancialDescriptors": { "AQ": { "input": [ { "code": "NOPE", "value": "1" } ] } } }""")!;
        Assert.Contains("none of the codes", Assert.Throws<CuiDataException>(() => new PrimaryDefaultPrediction("cdp").Compute(nothing).ToList()).Message);
    }

    // ---- the Module
    private static Result? RunModule(string work, string name, string? model, JsonObject company, params string[] only)
    {
        const string module = "1CUI-CPP-V2.1-I01";
        ReferenceModels.AsOf = AsOf;
        Environment.SetEnvironmentVariable("MPAI_ACCESS", Path.Combine(work, "access-" + name));
        using var api = new ControllerApi(Repo.AmdsWithModel(Path.Combine(work, "amds-" + name), model), Path.Combine(Repo.Root, "AIMs", "aim-settings.json"), new CuiProvider());
        Assert.Equal(AifError.OK, api.StartFlow(module));
        try
        {
            foreach (var (type, json) in Inputs(company, 12, only))
                Assert.Equal(AifError.OK, api.InputWrite(module, type, 1, json.ToJsonString(), 2000));
            // An AIM computes again when an optional Input comes after the first computation: the Module gives each result in turn, and the last is the one that has all the company gave.
            JsonNode? Read(string type, int wait = 5000)
            {
                JsonNode? last = null;
                for (var next = wait; api.OutputRead(module, type, 1, next) is { Ok: true, Json: { } j }; next = 700) last = JsonNode.Parse(j);
                return last;
            }
            var pdd = Read(CuiTypes.PrimaryDefault);
            var org = Read(CuiTypes.Organisation, only.Length == 0 ? 5000 : 1500);
            var psd = Read(CuiTypes.PrimaryDiscontinuity, only.Length == 0 ? 5000 : 1500);
            var sdp = company["SecondaryRiskStatements"] is null ? null : Read(CuiTypes.SecondaryProbability);
            return pdd is null ? null : new Result(pdd, org!, psd!, sdp);
        }
        finally { api.StopFlow(module); Environment.SetEnvironmentVariable("MPAI_ACCESS", null); ReferenceModels.AsOf = null; }
    }

    [Fact]
    public void TheModuleGivesWhatTheAimsGiveAndWhatItGivesConformsToItsSchemas()
    {
        var work = Path.Combine(Path.GetTempPath(), "cui-conformance-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            var model = PdModel.Load(ModelFile);
            var copy = Path.Combine(work, "model.json");
            File.Copy(ModelFile, copy);
            foreach (var (label, file, m) in new[] { ("reference", (string?)null, (PdModel?)null), ("trained", copy, model) })
                foreach (var (name, company) in Companies())
                {
                    var expected = Direct(company, m);
                    var got = RunModule(work, label, file, company) ?? throw new Xunit.Sdk.XunitException($"{name} ({label}): the Module gave nothing");
                    Assert.Equal(expected.Pd, got.Pd, 4);
                    Assert.Equal(expected.Impact, got.Impact, 4);
                    Assert.True(Math.Abs(expected.Discontinuity - got.Discontinuity) < 1e-4, $"{name} ({label}): direct {expected.Psd.ToJsonString()} against the Module {got.Psd.ToJsonString()}");
                    Assert.Equal(expected.Secondary, got.Secondary);
                    foreach (var (schema, datum) in new[] { ("PrimaryDefaultDescriptors", got.Pdd), ("OrganisationDescriptors", got.Org), ("PrimaryDiscontinuityDescriptors", got.Psd) })
                        Assert.True(Repo.Validate(schema, datum) == "valid", $"{name} ({label}): {schema}: {Repo.Validate(schema, datum)}");
                }
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    [Fact]
    public void AnAimComputesWhenItsRequiredInputsHaveComeAndNotBefore()
    {
        var work = Path.Combine(Path.GetTempPath(), "cui-conformance-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            var company = Companies().Single(c => c.Name == "average").Company;
            // the Horizon and the Financial Descriptors only: the Primary Default Prediction has what it requires; the Organisation Model Assessment still waits for the Governance Descriptors
            var got = RunModule(work, "partial", null, company, CuiTypes.PredictionHorizon, CuiTypes.FinancialDescriptors);
            Assert.NotNull(got);
            Assert.InRange(got!.Pd, 0, 1);
            Assert.Null(got.Org);
            // without the Financial Descriptors nothing of the default is given
            Assert.Null(RunModule(work, "none", null, company, CuiTypes.PredictionHorizon));
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }
}

// WHAT WAS RECORDED (Test/Expected/<file>), compared with what the code gives now, in both directions; MPAI_UPDATE_EXPECTED=1 accepts a difference.
internal static class Recorded
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static void Match(string file, IReadOnlyDictionary<string, string> actual)
    {
        var path = Path.Combine(Repo.Root, "Test", "Expected", file);
        var sorted = new SortedDictionary<string, string>(actual.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal);
        if (Environment.GetEnvironmentVariable("MPAI_UPDATE_EXPECTED") == "1" || !File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(sorted, Json) + Environment.NewLine);
            if (Environment.GetEnvironmentVariable("MPAI_UPDATE_EXPECTED") != "1") Assert.Fail($"{file} did not exist and has been written from what the code gives now. Review it, commit it, and run again.");
            return;
        }
        var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [];
        var differences = new List<string>();
        foreach (var key in expected.Keys.Union(sorted.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            expected.TryGetValue(key, out var was);
            sorted.TryGetValue(key, out var now);
            if (was != now) differences.Add($"  {key}\n    expected: {was ?? "(absent)"}\n    now:      {now ?? "(absent)"}");
        }
        if (differences.Count > 0) Assert.Fail($"{file}: {differences.Count} difference(s) from the recorded state (MPAI_UPDATE_EXPECTED=1 accepts them):\n" + string.Join("\n", differences));
    }
}
