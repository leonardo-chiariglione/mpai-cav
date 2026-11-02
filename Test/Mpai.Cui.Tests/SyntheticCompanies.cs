using System.Globalization;
using System.Text.Json.Nodes;

namespace Mpai.Cui.Tests;

// THE SYNTHETIC COMPANIES: a corpus of invented companies, each made to exercise something the Prediction AIMs must do - a strong company, an average one, one with debt, one that loses money, one
// that is young, one with nothing of what the models divide by, one with absurd figures, one that is only partly described, and the risks that are optional. They stand for no real company; they are
// the same on every run (the files in Test/Data/CUI/Companies are what this class makes, and a test says so), and what the AIMs give for them is recorded (Test/Expected/cui-companies.json).
internal static class Synthetic
{
    public sealed record Entry(string Name, string Why, JsonObject Company);

    // The twelve numbers of a balance sheet and an income statement that the ratios of the models are made of, by the codes of the questionnaire.
    private sealed record Sheet(double Revenue, double Ebit, double Depreciation, double Interest, double Profit, double CurrentAssets, double Inventories,
                                double ShortTerm, double LongTerm, double Liabilities, double Equity, double Assets);

    private static JsonObject Item(string code, string value) => new() { ["code"] = code, ["type"] = "text", ["editable"] = true, ["required"] = null, ["value"] = value, ["saves"] = new JsonArray() };
    private static JsonObject Time(string id) => new() { ["Header"] = "OSD-TIM-V1.5", ["TimeID"] = id, ["Data"] = "2026-01-10T10:00:00Z" };
    private static string N(double v) => v.ToString("0", CultureInfo.InvariantCulture);

    private static JsonObject Financial(string id, IEnumerable<(string Code, string Value)> items) => new()
    {
        ["Header"] = "CUI-FND-V2.1", ["MInstanceID"] = "M1", ["FinancialDescriptorsID"] = id, ["oType"] = 1,
        ["FinancialDescriptors"] = new JsonObject
        {
            ["AQ"] = new JsonArray(new JsonObject
            {
                ["id"] = 1, ["module_id"] = "CUI", ["poweruser_id"] = 1, ["survey_company_id"] = 1, ["period_start"] = "2025-01-01", ["period_end"] = "2025-12-31",
                ["input"] = new JsonArray(items.Select(i => (JsonNode)Item(i.Code, i.Value)).ToArray()),
                ["simulation"] = false, ["consolidated"] = true, ["created_at"] = "2026-01-10T10:00:00Z", ["updated_at"] = "2026-01-10T10:00:00Z"
            })
        }
    };

    private static JsonObject FinancialOf(string id, Sheet s) => Financial(id,
    [
        ("CEA1", N(s.Revenue)), ("CEBDIFFVAL", N(s.Ebit)), ("CEB5abc", N(s.Depreciation)), ("CEC3", N(s.Interest)), ("CEUTILE", N(s.Profit)), ("SPACTOT", N(s.CurrentAssets)),
        ("SPACITOT", N(s.Inventories)), ("SPPDTOTENTRO", N(s.ShortTerm)), ("SPPDTOTOLTRE", N(s.LongTerm)), ("SPPDTOT", N(s.Liabilities)), ("SPPATOT", N(s.Equity)), ("SPPTOT", N(s.Assets))
    ]);

    // The Governance Descriptors: a statutory auditor, an audit firm, a mandatory audit, capital below the legal limit, the shares of the shareholders, the day the company started.
    private static JsonObject Governance(string id, bool auditor, string? firm, bool mandatoryAudit, bool belowCapital, double[] shares, string started)
    {
        var items = new List<(string, string)> { ("SINDACO", auditor ? "1" : "0") };
        if (firm is not null) items.Add(("SOCREV", firm));
        items.Add(("REVOBBLIGATORIA", mandatoryAudit ? "1" : "0"));
        items.Add(("CAPMINIMILEGGE", belowCapital ? "1" : "0"));
        items.AddRange(shares.Select(q => ("QUOTE", N(q))));
        items.Add(("ANAGRDATAINIZIO", started));
        return new()
        {
            ["Header"] = "CUI-GVS-V2.1", ["MInstanceID"] = "M1", ["GovernanceDescriptorsID"] = id,
            ["GovernanceDescriptors"] = new JsonObject
            {
                ["EFQ"] = new JsonObject
                {
                    ["id"] = 1, ["module_id"] = "CUI", ["poweruser_id"] = 1, ["survey_company_id"] = 1, ["period_start"] = "2025-01-01", ["period_end"] = "2025-12-31",
                    ["input"] = new JsonArray(items.Select(i => (JsonNode)new JsonObject { ["type"] = "text", ["code"] = i.Item1, ["editable"] = true, ["value"] = i.Item2, ["saves"] = new JsonArray() }).ToArray()),
                    ["output"] = null, ["simulation"] = false, ["consolidated"] = true, ["last_processing_datetime"] = null, ["created_at"] = "2026-01-10T10:00:00Z", ["updated_at"] = "2026-01-10T10:00:00Z"
                }
            },
            ["oType"] = 1
        };
    }

