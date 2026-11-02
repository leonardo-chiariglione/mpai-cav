using System.Text.Json;
using System.Text.Json.Nodes;

using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Cui;

namespace Mpai.Cui.Tests;

internal static class Repo
{
    public static string Root { get; } = Find();
    private static string Find()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, "AIMs", "AMDs")) && Directory.Exists(Path.Combine(dir, "schemas"))) return dir;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new InvalidOperationException("Repository root not found");
    }
    public static string Amds => Path.Combine(Root, "AIMs", "AMDs");
    public static string Schemas => Path.Combine(Root, "schemas");

    // The datum validates against the schema of its Data Type in CUI1/V2.1.
    public static string Validate(string file, JsonNode datum)
    {
        var schemas = AIF.Metadata.PublishedSchemas.At(Schemas);
        var schema = schemas[Path.GetFullPath(Path.Combine(Schemas, "CUI1", "V2.1", "data", file + ".json"))];
        using var doc = JsonDocument.Parse(datum.ToJsonString());
        lock (AIF.Metadata.PublishedSchemas.Lock)
        {
            var result = schema.Evaluate(doc.RootElement, new Json.Schema.EvaluationOptions { OutputFormat = Json.Schema.OutputFormat.List });
            return result.IsValid ? "valid" : "invalid: " + string.Join("; ", result.Details.Where(d => !d.IsValid && d.Errors is not null).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation} {e.Key}: {e.Value}")).Take(5));
        }
    }
}

// The data of a small manufacturing company, as CUI-CPP gives it: records of a questionnaire whose items have a code and a value.
internal static class Companies
{
    private static JsonObject Item(string code, string value) => new() { ["code"] = code, ["type"] = "text", ["editable"] = true, ["required"] = null, ["value"] = value, ["saves"] = new JsonArray() };
    private static JsonObject Time(string id) => new() { ["Header"] = "OSD-TIM-V1.5", ["TimeID"] = id, ["Data"] = "2026-01-10T10:00:00Z" };

    private static JsonObject Record(IEnumerable<(string Code, string Value)> items) => new()
    {
        ["id"] = 1, ["module_id"] = "CUI", ["poweruser_id"] = 1, ["survey_company_id"] = 1, ["period_start"] = "2025-01-01", ["period_end"] = "2025-12-31",
        ["input"] = new JsonArray(items.Select(i => (JsonNode)Item(i.Code, i.Value)).ToArray()),
        ["simulation"] = false, ["consolidated"] = true, ["created_at"] = "2026-01-10T10:00:00Z", ["updated_at"] = "2026-01-10T10:00:00Z"
    };

    public static JsonObject Financial(bool healthy, string header = "CUI-FND-V2.1") => new()
    {
        ["Header"] = header, ["MInstanceID"] = "M1", ["FinancialDescriptorsID"] = healthy ? "A" : "B",
        ["oType"] = 1,   // required by the schema of the Financial Descriptors and Statements
        ["FinancialDescriptors"] = new JsonObject { ["AQ"] = new JsonArray(Record(healthy
            ? [("CEA1", "10000000"), ("CEBDIFFVAL", "1.200.000"), ("CEB5abc", "400000"), ("CEC3", "-150000"), ("CEUTILE", "800000"), ("SPACTOT", "4500000"), ("SPACITOT", "1200000"),
               ("SPPDTOTENTRO", "2500000"), ("SPPDTOTOLTRE", "1500000"), ("SPPDTOT", "4000000"), ("SPPATOT", "5000000"), ("SPPTOT", "9500000")]
            : [("CEA1", "5000000"), ("CEBDIFFVAL", "-300000"), ("CEB5abc", "350000"), ("CEC3", "280000"), ("CEUTILE", "-650000"), ("SPACTOT", "1500000"), ("SPACITOT", "900000"),
               ("SPPDTOTENTRO", "2800000"), ("SPPDTOTOLTRE", "1800000"), ("SPPDTOT", "4600000"), ("SPPATOT", "400000"), ("SPPTOT", "4900000")])) }
    };

