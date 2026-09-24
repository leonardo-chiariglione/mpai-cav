using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using AIF.Controller;
using AIF.Store;
using Json.Schema;

namespace Mpai.Aif.Tests;

// The Metadata tests of M3207 3.3. They need the repository only, and block a step.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class MetadataTests
{
    // Every L3, loaded through the Controller as the Service loads it.
    [Fact]
    public void L3Load()
    {
        var store = new AmdStore(Repository.Amds);
        store.Scan();
        var controller = new Controller(store);

        var result = new Dictionary<string, string>();
        foreach (var name in store.GetAimNames())
        {
            try
            {
                var graph = controller.RegisterAim(store.FindByAimName(name)
                                                   ?? throw new InvalidOperationException("not found"));
                result[name] = graph.Root.IsComposite ? "loads (composite)" : "loads";
            }
            catch (Exception failure)
            {
                result[name] = "fails: " + Short(failure.Message, 160);
            }
        }

        Expected.Match("l3-load.json", result);
    }

    // Every L3 against the AIM Metadata schema of AIF V3.0, references resolved
    // from the local schemas folder, never from the network.
    [Fact]
    public void Schema()
    {
        var schema = LoadSchemas(Path.Combine(Repository.Schemas, "AIF", "V3.0", "data", "AIMMetadata.json"));

        var result = new Dictionary<string, string>();
        foreach (var file in L3Files())
        {
            var name = Path.GetFileNameWithoutExtension(file);
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var evaluation = schema.Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical });
                result[name] = evaluation.IsValid ? "valid" : string.Join("; ", Violations(evaluation));
            }
            catch (Exception failure)
            {
                result[name] = "could not be validated: " + Short(failure.Message, 160);
            }
        }

        Expected.Match("schema.json", result);
    }

    // For every L3 with an L2, the input Ports on which they disagree about being optional.
    [Fact]
    public void OptionalAgreement()
    {
        var l2 = new Dictionary<string, JsonNode>();
        foreach (var file in Directory.EnumerateFiles(Repository.Schemas, "*.json", SearchOption.AllDirectories)
                                      .Where(f => Path.GetFileName(Path.GetDirectoryName(f)) == "AIMs"))
        {
            var node = TryParse(file);
            if (node?["Identifier"]?["AIMName"]?.GetValue<string>() is { } aim) l2[aim] = node;
        }

        var result = new Dictionary<string, string>();
        foreach (var file in L3Files())
        {
            var l3 = TryParse(file);
            var instance = l3?["Identifier"]?["AIMName"]?.GetValue<string>();
            if (l3 is null || instance is null) continue;

            var standard = Regex.Replace(Regex.Replace(instance, @"^\d+", ""), @"-I\d+$", "");
            if (!l2.TryGetValue(standard, out var type)) continue;

            var inL2 = Inputs(type);
            var differences = new List<string>();
            foreach (var (key, optional) in Inputs(l3))
                if (inL2.TryGetValue(key, out var l2Optional) && l2Optional != optional)
                    differences.Add($"{key.Name}: L2 {(l2Optional ? "optional" : "required")}, L3 {(optional ? "optional" : "required")}");

            if (differences.Count > 0) result[instance] = string.Join("; ", differences);
        }

        Expected.Match("optional.json", result);
    }

    // The fields the schema gained in Phase 2 (M3211 3.1): each, with a legal value,
    // adds no violation to an L3; with an illegal value, or where it may not appear,
    // adds one. An L3 carrying all of them loads exactly as it did without them.
    [Fact]
    public void NewFields()
    {
        var schema = LoadSchemas(Path.Combine(Repository.Schemas, "AIF", "V3.0", "data", "AIMMetadata.json"));
        var composite = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Amds, "1MMC-MAD-V2.5-I01.json")))!;
        var basic     = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Amds, "1MMC-ASR-V2.5-I01.json")))!;

        JsonNode Input(JsonNode aim)  => aim["ExternalPorts"]!.AsArray().First(p => p!["Direction"]!.GetValue<string>() == "Input")!;
        JsonNode Output(JsonNode aim) => aim["ExternalPorts"]!.AsArray().First(p => p!["Direction"]!.GetValue<string>() == "Output")!;
        JsonNode End(JsonNode aim)    => aim["Topology"]!.AsArray()[0]!["Input"]!;

        var cases = new (string Name, JsonNode Base, Action<JsonNode> Change, bool Legal)[]
        {
            ("Execution Continuous",          composite, a => a["Execution"] = "Continuous",            true),
            ("Execution Sometimes",           composite, a => a["Execution"] = "Sometimes",             false),
            ("Execution on a basic AIM",      basic,     a => a["Execution"] = "Exchange",              false),
            ("OnDegraded Continue",           composite, a => a["OnDegraded"] = "Continue",             true),
            ("OnDegraded Maybe",              composite, a => a["OnDegraded"] = "Maybe",                false),
            ("RestartLimit 2",                basic,     a => a["RestartLimit"] = 2,                    true),
            ("RestartLimit -1",               basic,     a => a["RestartLimit"] = -1,                   false),
            ("Period 33.3",                   basic,     a => a["Period"] = 33.3,                       true),
            ("Period 0",                      basic,     a => a["Period"] = 0,                          false),
            ("Deadline 50",                   basic,     a => a["Deadline"] = 50,                       true),
            ("Deadline -5",                   basic,     a => a["Deadline"] = -5,                       false),
            ("Depth 16",                      basic,     a => Input(a)["Depth"] = 16,                   true),
            ("Depth 0",                       basic,     a => Input(a)["Depth"] = 0,                    false),
            ("Depth on an Output Port",       basic,     a => Output(a)["Depth"] = 16,                  false),
            ("Overflow DropOldest",           basic,     a => Input(a)["Overflow"] = "DropOldest",      true),
            ("Overflow Drop",                 basic,     a => Input(a)["Overflow"] = "Drop",            false),
            ("MaxAge 200",                    basic,     a => Input(a)["MaxAge"] = 200,                 true),
            ("MaxAge 0",                      basic,     a => Input(a)["MaxAge"] = 0,                   false),
            ("Transport InProcess",           basic,     a => Output(a)["Transport"] = "InProcess",     true),
            ("Transport Carrier",             basic,     a => Output(a)["Transport"] = "Carrier",       false),
            ("Transport on an Input Port",    basic,     a => Input(a)["Transport"] = "InProcess",      false),
            ("AcceptedTransports",            basic,     a => Input(a)["AcceptedTransports"] = new JsonArray("InProcess", "Controller"), true),
            ("AcceptedTransports Pigeon",     basic,     a => Input(a)["AcceptedTransports"] = new JsonArray("Pigeon"), false),
            ("AcceptedTransports on Output",  basic,     a => Output(a)["AcceptedTransports"] = new JsonArray("InProcess"), false),
            ("Input group 1",                 composite, a => Input(a)["Input"] = 1,                    true),
            ("Input group on an Output Port", composite, a => Output(a)["Input"] = 1,                   false),
            ("Output group 0",                composite, a => Input(a)["Output"] = 0,                   false),
            ("DataType boolean",              basic,     a => Output(a)["DataType"] = "boolean",        true),
            ("DataType uint8[]",              basic,     a => Output(a)["DataType"] = "uint8[]",        true),
            ("DataType Boolean",              basic,     a => Output(a)["DataType"] = "Boolean",        false),
            ("Topology end by DataType",      composite, a => { var e = End(a).AsObject(); e.Remove("PortName"); e["DataType"] = "OSD-BSO-V1.5"; }, true),
            ("Topology end with neither",     composite, a => { var e = End(a).AsObject(); e.Remove("PortName"); e.Remove("PortNumber"); }, false),
        };

        var wrong = new List<string>();
        foreach (var (name, source, change, legal) in cases)
        {
            var before = Validate(schema, source);
            var changed = source.DeepClone();
            change(changed);
            var added = Validate(schema, changed).Except(before).ToList();
            if (legal && added.Count > 0)   wrong.Add($"{name}: refused ({string.Join("; ", added)})");
            if (!legal && added.Count == 0) wrong.Add($"{name}: accepted");
        }
        Assert.True(wrong.Count == 0, "The schema judged these wrongly:\n" + string.Join("\n", wrong));

        // The Controller reads the new fields and does not yet act on them: MAD with
        // all of them loads into the same connections as MAD without them.
        var all = composite.DeepClone();
        all["Execution"] = "Exchange"; all["OnDegraded"] = "Stop"; all["RestartLimit"] = 0;
        foreach (var port in all["ExternalPorts"]!.AsArray())
            if (port!["Direction"]!.GetValue<string>() == "Input") { port["Depth"] = 16; port["Overflow"] = "Block"; port["AcceptedTransports"] = new JsonArray("Controller"); }
            else port["Transport"] = "Controller";
        Assert.Equal(Connections(composite), Connections(all));
    }

    private static HashSet<string> Validate(JsonSchema schema, JsonNode aim)
    {
        using var doc = JsonDocument.Parse(aim.ToJsonString());
        var evaluation = schema.Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical });
        return evaluation.IsValid ? new HashSet<string>() : Violations(evaluation).ToHashSet();
    }

    // The connections the Controller builds for 1MMC-MAD-V2.5-I01 from this text of
    // its L3, the other L3s being those of the repository.
    private static List<string> Connections(JsonNode mad)
    {
        var folder = Path.Combine(Path.GetTempPath(), "mpai-newfields-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var file in L3Files()) File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
            File.WriteAllText(Path.Combine(folder, "1MMC-MAD-V2.5-I01.json"), mad.ToJsonString());
            var store = new AmdStore(folder);
            store.Scan();
            var graph = new Controller(store).RegisterAim(store.FindByAimName("1MMC-MAD-V2.5-I01")!);
            return graph.Connections.Select(c => $"{c.Output} -> {c.Input}").OrderBy(s => s, StringComparer.Ordinal).ToList();
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    // ---------------------------------------------------------------------

    internal static IEnumerable<string> L3Files() =>
        Directory.EnumerateFiles(Repository.Amds, "*.json").OrderBy(f => f, StringComparer.Ordinal);

    internal static JsonNode? TryParse(string file)
    {
        try { return JsonNode.Parse(File.ReadAllText(file)); } catch { return null; }
    }

    private static Dictionary<(string DataType, string Name), bool> Inputs(JsonNode aim)
    {
        var inputs = new Dictionary<(string, string), bool>();
        foreach (var port in aim["ExternalPorts"]?.AsArray() ?? new JsonArray())
        {
            if (port?["Direction"]?.GetValue<string>() != "Input") continue;
            var dataType = port["DataType"] is JsonArray set ? string.Join("|", set.Select(t => t?.GetValue<string>())) : port["DataType"]?.GetValue<string>() ?? "";
            var name = port["Name"]?.GetValue<string>() ?? "";
            inputs[(dataType, name)] = port["IsOptional"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        }
        return inputs;
    }

    private static readonly object Registering = new();
    private static Dictionary<string, JsonSchema>? loaded;

    // Registers every schema of the repository under its $id, so that the
    // https://schemas.mpai.community/... references resolve locally. Each file is
    // built once: building a schema registers its dynamic anchors, and a second
    // build of the same file collides with the first.
    private static JsonSchema LoadSchemas(string main)
    {
        lock (Registering)
        {
            if (loaded is null)
            {
                loaded = new Dictionary<string, JsonSchema>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.EnumerateFiles(Repository.Schemas, "*.json", SearchOption.AllDirectories))
                {
                    try
                    {
                        var s = JsonSchema.FromFile(file);
                        loaded[Path.GetFullPath(file)] = s;
                        if (s.BaseUri is { } id) SchemaRegistry.Global.Register(id, s);
                    }
                    catch { /* a file that is not a schema, or not valid: the test of that schema will say so */ }
                }
            }
            return loaded.TryGetValue(Path.GetFullPath(main), out var schema)
                ? schema
                : throw new InvalidOperationException("The AIM Metadata schema could not be built: " + main);
        }
    }

    // Each violation once, as "location: what is wrong". Where anyOf or oneOf fails,
    // the value matched none of its alternatives, and that is what is reported - not
    // the failure of every alternative, which would turn one fault into a dozen.
    // Elsewhere only the innermost failures are reported, not the "some properties
    // did not match" of each enclosing object.
    private static IEnumerable<string> Violations(EvaluationResults results)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        Walk(results, found);
        return found;
    }

    private static readonly HashSet<string> Summarising = new(StringComparer.Ordinal)
    {
        "properties", "patternProperties", "items", "prefixItems", "allOf", "$ref", "$dynamicRef",
        "dependentSchemas", "then", "else", "contains", "unevaluatedProperties", "unevaluatedItems"
    };

    private static void Walk(EvaluationResults node, SortedSet<string> found)
    {
        if (node.IsValid) return;
        var where = node.InstanceLocation.ToString() is { Length: > 0 } loc ? loc : "/";
        var keyword = node.EvaluationPath.ToString().Split('/').LastOrDefault() ?? "";

        var alternatives = keyword is "anyOf" or "oneOf" ? keyword
            : node.Errors?.Keys.FirstOrDefault(k => k is "anyOf" or "oneOf");
        if (alternatives is not null)
        {
            found.Add($"{where}: matches none of the alternatives ({alternatives})");
            return;
        }

        // A keyword that only summarises its subschemas ("some properties did not
        // match") says nothing its children do not say better; any other keyword
        // (required, type, pattern, const, enum, a false schema...) is a violation.
        var failing = (node.Details ?? []).Where(d => !d.IsValid).ToList();
        if (node.Errors is { Count: > 0 })
            foreach (var e in node.Errors)
                if (!Summarising.Contains(e.Key) || failing.Count == 0)
                    found.Add($"{where}: {e.Key} {Short(e.Value, 120)}");

        foreach (var child in failing) Walk(child, found);
    }

    internal static string Short(string text, int max)
    {
        var one = text.Replace('\r', ' ').Replace('\n', ' ');
        return one.Length <= max ? one : one[..max] + "...";
    }
}
