using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using AIF.Controller;
using AIF.Metadata;
using AIF.Store;

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
        var schema = AimMetadataSchema.Load(Repository.Schemas);

        var result = new Dictionary<string, string>();
        foreach (var file in L3Files())
        {
            var name = Path.GetFileNameWithoutExtension(file);
            try
            {
                var violations = schema.Violations(File.ReadAllText(file));
                result[name] = violations.Count == 0 ? "valid" : string.Join("; ", violations);
            }
            catch (Exception failure)
            {
                result[name] = "could not be validated: " + Short(failure.Message, 160);
            }
        }

        Expected.Match("schema.json", result);
    }

    // Every L3 against its L2 (M3211, the author): what the L2 does not allow.
    [Fact]
    public void Conformance()
    {
        JsonElement? FindL3(string instance)
        {
            var file = Path.Combine(Repository.Amds, instance + ".json");
            return File.Exists(file) ? JsonDocument.Parse(File.ReadAllText(file)).RootElement.Clone() : null;
        }
        var conformance = new L2Conformance(Repository.Schemas, FindL3);

        var result = new Dictionary<string, string>();
        foreach (var file in L3Files())
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var found = conformance.Check(doc.RootElement);
            result[Path.GetFileNameWithoutExtension(file)] = found.Count == 0 ? "conforms" : string.Join("; ", found);
        }

        Expected.Match("conformance.json", result);
    }

    // Rule 5, the combination: PSE's L3 uses SPE where its L2 has ESD and PSI, and
    // conforms because SPE exposes the interface of the two. With SPE's L2 exposing
    // something else - its output a Text Object - the same L3 does not conform.
    [Fact]
    public void Combination()
    {
        JsonElement? FindL3(string instance)
        {
            var file = Path.Combine(Repository.Amds, instance + ".json");
            return File.Exists(file) ? JsonDocument.Parse(File.ReadAllText(file)).RootElement.Clone() : null;
        }
        using var pse = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository.Amds, "1MMC-PSE-V2.5-I01.json")));
        Assert.Empty(new L2Conformance(Repository.Schemas, FindL3).Check(pse.RootElement));

        var copy = Path.Combine(Path.GetTempPath(), "mpai-schemas-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var file in Directory.EnumerateFiles(Repository.Schemas, "*.json", SearchOption.AllDirectories)
                                          .Where(f => Path.GetFileName(Path.GetDirectoryName(f)) == "AIMs"))
            {
                var target = Path.Combine(copy, Path.GetRelativePath(Repository.Schemas, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            var spe = Path.Combine(copy, "MMC", "V2.5", "AIMs", "SpeechPersonalStatusExtraction.json");
            var l2 = JsonNode.Parse(File.ReadAllText(spe))!;
            foreach (var port in l2["ExternalPorts"]!.AsArray())
                if (port!["Direction"]!.GetValue<string>() == "Output") port["DataType"] = "OSD-BTO-V1.5";
            File.WriteAllText(spe, l2.ToJsonString());

            var found = new L2Conformance(copy, FindL3).Check(pse.RootElement);
            Assert.Contains(found, f => f.Contains("1MMC-SPE-V2.5-I01") && f.Contains("combination"));
        }
        finally { Directory.Delete(copy, recursive: true); }
    }

    // Every L3's ResourcePolicies read as the Controller reads them when it builds a
    // Module's AIMs: a number (CPU:Number, GPU:Number) as well as a string, and every
    // Memory value understood. MAD and MPD failed to start in the Service when these
    // became numbers, and loading an L3 alone does not read them.
    [Fact]
    public void ResourcePolicies()
    {
        var problems = new List<string>();
        foreach (var file in L3Files())
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            try
            {
                foreach (var policy in ResourcePolicy.ReadFrom(doc.RootElement))
                    if (policy.Name == "Memory" && ResourcePolicy.MemoryBytes(policy.Minimum) is null)
                        problems.Add($"{Path.GetFileNameWithoutExtension(file)}: Memory minimum '{policy.Minimum}' not understood");
            }
            catch (Exception e) { problems.Add($"{Path.GetFileNameWithoutExtension(file)}: {e.Message}"); }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
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

    // What the Controller builds from every composite L3 that loads: each connection,
    // typed, as "AIM DataType#PortNumber -> AIM DataType#PortNumber". Labels do not
    // appear, since nothing routes by them: renaming a label changes nothing here.
    [Fact]
    public void Connections()
    {
        var store = new AmdStore(Repository.Amds);
        store.Scan();
        var controller = new Controller(store);

        var result = new Dictionary<string, string>();
        foreach (var name in store.GetAimNames())
        {
            DescriptorGraph graph;
            try { graph = controller.RegisterAim(store.FindByAimName(name)!); }
            catch { continue; }                       // L3Load records why
            if (!graph.Root.IsComposite) continue;
            result[name] = string.Join("; ", graph.Connections
                .Select(c => $"{Show(c.Output)} -> {Show(c.Input)}")
                .OrderBy(s => s, StringComparer.Ordinal));
        }

        Expected.Match("connections.json", result);
    }

    private static string Show(Endpoint e) =>
        (e.IsBoundary ? "(boundary)" : e.AimName) + " " + e.DataType + "#" + e.PortNumber;

    // Names never address (M3211 3.4). A Topology label the composite does not
    // declare stops the L3 from loading; and when every Sub-AIM of MAD names its
    // Ports differently, MAD loads into the same connections, since the Controller
    // reads no Sub-AIM's Port names.
    [Fact]
    public void Addressing()
    {
        JsonNode Read(string aim) => JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Amds, aim + ".json")))!;
        var mad = Read("1MMC-MAD-V2.5-I01");
        var baseline = MadConnections(mad);

        var undeclared = mad.DeepClone();
        var end = undeclared["Topology"]!.AsArray().Select(l => l!["Input"]!).First(e => e["AIMName"]!.GetValue<string>() != "");
        end["PortName"] = "NoSuchLabel";
        var failure = Assert.ThrowsAny<Exception>(() => MadConnections(undeclared));
        Assert.Contains("does not declare", failure.Message);

        var renamed = new Dictionary<string, JsonNode>();
        foreach (var sub in mad["SubAIMs"]!.AsArray())
        {
            var name = sub!["Identifier"]!["AIMName"]!.GetValue<string>();
            var l3 = Read(name);
            // A composite's own Port names are labels of its own Topology: renamed
            // there too, consistently. What MAD sees is only that they changed.
            var names = new Dictionary<string, string>();
            foreach (var port in l3["ExternalPorts"]!.AsArray())
            {
                var old = port!["Name"]!.GetValue<string>();
                if (!names.TryGetValue(old, out var fresh)) names[old] = fresh = $"Renamed{names.Count + 1}";
                port["Name"] = fresh;
            }
            foreach (var line in l3["Topology"]?.AsArray() ?? new JsonArray())
                foreach (var side in new[] { "Output", "Input" })
                    if (line![side]!["PortName"]?.GetValue<string>() is { } label && names.TryGetValue(label, out var fresh))
                        line[side]!["PortName"] = fresh;
            renamed[name] = l3;
        }
        Assert.Equal(baseline, MadConnections(renamed));
    }

    // The fields the schema gained in Phase 2 (M3211 3.1): each, with a legal value,
    // adds no violation to an L3; with an illegal value, or where it may not appear,
    // adds one. An L3 carrying all of them loads exactly as it did without them.
    [Fact]
    public void NewFields()
    {
        var schema = AimMetadataSchema.Load(Repository.Schemas);
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
            ("OnDegraded StopAIM",            composite, a => a["OnDegraded"] = "StopAIM",              true),
            ("OnDegraded StopModule",         composite, a => a["OnDegraded"] = "StopModule",           true),
            ("OnDegraded Stop",               composite, a => a["OnDegraded"] = "Stop",                 false),
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

        // None of the new fields changes the connections the Controller builds:
        // MAD with all of them loads into the same connections as MAD without them.
        var all = composite.DeepClone();
        all["Execution"] = "Exchange"; all["OnDegraded"] = "StopModule"; all["RestartLimit"] = 0;
        foreach (var port in all["ExternalPorts"]!.AsArray())
            if (port!["Direction"]!.GetValue<string>() == "Input") { port["Depth"] = 16; port["Overflow"] = "Block"; port["AcceptedTransports"] = new JsonArray("Controller"); }
            else port["Transport"] = "Controller";
        Assert.Equal(MadConnections(composite), MadConnections(all));
    }

    private static HashSet<string> Validate(AimMetadataSchema schema, JsonNode aim) =>
        schema.Violations(aim.ToJsonString()).ToHashSet();

    // The connections the Controller builds for 1MMC-MAD-V2.5-I01 from this text of
    // its L3, the other L3s being those of the repository.
    private static List<string> MadConnections(JsonNode mad) =>
        MadConnections(new Dictionary<string, JsonNode> { ["1MMC-MAD-V2.5-I01"] = mad });

    // The connections the Controller builds for 1MMC-MAD-V2.5-I01 when the L3s named
    // are replaced by the texts given, the others being those of the repository.
    private static List<string> MadConnections(IReadOnlyDictionary<string, JsonNode> replaced)
    {
        var folder = Path.Combine(Path.GetTempPath(), "mpai-l3s-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var file in L3Files()) File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
            foreach (var (name, text) in replaced) File.WriteAllText(Path.Combine(folder, name + ".json"), text.ToJsonString());
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

    internal static string Short(string text, int max)
    {
        var one = text.Replace('\r', ' ').Replace('\n', ' ');
        return one.Length <= max ? one : one[..max] + "...";
    }
}