    public static JsonObject Governance(bool healthy) => new()
    {
        ["Header"] = "CUI-GVS-V2.1", ["MInstanceID"] = "M1", ["GovernanceDescriptorsID"] = healthy ? "A" : "B",
        ["GovernanceDescriptors"] = new JsonObject
        {
            ["EFQ"] = new JsonObject
            {
                ["id"] = 1, ["module_id"] = "CUI", ["poweruser_id"] = 1, ["survey_company_id"] = 1, ["period_start"] = "2025-01-01", ["period_end"] = "2025-12-31",
                ["input"] = new JsonArray((healthy
                    ? new[] { ("SINDACO", "1"), ("SOCREV", "Auditors Srl"), ("REVOBBLIGATORIA", "1"), ("CAPMINIMILEGGE", "0"), ("QUOTE", "40"), ("QUOTE", "35"), ("QUOTE", "25"), ("ANAGRDATAINIZIO", "01/03/1998") }
                    : new[] { ("SINDACO", "0"), ("REVOBBLIGATORIA", "0"), ("CAPMINIMILEGGE", "1"), ("QUOTE", "90"), ("QUOTE", "10"), ("ANAGRDATAINIZIO", "01/06/2023") })
                    .Select(i => (JsonNode)new JsonObject { ["type"] = "text", ["code"] = i.Item1, ["editable"] = true, ["value"] = i.Item2, ["saves"] = new JsonArray() }).ToArray()),
                ["output"] = null, ["simulation"] = false, ["consolidated"] = true, ["last_processing_datetime"] = null, ["created_at"] = "2026-01-10T10:00:00Z", ["updated_at"] = "2026-01-10T10:00:00Z"
            }
        },
        ["oType"] = 1
    };

    private static JsonObject Observation(string value, JsonNode v) => new() { ["Time"] = Time("t"), ["Source"] = "weather service", ["Type"] = "number", [value] = v };

    public static JsonObject Climate() => new()
    {
        ["Header"] = "CUI-LRD-V2.1", ["MInstanceID"] = "M1", ["ClimateRiskDescriptorsID"] = Time("LRD1"), ["ClimateRiskDescriptorsTime"] = Time("LRD1-T"),
        ["ClimateRiskDescriptorsData"] = new JsonObject
        {
            ["AverageMaximumTemperature"] = Observation("AverageMaximumTemperature4Qs", 38), ["TotalMonthlyPrecipitation"] = Observation("TotalMonthlyPrecipitationMm", 260),
            ["AverageMaximumHumidity4Qs"] = Observation("AverageMaximumHumidity", 92)
        }
    };

    public static JsonObject Cyber() => new()
    {
        ["Header"] = "CUI-CRD-V2.1", ["MInstanceID"] = "M1", ["CyberRiskDescriptorsID"] = "CRD1", ["CyberRiskDescriptorsTime"] = Time("CRD1-T"),
        ["CyberRisk"] = new JsonObject
        {
            ["AttackerSourceIP"] = new JsonObject { ["AttackDetectionTime"] = Time("a"), ["VectorProviderOrSource"] = "firewall", ["Type"] = "IP address", ["AttackSourceIPAddress"] = "203.0.113.9" },
            ["SourcePort"] = new JsonObject { ["AttackDetectionTime"] = Time("b"), ["VectorProviderOrSource"] = "firewall", ["Type"] = 1, ["AttackSourcePortNumber"] = 445 }
        }
    };

    // The Cyber Risk Descriptors as the published schema accepts them: its patterns for the Type and the IP address of AttackerSourceIP (and DoSDestinationIP) are written
    // "ddd,.ddd,.ddd" - no real value matches - so the test leaves those two out (see MKG/pending-changes.md, 2026-11-02).
    public static JsonObject CyberConforming() { var c = Cyber(); ((JsonObject)c["CyberRisk"]!).Remove("AttackerSourceIP"); return c; }

    public static JsonObject SecondaryRisks() => JsonNode.Parse("""
    { "Header": "CUI-SRS-V2.1", "MInstanceID": "M1",
      "SecondaryRiskStatementsData": {
        "Level-1Risk1": { "Level-2RiskID#1": { "Impact": "High", "Probability": "Medium", "Severity": "High", "Retention": 20 },
                          "Level-2RiskID#2": { "Impact": "Medium", "Probability": "Low", "Severity": "Medium", "Retention": 50 } },
        "Level-1Risk2": { "Level-2RiskID#1": { "Impact": "High", "Probability": "Low", "Severity": "High", "Retention": 0 } } } }
    """)!.AsObject();

    public static Dictionary<string, JsonNode> In(params (string Type, JsonNode Json)[] items) => items.ToDictionary(i => i.Type, i => i.Json);
}

public class AimTests
{
    private static JsonNode Only(IEnumerable<(string Type, JsonNode Json)> outputs, string type) => outputs.Single(o => o.Type == type).Json;
    private static readonly DateTimeOffset AsOf = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static JsonNode Months(int m) => JsonValue.Create(m)!;
    private static double Default(bool healthy, int months = 12)
    {
        var p = Only(new PrimaryDefaultPrediction("CDP").Compute(Companies.In((CuiTypes.PredictionHorizon, Months(months)), (CuiTypes.FinancialDescriptors, Companies.Financial(healthy)))), CuiTypes.PrimaryDefault);
        return (double)p["PrimaryDefaultDescriptors"]![0]!["PrimaryDefaultProbability"]!;
    }

