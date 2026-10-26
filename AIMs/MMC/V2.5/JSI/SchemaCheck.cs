using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

using AIF.Metadata;
using Json.Schema;

namespace Mpai.Mmc.Jsi;

// A SCHEMA OF THE APPLICATION, as the Access of the JSON Interface names it: the address it is published at (Uri), and the file the Controller obtained for it (the Setting).
// An instance is checked against it before it leaves. The schema refers to other published schemas: they are found in the schemas folder, never on the network. Where the
// folder is not at hand the file of Access is read as it is; where neither can be, there is no schema, and nothing is said to conform.
public sealed class SchemaCheck
{
    public const string Authority = SchemaLibrary.Authority;

    public string Uri { get; }

    private readonly string? _file;
    private readonly string? _root;
    private JsonSchema? _schema;
    private bool _looked;

    public SchemaCheck(string uri, string? file = null, string? schemasRoot = null)
    {
        Uri = uri ?? "";
        _file = string.IsNullOrWhiteSpace(file) ? null : file;
        _root = string.IsNullOrWhiteSpace(schemasRoot) ? null : schemasRoot;
    }

    public bool Available => Find() is not null;

    // What is wrong with the instance; empty when it conforms; null where the schema is not available.
    public IReadOnlyList<string>? Violations(string instanceJson)
    {
        var schema = Find();
        if (schema is null) return null;
        try
        {
            using var document = JsonDocument.Parse(instanceJson);
            lock (PublishedSchemas.Lock)
            {
                var result = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
                if (result.IsValid) return [];
                var said = new StringBuilder();
                Describe(result, said, 0);
                var text = said.ToString().Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                return text.Length > 0 ? text : ["does not conform"];
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return [$"cannot be checked: {e.Message}"];
        }
    }

    private JsonSchema? Find()
    {
        if (_looked) return _schema;
        _looked = true;
        var relative = Uri.StartsWith(Authority, StringComparison.Ordinal) ? Uri[Authority.Length..].Replace('/', Path.DirectorySeparatorChar) : null;

        var roots = new List<string>();
        if (_root is not null) roots.Add(_root);
        if (global::Mpai.Aif.PortData.PortDataSchema.Root is { } found) roots.Add(found);
        if (_file is not null && relative is not null && _file.EndsWith(relative, StringComparison.OrdinalIgnoreCase))
            roots.Add(_file[..^relative.Length].TrimEnd(Path.DirectorySeparatorChar));

        if (relative is not null)
            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var all = PublishedSchemas.At(root);
                    if (all.TryGetValue(Path.GetFullPath(Path.Combine(root, relative)), out var schema)) return _schema = schema;
                }
                catch (Exception e) when (e is InvalidOperationException or IOException or DirectoryNotFoundException) { }
            }

        if (_file is not null && File.Exists(_file))
        {
            try { lock (PublishedSchemas.Lock) return _schema = JsonSchema.FromFile(_file); }
            catch (Exception e) when (e is not OutOfMemoryException) { }
        }
        return null;
    }

    // The folder of the published schemas that this check reads from, where it is known.
    public string? SchemasRoot
    {
        get
        {
            if (_root is not null) return _root;
            if (global::Mpai.Aif.PortData.PortDataSchema.Root is { } found) return found;
            var relative = Uri.StartsWith(Authority, StringComparison.Ordinal) ? Uri[Authority.Length..].Replace('/', Path.DirectorySeparatorChar) : null;
            if (_file is not null && relative is not null && _file.EndsWith(relative, StringComparison.OrdinalIgnoreCase))
                return _file[..^relative.Length].TrimEnd(Path.DirectorySeparatorChar);
            return null;
        }
    }

    private static void Describe(EvaluationResults r, StringBuilder into, int depth)
    {
        if (depth > 4) return;
        if (r.Errors is not null)
            foreach (var e in r.Errors)
                into.Append(' ').Append(r.InstanceLocation).Append(' ').Append(e.Value).Append(';');
        if (r.Details is null) return;
        foreach (var child in r.Details)
            if (!child.IsValid) Describe(child, into, depth + 1);
    }
}
