using System.Globalization;
using System.Text.Json.Nodes;

using AIF.Controller;

namespace Mpai.Cui;

// THE DATA TYPES OF CUI-CPP V2.1, by the acronym the Ports are addressed with; a Prediction Horizon is a number of months, a Basic type.
public static class CuiTypes
{
    // The Governance Descriptors have the Header of the Governance Statements: V2.0 gives them the same.
    public const string PredictionHorizon = "number", SecondaryProbability = "number",
                        FinancialStatements = "CUI-FNS-V2.1", GovernanceStatements = "CUI-GVS-V2.1", FinancialDescriptors = "CUI-FND-V2.1", GovernanceDescriptors = "CUI-GVS-V2.1",
                        ClimateRisk = "CUI-LRD-V2.1", CyberRisk = "CUI-CRD-V2.1", Organisation = "CUI-ORD-V2.1", PrimaryDefault = "CUI-PDD-V2.1",
                        PrimaryDiscontinuity = "CUI-PSD-V2.1", SecondaryStatements = "CUI-SRS-V2.1", SecondaryMatrix = "CUI-SRM-V2.1";
}

// A datum that is not what its Data Type says, or that the AIM cannot compute from: the AIM reports it and goes on.
public sealed class CuiDataException(string message) : Exception(message);

// THE AIMs OF CUI-CPP: each reads the Input Ports it has, and when every required one has given something it computes and writes its Outputs; an optional
// Input that comes later computes again. Nothing is kept but the latest of each Input.
public abstract class CuiAim(string instanceId) : IAimProcessor, IAimRunner
{
    private long count;
    private DateTimeOffset? clock;

    public string InstanceId { get; } = instanceId;
    public Task<Message> ProcessAsync(Message message) => throw new NotSupportedException($"{InstanceId} runs continuously.");

    // The Input Ports - by Data Type - and whether each is required.
    protected abstract (string DataType, bool Required)[] Inputs { get; }

    // The time of the Controller's time base, as read when the AIM last ran: what an AIM knows of the time (the machine's clock is not read).
    protected DateTimeOffset Now => ReferenceModels.AsOf ?? clock ?? throw new CuiDataException("The AIM has no time: the Controller gives it.");

    public abstract IEnumerable<(string DataType, JsonNode Json)> Compute(IReadOnlyDictionary<string, JsonNode> inputs);

    // Between two computations: where an AIM keeps something up to date (its model).
    protected virtual Task BeforeComputeAsync(IAimPorts ports) => Task.CompletedTask;

    public async Task RunAsync(IAimPorts ports, AimContext context)
    {
        var latest = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var select = Inputs.Select(i => (i.DataType, 1)).ToArray();
        while (await ports.SelectAsync(-1, select) is { } port)
        {
            if (await ports.ReadAsync(port.DataType, 1, 0) is not { } m) continue;
            latest[port.DataType] = JsonNode.Parse(m.Json)!;
            if (!Inputs.Where(i => i.Required).All(i => latest.ContainsKey(i.DataType))) continue;
            try
            {
                var at = ports.Now;
                clock = at;
                await BeforeComputeAsync(ports);
                foreach (var (type, json) in Compute(latest))
                    await ports.WriteAsync(type, 1, Stamp(json, type, ++count, at).ToJsonString());
            }
            catch (CuiDataException e) { context.Report(e.Message); }
        }
    }

    // The Header, identifier and time of what an AIM gives. A Prediction Horizon travels as it is.
    private static JsonNode Stamp(JsonNode node, string dataType, long count, DateTimeOffset at)
    {
        if (!dataType.StartsWith("CUI-", StringComparison.Ordinal) || node is not JsonObject json) return node;
        var acronym = dataType.Split('-')[1];
        json["Header"] = dataType;
        json[IdName(json)] = $"{acronym}{count:D6}";
        json[TimeName(json)] = new JsonObject { ["Header"] = "OSD-TIM-V1.5", ["TimeID"] = $"{acronym}{count:D6}-T", ["Data"] = at.ToString("o", CultureInfo.InvariantCulture) };
        return json;
    }

    private static string IdName(JsonObject json) => json.First(p => p.Key.EndsWith("ID", StringComparison.Ordinal) && p.Key != "MInstanceID" && p.Key != "UEnvironmentID").Key;
    private static string TimeName(JsonObject json) => IdName(json)[..^2] + "Time";

    public static JsonObject SimpleTime(string id, long ms) => new()
    {
        ["Header"] = "OSD-STM-V1.5", ["SimpleTimeID"] = id,
        ["SimpleTimeData"] = new JsonArray(new JsonObject { ["FlagsByte"] = 3, ["StartTime"] = ms, ["EndTime"] = ms, ["AccuracyMode"] = "single", ["AccuracyPlusMinus"] = 1 })
    };

    // ---- reading data
    public static double? Num(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<double>(out var d) ? d
        : n is JsonValue w && w.TryGetValue<long>(out var l) ? l
        : n is JsonValue u && u.TryGetValue<int>(out var i) ? i
        : n is JsonValue s && s.TryGetValue<string>(out var t) && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p
        : null;

    public static double Horizon(IReadOnlyDictionary<string, JsonNode> inputs)
    {
        var months = Num(inputs[CuiTypes.PredictionHorizon]);
        if (months is not { } h || h <= 0) throw new CuiDataException("The Prediction Horizon is not a number of months above zero.");
        return h;
    }

    public static double Round(double v, int digits = 4) => Math.Round(v, digits, MidpointRounding.AwayFromZero);
    public static double Sigmoid(double z) => 1 / (1 + Math.Exp(-z));
    public static double Clamp(double v, double lo, double hi) => Math.Min(hi, Math.Max(lo, v));
}
