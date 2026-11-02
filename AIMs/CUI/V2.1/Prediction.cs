using System.Globalization;
using System.Text.Json.Nodes;

namespace Mpai.Cui;

// THE REFERENCE MODELS. MPAI-CUI specifies what the Prediction AIMs receive and give, not the model inside - a trained model is the Implementer's. These are the transparent models of
// this implementation: a logistic score of descriptors, each term a standardised descriptor times a weight, whose terms are also the explainability the descriptors carry (each term's
// share of the score). They are not trained on real data and say nothing of any real company: they exist to run the AIMs, test the Data Types and show the shape of a model that fits.
// The descriptors the models read are some of the codes of the Financial and Governance Descriptors, by the semantics of CUI-CPP's Data Type pages; what a company does not give, the model leaves out.
public static class ReferenceModels
{
    // ---- Financial: the ratios of the questionnaire's balance sheet and income statement. The model that weighs them is a PdModel: the reference one below, or a trained one that is Access data.
    public static PdModel Reference => PdModel.Reference;
    public static readonly string[] RatioNames = ["CurrentRatio", "QuickRatio", "EBITDAMargin", "ROA", "ROE", "ROS", "DebtOnSales", "LongTermLiabilityRatio", "InterestCoverageRatio", "InterestOnSales"];

    // The ratios of a company from its Financial Descriptors: the codes are SPACTOT total current assets, SPACITOT inventories, SPPDTOTENTRO liabilities due within 12 months, SPPDTOTOLTRE liabilities due
    // after, SPPDTOT total liabilities, SPPATOT shareholders' equity, SPPTOT total liabilities and equity (the total assets), CEA1 revenues, CEBDIFFVAL operating result (EBIT), CEB5abc depreciation and
    // amortisation, CEC3 interest and other financial expenses, CEUTILE net profit.
    public static Dictionary<string, double> Ratios(JsonNode? financial)
    {
        var q = new Questionnaire(financial);
        double? Div(double? a, double? b) => a.HasValue && b is { } d && d != 0 ? a / d : null;
        var (revenues, ebit, depreciation, interest, profit) = (q.Number("CEA1"), q.Number("CEBDIFFVAL"), q.Number("CEB5abc") ?? 0, q.Number("CEC3") is { } c ? Math.Abs(c) : (double?)null, q.Number("CEUTILE"));
        var (current, inventories, shortTerm, longTerm, debts, equity, assets) = (q.Number("SPACTOT"), q.Number("SPACITOT") ?? 0, q.Number("SPPDTOTENTRO"), q.Number("SPPDTOTOLTRE"), q.Number("SPPDTOT"), q.Number("SPPATOT"), q.Number("SPPTOT"));
        var ebitda = ebit.HasValue ? ebit + depreciation : null;
        var r = new Dictionary<string, double>();
        void Put(string name, double? v) { if (v is { } x && double.IsFinite(x)) r[name] = x; }
        Put("CurrentRatio", Div(current, shortTerm));
        Put("QuickRatio", Div(current - inventories, shortTerm));
        Put("EBITDAMargin", Div(ebitda, revenues));
        Put("ROA", Div(ebit, assets));
        Put("ROE", equity is > 0 ? Div(profit, equity) : null);          // a return on equity that is negative or zero has no meaning: with the equity gone, a loss over it would look like a profit
        Put("ROS", Div(ebit, revenues));
        Put("DebtOnSales", Div(debts, revenues));
        Put("LongTermLiabilityRatio", Div(longTerm, assets));
        Put("InterestCoverageRatio", Div(ebitda, interest));
        Put("InterestOnSales", Div(interest, revenues));
        return r;
    }

    // The terms of the financial score: the weight times how far the ratio is from its mean in scales, within three.
    public static List<(string Name, double Term)> FinancialTerms(JsonNode? financial, PdModel? model = null) => (model ?? PdModel.Reference).Terms(Ratios(financial));

    // The probability of default within `months`, from the annual one.
    public static double DefaultProbability(IEnumerable<(string Name, double Term)> terms, double months, PdModel? model = null) => (model ?? PdModel.Reference).Probability(terms, months);

    // Each term's share of the whole, in percent: the descriptor's importance.
    public static IEnumerable<(string Name, double Percent)> Shares(IReadOnlyList<(string Name, double Term)> terms)
    {
        var total = terms.Sum(t => Math.Abs(t.Term));
        return terms.Select(t => (t.Name, total > 0 ? CuiAim.Round(100 * Math.Abs(t.Term) / total, 2) : 0));
    }

