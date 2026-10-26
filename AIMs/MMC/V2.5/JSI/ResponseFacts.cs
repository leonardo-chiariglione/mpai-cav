using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace Mpai.Mmc.Jsi;

// A RESPONSE OF THE APPLICATION, TOLD AS FACTS: what was done, what was not and why, in the person's terms - names, numbers, counts. It is not a sentence addressed to the
// person: the dialogue AIM composes what is said. The facts come out of the response itself, property by property, in the words the guide gives for the properties and the
// values of this application (Words), without those it says to leave out (Skip). No language model is asked: a fact that is not in the response is not in the facts.
public static class ResponseFacts
{
    private const int MostItems = 12;

    public static string Tell(JsonNode response, ApplicationGuide guide, string? inReplyTo = null)
    {
        var words = guide.Words;
        var skip = guide.Skip.ToHashSet(StringComparer.Ordinal);
        var intro = (string?)guide.Root["ResponseIntro"] ?? "The application answered";
        var text = intro + ": " + Render(response, words, skip, top: true) + ".";
        if (!string.IsNullOrWhiteSpace(inReplyTo)) text = "The person had asked: \"" + inReplyTo.Trim() + "\". " + text;
        return text;
    }

    private static string Render(JsonNode? node, IReadOnlyDictionary<string, string> words, HashSet<string> skip, bool top = false)
    {
        switch (node)
        {
            case null: return "nothing";
            case JsonValue v:
                if (v.TryGetValue<string>(out var s)) return words.TryGetValue(s, out var w) ? w : s;
                if (v.TryGetValue<bool>(out var b)) return b ? "yes" : "no";
                if (v.TryGetValue<double>(out var d)) return d.ToString("0.###", CultureInfo.InvariantCulture);
                return v.ToString();
            case JsonArray a:
                if (a.Count == 0) return "none";
                if (a.All(x => x is JsonValue)) return string.Join(", ", a.Select(x => Render(x, words, skip)));
                var shown = a.Take(MostItems).Select(x => "(" + Render(x, words, skip) + ")");
                return a.Count + ": " + string.Join("; ", shown) + (a.Count > MostItems ? $"; and {a.Count - MostItems} more" : "");
            case JsonObject o:
                var parts = new List<string>();
                foreach (var (key, value) in o)
                {
                    if (skip.Contains(key) || value is null) continue;
                    var label = words.TryGetValue(key, out var l) ? l : key;
                    var rendered = Render(value, words, skip);
                    parts.Add(label.Length == 0 ? rendered : label + " " + rendered);
                }
                return string.Join(top ? "; " : ", ", parts);
            default: return node.ToString();
        }
    }
}
