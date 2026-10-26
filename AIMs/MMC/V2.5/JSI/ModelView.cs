using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace Mpai.Mmc.Jsi;

// THE SCHEMA AS THE LANGUAGE MODEL SEES IT. A published schema is written for a validator: it refers to other schemas, allows an identifier or a whole object wherever an
// object is named, lists a hundred optional properties, and asks for headers and identifiers that no one should have to make up. The language model is asked for its answer
// in the shape of a schema (structured output), so it is given the schema reduced to what it has to decide, mechanically, from the schema and the guide of the application:
//
//   - every "$ref" is followed (inside the folder of the published schemas);
//   - where a schema allows an identifier (a string) or the object, the model gives the identifier: the name;
//   - a property is offered if the guide lists its path (or a path under it), or if the schema requires it;
//   - a required property whose value is a constant, or an identifier, or a time, is not asked of the model: the AIM fills it in afterwards (Complete);
//   - a property that is the name of something the application has can be limited to the names the application has told (Names);
//   - where a schema asks for one of several properties, the first of those on offer is asked for.
//
// What the model gives is then completed and checked against the whole, unreduced schema: this is the schema the answer has to conform to, not the one the model saw.
public sealed class ModelView
{
    private readonly SchemaLibrary _library;
    private readonly ApplicationGuide _guide;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _nameSets;

    // The reduced schema, with the "x-fill" of the AIM in it.
    public JsonObject Full { get; private set; } = new();

    public ModelView(SchemaLibrary library, ApplicationGuide guide, IReadOnlyDictionary<string, IReadOnlyList<string>>? nameSets = null)
    {
        _library = library;
        _guide = guide;
        _nameSets = nameSets ?? new Dictionary<string, IReadOnlyList<string>>();
    }

    // The reduced schema of the schema published at that address; null where it is not at hand.
    public JsonObject? Build(string schemaUri)
    {
        var document = _library.Document(schemaUri);
        if (document is null) return null;
        var uri = schemaUri.Contains('#') ? schemaUri[..schemaUri.IndexOf('#')] : schemaUri;
        Full = Convert(document, document, uri, "", 0, new HashSet<string>(StringComparer.Ordinal)) as JsonObject ?? new JsonObject();
        return Full;
    }

    // What is sent to the model: the same without what is the AIM's own.
    public JsonObject ForModel() => (JsonObject)Strip(Full.DeepClone(), descriptions: false)!;

    // The same without the descriptions, as the model is shown the shape of its answer: they are in the outline, and a grammar has no use for them.
    private static JsonNode? Strip(JsonNode? node, bool descriptions)
    {
        if (node is JsonObject o)
        {
            o.Remove("x-fill");
            if (!descriptions && o["description"] is JsonValue) o.Remove("description");
            foreach (var p in o.ToList()) Strip(p.Value, descriptions);
        }
        else if (node is JsonArray a)
            foreach (var x in a) Strip(x, descriptions);
        return node;
    }

    // ---- the reduction ----------------------------------------------------------------------------------------------------------

    private bool Included(string path)
    {
        if (path.Length == 0) return true;
        if (_guide.Drop.Contains(path)) return false;
        foreach (var include in _guide.Include)
            if (include == path || include.StartsWith(path + ".", StringComparison.Ordinal)) return true;
        return false;
    }

    private bool Offered(string path) => Included(path) || _guide.Require.Contains(path);

    private JsonNode Convert(JsonNode? node, JsonNode document, string uri, string path, int depth, HashSet<string> following)
    {
        if (node is not JsonObject o || depth > 24) return new JsonObject();
        var description = Clean((string?)o["description"]);

        if (o["$ref"] is JsonValue r && r.TryGetValue<string>(out var reference))
        {
            var (target, targetDocument, key) = _library.Resolve(reference, document, uri);
            if (target is null || targetDocument is null) return Described(new JsonObject(), description);
            if (!following.Add(key)) return Described(new JsonObject { ["type"] = "object" }, description);
            try
            {
                var targetUri = reference.Contains('#') && reference.IndexOf('#') > 0 ? reference[..reference.IndexOf('#')] : (reference.StartsWith('#') ? uri : reference);
                return Described(Convert(target, targetDocument, targetUri, path, depth + 1, following), description, overwrite: true);
            }
            finally { following.Remove(key); }
        }

        // An object.
        if (o["properties"] is JsonObject properties)
            return Described(ConvertObject(o, properties, document, uri, path, depth, following), description);

        // One of several: an identifier, where the schema allows an identifier or the object.
        foreach (var keyword in new[] { "oneOf", "anyOf" })
            if (o[keyword] is JsonArray alternatives)
            {
                var converted = alternatives.Select(a => Convert(a, document, uri, path, depth + 1, following)).OfType<JsonObject>().ToList();
                var identifier = converted.FirstOrDefault(IsPlainString);
                if (identifier is not null) return Described(new JsonObject { ["type"] = "string" }, description ?? Clean((string?)identifier["description"]));
                converted = converted.Where(c => c.Count > 0).ToList();
                if (converted.Count == 0) return Described(new JsonObject(), description);
                if (converted.Count == 1) return Described(converted[0], description);
                return Described(new JsonObject { ["anyOf"] = new JsonArray(converted.Select(c => (JsonNode)c).ToArray()) }, description);
            }

        // An array.
        if (o["items"] is JsonObject items)
        {
            var array = new JsonObject { ["type"] = "array", ["items"] = Convert(items, document, uri, path, depth + 1, following) };
            foreach (var k in new[] { "minItems", "maxItems" })
                if (o[k] is not null) array[k] = o[k]!.DeepClone();
            return Described(array, description);
        }

        // Anything else: a value.
        var value = new JsonObject();
        foreach (var k in new[] { "type", "enum", "const", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "minLength" })
            if (o[k] is not null) value[k] = o[k]!.DeepClone();
        return Described(value, description);
    }

