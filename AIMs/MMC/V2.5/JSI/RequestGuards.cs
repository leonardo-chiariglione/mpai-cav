using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mpai.Mmc.Jsi;

// WHAT CAN BE SAID WITHOUT ASKING THE LANGUAGE MODEL. A small model asked "move it" answers with a move; asked "save the scene", with something. Some requests are not for the
// application, or name nothing that the application has, or give no number: they are told from the words of the request, with the names the application has told, by what the
// guide says (Refusals, NeedsObject, NeedsNumber), and the person is asked at once - nothing is sent, nothing is guessed.
public static class RequestGuards
{
    // A request that is not for the application: its words are in the guide's Refusals.
    public static string? Refused(string request, ApplicationGuide guide)
    {
        var lower = request.ToLowerInvariant();
        foreach (var refusal in (guide.Root["Refusals"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var words = (refusal["Words"] as JsonArray)?.Select(x => (string?)x ?? "").Where(x => x.Length > 0) ?? [];
            if (words.Any(w => Regex.IsMatch(lower, @"\b" + Regex.Escape(w.ToLowerInvariant()) + @"\b"))) return (string?)refusal["Problem"] ?? "That is not something I can do here.";
        }
        return null;
    }

    // A request that works on an object and names none: "move it", "the instrument closer", "rotate the guitar" when there is no guitar. The words of the request are compared with
    // the names the application has (a name written differently by speech recognition is still found: a letter or two of difference), and with the synonyms the guide gives.
    public static string? NamesNothing(string request, ApplicationGuide guide, IReadOnlyCollection<string> paths, IReadOnlyList<string> names)
    {
        if (guide.Root["NeedsObject"] is not JsonObject needs || names.Count == 0) return null;
        var objectPaths = (needs["Paths"] as JsonArray)?.Select(x => (string?)x ?? "").ToList() ?? [];
        if (!paths.Any(p => objectPaths.Contains(p))) return null;
        // What else the request asks for may be about no object (the listener, the whole scene, an earlier command, a sound to add).
        var unless = (needs["Unless"] as JsonArray)?.Select(x => (string?)x ?? "").ToList() ?? [];
        if (paths.Any(p => unless.Contains(p))) return null;
        var lower = request.ToLowerInvariant();
        var anything = (needs["Anything"] as JsonArray)?.Select(x => (string?)x ?? "").Where(x => x.Length > 0).ToList() ?? [];
        if (anything.Any(w => Regex.IsMatch(lower, @"\b" + Regex.Escape(w.ToLowerInvariant()) + @"\b"))) return null;
        if (names.Any(n => lower.Contains(n.ToLowerInvariant()))) return null;
        var tokens = Regex.Matches(lower, @"[a-z0-9']+").Select(m => m.Value).ToList();
        foreach (var (synonym, name) in (needs["Synonyms"] as JsonObject)?.Select(p => (p.Key.ToLowerInvariant(), (string?)p.Value ?? "")) ?? [])
            if (tokens.Contains(synonym) && names.Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
        foreach (var name in names)
            foreach (var part in name.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => p.Length >= 4))
                if (tokens.Any(t => t.Length >= 4 && Distance(t, part) <= (Math.Min(t.Length, part.Length) >= 5 ? 2 : 1))) return null;
        var question = (string?)needs["Question"] ?? "Which object do you mean?";
        return question + " " + string.Format((string?)needs["Names"] ?? "The objects are: {0}.", string.Join(", ", names));
    }

    // The paths whose words are in the request (the same reading as the guide's Triggers).
    public static HashSet<string> Asked(string request, ApplicationGuide guide, IReadOnlyDictionary<string, IReadOnlyList<string>> nameSets)
    {
        var lower = request.ToLowerInvariant();
        var asked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, words) in guide.Triggers)
        {
            var all = words.SelectMany(w => w.StartsWith('{') && w.EndsWith('}') ? (nameSets.TryGetValue(w[1..^1], out var set) ? set : []) : [w]);
            if (all.Any(w => Regex.IsMatch(lower, @"\b" + Regex.Escape(w.ToLowerInvariant())))) asked.Add(path);
        }
        return asked;
    }

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