    [Fact]
    public void TheRatiosAreReadFromTheCodesOfTheQuestionnaire()
    {
        var r = ReferenceModels.Ratios(Companies.Financial(true));
        Assert.Equal(1.8, r["CurrentRatio"], 4);                                        // SPACTOT / SPPDTOTENTRO
        Assert.Equal((4_500_000 - 1_200_000) / 2_500_000.0, r["QuickRatio"], 4);
        Assert.Equal((1_200_000 + 400_000) / 10_000_000.0, r["EBITDAMargin"], 4);       // "1.200.000" is read as Italians write it
        Assert.Equal(1_200_000 / 9_500_000.0, r["ROA"], 4);
        Assert.Equal(800_000 / 5_000_000.0, r["ROE"], 4);
        Assert.Equal((1_200_000 + 400_000) / 150_000.0, r["InterestCoverageRatio"], 3);  // the interest is read in absolute value
    }

    [Fact]
    public void TheAssessmentAimsPassWhatTheyReceiveOn()
    {
        var f = Only(new FinancialAssessment("FNA").Compute(Companies.In((CuiTypes.FinancialStatements, Companies.Financial(true, "CUI-FNS-V2.1")))), CuiTypes.FinancialDescriptors);
        Assert.Equal(Companies.Financial(true)["FinancialDescriptors"]!.ToJsonString(), f["FinancialDescriptors"]!.ToJsonString());
        var g = Only(new GovernanceAssessment("GVA").Compute(Companies.In((CuiTypes.GovernanceStatements, Companies.Governance(true)))), CuiTypes.GovernanceDescriptors);
        Assert.Equal(Companies.Governance(true)["GovernanceDescriptors"]!.ToJsonString(), g["GovernanceDescriptors"]!.ToJsonString());
    }

    [Fact]
    public void ADistressedCompanyIsMoreLikelyToDefaultThanAHealthyOne()
    {
        Assert.True(Default(true) < 0.03, $"healthy {Default(true):F4}");
        Assert.True(Default(false) > 0.20, $"distressed {Default(false):F4}");
    }

    [Fact]
    public void ALongerHorizonIsLessSafe() => Assert.True(Default(false, 6) < Default(false, 12) && Default(false, 12) < Default(false, 36));

    [Fact]
    public void TheImpactsOfTheDescriptorsAddUpToAHundred()
    {
        var p = Only(new PrimaryDefaultPrediction("CDP").Compute(Companies.In((CuiTypes.PredictionHorizon, Months(12)), (CuiTypes.FinancialDescriptors, Companies.Financial(false)))), CuiTypes.PrimaryDefault);
        Assert.InRange(p["PrimaryDefaultDescriptors"]![0]!["PrimaryDefaultDescriptorImpacts"]!.AsArray().Sum(i => (double)i!["PrimaryDefaultDescriptorImpact"]!), 99.9, 100.1);
    }

    [Fact]
    public void AWeakOrganisationDoesMoreToTheDefaultThanASoundOne()
    {
        ReferenceModels.AsOf = AsOf;   // the tests pin the time
        double Impact(bool healthy)
        {
            var o = Only(new OrganisationModelAssessment("OMA").Compute(Companies.In((CuiTypes.PredictionHorizon, Months(12)), (CuiTypes.GovernanceDescriptors, Companies.Governance(healthy)), (CuiTypes.FinancialDescriptors, Companies.Financial(healthy)))), CuiTypes.Organisation);
            return (double)o["OrganisationDescriptors"]![0]!["ImpactOnDefault"]!;
        }
        Assert.True(Impact(false) > Impact(true) * 2, $"sound {Impact(true):F3}, weak {Impact(false):F3}");
    }

    [Fact]
    public void ClimateAndCyberRisksRaiseTheDiscontinuityProbability()
    {
        double Probability(params (string, JsonNode)[] extra)
        {
            var inputs = Companies.In([(CuiTypes.PredictionHorizon, Months(12)), (CuiTypes.GovernanceDescriptors, Companies.Governance(true)), (CuiTypes.FinancialDescriptors, Companies.Financial(true)), .. extra]);
            var p = Only(new PrimaryDiscontinuityPrediction("PDP").Compute(inputs), CuiTypes.PrimaryDiscontinuity);
            return (double)p["PrimaryDiscontinuityDescriptors"]![0]!["PrimaryDiscontinuityProbability"]!;
        }
        var without = Probability();
        var with = Probability((CuiTypes.ClimateRisk, Companies.Climate()), (CuiTypes.CyberRisk, Companies.Cyber()));
        Assert.True(with > without, $"{without:F4} -> {with:F4}");
    }

