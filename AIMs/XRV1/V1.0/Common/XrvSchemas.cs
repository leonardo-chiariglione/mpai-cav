using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

using AIF.Metadata;

namespace Mpai.Xrv;

// THE SCHEMAS OF XRV1 V1.0 (and the OSD, PTF, TFA, ... schemas they refer to), as published in D:\AI\schemas: a datum is valid when it validates against the schema of its
// Data Type, references resolved from the local folder.
public static class XrvSchemas
{
    // The schemas folder: the first folder above the program that holds XRV1.
    public static string Root { get; } = Find();

    private static string Find()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "schemas");
            if (Directory.Exists(Path.Combine(candidate, "XRV1"))) return candidate;
        }
        throw new DirectoryNotFoundException("the schemas folder (with XRV1) is not above " + AppContext.BaseDirectory);
    }

    public static string SchemaPath(string relative) => Path.GetFullPath(Path.Combine(Root, relative));

    // What in the datum the schema does not allow; none when it validates.
    public static IReadOnlyList<string> Violations(string schemaFile, JsonNode datum)
    {
        var all = PublishedSchemas.At(Root);
        if (!all.TryGetValue(SchemaPath(schemaFile), out var schema)) return [$"no schema {schemaFile}"];
        using var doc = System.Text.Json.JsonDocument.Parse(datum.ToJsonString());
        Json.Schema.EvaluationResults result;
        lock (PublishedSchemas.Lock)
            result = schema.Evaluate(doc.RootElement, new Json.Schema.EvaluationOptions { OutputFormat = Json.Schema.OutputFormat.List });
        if (result.IsValid) return [];
        var found = result.Details.Where(d => d.Errors is { Count: > 0 })
                                  .Select(d => $"{d.InstanceLocation}: {d.Errors!.First().Value}").Distinct().Take(5).ToList();
        return found.Count == 0 ? ["not valid"] : found;
    }

    // The names of the properties a schema gives to the object at a path of "properties" names, e.g. ("XRV1/V1.0/data/REPerformanceStatus.json", "REPerformanceStatusData", "IndividualStatuses", "items").
    public static HashSet<string> Properties(string schemaFile, params string[] path)
    {
        var node = JsonNode.Parse(File.ReadAllText(SchemaPath(schemaFile)))!;
        foreach (var step in path)
            node = step == "items" ? node["items"]! : node["properties"]![step]!;
        return node["properties"]!.AsObject().Select(p => p.Key).ToHashSet();
    }
}
