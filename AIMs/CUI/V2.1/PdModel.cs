using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using AIF.Controller;

namespace Mpai.Cui;

// THE FINANCIAL MODEL OF THE PREDICTION AIMs: a logistic score of the financial ratios of a company. Each term is a ratio standardised (its distance from the mean in scales, within three) times a weight;
// the terms are also the explainability the descriptors carry (each term's share of the score). MPAI-CUI specifies what the AIMs receive and give, not the model inside: the model is the Implementer's.
//
// Two models exist. The REFERENCE model is built into the AIM: weights set by hand to give a plausible, transparent behaviour, trained on nothing. A TRAINED model is Access data: a file the Controller
// obtains from the URI the AIM's Metadata names (AccessSources), checks against its SHA-256 and hands to the AIM as the setting "FinancialModel"; the AIM reads it, and without it uses the reference
// model. The file is JSON (Format "MPAI-CUI-LOGISTIC-1"): Intercept and, for each Feature, its Name (one of the ratios of ReferenceModels.Ratios), Mean, Scale and Weight; Training says what it was trained on.
public sealed class PdModel
{
    public const string Format = "MPAI-CUI-LOGISTIC-1";
    public const string Setting = "FinancialModel";

    public sealed record Feature(string Name, double Mean, double Scale, double Weight);

    public string Name { get; }
    public double Intercept { get; }
    public IReadOnlyList<Feature> Features { get; }
    public JsonNode? Training { get; }

    public PdModel(string name, double intercept, IReadOnlyList<Feature> features, JsonNode? training = null)
    {
        Name = name;
        Intercept = intercept;
        Features = features;
        Training = training;
    }

    // The built-in model: ratios, mean, scale and weight - a positive weight raises the risk of default. The intercept makes the annual default probability of a company at the means 2%.
    public static PdModel Reference { get; } = new("reference", -3.9,
    [
        new("CurrentRatio", 1.3, 0.6, -0.45), new("QuickRatio", 1.0, 0.5, -0.25), new("EBITDAMargin", 0.10, 0.08, -0.35), new("ROA", 0.04, 0.05, -0.50), new("ROE", 0.08, 0.12, -0.25),
        new("ROS", 0.05, 0.06, -0.20), new("DebtOnSales", 0.8, 0.6, 0.40), new("LongTermLiabilityRatio", 0.25, 0.2, 0.25), new("InterestCoverageRatio", 4, 4, -0.35), new("InterestOnSales", 0.02, 0.02, 0.30)
    ]);

    // The terms of the score of a company: the weight times how far each ratio is from its mean in scales, within three. A ratio the company does not give is left out.
    public List<(string Name, double Term)> Terms(IReadOnlyDictionary<string, double> ratios) =>
        Features.Where(f => ratios.ContainsKey(f.Name)).Select(f => (f.Name, f.Weight * CuiAim.Clamp((ratios[f.Name] - f.Mean) / f.Scale, -3, 3))).ToList();

    // The probability of default within `months`, from the annual one.
    public double Probability(IEnumerable<(string Name, double Term)> terms, double months) =>
        1 - Math.Pow(1 - CuiAim.Sigmoid(Intercept + terms.Sum(t => t.Term)), months / 12.0);

    // ---- the file
    public JsonObject ToJson() => new()
    {
        ["Format"] = Format, ["Name"] = Name, ["Intercept"] = Intercept,
        ["Features"] = new JsonArray(Features.Select(f => (JsonNode)new JsonObject { ["Name"] = f.Name, ["Mean"] = f.Mean, ["Scale"] = f.Scale, ["Weight"] = f.Weight }).ToArray()),
        ["Training"] = Training?.DeepClone()
    };