    [Fact]
    public void TheRiskMatrixHasARowForEachLevel2RiskAndThePerturbationRaisesTheDefaultProbability()
    {
        var matrix = Only(new RiskMatrixGeneration("RMG").Compute(Companies.In((CuiTypes.SecondaryStatements, Companies.SecondaryRisks()))), CuiTypes.SecondaryMatrix);
        var rows = matrix["SecondaryRiskMatrixData"]!.AsObject();
        Assert.Equal(3, rows.Count);
        Assert.Equal("Medium", (string?)rows["RiskID#1"]!["Occurrence"]!["ValueOfOccurrence"]);
        Assert.Equal("Major", (string?)rows["RiskID#1"]!["Impact"]!["ValueOfImpact"]);
        Assert.Equal("Serious", (string?)rows["RiskID#1"]!["Severity"]!["ValueOfSeverity"]);
        Assert.Equal(20, (double)rows["RiskID#1"]!["Retention"]!["ValueOfRetention"]!);
        var pdd = JsonNode.Parse("""{ "PrimaryDefaultDescriptors": [ { "PrimaryDefaultProbability": 0.05 } ] }""")!;
        var p = (double)Only(new PredictionResultPerturbation("PRP").Compute(Companies.In((CuiTypes.PrimaryDefault, pdd), (CuiTypes.SecondaryMatrix, matrix), (CuiTypes.PredictionHorizon, Months(12)))), CuiTypes.SecondaryProbability)!;
        Assert.InRange(p, 0.0501, 1);
    }

    [Fact]
    public void ADatumThatIsNotWhatItsDataTypeSaysIsReportedNotComputed()
    {
        Assert.Throws<CuiDataException>(() => new PrimaryDefaultPrediction("CDP").Compute(Companies.In((CuiTypes.PredictionHorizon, Months(0)), (CuiTypes.FinancialDescriptors, Companies.Financial(true)))).ToList());
        Assert.Throws<CuiDataException>(() => new PrimaryDefaultPrediction("CDP").Compute(Companies.In((CuiTypes.PredictionHorizon, Months(12)), (CuiTypes.FinancialDescriptors, JsonNode.Parse("""{ "FinancialDescriptors": { "AQ": [] } }""")!))).ToList());
    }

    internal static JsonNode Stamped(JsonNode node, string acronym)
    {
        if (node is not JsonObject o) return node;
        var s = (JsonObject)o.DeepClone();
        s["Header"] = $"CUI-{acronym}-V2.1";
        s["MInstanceID"] = "M1";
        return s;
    }
}

