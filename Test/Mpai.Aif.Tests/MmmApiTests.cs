using System.Text.Json.Nodes;

namespace Mpai.Aif.Tests;

// THE MMM-TEC V2.2 API (MMM/Api/MMM-API.json, built from the schemas by
// MMM/Api/build_openapi.py): the schemas under the server and the client have no
// recursion (the author, 2026/10/01). Every $ref of the document names a component
// that exists, and no component reaches itself through its $refs. The paths are
// those of the MMM-API chapter: one POST per Process Action, the Baseline Profile's
// among them, and the read-only Item and Capabilities paths.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class MmmApiTests
{
    private static readonly string[] Baseline =
    [
        "/api/v2.2/communicate/mm-send", "/api/v2.2/export/mu-actuate", "/api/v2.2/identity/identify",
        "/api/v2.2/import/um-actuate", "/api/v2.2/import/um-capture", "/api/v2.2/locate/mm-add",
        "/api/v2.2/locate/mm-animate", "/api/v2.2/locate/mm-move"
    ];

    private static IEnumerable<string> Refs(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o)
                {
                    if (key == "$ref" && value is JsonValue v && v.TryGetValue<string>(out var r)) yield return r;
                    foreach (var x in Refs(value)) yield return x;
                }
                break;
            case JsonArray a:
                foreach (var item in a)
                    foreach (var x in Refs(item)) yield return x;
                break;
        }
    }

    [Fact]
    public void NoRecursion()
    {
        var api = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Root, "MMM", "Api", "MMM-API.json")))!.AsObject();
        var schemas = api["components"]!["schemas"]!.AsObject();
        var parameters = api["components"]!["parameters"]!.AsObject();
        const string prefix = "#/components/schemas/";

        var unresolved = Refs(api)
            .Where(r => r.StartsWith(prefix) ? !schemas.ContainsKey(r[prefix.Length..])
                      : !(r.StartsWith("#/components/parameters/") && parameters.ContainsKey(r["#/components/parameters/".Length..])))
            .Distinct().OrderBy(r => r, StringComparer.Ordinal).ToList();

        var edges = schemas.ToDictionary(p => p.Key,
            p => Refs(p.Value).Where(r => r.StartsWith(prefix)).Select(r => r[prefix.Length..]).Distinct().ToList());

        // A cycle is a component met again while its own $refs are being followed.
        var cycles = new List<string>();
        var state = new Dictionary<string, int>();                 // 1 on the way, 2 done
        var path = new Stack<string>();
        void Visit(string name)
        {
            state[name] = 1; path.Push(name);
            foreach (var next in edges.GetValueOrDefault(name) ?? [])
            {
                if (state.GetValueOrDefault(next) == 1)
                    cycles.Add(string.Join(" -> ", path.Reverse().SkipWhile(n => n != next).Append(next)));
                else if (state.GetValueOrDefault(next) == 0)
                    Visit(next);
            }
            path.Pop(); state[name] = 2;
        }
        foreach (var name in edges.Keys.OrderBy(n => n, StringComparer.Ordinal))
            if (state.GetValueOrDefault(name) == 0) Visit(name);

        var paths = api["paths"]!.AsObject();
        var result = new Dictionary<string, string>
        {
            ["cycles among the component schemas"] = cycles.Count == 0 ? "none" : string.Join("; ", cycles),
            ["unresolved $refs"] = unresolved.Count == 0 ? "none" : string.Join(", ", unresolved),
            ["Process Action paths (POST)"] = paths.Count(p => p.Value!["post"] is not null).ToString(),
            ["Item and Capabilities paths (GET)"] = paths.Count(p => p.Value!["get"] is not null).ToString(),
            ["Baseline Profile paths"] = string.Join(", ", Baseline.Select(b => paths.ContainsKey(b) ? "present" : b + " absent").Distinct()),
            ["every MMM V2.2 data schema a component"] = Directory.GetFiles(Path.Combine(Repository.Schemas, "MMM4", "V2.2", "data"), "*.json")
                .Select(f => "MMM4_V22_" + Path.GetFileNameWithoutExtension(f)).Where(n => !schemas.ContainsKey(n))
                .DefaultIfEmpty("yes").Aggregate((a, b) => a == "yes" ? b : a + ", " + b)
        };
        Expected.Match("mmm-api.json", result);
    }
}
