using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

using Json.Schema;

namespace Mpai.Xrv;

// THE SCHEMAS OF XRV1 V1.0 (and the OSD, PTF, TFA, ... schemas they refer to), BUILT INTO THIS ASSEMBLY (Mpai.Xrv.Common.csproj): a datum is valid when it validates
// against the schema of its Data Type, its references resolved among the schemas built in. The AIMs read no schema from the file system - they use no data the
// Controller did not give them (AIF V3.0, AI Modules 10.4; review X1, 2026-11-13) - and the schemas are a part of the Implementation, measured with its code.
//
// They have a registry of their own: the process may have the published schemas of its own folder in the library's global registry (the tests do), and the
// library takes the local registry first.
public static class XrvSchemas
{
    private const string Prefix = "schemas/";
    private static readonly object Lock = new();
    private static readonly Lazy<(IReadOnlyDictionary<string, string> Text, IReadOnlyDictionary<string, JsonSchema> Built)> Loaded = new(Load);

    // The schemas built in, by their path under the schemas folder (e.g. "XRV1/V1.0/data/REDataIn.json").
    public static IReadOnlyCollection<string> Files => Loaded.Value.Text.Keys.ToList();

    public static string Text(string relative) =>
        Loaded.Value.Text.TryGetValue(relative.Replace('\\', '/'), out var text) ? text : throw new FileNotFoundException($"no schema {relative} is built into {typeof(XrvSchemas).Assembly.GetName().Name}");

    private static (IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, JsonSchema>) Load()
    {
        var assembly = typeof(XrvSchemas).Assembly;
        var text = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            text[name[Prefix.Length..]] = reader.ReadToEnd();
        }
        var options = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
        var built = new Dictionary<string, JsonSchema>(StringComparer.OrdinalIgnoreCase);
        // Built in rounds: a schema whose references are not yet registered waits for the next round.
        var waiting = text.Keys.ToList();
        for (var round = 0; waiting.Count > 0 && round < 16; round++)
        {
            var next = new List<string>();
            foreach (var file in waiting)
            {
                try
                {
                    var schema = JsonSchema.FromText(text[file], options, new Uri("https://schemas.mpai.community/" + file));
                    built[file] = schema;
                    if (options.SchemaRegistry.Get(schema.BaseUri) is null) options.SchemaRegistry.Register(schema);
                }
                catch (Exception) { next.Add(file); }
            }
            if (next.Count == waiting.Count) break;                                    // what is left is not a schema of its own (a fragment): it stays text
            waiting = next;
        }
        return (text, built);
    }

    // What in the datum the schema does not allow; none when it validates.
    public static IReadOnlyList<string> Violations(string schemaFile, JsonNode datum)
    {
        if (!Loaded.Value.Built.TryGetValue(schemaFile.Replace('\\', '/'), out var schema)) return [$"no schema {schemaFile}"];
        using var doc = System.Text.Json.JsonDocument.Parse(datum.ToJsonString());
        EvaluationResults result;
        lock (Lock)
            result = schema.Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid) return [];
        var found = (result.Details ?? []).Where(d => d.Errors is { Count: > 0 })
                                          .Select(d => $"{d.InstanceLocation}: {d.Errors!.First().Value}").Distinct().Take(5).ToList();
        return found.Count == 0 ? ["not valid"] : found;
    }

    // The names of the properties a schema gives to the object at a path of "properties" names, e.g. ("XRV1/V1.0/data/REPerformanceStatus.json", "REPerformanceStatusData", "IndividualStatuses", "items").
    public static HashSet<string> Properties(string schemaFile, params string[] path)
    {
        var node = JsonNode.Parse(Text(schemaFile))!;
        foreach (var step in path)
            node = step == "items" ? node["items"]! : node["properties"]![step]!;
        return node["properties"]!.AsObject().Select(p => p.Key).ToHashSet();
    }
}
