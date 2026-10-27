using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace Mpai.Mmc.Jsi;

// THE STATE OF THE APPLICATION, as it told it (the responses of the application that list what it holds), read as the guide says where to look. The AIM does not know what the
// application is: the guide names the array of items, the property that names each, the kind that makes an item a scene, and what to say about each of its members.
public sealed class ApplicationState
{
    public sealed record Member(string Name, IReadOnlyList<(string Label, string Value)> Facts);
    public sealed record Scene(string Name, IReadOnlyList<Member> Members, bool Detailed = true);

    public IReadOnlyList<Scene> Scenes { get; init; } = [];
    public IReadOnlyList<string> Stored { get; init; } = [];
    public bool ListsStored { get; init; }

    public bool IsKnown => Scenes.Count > 0 || Stored.Count > 0;

    // The names the application has: of the scenes and what is in them, and of what is kept and can be added.
    public IReadOnlyDictionary<string, IReadOnlyList<string>> NameSets => new Dictionary<string, IReadOnlyList<string>>
    {
        ["Present"] = Scenes.SelectMany(s => new[] { s.Name }.Concat(s.Members.Select(m => m.Name))).Distinct(StringComparer.Ordinal).ToList(),
        ["Members"] = Scenes.SelectMany(s => s.Members.Select(m => m.Name)).Distinct(StringComparer.Ordinal).ToList(),
        ["Stored"] = Stored,
        ["Latest"] = Stored.Where(n => !Scenes.Any(sc => sc.Name == n || sc.Members.Any(m => m.Name == n))).TakeLast(1).ToList(),      // the sound kept and not yet in the scene, last kept: the one a bare "two metres to the right" is about
        ["Kept"] = Scenes.SelectMany(s => new[] { s.Name }.Concat(s.Members.Select(m => m.Name))).Concat(Stored).Distinct(StringComparer.Ordinal).ToList()      // everything the application keeps, scenes and objects
    };

    // Is this instance a listing of the application - does it hold the array of items that the guide reads the state from? A response that is not one (the
    // response to a command that listed nothing, say) says nothing about the state and does not replace it.
    // WHAT A LISTING ADDS TO THE STATE KNOWN, when the listings come one after another - one names the sounds that are kept, another a scene with where its members are: the facts
    // about the members of a scene that the newer listing only names are those the older gave; the sounds kept are the newer's if it lists any; the scenes are the newer's if it
    // lists any, else the older's.
    public ApplicationState OnTopOf(ApplicationState older)
    {
        var scenes = Scenes.Select(s =>
        {
            if (s.Detailed || older.Scenes.FirstOrDefault(o => o.Name == s.Name) is not { } before) return s;
            return s with { Members = s.Members.Select(m => before.Members.FirstOrDefault(b => b.Name == m.Name) ?? m).ToList() };
        }).ToList();
        return new ApplicationState
        {
            Scenes = scenes.Count > 0 ? scenes : older.Scenes,
            Stored = ListsStored ? Stored : older.Stored,
            ListsStored = ListsStored || older.ListsStored
        };
    }

    public static bool IsListing(JsonNode? instance, ApplicationContext guide) =>
        guide.Context is { } context && instance is not null && Select(instance, (string?)context["Items"] ?? "").Any();