    private static JsonObject Observation(string value, JsonNode v) => new() { ["Time"] = Time("t"), ["Source"] = "weather service", ["Type"] = "number", [value] = v };

    private static JsonObject Climate(double? hot, double? cold, double? rain, double? humidity)
    {
        var data = new JsonObject();
        if (hot is { } h) data["AverageMaximumTemperature"] = Observation("AverageMaximumTemperature4Qs", h);
        if (cold is { } c) data["AverageMinimumTemperature"] = Observation("AverageMinimumTemperature4Qs", c);
        if (rain is { } r) data["TotalMonthlyPrecipitation"] = Observation("TotalMonthlyPrecipitationMm", r);
        if (humidity is { } u) data["AverageMaximumHumidity4Qs"] = Observation("AverageMaximumHumidity", u);
        return new() { ["Header"] = "CUI-LRD-V2.1", ["MInstanceID"] = "M1", ["ClimateRiskDescriptorsID"] = Time("LRD1"), ["ClimateRiskDescriptorsTime"] = Time("LRD1-T"), ["ClimateRiskDescriptorsData"] = data };
    }

    // The Cyber Risk Descriptors as the published schema accepts them (the IP patterns of the schema match nothing, so the descriptors of an IP address are not used): each of the attacks given.
    private static JsonObject Cyber(params string[] attacks)
    {
        var risk = new JsonObject();
        foreach (var attack in attacks)
            risk[attack] = new JsonObject { ["AttackDetectionTime"] = Time("a-" + attack), ["VectorProviderOrSource"] = "firewall", ["Type"] = 1 };
        return new() { ["Header"] = "CUI-CRD-V2.1", ["MInstanceID"] = "M1", ["CyberRiskDescriptorsID"] = "CRD1", ["CyberRiskDescriptorsTime"] = Time("CRD1-T"), ["CyberRisk"] = risk };
    }

    private static JsonObject SecondaryRisks() => JsonNode.Parse("""
    { "Header": "CUI-SRS-V2.1", "MInstanceID": "M1",
      "SecondaryRiskStatementsData": {
        "Level-1Risk1": { "Level-2RiskID#1": { "Impact": "High", "Probability": "Medium", "Severity": "High", "Retention": 20 },
                          "Level-2RiskID#2": { "Impact": "Medium", "Probability": "Low", "Severity": "Medium", "Retention": 50 } },
        "Level-1Risk2": { "Level-2RiskID#1": { "Impact": "High", "Probability": "Low", "Severity": "High", "Retention": 0 } } } }
    """)!.AsObject();

    private static JsonObject Company(JsonObject financial, JsonObject governance, JsonObject? climate = null, JsonObject? cyber = null, JsonObject? secondary = null)
    {
        var company = new JsonObject { ["PredictionHorizon"] = 12, ["FinancialDescriptors"] = financial, ["GovernanceDescriptors"] = governance };
        if (climate is not null) company["ClimateRiskDescriptors"] = climate;
        if (cyber is not null) company["CyberRiskDescriptors"] = cyber;
        if (secondary is not null) company["SecondaryRiskStatements"] = secondary;
        return company;
    }

    // What the ratios of the models take as the average company (their means): a current ratio 1.3, a quick ratio 1.0, a margin of 10 %, ROA 4 %, ROE 8 %, ROS 5 %, debts of 0.8 of the sales.
    private static readonly Sheet Average = new(10_000_000, 500_000, 500_000, 250_000, 300_000, 6_337_500, 1_462_500, 4_875_000, 3_125_000, 8_000_000, 4_500_000, 12_500_000);

