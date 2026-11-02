using System.Text.Json.Nodes;

namespace Mpai.Cui;

// FINANCIAL ASSESSMENT (CUI-FNA): the Financial Descriptors from the Financial Statements. At publication time the AIM is a pass-through one: the Financial Statements have the format
// of the Financial Descriptors (CUI-CPP V2.0, and V2.1), so what it gives is what it receives, as Financial Descriptors. A jurisdiction that changes the format of its Financial Statements
// needs the AIM re-implemented for it.
public sealed class FinancialAssessment(string instanceId) : CuiAim(instanceId)
{
    protected override (string, bool)[] Inputs => [(CuiTypes.FinancialStatements, true)];

    public override IEnumerable<(string, JsonNode)> Compute(IReadOnlyDictionary<string, JsonNode> inputs)
    {
        var statements = inputs[CuiTypes.FinancialStatements] as JsonObject ?? throw new CuiDataException("The Financial Statements are not an object.");
        yield return (CuiTypes.FinancialDescriptors, (JsonObject)statements.DeepClone());
    }
}

// GOVERNANCE ASSESSMENT (CUI-GVA): the Governance Descriptors from the Governance Statements, which have their format: a pass-through AIM, as the Financial Assessment is. The Financial
// Statements, which V2.0 gives it too, are not used by this one.
public sealed class GovernanceAssessment(string instanceId) : CuiAim(instanceId)
{
    protected override (string, bool)[] Inputs => [(CuiTypes.GovernanceStatements, true), (CuiTypes.FinancialStatements, false)];

    public override IEnumerable<(string, JsonNode)> Compute(IReadOnlyDictionary<string, JsonNode> inputs)
    {
        var statements = inputs[CuiTypes.GovernanceStatements] as JsonObject ?? throw new CuiDataException("The Governance Statements are not an object.");
        yield return (CuiTypes.GovernanceDescriptors, (JsonObject)statements.DeepClone());
    }
}

// RISK MATRIX GENERATION (CUI-RMG): the Secondary Risk Matrix from the Secondary Risk Statements. Each Level-2 Risk of the statements - Impact, Probability, Severity, none, Low, Medium or High,
// and Retention in percent - is a row RiskID#n of the matrix, with the Occurrence (Low, Medium, High), Impact (Minor, Moderate, Major), Severity (Irrelevant to Serious) and Retention the matrix
// has. The scales are read: none and Low are the lowest, Medium the middle, High the highest; Severity, of five, as none Irrelevant, Low Not very relevant, Medium Relevant, High Serious.
public sealed class RiskMatrixGeneration(string instanceId) : CuiAim(instanceId)
{
    protected override (string, bool)[] Inputs => [(CuiTypes.SecondaryStatements, true)];

    private static string Scale(string? v, string low, string medium, string high) => v?.ToLowerInvariant() switch { "medium" => medium, "high" => high, _ => low };
    private static JsonObject Item(string type, string valueName, JsonNode value) => new() { ["Type"] = type, [valueName] = value };

    public override IEnumerable<(string, JsonNode)> Compute(IReadOnlyDictionary<string, JsonNode> inputs)
    {
        var data = inputs[CuiTypes.SecondaryStatements]["SecondaryRiskStatementsData"] as JsonObject ?? throw new CuiDataException("The Secondary Risk Statements have no data.");
        var matrix = new JsonObject();
        var n = 0;
        foreach (var (level1, risks) in data)
        {
            if (risks is not JsonObject l2) continue;
            foreach (var (level2, risk) in l2)
            {
                if (risk is not JsonObject r) continue;
                var retention = Questionnaire.Parse(r["Retention"]?.ToString()) ?? throw new CuiDataException($"{level1}/{level2} has no Retention.");
                matrix[$"RiskID#{++n}"] = new JsonObject
                {
                    ["Occurrence"] = Item("Qualitative scale", "ValueOfOccurrence", Scale((string?)r["Probability"], "Low", "Medium", "High")),
                    ["Impact"] = Item("Qualitative scale", "ValueOfImpact", Scale((string?)r["Impact"], "Minor", "Moderate", "Major")),
                    ["Severity"] = Item("Recovery time based scale", "ValueOfSeverity", (string?)r["Severity"] switch { "Low" => "Not very relevant", "Medium" => "Relevant", "High" => "Serious", _ => "Irrelevant" }),
                    ["Retention"] = Item("Real number", "ValueOfRetention", Math.Clamp(retention, 0, 100))
                };
            }
        }
        if (n == 0) throw new CuiDataException("The Secondary Risk Statements have no Level-2 Risk.");
        yield return (CuiTypes.SecondaryMatrix, new JsonObject { ["SecondaryRiskMatrixID"] = "", ["SecondaryRiskMatrixData"] = matrix });
    }
}
