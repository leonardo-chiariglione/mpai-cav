using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

namespace Mpai.Mmc.Jsi;

// THE PUBLISHED SCHEMAS, read from the schemas folder as JSON, for the one job of following a "$ref": a schema refers to other published schemas (and to its own $defs), and
// they are found beside it in the folder, never on the network.
public sealed class SchemaLibrary
{
    public const string Authority = "https://schemas.mpai.community/";

    private readonly string _root;
    private readonly string? _single;
    private readonly string? _singleUri;
    private readonly Dictionary<string, JsonNode?> _documents = new(StringComparer.Ordinal);

    public SchemaLibrary(string schemasRoot) { _root = schemasRoot; }

    // A library that, besides the folder, knows one schema by the file it is in: where the folder is not at hand, the schema of Access alone.
    public SchemaLibrary(string schemasRoot, string uri, string file) : this(schemasRoot) { _singleUri = uri; _single = file; }

    // The document published at that address (no fragment), or null.
    public JsonNode? Document(string uri)
    {
        var at = uri.IndexOf('#');
        if (at >= 0) uri = uri[..at];
        if (_documents.TryGetValue(uri, out var known)) return known;
        JsonNode? node = null;
        try
        {
            string? file = null;
            if (uri.StartsWith(Authority, StringComparison.Ordinal) && _root.Length > 0)
            {
                var candidate = Path.Combine(_root, uri[Authority.Length..].Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate)) file = candidate;
            }
            if (file is null && _single is not null && string.Equals(uri, _singleUri, StringComparison.Ordinal) && File.Exists(_single)) file = _single;
            if (file is not null) node = JsonNode.Parse(File.ReadAllText(file), documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip });
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { node = null; }
        _documents[uri] = node;
        return node;
    }

    // What a "$ref" points to: the node, the document it is in, and a key that names it. A reference with a fragment only is in the document it is written in.
    public (JsonNode? Node, JsonNode? Document, string Key) Resolve(string reference, JsonNode currentDocument, string currentUri)
    {
        var at = reference.IndexOf('#');
        var documentPart = at >= 0 ? reference[..at] : reference;
        var fragment = at >= 0 ? reference[(at + 1)..] : "";
        JsonNode? document; string uri;
        if (documentPart.Length == 0) { document = currentDocument; uri = currentUri; }
        else { uri = documentPart; document = Document(documentPart); }
        if (document is null) return (null, null, uri + "#" + fragment);
        JsonNode? node = document;
        foreach (var part in fragment.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = part.Replace("~1", "/").Replace("~0", "~");
            node = node is JsonObject o && o.TryGetPropertyValue(key, out var next) ? next : null;
            if (node is null) break;
        }
        return (node, document, uri + "#" + fragment);
    }
}
