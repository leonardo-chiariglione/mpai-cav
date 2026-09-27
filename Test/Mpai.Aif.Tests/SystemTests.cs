using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using AIF.Controller;
using AIF.Store;
using Mpai.Wdl;

namespace Mpai.Aif.Tests;

// The Loops and CAV status tests of M3207 3.3. They need the repository only,
// and block a step.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class SystemTests
{
    // Every Topology with a loop: a Module the exchange executor, which runs a
    // Module's AIMs once each in the order of its Topology, cannot run. In an L3 a
    // connection runs from its Output end to its Input end.
    [Fact]
    public void Loops()
    {
        var result = new Dictionary<string, string>();
        foreach (var file in MetadataTests.L3Files())
        {
            var l3 = MetadataTests.TryParse(file);
            var name = l3?["Identifier"]?["AIMName"]?.GetValue<string>();
            if (l3 is null || name is null) continue;

            var loop = FindLoop(Edges(l3));
            if (loop is not null) result[name] = string.Join(" -> ", loop);
        }

        Expected.Match("loops.json", result);
    }

    // Where the CAV stands, for each subsystem and for the whole CAV: whether its
    // L2 is in this repository, which of its Sub-AIMs have any code, its L3,
    // whether it loads, whether the exchange executor could run it - and the Ports
    // it declares that no code reads or writes: declared, not implemented.
    [Fact]
    public void CavStatus()
    {
        var subsystems = new (string Subsystem, string Standard, string Instance)[]
        {
            ("CAV (Connected Autonomous Operation)", "CAV-CAO-V1.1", "1CAV-CAO-V1.1-I01"),
            ("Human-CAV Interaction (HCI)",          "MMC-HCI-V2.5", "1MMC-HCI-V2.5-I01"),
            ("Environment Sensing (ESS)",            "CAV-ESS-V1.1", "1CAV-ESS-V1.1-I01"),
            ("Autonomous Motion (AMS)",              "CAV-AMS-V1.1", "1CAV-AMS-V1.1-I01"),
            ("Motion Actuation (MAS)",               "CAV-MAS-V1.1", "1CAV-MAS-V1.1-I01")
        };

        var l2s = Directory.EnumerateFiles(Repository.Schemas, "*.json", SearchOption.AllDirectories)
                           .Where(f => Path.GetFileName(Path.GetDirectoryName(f)) == "AIMs")
                           .Select(MetadataTests.TryParse)
                           .Where(n => n?["Identifier"]?["AIMName"] is not null)
                           .ToDictionary(n => n!["Identifier"]!["AIMName"]!.GetValue<string>(), n => n!);
        // An AIM has code if a plugin or a provider creates it (CodeIndex).
        var code = new CodeIndex(Repository.Root);

        var store = new AmdStore(Repository.Amds);
        store.Scan();
        var controller = new Controller(store);

        var result = new Dictionary<string, string>();
        foreach (var (subsystem, standard, instance) in subsystems)
        {
            var parts = new List<string>();

            // The L2, and which of its Sub-AIMs have code.
            if (l2s.TryGetValue(standard, out var l2))
            {
                var subs = (l2["SubAIMs"]?.AsArray() ?? new JsonArray())
                           .Select(s => s?["Identifier"]?["AIMName"]?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList();
                var withCode = subs.Where(code.HasCode).ToList();
                parts.Add($"L2 {standard} present; code for {withCode.Count} of {subs.Count} Sub-AIMs" +
                          (withCode.Count > 0 ? $" ({string.Join(", ", withCode)})" : ""));
            }
            else parts.Add($"L2 {standard} not in this repository");

            var file = Path.Combine(Repository.Amds, instance + ".json");
            if (!File.Exists(file)) parts.Add($"no L3 {instance}");
            else
            {
                try
                {
                    controller.RegisterAim(store.FindByAimName(instance) ?? throw new InvalidOperationException("not found"));
                    parts.Add($"L3 {instance} loads");
                }
                catch { parts.Add($"L3 {instance} does not load"); }

                parts.Add(FindLoop(Edges(MetadataTests.TryParse(file)!)) is not null
                    ? "its Topology has a loop, so the exchange executor cannot run it" : "no loop");

                var missing = Unimplemented(instance, code, store, l2s).Distinct().ToList();
                parts.Add(missing.Count == 0
                    ? "every declared Port of an AIM with code is implemented"
                    : "declared, not implemented: " + string.Join(", ", missing));
            }

            parts.Add("not run");
            result[subsystem] = string.Join("; ", parts);
        }

        Expected.Match("cav-status.json", result);
    }

    // THE PORTS NO CODE READS OR WRITES, in an L3 and all it contains: a Port of a
    // basic AIM whose code never names its Data Type, and a Port of a composite that
    // no line of its Topology connects. (A basic AIM without code is counted above;
    // its Ports are not listed one by one. Optional Ports of an L2 that the L3 leaves
    // out are not listed: what the Standard offers beyond what is implemented is for later.) A Port counts only where the
    // composite containing the AIM connects it: the same AIM serves several
    // composites - a LiDAR Object Acquisition captures the environment in the ESS
    // and the cabin in HCI - and what one of them does not connect is not its concern.
    private static IEnumerable<string> Unimplemented(string instance, CodeIndex code, AmdStore store, IReadOnlyDictionary<string, JsonNode> l2s,
                                                     Func<string, IReadOnlyList<string>, int, bool>? connected = null)
    {
        connected ??= (_, _, _) => true;
        if (store.FindByAimName(instance) is not { } id) yield break;
        var amd = store.GetAMD(id).RootElement;
        var type = CodeIndex.TypeOf(instance);
        var ports = amd.GetProperty("ExternalPorts").EnumerateArray().Select(p => (
            Direction: p.GetProperty("Direction").GetString() ?? "",
            Types: p.GetProperty("DataType").ValueKind == JsonValueKind.Array
                ? p.GetProperty("DataType").EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                : [p.GetProperty("DataType").GetString() ?? ""],
            Number: p.TryGetProperty("PortNumber", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 1)).ToList();
        var subs = amd.TryGetProperty("SubAIMs", out var s) && s.ValueKind == JsonValueKind.Array
            ? s.EnumerateArray().Select(x => x.GetProperty("Identifier").GetProperty("AIMName").GetString() ?? "").ToList()
            : [];

        if (subs.Count == 0)
        {
            if (code.Handles(type) is not { } handled) yield break;
            foreach (var p in ports.Where(p => connected(p.Direction, p.Types, p.Number) && !p.Types.Any(handled.Contains)))
                yield return $"{type} {p.Direction} {p.Types[0]}";
            yield break;
        }

        var ends = amd.GetProperty("Topology").EnumerateArray()
                      .SelectMany(l => new[] { (Side: "Output", End: l.GetProperty("Output")), (Side: "Input", End: l.GetProperty("Input")) })
                      .Where(e => (e.End.GetProperty("AIMName").GetString() ?? "") == "")
                      .Select(e => (e.Side, DataType: e.End.GetProperty("DataType").GetString() ?? "",
                                    Number: e.End.TryGetProperty("PortNumber", out var pn) && pn.ValueKind == JsonValueKind.Number ? pn.GetInt32() : 1))
                      .ToList();
        // An Input Port is where a line's Output end is the boundary, and the reverse.
        foreach (var p in ports.Where(p => connected(p.Direction, p.Types, p.Number) &&
                                           !ends.Any(e => e.Side == (p.Direction == "Input" ? "Output" : "Input") &&
                                                          p.Types.Contains(e.DataType) && e.Number == p.Number)))
            yield return $"{type} {p.Direction} {p.Types[0]} (not connected)";

        // What this composite connects of each Sub-AIM: a line's Output end is the
        // Sub-AIM's Output Port, its Input end an Input Port; no Port Number cited, any.
        var subEnds = amd.GetProperty("Topology").EnumerateArray()
                         .SelectMany(l => new[] { (Direction: "Output", End: l.GetProperty("Output")), (Direction: "Input", End: l.GetProperty("Input")) })
                         .Select(e => (Aim: e.End.GetProperty("AIMName").GetString() ?? "", e.Direction,
                                       DataType: e.End.GetProperty("DataType").GetString() ?? "",
                                       Number: e.End.TryGetProperty("PortNumber", out var pn) && pn.ValueKind == JsonValueKind.Number ? pn.GetInt32() : (int?)null))
                         .ToList();
        foreach (var sub in subs)
        {
            bool Connected(string direction, IReadOnlyList<string> types, int number) =>
                subEnds.Any(e => e.Aim == sub && e.Direction == direction && types.Contains(e.DataType) && (e.Number is null || e.Number == number));
            foreach (var m in Unimplemented(sub, code, store, l2s, Connected))
                yield return m;
        }
    }

    // WHICH CODE IMPLEMENTS WHICH AIM, AND WHICH DATA TYPES THAT CODE NAMES. An AIM
    // has code where a plugin declares it (AimName => "CAE-AII-V2.5") or a provider
    // creates it ("CVE-VII-V1.0" => new ViiAimProcessor(...), or Fed => new
    // FullEnvironmentDescription(...) with Fed = "1CAV-FED-V1.1-I01"). The Data Types
    // it reads or writes are those its classes name - a literal, or a constant
    // (AmsTypes.Hci = "CAV-AHM-V1.1"). A mention in a comment counts too: this is a
    // survey, not a proof.
    private sealed class CodeIndex
    {
        private static readonly Regex Aim = new(@"^\d?[A-Z]{3}-[A-Z0-9]{3}-V\d+\.\d+(-I\d+)?$");
        private readonly Dictionary<string, HashSet<string>> classesOf = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> classFiles = new(StringComparer.Ordinal);       // a class name may be defined in several files
        private readonly Dictionary<string, string> constant = new(StringComparer.Ordinal);                // the first of a name: a provider's key
        private readonly Dictionary<string, HashSet<string>> constants = new(StringComparer.Ordinal);     // every value of a name
        private readonly Dictionary<string, string> qualified = new(StringComparer.Ordinal);              // Class.Name -> its value
        private readonly Dictionary<string, string> text = new(StringComparer.Ordinal);

        public static string TypeOf(string aim) => Regex.Replace(Regex.Replace(aim, @"^\d+", ""), @"-I\d+$", "");

        public CodeIndex(string root)
        {
            var sep = Path.DirectorySeparatorChar;
            foreach (var dir in new[] { "AIMs", "MPAIApps", "CAV" }.Select(d => Path.Combine(root, d)).Where(Directory.Exists))
                foreach (var f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                                           .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}")))
                    text[f] = File.ReadAllText(f);
            foreach (var (f, t) in text)
            {
                var declared = Regex.Matches(t, @"\b(?:class|record)\s+(\w+)").Select(m => (m.Index, Name: m.Groups[1].Value)).ToList();
                foreach (var c in declared) { if (!classFiles.TryGetValue(c.Name, out var fs)) classFiles[c.Name] = fs = new List<string>(); fs.Add(f); }
                foreach (Match m in Regex.Matches(t, @"\b(\w+)\s*=\s*""(\d?[A-Z]{3}-[A-Z0-9]{3}-V\d+\.\d+(?:-I\d+)?)"""))
                {
                    var (name, value) = (m.Groups[1].Value, m.Groups[2].Value);
                    constant.TryAdd(name, value);
                    if (!constants.TryGetValue(name, out var all)) constants[name] = all = new HashSet<string>(StringComparer.Ordinal);
                    all.Add(value);
                    if (declared.LastOrDefault(c => c.Index < m.Index).Name is { } owner) qualified.TryAdd(owner + "." + name, value);
                }
            }
            foreach (var (_, t) in text)
            {
                foreach (Match m in Regex.Matches(t, @"(?:""([^""]+)""|\b(\w+))\s*=>\s*new\s+(\w+)\s*\("))
                {
                    var key = m.Groups[1].Success ? m.Groups[1].Value : constant.GetValueOrDefault(m.Groups[2].Value);
                    if (key is not null && Aim.IsMatch(key)) Add(TypeOf(key), m.Groups[3].Value);
                }
                if (Regex.Match(t, @"AimName\s*=>\s*""([^""]+)""") is { Success: true } plugin)
                    foreach (Match m in Regex.Matches(t, @"new\s+(\w+)\s*\(")) Add(TypeOf(plugin.Groups[1].Value), m.Groups[1].Value);
            }
        }

        private void Add(string aimType, string cls)
        {
            if (!classesOf.TryGetValue(aimType, out var set)) classesOf[aimType] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(cls);
        }

        public bool HasCode(string aimType) => classesOf.ContainsKey(aimType);

        // An AIM Instance ("1CAV-FED-V1.1-I01"), not a Data Type ("CAV-INT-V1.1").
        private static bool IsInstance(string value) => Regex.IsMatch(value, @"-I\d+$");

        // The Data Types the code of an AIM names; null when it has no code.
        public HashSet<string>? Handles(string aimType)
        {
            if (!classesOf.TryGetValue(aimType, out var classes)) return null;
            var types = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in classes.SelectMany(c => classFiles.GetValueOrDefault(c) ?? []).Distinct())
            {
                var t = text[f];
                foreach (Match m in Regex.Matches(t, @"""([A-Z]{3}-[A-Z0-9]{3}-V\d+\.\d+|boolean|integer|number|string|uint8\[\])""")) types.Add(m.Groups[1].Value);
                // Class.Name: that class's constant; a bare Name: any constant of that name.
                foreach (Match m in Regex.Matches(t, @"\b(\w+)\.(\w+)\b"))
                    if (qualified.TryGetValue(m.Groups[1].Value + "." + m.Groups[2].Value, out var q)) { if (!IsInstance(q)) types.Add(q); }
                    else if (constants.TryGetValue(m.Groups[2].Value, out var named))
                        foreach (var v in named.Where(v => !IsInstance(v))) types.Add(v);
                foreach (Match m in Regex.Matches(t, @"(?<![\w.])(\w+)\b(?!\.)"))
                    if (constants.TryGetValue(m.Groups[1].Value, out var all))
                        foreach (var v in all.Where(v => !IsInstance(v))) types.Add(v);
            }
            return types;
        }
    }

    // ---------------------------------------------------------------------

    internal static Dictionary<string, HashSet<string>> Edges(JsonNode l3)
    {
        var edges = new Dictionary<string, HashSet<string>>();
        foreach (var connection in l3["Topology"]?.AsArray() ?? new JsonArray())
        {
            var from = connection?["Output"]?["AIMName"]?.GetValue<string>() ?? "";
            var to   = connection?["Input"]?["AIMName"]?.GetValue<string>() ?? "";
            if (from.Length == 0 || to.Length == 0 || from == to) continue;   // the boundary, or within one AIM
            if (!edges.TryGetValue(from, out var set)) edges[from] = set = new HashSet<string>();
            set.Add(to);
        }
        return edges;
    }

    // The first loop found, as the AIMs along it with the first repeated at the end.
    internal static List<string>? FindLoop(Dictionary<string, HashSet<string>> edges)
    {
        var state = new Dictionary<string, int>();   // 1 on the path, 2 done
        var path = new List<string>();

        List<string>? Visit(string node)
        {
            state[node] = 1; path.Add(node);
            foreach (var next in (edges.TryGetValue(node, out var n) ? n : []).OrderBy(x => x, StringComparer.Ordinal))
            {
                if (state.GetValueOrDefault(next) == 1)
                    return path.Skip(path.IndexOf(next)).Append(next).ToList();
                if (state.GetValueOrDefault(next) == 0 && Visit(next) is { } found) return found;
            }
            path.RemoveAt(path.Count - 1); state[node] = 2;
            return null;
        }

        foreach (var start in edges.Keys.OrderBy(x => x, StringComparer.Ordinal))
            if (state.GetValueOrDefault(start) == 0 && Visit(start) is { } loop) return loop;
        return null;
    }
}

// The Model hashes test of M3207 3.3. In a group of its own: hashing 7.2 GB of
// models takes most of a minute, and the Fast group is meant to take seconds.
// It blocks a step, and is skipped where the models are absent.
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
public class ModelTests
{
    // Every SHA256 entry of AIMs/aim-settings.json against the file its setting names.
    [SkippableFact]
    public void ModelHashes()
    {
        var settingsFile = Path.Combine(Repository.Root, "AIMs", "aim-settings.json");
        Skip.IfNot(Directory.Exists(Path.Combine(Repository.Root, "Models")),
            "Models is absent: the model files are obtained separately (docs/MAS-App-Models.md).");

        var settings = JsonNode.Parse(File.ReadAllText(settingsFile))!.AsObject();
        var mismatches = new List<string>();
        var checkedCount = 0;

        foreach (var (aim, node) in settings)
        {
            if (node is not JsonObject entries) continue;
            foreach (var (key, value) in entries)
            {
                if (!key.StartsWith("SHA256:", StringComparison.Ordinal)) continue;
                var setting = key["SHA256:".Length..];
                var path = entries[setting]?.GetValue<string>();
                if (path is null) { mismatches.Add($"{aim} {setting}: no such setting"); continue; }

                var full = Path.IsPathRooted(path) ? path : Path.Combine(Repository.Root, path);
                if (!File.Exists(full)) { mismatches.Add($"{aim} {setting}: {path} missing"); continue; }

                using var stream = File.OpenRead(full);
                var actual = Convert.ToHexString(SHA256.HashData(stream));
                checkedCount++;
                if (!string.Equals(actual, value?.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
                    mismatches.Add($"{aim} {setting}: {path} has {actual}");
            }
        }

        Assert.True(mismatches.Count == 0,
            $"{mismatches.Count} of {checkedCount + mismatches.Count} model hashes do not match:\n  " + string.Join("\n  ", mismatches));
    }
}

// The Workflows test of M3207 3.3: it concerns MAS-App, and informs.
[Trait("Group", "Fast")]
[Trait("Blocks", "No")]
public class WorkflowTests
{
    // For each App: the Module its workflow names exists, and declares every Port
    // the workflow offers to (an Input) or asks of (an Output).
    [Fact]
    public void Workflows()
    {
        var files = Directory.EnumerateFiles(Path.Combine(Repository.Root, "Apps"), "*.orch", SearchOption.AllDirectories)
                             .Concat(Directory.EnumerateFiles(Path.Combine(Repository.Root, "UserAgent", "Orchestration"), "MPAI-MAS.orch"))
                             .OrderBy(f => f, StringComparer.Ordinal);

        var result = new Dictionary<string, string>();
        foreach (var file in files)
        {
            var key = Path.GetRelativePath(Repository.Root, file).Replace('\\', '/');
            Workflow workflow;
            try { workflow = new WorkflowReader().Read(File.ReadAllText(file)); }
            catch (Exception failure) { result[key] = "does not read: " + MetadataTests.Short(failure.Message, 120); continue; }

            var module = workflow.Modules.FirstOrDefault();
            var l3File = module is null ? null : Path.Combine(Repository.Amds, module + ".json");
            if (l3File is null || !File.Exists(l3File)) { result[key] = $"Module {module ?? "(none)"}: no L3"; continue; }

            var ports = (MetadataTests.TryParse(l3File)?["ExternalPorts"]?.AsArray() ?? new JsonArray())
                        .Select(p => (Direction: p?["Direction"]?.GetValue<string>() ?? "",
                                      Types: p?["DataType"] is JsonArray set ? set.Select(t => t!.GetValue<string>()).ToArray()
                                                                            : new[] { p?["DataType"]?.GetValue<string>() ?? "" },
                                      Number: p?["PortNumber"]?.GetValue<int>() ?? 1))
                        .ToList();

            var missing = new SortedSet<string>(StringComparer.Ordinal);
            var requests = Requests(workflow.OnStart.Concat(workflow.OnStop)).ToList();
            foreach (var (direction, port) in requests)
                if (!ports.Any(p => p.Direction == direction && p.Number == port.PortNumber && p.Types.Contains(port.DataType)))
                    missing.Add($"{direction} {port.DataType}:{port.PortNumber}");

            // The number of requests checked is recorded too, so that a reading that
            // found none cannot pass for one that found everything declared.
            result[key] = $"Module {module}: {requests.Count(r => r.Direction == "Input")} offers, " +
                          $"{requests.Count(r => r.Direction == "Output")} asks; " +
                          (missing.Count == 0 ? "every Port declared" : "not declared: " + string.Join(", ", missing));
        }

        Expected.Match("workflows.json", result);
    }

    private static IEnumerable<(string Direction, PortRef Port)> Requests(IEnumerable<Step> steps)
    {
        foreach (var step in steps)
        {
            if (step.Kind == StepKind.Take && step.Port is { } offered) yield return ("Input", offered);
            if (step.Kind == StepKind.Give) foreach (var asked in step.Ports) yield return ("Output", asked);
            foreach (var inner in Requests(step.Body.Concat(step.Else).Concat(step.Alternatives.Where(a => a != step))))
                yield return inner;
        }
    }
}