    public static ApplicationState Read(JsonNode? instance, ApplicationContext guide)
    {
        if (guide.Context is not { } context || instance is null) return new ApplicationState();
        var itemsPath = (string?)context["Items"] ?? "";
        var nameKey = (string?)context["Name"] ?? "Name";
        var kindKey = (string?)context["Kind"] ?? "Kind";
        var sceneKinds = (context["SceneKinds"] as JsonArray)?.Select(x => (string?)x ?? "").ToHashSet() ?? [];
        var membersKey = (string?)context["Members"] ?? "Members";
        var memberName = (string?)context["MemberName"] ?? nameKey;
        var memberNamesKey = (string?)context["MemberNames"] ?? "";
        var describe = (context["Describe"] as JsonArray)?.OfType<JsonObject>().Select(d => ((string?)d["Label"] ?? "", (string?)d["Path"] ?? "")).ToList() ?? [];

        var scenes = new List<Scene>();
        var listed = new List<(string Name, bool IsScene)>();
        var inScenes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Elements(instance, itemsPath))
        {
            if (item is not JsonObject o || (string?)o[nameKey] is not { Length: > 0 } name) continue;
            var isScene = sceneKinds.Contains((string?)o[kindKey] ?? "");
            listed.Add((name, isScene));
            if (!isScene) continue;
            var members = new List<Member>();
            foreach (var m in Elements(o, membersKey))
            {
                if (m is not JsonObject mo || (string?)mo[memberName] is not { Length: > 0 } mn) continue;
                var facts = new List<(string, string)>();
                foreach (var (label, path) in describe)
                    if (Select(mo, path).FirstOrDefault() is { } value) facts.Add((label, Show(value)));
                members.Add(new Member(mn, facts));
                inScenes.Add(mn);
            }
            // A listing that only names the members of a scene (the names, no facts about them): the members are those, and the scene is not detailed.
            var detailed = members.Count > 0 || memberNamesKey.Length == 0 || !Select(o, memberNamesKey).Any();
            if (members.Count == 0 && memberNamesKey.Length > 0)
                foreach (var n in Elements(o, memberNamesKey))
                    if ((string?)n is { Length: > 0 } named) { members.Add(new Member(named, [])); inScenes.Add(named); }
            scenes.Add(new Scene(name, members, detailed));
        }
        var stored = listed.Where(l => !l.IsScene && !inScenes.Contains(l.Name)).Select(l => l.Name).Distinct(StringComparer.Ordinal).ToList();
        return new ApplicationState { Scenes = scenes, Stored = stored, ListsStored = listed.Any(l => !l.IsScene) };
    }

    // The state, told to the language model.
    public string Tell(ApplicationContext guide)
    {
        var context = guide.Context;
        string Word(string key, string fallback) => (string?)(context?["Words"] as JsonObject)?[key] ?? fallback;
        if (!IsKnown) return Word("Unknown", "The state of the application is not known.");
        var text = new StringBuilder();
        text.Append(Word("State", "The state of the application:")).Append('\n');
        foreach (var scene in Scenes)
        {
            text.Append(string.Format(CultureInfo.InvariantCulture, Word("Scene", "{0} (contains {1}):"), scene.Name, scene.Members.Count)).Append('\n');
            foreach (var m in scene.Members)
                text.Append("- ").Append(m.Name).Append(m.Facts.Count > 0 ? ": " + string.Join("; ", m.Facts.Select(f => f.Label + " = " + f.Value)) : "").Append('\n');
        }
        if (Stored.Count > 0)
            text.Append(Word("Stored", "Kept, can be added:")).Append(' ').Append(string.Join(", ", Stored)).Append('\n');
        return text.ToString().TrimEnd();
    }

    private static string Show(JsonNode value) =>
        value is JsonArray a ? "(" + string.Join(", ", a.Select(x => Show(x!))) + ")"
        : value is JsonValue v && v.TryGetValue<double>(out var d) ? d.ToString("0.###", CultureInfo.InvariantCulture)
        : value.ToString();

    // The nodes that a path leads to; arrays on the way are walked through.
    public static IEnumerable<JsonNode> Select(JsonNode? from, string path)
    {
        IEnumerable<JsonNode> current = from is null ? [] : [from];
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = new List<JsonNode>();
            foreach (var node in current)
            {
                var targets = node is JsonArray arr ? arr.Where(x => x is not null).Select(x => x!) : [node];
                foreach (var t in targets)
                    if (t is JsonObject o && o.TryGetPropertyValue(part, out var v) && v is not null) next.Add(v);
            }
            current = next;
        }
        return current.ToList();
    }

    // The same, with the arrays that the path ends in opened: their elements.
    public static IEnumerable<JsonNode> Elements(JsonNode? from, string path) =>
        Select(from, path).SelectMany(n => n is JsonArray a ? a.Where(x => x is not null).Select(x => x!) : [n]).ToList();
}