    // ---- Governance: what the questionnaire says of who controls and watches the company: SINDACO statutory auditor and SOCREV audit firm, REVOBBLIGATORIA mandatory audit, CAPMINIMILEGGE capital
    // below the legal limit, QUOTE the share of each shareholder, ANAGRDATAINIZIO the date the activity started (dd/mm/yyyy), a Startup being up to 4 years old and a Historic company 16 or more.
    // For the tests only: a time that overrides the Controller's. A Module takes the time from the Controller (MPAI_AIFM_Time_Get), never from the clock of the machine (AIF V3.0 CTR-5, COM-6).
    public static DateTimeOffset? AsOf { get; set; }
    public const double OrganisationIntercept = 0.4;

    public static List<(string Name, double Term)> OrganisationTerms(JsonNode? governance, DateTimeOffset now)
    {
        var q = new Questionnaire(governance);
        var terms = new List<(string, double)>();
        if (q.Has("SINDACO") || q.Has("SOCREV")) terms.Add(("AuditBody", (q.Yes("SINDACO") ?? true) || !string.IsNullOrWhiteSpace(q.Text("SOCREV")) && q.Text("SOCREV") != "0" ? 0.30 : 0));
        if (q.Yes("REVOBBLIGATORIA") is { } mandatory) terms.Add(("MandatoryAudit", mandatory ? 0.15 : 0));
        if (q.Yes("CAPMINIMILEGGE") is { } below) terms.Add(("CapitalBelowLegalLimit", below ? -0.60 : 0));
        var shares = q.Numbers("QUOTE").ToList();
        if (shares.Count > 0) terms.Add(("ShareholderConcentration", -0.4 * Math.Max(0, shares.Max() - 50) / 50));
        if (DateTimeOffset.TryParseExact(q.Text("ANAGRDATAINIZIO"), "d/M/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var started))
        {
            var age = (now - started).TotalDays / 365.25;
            terms.Add(("CompanyAge", age <= 4 ? -0.30 : age >= 16 ? 0.10 : 0));
        }
        return terms;
    }
    public static double OrganisationIndex(IEnumerable<(string Name, double Term)> terms) => CuiAim.Sigmoid(OrganisationIntercept + terms.Sum(t => t.Term));
}

// PRIMARY DEFAULT PREDICTION (CUI-CDP): the Primary Default Descriptors - the probability of default within the Prediction Horizon and the importance of each Financial Descriptor to it.
public sealed class PrimaryDefaultPrediction(string instanceId, PdModel? model = null) : CuiAim(instanceId)
{
    protected override (string, bool)[] Inputs => [(CuiTypes.PredictionHorizon, true), (CuiTypes.FinancialDescriptors, true)];

    public override IEnumerable<(string, JsonNode)> Compute(IReadOnlyDictionary<string, JsonNode> inputs)
    {
        var months = Horizon(inputs);
        var terms = ReferenceModels.FinancialTerms(inputs[CuiTypes.FinancialDescriptors], model);
        if (terms.Count == 0) throw new CuiDataException("The Financial Descriptors give none of the codes the model reads.");
        var impacts = new JsonArray(ReferenceModels.Shares(terms).Select(s => (JsonNode)new JsonObject { ["PrimaryDefaultDescriptorID"] = s.Name, ["PrimaryDefaultDescriptorImpact"] = s.Percent }).ToArray());
        yield return (CuiTypes.PrimaryDefault, new JsonObject
        {
            ["PrimaryDefaultDescriptorsID"] = "",
            ["PrimaryDefaultDescriptors"] = new JsonArray(new JsonObject { ["PrimaryDefaultProbability"] = Round(ReferenceModels.DefaultProbability(terms, months, model)), ["PrimaryDefaultDescriptorImpacts"] = impacts })
        });
    }
}

// ORGANISATION MODEL ASSESSMENT (CUI-OMA): the Organisation Descriptors - what the organisation does to the probability of default within the horizon, and the importance of each Governance Descriptor
// to it. A weaker organisation raises it, and weaker finances amplify that. (The names of the properties are those of the V2.0 schema, GovernaceDescriptorID included.)
public sealed class OrganisationModelAssessment(string instanceId, PdModel? model = null) : CuiAim(instanceId)
{
    protected override (string, bool)[] Inputs => [(CuiTypes.PredictionHorizon, true), (CuiTypes.GovernanceDescriptors, true), (CuiTypes.FinancialDescriptors, true)];

    public override IEnumerable<(string, JsonNode)> Compute(IReadOnlyDictionary<string, JsonNode> inputs)
    {
        var months = Horizon(inputs);
        var terms = ReferenceModels.OrganisationTerms(inputs[CuiTypes.GovernanceDescriptors], Now);
        if (terms.Count == 0) throw new CuiDataException("The Governance Descriptors give none of the codes the model reads.");
        var index = ReferenceModels.OrganisationIndex(terms);
        var fin = ReferenceModels.FinancialTerms(inputs[CuiTypes.FinancialDescriptors], model);
        var p = fin.Count > 0 ? ReferenceModels.DefaultProbability(fin, months, model) : 0;
        var impacts = new JsonArray(ReferenceModels.Shares(terms).Select(s => (JsonNode)new JsonObject { ["GovernaceDescriptorID"] = s.Name, ["GovernanceDescriptorImpact"] = s.Percent }).ToArray());
        yield return (CuiTypes.Organisation, new JsonObject
        {
            ["OrganisationDescriptorsID"] = "",
            ["OrganisationDescriptors"] = new JsonArray(new JsonObject { ["ImpactOnDefault"] = Round(Clamp(0.3 * (1 - index) * (1 + 5 * p), 0, 1)), ["GovernanceDescriptorsImpact"] = impacts })
        });
    }
}

// PRIMARY DISCONTINUITY PREDICTION (CUI-PDP): the probability that operations are interrupted within the horizon from the risks that have descriptors - financial, governance and, where given, climate
// and cyber - and what each contributes. The risks combine as independent ones; the Climate and Cyber Risk Descriptors are optional, as the Primary Risks that have AIMs are. The first entry of the
// descriptors is the company's, each contribution a category of descriptors; one entry follows for each Primary Risk given, with the impact of its descriptors.
public sealed class PrimaryDiscontinuityPrediction(string instanceId, PdModel? model = null) : CuiAim(instanceId)
{
    protected override (string, bool)[] Inputs => [(CuiTypes.PredictionHorizon, true), (CuiTypes.ClimateRisk, false), (CuiTypes.CyberRisk, false), (CuiTypes.GovernanceDescriptors, true), (CuiTypes.FinancialDescriptors, true)];

    // The climate risk, 0 to 0.2, from the descriptors that say a place is extreme.
    public static List<(string Name, double Risk)> Climate(JsonNode? data)
    {
        double? V(string group, string value) => Num(data?[group]?[value]);
        var risks = new List<(string, double)>();
        if (V("AverageMaximumTemperature", "AverageMaximumTemperature4Qs") is { } hot) risks.Add(("AverageMaximumTemperature", Clamp((hot - 30) / 20, 0, 1) * 0.05));
        if (V("AverageMinimumTemperature", "AverageMinimumTemperature4Qs") is { } cold) risks.Add(("AverageMinimumTemperature", Clamp(-cold / 20, 0, 1) * 0.04));
        if (V("TotalMonthlyPrecipitation", "TotalMonthlyPrecipitationMm") is { } rain) risks.Add(("TotalMonthlyPrecipitation", Clamp((rain - 100) / 300, 0, 1) * 0.06));
        if (V("AverageNumberOfMonthlyRainyDays4Qs", "AverageNumberOfMonthlyRainyDays") is { } days) risks.Add(("AverageNumberOfMonthlyRainyDays", Clamp((days - 8) / 22, 0, 1) * 0.03));
        if (V("AverageMaximumHumidity4Qs", "AverageMaximumHumidity") is { } wet) risks.Add(("AverageMaximumHumidity", Clamp((wet - 80) / 20, 0, 1) * 0.02));
        return risks;
    }

    // The cyber risk: each of the descriptors of an attack that is given adds 0.05 to it.
    public static List<(string Name, double Risk)> Cyber(JsonNode? data) =>
        (data as JsonObject)?.Where(p => p.Key != "Regulations" && p.Value is JsonObject).Select(p => (p.Key, 0.05)).ToList() ?? [];

    public override IEnumerable<(string, JsonNode)> Compute(IReadOnlyDictionary<string, JsonNode> inputs)
    {
        var months = Horizon(inputs);
        var fin = ReferenceModels.FinancialTerms(inputs[CuiTypes.FinancialDescriptors], model);
        var gov = ReferenceModels.OrganisationTerms(inputs[CuiTypes.GovernanceDescriptors], Now);
        var year = Math.Min(1, months / 12.0);
        var categories = new List<(string Category, double Risk, List<(string Name, double Weight)> Parts)>();
        if (fin.Count > 0) categories.Add(("Financial", ReferenceModels.DefaultProbability(fin, months, model), fin.Select(t => (t.Name, Math.Abs(t.Term))).ToList()));
        if (gov.Count > 0) categories.Add(("Governance", 0.3 * (1 - ReferenceModels.OrganisationIndex(gov)) * year, gov.Select(t => (t.Name, Math.Abs(t.Term))).ToList()));
        List<(string Name, double Risk)>? climate = null, cyber = null;
        if (inputs.TryGetValue(CuiTypes.ClimateRisk, out var l)) { climate = Climate(l["ClimateRiskDescriptorsData"]); categories.Add(("Climate", climate.Sum(c => c.Risk) * year, climate.Select(c => (c.Name, c.Risk)).ToList())); }
        if (inputs.TryGetValue(CuiTypes.CyberRisk, out var c)) { cyber = Cyber(c["CyberRisk"]); categories.Add(("Cyber", Math.Min(1, cyber.Sum(x => x.Risk)) * year, cyber.Select(x => (x.Name, x.Risk)).ToList())); }
        if (categories.Count == 0) throw new CuiDataException("None of the descriptors gives the model a code it reads.");

        var probability = 1 - categories.Aggregate(1.0, (keep, cat) => keep * (1 - Clamp(cat.Risk, 0, 1)));
        var total = categories.Sum(cat => cat.Risk);
        var entries = new JsonArray();
        entries.Add(new JsonObject
        {
            ["PrimaryRiskID"] = "Company", ["PrimaryRisk"] = "All the descriptors", ["PrimaryDiscontinuityProbability"] = Round(probability),
            ["PrimaryDiscontinuityDescriptorImpacts"] = new JsonArray(categories.Select(cat => (JsonNode)new JsonObject { ["PrimaryRiskID"] = cat.Category, ["PrimaryRiskDescriptorImpact"] = Round(total > 0 ? 100 * cat.Risk / total : 0, 2) }).ToArray())
        });
        foreach (var (id, parts) in new[] { ("Climate", climate), ("Cyber", cyber) })
        {
            if (parts is null) continue;
            var risk = categories.First(x => x.Category == id).Risk;
            var sum = parts.Sum(p => p.Risk);
            entries.Add(new JsonObject
            {
                ["PrimaryRiskID"] = id, ["PrimaryRisk"] = id, ["PrimaryDiscontinuityProbability"] = Round(Clamp(risk, 0, 1)),
                ["PrimaryDiscontinuityDescriptorImpacts"] = new JsonArray(parts.Select(p => (JsonNode)new JsonObject { ["PrimaryRiskID"] = p.Name, ["PrimaryRiskDescriptorImpact"] = Round(sum > 0 ? p.Risk / sum : 0) }).ToArray())
            });
        }
        yield return (CuiTypes.PrimaryDiscontinuity, new JsonObject { ["PrimaryDiscontinuityDescriptorsID"] = "", ["PrimaryDiscontinuityDescriptors"] = entries });
    }
}

// PREDICTION RESULT PERTURBATION (CUI-PRP): the Secondary Discontinuity Probability, a number between 0 and 1: the Primary Default Probability perturbed by the risks of the Secondary Risk Matrix,
// which no AIM predicts - each, by its weight (occurrence, impact and severity each as a fraction of its scale, and what the company does not retain), takes half of what is left of the company's
// chances over a year, in proportion to the horizon within the year.
public sealed class PredictionResultPerturbation(string instanceId) : CuiAim(instanceId)
{
    protected override (string, bool)[] Inputs => [(CuiTypes.PrimaryDefault, true), (CuiTypes.SecondaryMatrix, true), (CuiTypes.PredictionHorizon, true)];

    private static double Level(JsonNode? item, string value, string[] scale) => (Array.IndexOf(scale, (string?)item?[value]) + 1.0) / scale.Length;

    public override IEnumerable<(string, JsonNode)> Compute(IReadOnlyDictionary<string, JsonNode> inputs)
    {
        var months = Horizon(inputs);
        var p = Num(inputs[CuiTypes.PrimaryDefault]["PrimaryDefaultDescriptors"]?[0]?["PrimaryDefaultProbability"]) ?? throw new CuiDataException("The Primary Default Descriptors give no probability.");
        var keep = 1 - p;
        foreach (var (_, risk) in inputs[CuiTypes.SecondaryMatrix]["SecondaryRiskMatrixData"] as JsonObject ?? [])
        {
            var weight = Level(risk?["Occurrence"], "ValueOfOccurrence", ["Low", "Medium", "High"]) * Level(risk?["Impact"], "ValueOfImpact", ["Minor", "Moderate", "Major"])
                       * Level(risk?["Severity"], "ValueOfSeverity", ["Irrelevant", "Not very relevant", "Relevant", "Very relevant", "Serious"]) * (1 - (Num(risk?["Retention"]?["ValueOfRetention"]) ?? 0) / 100);
            keep *= 1 - 0.5 * Clamp(weight, 0, 1) * Math.Min(1, months / 12.0);
        }
        yield return (CuiTypes.SecondaryProbability, JsonValue.Create(Round(1 - keep))!);
    }
}
