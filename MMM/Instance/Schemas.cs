using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Mpai.Mmm;

// THE SCHEMAS OF THE ITEMS, BY HEADER. Every Item an M-Instance receives says what it
// is by its Header (MMM-RGT-V2.2: Rights); the schema with that Header as its const
// checks it. The Process Actions are named by their schemas too: MMMovePA.json,
// Header MMM-2MP-V2.2, is the Process Action MM-Move.
public sealed class Schemas
{
    private readonly Dictionary<string, JsonSchema> byHeader = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> actionByHeader = new(StringComparer.Ordinal);

    public Schemas(string root)
    {
        var all = AIF.Metadata.PublishedSchemas.At(root);
        foreach (var (file, schema) in all)
        {
            var header = HeaderOf(file);
            if (header is null) continue;
            byHeader.TryAdd(header, schema);
            var dir = Path.GetFileName(Path.GetDirectoryName(file));
            var name = Path.GetFileNameWithoutExtension(file);
            if (dir == "actions" && name.EndsWith("PA", StringComparison.Ordinal) && file.Contains("MMM4"))
                actionByHeader[header] = ActionName(name[..^2]);
        }
    }

    private static string? HeaderOf(string file)
    {
        try
        {
            var h = JsonNode.Parse(File.ReadAllText(file))?["properties"]?["Header"]?["const"];
            return h is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        }
        catch { return null; }
    }

    // MMMove -> MM-Move, UMCapture -> UM-Capture, PropertyChange -> Property Change.
    public static string ActionName(string file) => file switch
    {
        _ when file.Length > 2 && file[..2] is "MM" or "MU" or "UM" && char.IsUpper(file[2]) => file[..2] + "-" + file[2..],
        "PropertyChange" => "Property Change",
        "RightsChange" => "Rights Change",
        _ => file
    };

    // The Action a concrete Process Action Item performs, from its Header.
    public string? ActionOf(string header) => actionByHeader.GetValueOrDefault(header);

    public IEnumerable<string> Actions => actionByHeader.Values.Distinct().Order(StringComparer.Ordinal);

    // Null when the Item satisfies the schema of its Header (or no schema has it).
    public string? Check(JsonObject item)
    {
        var header = item["Header"]?.GetValue<string>();
        if (header is null) return "the Item has no Header";
        if (!byHeader.TryGetValue(header, out var schema)) return null;
        using var doc = JsonDocument.Parse(item.ToJsonString());
        lock (AIF.Metadata.PublishedSchemas.Lock)
        {
            var r = schema.Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (r.IsValid) return null;
            var first = r.Details.Where(d => d.Errors is { Count: > 0 }).OrderByDescending(d => d.InstanceLocation.ToString().Length).FirstOrDefault();
            var why = first is null ? "" : $": at {first.InstanceLocation}, {first.Errors!.First().Value}";
            return $"the Item does not satisfy the schema of {header}{why}";
        }
    }

    public bool Knows(string header) => byHeader.ContainsKey(header);
}