    // A model from its file. A file that is not one is refused with the reason: the AIM does not run on a model it cannot read.
    public static PdModel Load(string path)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(File.ReadAllText(path)); }
        catch (Exception e) when (e is JsonException or IOException) { throw new CuiDataException($"The financial model {Path.GetFileName(path)} cannot be read: {e.Message}"); }
        if (root is not JsonObject o || (string?)o["Format"] != Format) throw new CuiDataException($"{Path.GetFileName(path)} is not a financial model ({Format}).");
        if (CuiAim.Num(o["Intercept"]) is not { } intercept || !double.IsFinite(intercept)) throw new CuiDataException("The financial model has no intercept.");
        var known = ReferenceModels.RatioNames;
        var features = new List<Feature>();
        foreach (var f in o["Features"] as JsonArray ?? [])
        {
            var name = (string?)f?["Name"] ?? "";
            if (!known.Contains(name)) throw new CuiDataException($"The financial model names a ratio the AIM does not compute: {name}.");
            if (CuiAim.Num(f?["Mean"]) is not { } mean || CuiAim.Num(f?["Scale"]) is not { } scale || CuiAim.Num(f?["Weight"]) is not { } weight || scale <= 0
                || !double.IsFinite(mean) || !double.IsFinite(weight))
                throw new CuiDataException($"The financial model has an unusable feature: {name}.");
            features.Add(new Feature(name, mean, scale, weight));
        }
        if (features.Count == 0) throw new CuiDataException("The financial model has no features.");
        return new PdModel((string?)o["Name"] ?? Path.GetFileNameWithoutExtension(path), intercept, features, o["Training"]?.DeepClone());
    }

    private static readonly Dictionary<string, (DateTime Written, PdModel Model)> loaded = new(StringComparer.Ordinal);

    // The model a setting names, if it names one: read once for as long as the file is unchanged.
    public static PdModel? FromSettings(IReadOnlyDictionary<string, string> settings)
    {
        if (!settings.TryGetValue(Setting, out var path) || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        var written = File.GetLastWriteTimeUtc(path);
        lock (loaded)
        {
            if (loaded.TryGetValue(path, out var kept) && kept.Written == written) return kept.Model;
            var model = Load(path);
            loaded[path] = (written, model);
            return model;
        }
    }
}

// THE MODEL OF AN AIM, KEPT UP TO DATE. It starts with the model the Controller gave it as the setting FinancialModel (or the reference model). When the Controller can say that a model is new
// (MPAI_AIFM_Model_Update; the Metadata the Controller holds states a SHA-256 Access does not hold, for the same URI), the AIM takes it from Access (Access_Version, Access_Path) and swaps it in
// between two computations: nothing computes on half a model, and a model that cannot be read leaves the AIM on the one it had. It also keeps how far the ratios it was given have been from those the
// model was trained on (MPAI_AIFM_Model_Drift): the share, over the last Window ratios, that lie beyond three scales of the mean - the model holds a ratio to three scales, so beyond it the model no
// longer tells companies apart.
public sealed class ModelSource
{
    public const string SourceName = "financial-default-model";
    public const int Window = 200;

    // How long the AIM goes between asking the Controller whether its model is new, on the Controller's time base (tests set it to zero).
    public static TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

    private readonly IAccessReader? access;
    private readonly object gate = new();
    private readonly Queue<bool> seen = new();
    private PdModel? current;
    private string? uri;
    private long version = -1;
    private DateTimeOffset? checkedAt;

    public ModelSource(IReadOnlyDictionary<string, string> settings, IAccessReader? access)
    {
        this.access = access;
        current = PdModel.FromSettings(settings);
        if (access?.SourcesOf(SourceName) is { Count: > 0 } uris) { uri = uris[0]; version = access.Version(uri); }
    }

    // The model now in use: null is the reference model.
    public PdModel? Model { get { lock (gate) return current; } }

    // Between two computations: asks the Controller whether the model is new and, if Access has a new Version of it, reads it.
    public async Task CheckAsync(IAimPorts ports)
    {
        if (access is null || uri is null) return;
        var now = ports.Now;
        if (checkedAt is { } at && now - at < Interval) return;
        checkedAt = now;
        if (await ports.ModelUpdateAsync(uri) != AifError.OK) return;                       // not new, not trusted, not obtainable: the AIM goes on with the one it has
        var v = access.Version(uri);
        if (v == version) return;
        if (access.LocalPath(uri, "", out var path) != AccessOutcome.OK) return;
        try
        {
            var model = PdModel.Load(path);
            lock (gate) { current = model; version = v; seen.Clear(); }
        }
        catch (CuiDataException) { /* a file that is not a model: the AIM keeps the one it has */ }
    }

    // The ratios of a company, taken for what they say of the data the model is given.
    public void Observe(IReadOnlyDictionary<string, double> ratios)
    {
        lock (gate)
        {
            var model = current ?? PdModel.Reference;
            foreach (var f in model.Features)
                if (ratios.TryGetValue(f.Name, out var x))
                {
                    seen.Enqueue(Math.Abs((x - f.Mean) / f.Scale) >= 3);
                    while (seen.Count > Window) seen.Dequeue();
                }
        }
    }

    // How far the inputs have been from those the model was trained on: 0 (none beyond three scales) to 1 (all of them); null while the AIM has been given nothing.
    public double? Drift
    {
        get { lock (gate) return seen.Count == 0 ? null : seen.Count(beyond => beyond) / (double)seen.Count; }
    }
}