    private JsonObject ConvertObject(JsonObject o, JsonObject properties, JsonNode document, string uri, string path, int depth, HashSet<string> following)
    {
        var required = (o["required"] as JsonArray)?.Select(x => (string?)x ?? "").ToHashSet() ?? [];
        string PathOf(string name) => path.Length == 0 ? name : path + "." + name;

        // Where the schema asks for one of several properties, the first alternative that is on offer.
        if (o["anyOf"] is JsonArray alternatives)
        {
            var wanted = alternatives.OfType<JsonObject>().Select(a => (a["required"] as JsonArray)?.Select(x => (string?)x ?? "").ToList()).Where(n => n is { Count: > 0 }).ToList();
            var chosen = wanted.FirstOrDefault(names => names!.All(n => properties.ContainsKey(n) && Offered(PathOf(n))))
                         ?? wanted.FirstOrDefault(names => names!.All(n => properties.ContainsKey(n) && !_guide.Drop.Contains(PathOf(n))));
            if (chosen is not null) foreach (var n in chosen) required.Add(n);
        }

        var result = new JsonObject { ["type"] = "object" };
        var offered = new JsonObject();
        var order = new List<string>();
        var fill = new JsonObject();
        foreach (var (name, schema) in properties)
        {
            var p = PathOf(name);
            var isRequired = required.Contains(name);
            // A value the application gives at a place, whatever the model says there: the guide's Fill, by the path of the property.
            if (!isRequired && _guide.Fill.TryGetValue(p, out var atPath)) { fill[name] = atPath.DeepClone(); continue; }
            if (isRequired)
            {
                // The AIM's: a constant, an identifier, a time.
                var constant = ConstantOf(schema, document, uri);
                if (constant is not null) { fill[name] = constant; continue; }
                if (_guide.Generated.Contains(name)) { fill[name] = "{id}"; continue; }
                if (_guide.Fill.TryGetValue(name, out var template)) { fill[name] = template.DeepClone(); continue; }
            }
            if (!Offered(p) && !isRequired) continue;
            if (_guide.Drop.Contains(p) && !isRequired) continue;
            var converted = Convert(schema, document, uri, p, depth + 1, following);
            ApplyNames((JsonObject)converted, p);
            if (_guide.Descriptions.TryGetValue(p, out var meaning) && converted is JsonObject described) described["description"] = meaning;
            offered[name] = converted;
            if (isRequired || _guide.Require.Contains(p)) order.Add(name);
        }
        result["properties"] = offered;
        if (order.Count > 0) result["required"] = new JsonArray(order.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
        result["additionalProperties"] = false;
        if (fill.Count > 0) result["x-fill"] = fill;
        return result;
    }

    // The constant a property has to be, where the schema (or what it refers to) says so.
    private JsonNode? ConstantOf(JsonNode? schema, JsonNode document, string uri)
    {
        for (var hops = 0; hops < 6 && schema is JsonObject o; hops++)
        {
            if (o["const"] is not null) return o["const"]!.DeepClone();
            if (o["$ref"] is JsonValue r && r.TryGetValue<string>(out var reference))
            {
                var (target, targetDocument, _) = _library.Resolve(reference, document, uri);
                if (target is null || targetDocument is null) return null;
                schema = target; document = targetDocument;
                if (reference.IndexOf('#') != 0) uri = reference.Contains('#') ? reference[..reference.IndexOf('#')] : reference;
            }
            else return null;
        }
        return null;
    }

    private void ApplyNames(JsonObject converted, string path)
    {
        foreach (var (rulePath, set) in _guide.Names)
        {
            if (rulePath != path) continue;
            if (converted["type"]?.ToString() != "string" || !_nameSets.TryGetValue(set, out var names) || names.Count == 0) continue;
            converted["enum"] = new JsonArray(names.Distinct(StringComparer.Ordinal).Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
        }
    }

    private static bool IsPlainString(JsonObject o) => o["type"]?.ToString() == "string" && o["enum"] is null && o["const"] is null;

    private static JsonObject Described(JsonNode node, string? description, bool overwrite = false)
    {
        var o = node as JsonObject ?? new JsonObject();
        if (description is not null && (overwrite || o["description"] is null)) o["description"] = description;
        return o;
    }

    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    // ---- the completion -----------------------------------------------------------------------------------------------------------

    // What the model gave, with what the AIM fills in: the constants, the identifiers, the times.
    public JsonNode Complete(JsonNode instance, Func<string>? newId = null, Func<double>? now = null)
    {
        newId ??= () => Guid.NewGuid().ToString();
        now ??= () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        return CompleteNode(instance, Full, newId, now);
    }

    private static JsonNode CompleteNode(JsonNode node, JsonObject view, Func<string> newId, Func<double> now)
    {
        if (node is JsonObject o && view["properties"] is JsonObject properties)
        {
            var result = new JsonObject();
            if (view["x-fill"] is JsonObject fill)
                foreach (var (name, template) in fill)
                    result[name] = o.ContainsKey(name) ? o[name]!.DeepClone() : Expand(template!, newId, now);
            foreach (var (name, value) in o)
            {
                if (result.ContainsKey(name) || value is null) continue;
                result[name] = properties[name] is JsonObject child ? CompleteNode(value.DeepClone(), child, newId, now) : value.DeepClone();
            }
            return result;
        }
        if (node is JsonArray a && view["items"] is JsonObject items)
            return new JsonArray(a.Select(x => x is null ? null : CompleteNode(x, items, newId, now)).ToArray());
        return node.DeepClone();
    }

    private static JsonNode Expand(JsonNode template, Func<string> newId, Func<double> now)
    {
        switch (template)
        {
            case JsonValue v when v.TryGetValue<string>(out var s):
                return s switch
                {
                    "{id}" => JsonValue.Create(newId())!,
                    "{now}" => JsonValue.Create(now())!,
                    _ => JsonValue.Create(s)!
                };
            case JsonObject o:
                var copy = new JsonObject();
                foreach (var (k, v) in o) copy[k] = v is null ? null : Expand(v, newId, now);
                return copy;
            case JsonArray a:
                return new JsonArray(a.Select(x => x is null ? null : Expand(x, newId, now)).ToArray());
            default:
                return template.DeepClone();
        }
    }

    // ---- the outline ------------------------------------------------------------------------------------------------------------------

    // The reduced schema told to the model as the shape of the answer, each property with what it means.
    public string Outline()
    {
        var text = new StringBuilder();
        Print((JsonObject)Strip(Full.DeepClone(), descriptions: true)!, 0, text);
        return text.ToString().TrimEnd();
    }

    private static void Print(JsonObject node, int depth, StringBuilder text)
    {
        var pad = new string(' ', depth * 2);
        if (node["properties"] is JsonObject properties)
        {
            text.Append("{\n");
            var required = (node["required"] as JsonArray)?.Select(x => (string?)x).ToHashSet() ?? [];
            var names = properties.Select(p => p.Key).ToList();
            for (var i = 0; i < names.Count; i++)
            {
                var child = (JsonObject)properties[names[i]]!;
                text.Append(pad).Append("  \"").Append(names[i]).Append("\": ");
                Print(child, depth + 1, text);
                if (i < names.Count - 1) text.Append(',');
                var comment = Comment(child, required.Contains(names[i]));
                if (comment.Length > 0) text.Append("  // ").Append(comment);
                text.Append('\n');
            }
            text.Append(pad).Append('}');
        }
        else if (node["type"]?.ToString() == "array")
        {
            var items = node["items"] as JsonObject ?? new JsonObject();
            if (items["type"]?.ToString() == "number" && node["minItems"] is { } mi && mi.ToString() == node["maxItems"]?.ToString())
                text.Append('[').Append(string.Join(", ", Enumerable.Repeat("<number>", int.Parse(mi.ToString(), CultureInfo.InvariantCulture)))).Append(']');
            else { text.Append("[ "); Print(items, depth, text); text.Append(" ]"); }
        }
        else if (node["enum"] is JsonArray { Count: > 4 }) text.Append("<a name>");
        else if (node["enum"] is JsonArray e) text.Append(string.Join(" | ", e.Select(x => "\"" + x + "\"")));
        else if (node["anyOf"] is JsonArray alternatives) text.Append(string.Join(" | ", alternatives.OfType<JsonObject>().Select(a => { var s = new StringBuilder(); Print(a, depth, s); return s.ToString(); })));
        else text.Append(node["type"]?.ToString() switch { "number" => "<number>", "integer" => "<integer>", "boolean" => "<true or false>", "string" => "<string>", _ => "<value>" });
    }

    private static string Comment(JsonObject node, bool required)
    {
        var description = (string?)node["description"] ?? "";
        // Up to two sentences, whole, where they are short; the schema's own long descriptions are cut at the first.
        var first = description.IndexOf(". ", StringComparison.Ordinal);
        if (first > 0)
        {
            var second = description.IndexOf(". ", first + 2, StringComparison.Ordinal);
            description = second > 0 && second < 200 ? description[..(second + 1)] : description[..(first + 1)];
        }
        var parenthesis = description.IndexOf(" (", StringComparison.Ordinal);
        if (parenthesis > 20) description = description[..parenthesis] + ".";
        if (description.Length > 200) description = description[..197] + "...";
        return description;
    }
}
