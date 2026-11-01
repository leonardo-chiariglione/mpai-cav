using System.Text.Json;
using System.Text.RegularExpressions;

using AIF.Metadata;
using Mpai.Wdl;

namespace Mpai.Aif.Tests;

// THE CAE-USC V2.5 REFERENCE SOFTWARE AGAINST THE AIF V3.0 CONFORMANCE CRITERIA (Conformance 1, 3): the AIM Implementations - their AIM Instance Metadata validates against the AIM Metadata
// Schema and is an instance of the AIM Metadata (MET-1, MET-2, AIM-6), identifies its Ports by Data Type and Port Number (MET-4, MET-5), and states the Terms of what it obtains (MET-8 to MET-10)
// - and the Workflows: each is read without error, names the one Module it is written against, and offers to and asks of Ports its Module declares (WDL-1, WDL-2, WDL-4, WDL-5).
// What a test cannot see - the User Agent only through the Controller API (UA-1 to UA-7), an AIM that keeps no state between executions (AIM-4) - is reviewed in the source.
[Trait("Group", "Fast")]
public class CaeUscAifConformanceTests
{
    // The L3s of the CAE-USC V2.5 AIMs and their Composite AIMs.
    private static IEnumerable<string> Instances() =>
        Directory.EnumerateFiles(Repository.Amds, "1CAE-*-V2.5-I*.json").OrderBy(f => f, StringComparer.Ordinal);