    public static IReadOnlyList<Entry> All() =>
    [
        new("average", "a company at the means of the models: its ratios are the ones the models call average",
            Company(FinancialOf("F-average", Average), Governance("G-average", true, "Auditors Srl", true, false, [60, 40], "15/03/2008"))),
        new("blue-chip", "a large, strong company: profitable, little debt, audited, widely held",
            Company(FinancialOf("F-blue-chip", new(200_000_000, 30_000_000, 8_000_000, 1_000_000, 22_000_000, 90_000_000, 15_000_000, 40_000_000, 30_000_000, 70_000_000, 130_000_000, 200_000_000)),
                    Governance("G-blue-chip", true, "Big Auditors SpA", true, false, [30, 25, 25, 20], "01/01/1975"))),
        new("healthy-small", "the healthy company of the tests: a small manufacturer that is sound and audited",
            Company(Financial("F-healthy", [("CEA1", "10000000"), ("CEBDIFFVAL", "1.200.000"), ("CEB5abc", "400000"), ("CEC3", "-150000"), ("CEUTILE", "800000"), ("SPACTOT", "4500000"), ("SPACITOT", "1200000"),
                                              ("SPPDTOTENTRO", "2500000"), ("SPPDTOTOLTRE", "1500000"), ("SPPDTOT", "4000000"), ("SPPATOT", "5000000"), ("SPPTOT", "9500000")]),
                    Governance("G-healthy", true, "Auditors Srl", true, false, [40, 35, 25], "01/03/1998"))),
        new("leveraged", "profitable before interest but carrying debts it barely pays: interest takes the profit",
            Company(FinancialOf("F-leveraged", new(20_000_000, 1_500_000, 1_000_000, 1_600_000, -400_000, 4_000_000, 1_500_000, 6_000_000, 14_000_000, 20_000_000, 2_000_000, 22_000_000)),
                    Governance("G-leveraged", true, null, true, false, [70, 30], "10/10/2005"))),
        new("loss-making", "an operating loss, a net loss and little equity",
            Company(FinancialOf("F-loss", new(8_000_000, -1_200_000, 500_000, 300_000, -1_600_000, 3_000_000, 1_000_000, 3_500_000, 1_500_000, 5_000_000, 2_000_000, 7_000_000)),
                    Governance("G-loss", false, null, false, false, [55, 45], "20/05/2012"))),
        new("distressed", "the company in difficulty of the tests: a loss, liabilities beyond its assets, no audit, capital below the legal limit, one owner",
            Company(Financial("F-distressed", [("CEA1", "5000000"), ("CEBDIFFVAL", "-300000"), ("CEB5abc", "350000"), ("CEC3", "280000"), ("CEUTILE", "-650000"), ("SPACTOT", "1500000"), ("SPACITOT", "900000"),
                                                ("SPPDTOTENTRO", "2800000"), ("SPPDTOTOLTRE", "1800000"), ("SPPDTOT", "4600000"), ("SPPATOT", "400000"), ("SPPTOT", "4900000")]),
                    Governance("G-distressed", false, null, false, true, [90, 10], "01/06/2023"))),
        new("negative-equity", "the liabilities exceed the assets: the equity is negative",
            Company(FinancialOf("F-negative", new(12_000_000, 300_000, 400_000, 800_000, -700_000, 4_000_000, 2_000_000, 6_000_000, 3_000_000, 9_000_000, -2_000_000, 7_000_000)),
                    Governance("G-negative", true, null, true, true, [100], "02/02/2001"))),
        new("startup", "a company in its second year: losses while it grows, funded by its owners",
            Company(FinancialOf("F-startup", new(1_200_000, -400_000, 100_000, 50_000, -450_000, 1_500_000, 100_000, 800_000, 200_000, 1_000_000, 1_500_000, 2_500_000)),
                    Governance("G-startup", false, null, false, false, [60, 40], "01/02/2024"))),
        new("family-owned", "a very old company held by one family: sound, with no audit",
            Company(FinancialOf("F-family", new(15_000_000, 1_400_000, 600_000, 200_000, 900_000, 7_000_000, 2_500_000, 3_800_000, 1_700_000, 5_500_000, 9_500_000, 15_000_000)),
                    Governance("G-family", false, null, false, false, [100], "12/09/1962"))),
        new("zero-revenue", "no revenue at all: every ratio that divides by the sales has nothing to divide by",
            Company(FinancialOf("F-zero", new(0, -100_000, 20_000, 10_000, -110_000, 800_000, 0, 300_000, 100_000, 400_000, 600_000, 1_000_000)),
                    Governance("G-zero", false, null, false, false, [50, 50], "05/05/2025"))),
        new("partial-financials", "only some of the codes of the questionnaire are given: the models leave out what the company does not give",
            Company(Financial("F-partial", [("CEA1", "6000000"), ("CEBDIFFVAL", "250000"), ("SPACTOT", "2400000"), ("SPPDTOTENTRO", "1900000"), ("SPPTOT", "5200000")]),
                    Governance("G-partial", true, null, false, false, [100], "03/03/2015"))),
        new("absurd-figures", "figures far beyond anything the models were set on: every ratio is held to three scales",
            Company(FinancialOf("F-absurd", new(5_000_000_000, 900_000_000, 40_000_000, 1, 700_000_000, 2_000_000_000, 10_000_000, 100_000_000, 50_000_000, 150_000_000, 2_850_000_000, 3_000_000_000)),
                    Governance("G-absurd", true, "Auditors", true, false, [10, 10, 10, 10, 10, 10, 10, 10, 10, 10], "01/01/1990"))),
        new("climate-exposed", "the healthy company in a place of heat, rain and humidity (the Climate Risk Descriptors are given)",
            Company(Financial("F-healthy", [("CEA1", "10000000"), ("CEBDIFFVAL", "1.200.000"), ("CEB5abc", "400000"), ("CEC3", "-150000"), ("CEUTILE", "800000"), ("SPACTOT", "4500000"), ("SPACITOT", "1200000"),
                                              ("SPPDTOTENTRO", "2500000"), ("SPPDTOTOLTRE", "1500000"), ("SPPDTOT", "4000000"), ("SPPATOT", "5000000"), ("SPPTOT", "9500000")]),
                    Governance("G-healthy", true, "Auditors Srl", true, false, [40, 35, 25], "01/03/1998"), climate: Climate(41, -12, 380, 96))),
        new("cyber-attacked", "the healthy company under attack (the Cyber Risk Descriptors are given: three kinds of attack)",
            Company(Financial("F-healthy", [("CEA1", "10000000"), ("CEBDIFFVAL", "1.200.000"), ("CEB5abc", "400000"), ("CEC3", "-150000"), ("CEUTILE", "800000"), ("SPACTOT", "4500000"), ("SPACITOT", "1200000"),
                                              ("SPPDTOTENTRO", "2500000"), ("SPPDTOTOLTRE", "1500000"), ("SPPDTOT", "4000000"), ("SPPATOT", "5000000"), ("SPPTOT", "9500000")]),
                    Governance("G-healthy", true, "Auditors Srl", true, false, [40, 35, 25], "01/03/1998"), cyber: Cyber("SourcePort", "DestinationPort", "Protocol"))),
        new("all-risks", "the average company with everything optional given: climate, cyber and the secondary risks",
            Company(FinancialOf("F-average", Average), Governance("G-average", true, "Auditors Srl", true, false, [60, 40], "15/03/2008"),
                    Climate(33, -4, 150, 85), Cyber("SourcePort"), SecondaryRisks())),
        new("distressed-all-risks", "the company in difficulty with everything optional given",
            Company(Financial("F-distressed", [("CEA1", "5000000"), ("CEBDIFFVAL", "-300000"), ("CEB5abc", "350000"), ("CEC3", "280000"), ("CEUTILE", "-650000"), ("SPACTOT", "1500000"), ("SPACITOT", "900000"),
                                                ("SPPDTOTENTRO", "2800000"), ("SPPDTOTOLTRE", "1800000"), ("SPPDTOT", "4600000"), ("SPPATOT", "400000"), ("SPPTOT", "4900000")]),
                    Governance("G-distressed", false, null, false, true, [90, 10], "01/06/2023"), Climate(41, -12, 380, 96), Cyber("SourcePort", "Protocol"), SecondaryRisks()))
    ];
}
