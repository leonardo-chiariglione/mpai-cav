using System.Security.Cryptography;
using System.Text.Json.Nodes;

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

    // Where the CAV stands: for each subsystem, its L3, whether it loads, and
    // whether the exchange executor could run it.
    [Fact]
    public void CavStatus()
    {
        var subsystems = new (string Subsystem, string Instance)[]
        {
            ("Human-CAV Interaction (HCI)",       "1MMC-HCI-V2.5-I01"),
            ("Environment Sensing (ESS)",         "1CAV-ESS-V1.1-I01"),
            ("Autonomous Motion (AMS)",           "1CAV-AMS-V1.1-I01"),
            ("Motion Actuation (MAS)",            "1CAV-MAS-V1.1-I01")
        };

        var store = new AmdStore(Repository.Amds);
        store.Scan();
        var controller = new Controller(store);

        var result = new Dictionary<string, string>();
        foreach (var (subsystem, instance) in subsystems)
        {
            var file = Path.Combine(Repository.Amds, instance + ".json");
            if (!File.Exists(file)) { result[subsystem] = $"{instance}: no L3"; continue; }

            string loads;
            try
            {
                controller.RegisterAim(store.FindByAimName(instance) ?? throw new InvalidOperationException("not found"));
                loads = "loads";
            }
            catch { loads = "does not load"; }

            var loop = FindLoop(Edges(MetadataTests.TryParse(file)!)) is not null;
            result[subsystem] = $"{instance}: {loads}; " +
                (loop ? "its Topology has a loop, so the exchange executor cannot run it" : "no loop") +
                "; not run";
        }

        Expected.Match("cav-status.json", result);
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
    // In a group of its own: hashing 7.2 GB of models takes most of a minute, and the
    // Fast group is meant to take seconds.
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
                             .Concat(Directory.EnumerateFiles(Path.Combine(Repository.Root, "UAs", "Orchestration"), "MPAI-MAS.orch"))
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
