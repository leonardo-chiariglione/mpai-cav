using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mpai.Xrv;

namespace Mpai.Aif.Tests;

// THE XRV SCHEMAS ARE PART OF THE IMPLEMENTATION (AIF V3.0 review X1, 2026-11-13): the XRV-LTP AIMs use no data the Controller did not give them (AI Modules 10.4)
// - they read no schemas folder; the schemas they validate and interpret with are built into Mpai.Xrv.Common.dll. Judged: every schema of XRV1/V1.0/data is
// built in, and every schema any of them refers to, at any depth; each built-in schema is the published one, as it is in the repository; the code finds no folder.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class XrvSchemasTests
{
    private static readonly Regex Ref = new("\"\\$ref\"\\s*:\\s*\"https://schemas\\.mpai\\.community/([^\"#]+)", RegexOptions.Compiled);

    [Fact]
    public void EverySchemaTheXrvDataTypesReferToIsBuiltIn()
    {
        var built = XrvSchemas.Files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var xrv = Directory.GetFiles(Path.Combine(Repository.Schemas, "XRV1", "V1.0", "data"), "*.json").Select(f => "XRV1/V1.0/data/" + Path.GetFileName(f)).ToList();
        Assert.All(xrv, f => Assert.Contains(f, built));
        // From the XRV Data Types, every schema they refer to, at any depth (the folders built in hold more than that: only the closure matters).
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var todo = new Stack<string>(xrv);
        while (todo.TryPop(out var file))
        {
            if (!seen.Add(file)) continue;
            foreach (Match m in Ref.Matches(XrvSchemas.Text(file)))
                if (built.Contains(m.Groups[1].Value)) todo.Push(m.Groups[1].Value);
                else missing.Add($"{file} -> {m.Groups[1].Value}");
        }
        Assert.True(seen.Count > xrv.Count, "the XRV Data Types refer to other schemas");
        Assert.True(missing.Count == 0, "referred to and not built in (add its folder to Mpai.Xrv.Common.csproj): " + string.Join("; ", missing.Distinct()));
    }

    [Fact]
    public void TheBuiltInSchemasAreThoseOfTheRepository()
    {
        foreach (var file in XrvSchemas.Files)
            Assert.Equal(File.ReadAllText(Path.Combine(Repository.Schemas, file)), XrvSchemas.Text(file));
    }

    [Fact]
    public void ADatumIsJudgedByTheBuiltInSchemaAndTheCodeLooksForNoFolder()
    {
        var bad = new JsonObject { ["Header"] = "XRV-RDI-V1.0" };
        Assert.NotEmpty(XrvSchemas.Violations("XRV1/V1.0/data/REDataIn.json", bad));
        Assert.Equal(["no schema XRV1/V1.0/data/NoSuchType.json"], XrvSchemas.Violations("XRV1/V1.0/data/NoSuchType.json", bad));
        foreach (var source in Directory.GetFiles(Path.Combine(Repository.Root, "AIMs", "XRV1", "V1.0"), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var code = File.ReadAllText(source);
            Assert.DoesNotContain("AppContext.BaseDirectory", code);
            Assert.DoesNotContain("PublishedSchemas.At", code);
        }
    }
}