// THE DATA TYPES: each datum the test builds, and each the AIMs give, validates against its schema.
public class DataTypeTests
{
    [Fact]
    public void WhatTheAimsReceiveAndGiveValidatesAgainstTheSchemasOfCuiCpp()
    {
        ReferenceModels.AsOf = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var months = JsonValue.Create(12)!;
        var fnd = Companies.Financial(true); var gvs = Companies.Governance(true);
        var pdd = new PrimaryDefaultPrediction("CDP").Compute(Companies.In((CuiTypes.PredictionHorizon, months), (CuiTypes.FinancialDescriptors, fnd))).Single().Item2;
        var ord = new OrganisationModelAssessment("OMA").Compute(Companies.In((CuiTypes.PredictionHorizon, months), (CuiTypes.GovernanceDescriptors, gvs), (CuiTypes.FinancialDescriptors, fnd))).Single().Item2;
        var psd = new PrimaryDiscontinuityPrediction("PDP").Compute(Companies.In((CuiTypes.PredictionHorizon, months), (CuiTypes.GovernanceDescriptors, gvs), (CuiTypes.FinancialDescriptors, fnd),
                                                                                  (CuiTypes.ClimateRisk, Companies.Climate()), (CuiTypes.CyberRisk, Companies.Cyber()))).Single().Item2;
        var srm = new RiskMatrixGeneration("RMG").Compute(Companies.In((CuiTypes.SecondaryStatements, Companies.SecondaryRisks()))).Single().Item2;
        JsonNode WithTime(JsonNode n, string name) { var o = (JsonObject)n.DeepClone(); o[name] = new JsonObject { ["Header"] = "OSD-TIM-V1.5", ["TimeID"] = "t", ["Data"] = "2026-01-10T10:00:00Z" }; return o; }
        var result = new Dictionary<string, string>
        {
            ["FinancialDescriptors"] = Repo.Validate("FinancialDescriptors", fnd),
            ["FinancialStatements"] = Repo.Validate("FinancialStatements", Companies.Financial(true, "CUI-FNS-V2.1")),
            ["GovernanceDescriptors"] = Repo.Validate("GovernanceDescriptors", gvs),
            ["GovernanceStatements"] = Repo.Validate("GovernanceStatements", gvs),
            ["ClimateRiskDescriptors"] = Repo.Validate("ClimateRiskDescriptors", Companies.Climate()),
            ["CyberRiskDescriptors"] = Repo.Validate("CyberRiskDescriptors", Companies.CyberConforming()),
            ["SecondaryRiskStatements"] = Repo.Validate("SecondaryRiskStatements", Companies.SecondaryRisks()),
            ["PrimaryDefaultDescriptors"] = Repo.Validate("PrimaryDefaultDescriptors", WithTime(AimTests.Stamped(pdd, "PDD"), "PrimaryDefaultDescriptorsTime")),
            ["OrganisationDescriptors"] = Repo.Validate("OrganisationDescriptors", WithTime(AimTests.Stamped(ord, "ORD"), "OrganisationDescriptorsTime")),
            ["PrimaryDiscontinuityDescriptors"] = Repo.Validate("PrimaryDiscontinuityDescriptors", WithTime(AimTests.Stamped(psd, "PSD"), "PrimaryDiscontinuityDescriptorsTime")),
            ["SecondaryRiskMatrix"] = Repo.Validate("SecondaryRiskMatrix", WithTime(AimTests.Stamped(srm, "SRM"), "SecondaryRiskMatrixTime")),
        };
        Assert.All(result, kv => Assert.True(kv.Value == "valid", $"{kv.Key}: {kv.Value}"));
    }
}

// THE MODULE: Company Performance Prediction, run by the Controller from its Metadata - the composites built from their Sub-AIMs - on the data of a company.
public class ModuleTests
{
    private const string Module = "1CUI-CPP-V2.1-I01";

    private static JsonNode? Read(ControllerApi api, string type, int wait = 5000) =>
        api.OutputRead(Module, type, 1, wait) is { Ok: true, Json: { } json } ? JsonNode.Parse(json) : null;

    [Fact]
    public void TheModuleGivesTheDescriptorsOfACompany()
    {
        ReferenceModels.AsOf = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var api = new ControllerApi(Repo.Amds, Path.Combine(Repo.Root, "AIMs", "aim-settings.json"), new CuiProvider());
        Assert.Equal(AifError.OK, api.StartFlow(Module));
        try
        {
            void Write(string type, JsonNode json) => Assert.Equal(AifError.OK, api.InputWrite(Module, type, 1, json.ToJsonString(), 2000));
            Write(CuiTypes.PredictionHorizon, JsonValue.Create(12)!);
            Write(CuiTypes.FinancialDescriptors, Companies.Financial(false));
            Write(CuiTypes.GovernanceDescriptors, Companies.Governance(false));
            Write(CuiTypes.ClimateRisk, Companies.Climate());
            Write(CuiTypes.CyberRisk, Companies.Cyber());
            Write(CuiTypes.SecondaryStatements, Companies.SecondaryRisks());

            var organisation = Read(api, CuiTypes.Organisation);
            var primaryDefault = Read(api, CuiTypes.PrimaryDefault);
            var discontinuity = Read(api, CuiTypes.PrimaryDiscontinuity);
            var secondary = Read(api, CuiTypes.SecondaryProbability);
            Assert.True(organisation is not null, "ORD"); Assert.True(primaryDefault is not null, "PDD"); Assert.True(discontinuity is not null, "PSD"); Assert.True(secondary is not null, "the Secondary Discontinuity Probability");
            Assert.Equal("CUI-PDD-V2.1", (string?)primaryDefault!["Header"]);
            var pd = (double)primaryDefault["PrimaryDefaultDescriptors"]![0]!["PrimaryDefaultProbability"]!;
            var pc = (double)discontinuity!["PrimaryDiscontinuityDescriptors"]![0]!["PrimaryDiscontinuityProbability"]!;
            var sd = (double)secondary!;
            Assert.True(pd > 0.2, $"a company in difficulty: default {pd:F3}");
            Assert.True(pc >= pd - 1e-9, $"discontinuity {pc:F3} at least the default {pd:F3}");
            Assert.True(sd > pd, $"the secondary risks perturb the default {pd:F3} to {sd:F3}");
        }
        finally { api.StopFlow(Module); }
    }
}