    [Fact]
    public void EveryL3ValidatesAgainstTheAimMetadataSchemaAndIsAnInstanceOfItsL2()
    {
        var schema = AimMetadataSchema.Load(Repository.Schemas);
        var l2 = new L2Conformance(Repository.Schemas, _ => null);
        var problems = new List<string>();
        foreach (var file in Instances())
        {
            var json = File.ReadAllText(file);
            foreach (var v in schema.Violations(json)) problems.Add($"{Path.GetFileName(file)}: {v}");
            using var document = JsonDocument.Parse(json);
            foreach (var v in l2.Check(document.RootElement)) problems.Add($"{Path.GetFileName(file)} (L2): {v}");
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    // MET-4, MET-5: a Port is identified by Data Type and Port Number, a Topology names each end by a label of its Composite AIM; no name routes data.
    [Fact]
    public void PortsAreIdentifiedByDataTypeAndPortNumberAndNoTopologyEndRoutesByAName()
    {
        var problems = new List<string>();
        foreach (var file in Instances())
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (!document.RootElement.TryGetProperty("Topology", out var topology)) continue;
            foreach (var connection in topology.EnumerateArray())
                foreach (var side in new[] { "Output", "Input" })
                    if (connection.TryGetProperty(side, out var end) && (end.TryGetProperty("PortName", out _) || !end.TryGetProperty("DataType", out _)))
                        problems.Add($"{Path.GetFileName(file)}: a Topology end {end} is not by Data Type and Port Number.");
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    // MET-10: every entry of AccessSources that names data states its Terms: the licence and the URI where the terms are stated. MET-8, MET-9: a Name once, a Uri with one Sha256.
    [Fact]
    public void EveryAccessSourceStatesItsTermsAndNamesAreOnceAndAUriHasOneSha256()
    {
        var problems = new List<string>();
        foreach (var file in Instances())
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (!document.RootElement.TryGetProperty("AccessSources", out var sources)) continue;
            var names = new HashSet<string>(); var uris = new Dictionary<string, string>();
            foreach (var s in sources.EnumerateArray())
            {
                var name = s.GetProperty("Name").GetString()!;
                if (!names.Add(name)) problems.Add($"{Path.GetFileName(file)}: the Name {name} is used twice (MET-9).");
                if (s.TryGetProperty("Uri", out var uri) && s.TryGetProperty("Sha256", out var sha))
                    if (uris.TryGetValue(uri.GetString()!, out var other) && other != sha.GetString()) problems.Add($"{Path.GetFileName(file)}: {uri} has two Sha256 (MET-9).");
                    else uris[uri.GetString()!] = sha.GetString()!;
                if (!s.TryGetProperty("Terms", out var terms) || !terms.TryGetProperty("Licence", out _) || !terms.TryGetProperty("Uri", out _))
                    problems.Add($"{Path.GetFileName(file)}: {name} states no Terms (MET-10).");
            }
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    // EXE-1, EXE-3: a Module whose Topology has a loop is executed continuously, and says so in its Metadata.
    [Fact]
    public void AModuleWhoseTopologyHasALoopDeclaresContinuousExecution()
    {
        var problems = new List<string>();
        foreach (var file in Instances())
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (!document.RootElement.TryGetProperty("Topology", out var topology) || topology.GetArrayLength() == 0) continue;
            var edges = new Dictionary<string, HashSet<string>>();
            foreach (var c in topology.EnumerateArray())
            {
                string From(string side) => c.GetProperty(side).GetProperty("AIMName").GetString() ?? "";
                var (from, to) = (From("Output"), From("Input"));
                if (from.Length == 0 || to.Length == 0) continue;               // an end at the boundary of the Module
                if (!edges.TryGetValue(from, out var set)) edges[from] = set = [];
                set.Add(to);
            }
            var done = new HashSet<string>(); var onPath = new HashSet<string>(); var loop = false;
            void Visit(string node)
            {
                if (loop || done.Contains(node)) return;
                onPath.Add(node);
                foreach (var next in edges.GetValueOrDefault(node) ?? [])
                    if (onPath.Contains(next)) { loop = true; return; } else Visit(next);
                onPath.Remove(node); done.Add(node);
            }
            foreach (var node in edges.Keys.ToList()) Visit(node);
            var execution = document.RootElement.TryGetProperty("Execution", out var e) ? e.GetString() : "Exchange";
            if (loop && execution != "Continuous") problems.Add($"{Path.GetFileName(file)}: its Topology has a loop and it declares {execution} execution (EXE-3).");
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    // UA-2, UA-3, UA-4: a User Agent names no AIM, names no Module but the one it drives, and does not depend on the Metadata of the Module. The Apps of CAE-USC and the deployment they use
    // (ModuleHost) name only the Module of their Workflow, and ModuleHost does not read the Module's L3 for its AIMs or rewrite it.
    [Fact]
    public void TheAppsNameNoAimAndTheDeploymentReadsNoMetadataOfTheModule()
    {
        var problems = new List<string>();
        // The Apps of CAE-USC V2.5 and what they share; the Apps of CAE-ASM V1.0 are another standard's.
        var usc = new[] { "ARP", "EAE", "EES", "SRS", "Support" }.Select(d => Path.Combine(Repository.Root, "Apps", "CAE", d)).ToList();
        var sources = Directory.EnumerateFiles(Path.Combine(Repository.Root, "Apps", "CAE"), "*.cs", SearchOption.AllDirectories)
            .Where(f => usc.Any(d => f.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")).ToList();
        foreach (var file in sources)
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"""(1[A-Z]{3}-[A-Z]{3}-V\d+\.\d+-I\d+)"""))
            {
                // The Module an App drives: the one of its Workflow.
                var module = m.Groups[1].Value;
                var drivenByAWorkflow = Directory.EnumerateFiles(Path.GetDirectoryName(file)!, "*.orch", SearchOption.AllDirectories).Concat(Directory.EnumerateFiles(Directory.GetParent(Path.GetDirectoryName(file)!)!.FullName, "*.orch"))
                    .Any(o => File.ReadAllText(o).Contains($" over {module}"));
                if (!drivenByAWorkflow) problems.Add($"{Path.GetRelativePath(Repository.Root, file)}: names {module}, which no Workflow beside it drives (UA-2, UA-3).");
            }
        }
        var host = File.ReadAllText(Path.Combine(Repository.Root, "Apps", "CAE", "Support", "ModuleHost.cs"));
        foreach (var forbidden in new[] { "SubAIMs", "\"Relation\"", "AIMName" })
            if (host.Contains(forbidden)) problems.Add($"ModuleHost.cs reads or rewrites Metadata of the Module ({forbidden}) (UA-4).");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    // AIM-5: an AIM neither acquires data from the real world nor delivers data to it: none of the AIMs of CAE-USC (the voice engine's own working files excepted) opens a file; the data reach
    // it in the Object.
    [Fact]
    public void NoAimReadsOrWritesAFile()
    {
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Repository.Root, "AIMs", "CAE1", "V2.5"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Path.Combine(Repository.Root, "AIMs", "CAE1", "V2.5"), file);
            if (relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            if (relative.StartsWith("Voice" + Path.DirectorySeparatorChar)) continue;                   // the voice engine's working files (an engine process, its temporary files), not data of the real world
            var text = File.ReadAllText(file);
            foreach (var pattern in new[] { @"(?<![A-Za-z])File\.(Read|Write|Open|Create|Append)", @"""DataURI""", @"StreamReader|StreamWriter|FileStream" })
                if (Regex.IsMatch(text, pattern)) problems.Add($"{relative}: {pattern} (AIM-5).");
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    // CTR-2, CTR-3, AIM-7: the Controller measures the binary that implements each AIM and the Store says whether it is the one it approved. The provider reports, for each AIM it builds, the
    // binary that implements it, and the L3 names that binary: they are one.
    [Fact]
    public void TheBinaryTheProviderReportsForEachAimIsTheOneItsL3Names()
    {
        var provider = new Mpai.Cae.Usc.UscProvider(new AIF.Store.AmdStore(Repository.Amds));
        var problems = new List<string>();
        foreach (var file in Instances().Concat(Directory.EnumerateFiles(Repository.Amds, "1MMC-TTS-V2.5-I01.json")))
        {
            var aim = Path.GetFileNameWithoutExtension(file);
            if (!provider.CanCreate(aim)) continue;
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var named = document.RootElement.GetProperty("Implementations")[0].GetProperty("BinaryName").GetString();
            var reported = provider.ImplementationOf(aim) is { } path ? Path.GetFileNameWithoutExtension(path) : null;
            if (reported != named) problems.Add($"{aim}: the provider reports {reported ?? "no binary"}, its L3 names {named}.");
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static IEnumerable<Step> Walk(IEnumerable<Step> steps)
    {
        foreach (var s in steps)
        {
            yield return s;
            foreach (var inner in Walk(s.Body)) yield return inner;
            foreach (var inner in Walk(s.Else)) yield return inner;
        }
    }

    // WDL-1, WDL-2, WDL-4, WDL-5 and the Workflow conformance: read without error; the one Module it names is the one whose L3 declares every Port it offers to or asks of, in the right direction.
    [Fact]
    public void EveryCaeWorkflowIsReadAndOffersToAndAsksOfPortsItsModuleDeclares()
    {
        var problems = new List<string>();
        // The workflows of CAE-USC V2.5 (ARP, EAE, EES, SRS); those of CAE-ASM V1.0 are another standard's.
        var files = Directory.EnumerateFiles(Path.Combine(Repository.Root, "Apps", "CAE"), "*.orch", SearchOption.AllDirectories)
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"^workflow\s+\S+\s+over\s+1CAE-[A-Z]{3}-V2\.5-", RegexOptions.Multiline)).ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);
            Workflow workflow;
            try { workflow = new WorkflowReader().Read(text); }
            catch (Exception e) { problems.Add($"{name}: not read ({e.Message})"); continue; }

            var module = Regex.Match(text, @"^workflow\s+\S+\s+over\s+(\S+)\s*$", RegexOptions.Multiline).Groups[1].Value;
            if (module.Contains(',') || module.Length == 0) { problems.Add($"{name}: it must name one Module (WDL-1)."); continue; }
            var l3Path = Path.Combine(Repository.Amds, module + ".json");
            if (!File.Exists(l3Path)) { problems.Add($"{name}: the Module {module} has no L3."); continue; }
            using var l3 = JsonDocument.Parse(File.ReadAllText(l3Path));
            var declared = l3.RootElement.GetProperty("ExternalPorts").EnumerateArray()
                .Select(p => (Direction: p.GetProperty("Direction").GetString()!, DataType: p.GetProperty("DataType").GetString()!, Number: p.TryGetProperty("PortNumber", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 1))
                .ToHashSet();

            foreach (var step in Walk(workflow.OnStart.Concat(workflow.OnStop).Concat(workflow.OnDegraded)))
            {
                var asked = new List<(string Direction, PortRef Port)>();
                if (step.Kind is StepKind.Stream or StepKind.Take && step.Port is { } input) asked.Add(("Input", input));
                if (step.Kind == StepKind.Deliver && step.Port is { } output) asked.Add(("Output", output));
                if (step.Kind == StepKind.Give) asked.AddRange(step.Ports.Select(p => ("Output", p)));
                foreach (var (direction, port) in asked)
                    if (!declared.Contains((direction, port.DataType, port.PortNumber)))
                        problems.Add($"{name} line {step.Line}: {direction} Port {port.DataType}:{port.PortNumber} is not declared by {module}.");
            }
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
