using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Mpai.RcaWeb.Mas;

// WHAT THE MPAI STORE HAS APPROVED, read from the page through this origin
// (the Host passes /thalia/store/... on to the Store). The Store lists its L3s;
// each L3 says what the AIM is and who made it. What a person sees is made of
// that and nothing else: no price, no rating - the Store does not have them.
public sealed class WebStore
{
    // IsApp: the L3 is of a Composite AIM, which is what an App is made on.
    public sealed record Entry(string Id, string Name, string Description, bool IsApp, string Implementer,
                               int Version, int Errors, int Warnings, IReadOnlyList<string> Parts);

    private readonly HttpClient http;
    public WebStore(HttpClient http) => this.http = http;

    // Whether the Store answers at all.
    public async Task<bool> ReachableAsync()
    {
        try { using var r = await http.GetAsync("thalia/store/L3"); return r.IsSuccessStatusCode; }
        catch { return false; }
    }

    // Every L3 of the Store, latest version, with what the L3 says of itself.
    public async Task<IReadOnlyList<Entry>> ListAsync()
    {
        using var index = JsonDocument.Parse(await http.GetStringAsync("thalia/store/L3"));
        var rows = index.RootElement.EnumerateArray().Select(e => (
            Id: Str(e, "id"), Version: Int(e, "version"), Errors: Int(e, "errors"), Warnings: Int(e, "warnings"))).ToList();
        var entries = await Task.WhenAll(rows.Select(async r =>
        {
            try
            {
                using var doc = JsonDocument.Parse(await http.GetStringAsync($"thalia/store/L3/{Uri.EscapeDataString(r.Id)}"));
                return Describe(r.Id, r.Version, r.Errors, r.Warnings, doc.RootElement);
            }
            catch { return new Entry(r.Id, r.Id, "", false, "", r.Version, r.Errors, r.Warnings, Array.Empty<string>()); }
        }));
        return entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // A name for people, from the first words of the L3's own description - "Text to Speech: converts ..." is
    // "Text to Speech" - or the AIM's name when it has none.
    internal static Entry Describe(string id, int version, int errors, int warnings, JsonElement l3)
    {
        var description = l3.TryGetProperty("Description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
        var colon = description.IndexOf(':');
        var name = colon > 0 ? description[..colon] : "";
        var paren = name.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0) name = name[..paren];
        if (name.Length == 0 || name.Length > 60) name = id;
        var rest = colon > 0 ? description[(colon + 1)..].Trim() : description;

        var parts = new List<string>();
        var isApp = false;
        if (l3.TryGetProperty("SubAIMs", out var subs) && subs.ValueKind == JsonValueKind.Array && subs.GetArrayLength() > 0)
        {
            isApp = true;
            foreach (var s in subs.EnumerateArray())
                if (s.TryGetProperty("Identifier", out var i) && i.TryGetProperty("AIMName", out var n) && n.GetString() is { Length: > 0 } part)
                    parts.Add(part);
        }
        var implementer = l3.TryGetProperty("Identifier", out var ident) && ident.TryGetProperty("ImplementerID", out var im)
            ? im.GetString() ?? "" : "";
        return new Entry(id, name, rest, isApp, implementer, version, errors, warnings, parts);
    }

    private static string Str(JsonElement e, string name) =>
        e.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: JsonValueKind.String } v
            ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string name) =>
        e.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: JsonValueKind.Number } v
            ? v.GetInt32() : 0;
}
