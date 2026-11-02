using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mpai.Mmc.Jsi;

// A QUESTION ABOUT A PROPERTY OF A THING THE APPLICATION HOLDS ("what is the duration of clip777?", "where is the guitar?"): the application is asked for the listing of its scene, as it is for
// "what is in the scene", and what is said is the one fact asked for, not the whole listing. Which words ask for which fact is the application's: the entries of Context.Describe of its
// Application Context carry them (Words), with the Label the fact is told by and the Unit it is told in. A request is such a question when it starts as a question, names a member of the scene
// and has a word of a fact.
public static class PropertyQuestions
{
    public sealed record Asked(string Member, IReadOnlyList<string> Labels);

    private static readonly Regex Starts = new(@"^\s*(?:please\s+|can you\s+|could you\s+)?(?:what|what's|whats|how|where|when|which|tell me|show me|give me|say|display)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private sealed record Fact(string Label, string Unit, IReadOnlyList<string> Words);

    private static List<Fact> Facts(ApplicationContext guide) =>
        (guide.Context?["Describe"] as JsonArray)?.OfType<JsonObject>()
            .Where(d => d["Words"] is JsonArray { Count: > 0 })
            .Select(d => new Fact((string?)d["Label"] ?? "", (string?)d["Unit"] ?? "", ((JsonArray)d["Words"]!).Select(w => ((string?)w ?? "").ToLowerInvariant()).Where(w => w.Length > 0).ToList()))
            .ToList() ?? [];

    // The question the request is, or null: the member of a scene it is about and the Labels of the facts it asks for.
    public static Asked? Match(string text, ApplicationContext guide, ApplicationState state)
    {
        var facts = Facts(guide);
        if (facts.Count == 0 || !Starts.IsMatch(text)) return null;
        var lower = text.ToLowerInvariant().Replace("’", "'").Replace("?", " ");
        string? member = null;
        foreach (var name in state.Scenes.SelectMany(s => s.Members).Select(m => m.Name).Distinct(StringComparer.Ordinal).OrderByDescending(n => n.Length))
            if (Regex.IsMatch(lower, @"(?<![\p{L}\p{N}-])" + Regex.Escape(name.ToLowerInvariant()) + @"(?![\p{L}\p{N}-])")) { member = name; break; }
        if (member is null) return null;
        var rest = lower.Replace(member.ToLowerInvariant(), " ");
        var labels = new List<string>();
        foreach (var fact in facts)
            if (fact.Words.Any(w => Regex.IsMatch(rest, @"(?<![\p{L}\p{N}])" + Regex.Escape(w) + @"(?![\p{L}\p{N}])")) && !labels.Contains(fact.Label)) labels.Add(fact.Label);
        return labels.Count == 0 ? null : new Asked(member, labels);
    }

    // What is said of it, from the listing the application has just sent: the member, and each fact asked for with its value and unit.
    public static string Answer(Asked asked, ApplicationState state, ApplicationContext guide)
    {
        var units = Facts(guide).ToDictionary(f => f.Label, f => f.Unit);
        var member = state.Scenes.SelectMany(s => s.Members).LastOrDefault(m => m.Name == asked.Member);
        if (member is null) return $"{asked.Member} is not in the scene.";
        var said = new List<string>();
        foreach (var label in asked.Labels)
        {
            var value = member.Facts.FirstOrDefault(f => f.Label == label).Value;
            var unit = units.GetValueOrDefault(label, "");
            said.Add(value is { Length: > 0 } ? $"{label} {value}{(unit.Length > 0 ? " " + unit : "")}" : $"{label} not set");
        }
        return $"{member.Name}: {string.Join("; ", said)}.";
    }
}
