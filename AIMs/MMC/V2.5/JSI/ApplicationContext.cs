using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Mpai.Mmc.Jsi;

// THE GUIDE OF AN APPLICATION: everything the JSON Interface needs to know about one application that its two schemas do not say. It is a file of the Access of the
// implementation (AccessSources, Setting ApplicationGuide), next to CommandSchema and ResponseSchema; the AIM itself knows no application.
//
//   Application      what the application is, in a sentence, for the language model.
//   Conventions      text: the frame and the units the numbers of a command are in, and how the person's words become numbers.
//   Include          the paths of the schema (property names, arrays elided) that a command may use; what the schema requires is always there. What is not listed does not exist
//                    for the language model.
//   Require          paths that the command has to have.
//   Drop             paths of the schema that are not offered even though a listed path contains them.
//   Generated        the properties that the AIM fills with a fresh identifier (the language model never writes an identifier).
//   Fill             property name, or the path of a property, -> a template filled by the AIM (at a path, whatever the model says there); "{id}" is a fresh identifier and "{now}" the current time in seconds.
//   Names            the paths whose value is the name of something the application has, with the set of names (Present or Stored) it is chosen from.
//   Context          (Items, Name, Kind, SceneKinds, Members, MemberName, MemberNames, Describe, Words) how the state of the application (an Application JSON Instance) is read: where its items are, their names, and what to say of each.
//   NeedsNumber      words that ask for a change by an amount the person did not give, and the question to ask when no number comes with them.
//   Earlier          the property that identifies a command, so that the AIM can name earlier commands to the model (to revoke one).
//   Descriptions     what a path of the schema means in this application: it replaces the description the schema gives, where the model is shown the shape of a command.
//   Words, Skip      how a response is told: the wording of properties and values, and the properties left out.
//   StateRequests    the commands that ask the application what it holds (sent in order, each when the answer to the one before has come, to keep the state fresh).
//   Shortcuts        phrases that are always the same command, and that command: it is sent without the language model.
//   Examples         requests and the commands (or the problem) they come to, in the shape the language model gives them.
public sealed class ApplicationGuide
{
    public JsonObject Root { get; }

    public ApplicationGuide(JsonObject root) { Root = root; }

    public static ApplicationGuide Parse(string json) =>
        new(JsonNode.Parse(json) as JsonObject ?? throw new FormatException("The guide of the application is not a JSON object."));

    public string Application => Text("Application");
    public string Conventions => Text("Conventions");
    public string ResponseConventions => Text("ResponseConventions");
    public IReadOnlyList<string> Include => Texts("Include");
    public IReadOnlyList<string> Require => Texts("Require");
    public IReadOnlyList<string> Drop => Texts("Drop");
    public IReadOnlyList<string> Generated => Texts("Generated");
    public string Earlier => Text("Earlier");
    public string Reminder => Text("Reminder");

    // Whether the model writes a line of working before its answer (it costs time, and helps with numbers): true unless the guide says false.
    public bool Working => Root["Working"] is not JsonValue v || !v.TryGetValue<bool>(out var b) || b;
    public IReadOnlyList<string> Skip => Texts("Skip");

    // What a path of the schema means in this application, in a sentence: it replaces the description the schema gives it where the model is shown the shape of a command.
    public IReadOnlyDictionary<string, string> Descriptions =>
        (Root["Descriptions"] as JsonObject)?.ToDictionary(p => p.Key, p => (string?)p.Value ?? "") ?? new Dictionary<string, string>();

    public IReadOnlyDictionary<string, JsonNode> Fill =>
        (Root["Fill"] as JsonObject)?.ToDictionary(p => p.Key, p => p.Value!.DeepClone()) ?? new Dictionary<string, JsonNode>();

    public IReadOnlyDictionary<string, string> Words =>
        (Root["Words"] as JsonObject)?.ToDictionary(p => p.Key, p => (string?)p.Value ?? p.Key) ?? new Dictionary<string, string>();

    public IReadOnlyList<(string Path, string Set)> Names =>
        (Root["Names"] as JsonArray)?.OfType<JsonObject>().Select(n => ((string?)n["Path"] ?? "", (string?)n["Set"] ?? "Present")).ToList() ?? [];

    public JsonObject? Context => Root["Context"] as JsonObject;

    // WHAT THE REQUEST CAN BE ABOUT. Some paths of the schema are offered to the model only when the request has a word that asks for them (louder, quieter: a loudness;
    // undo: an earlier command): a small model told it can do everything does what the name of an object suggests rather than what the person said. If no word of any path
    // is in the request - speech recognition has changed them - every path is offered.
    public IReadOnlyList<(string Path, IReadOnlyList<string> Words)> Triggers =>
        (Root["Triggers"] as JsonArray)?.OfType<JsonObject>().Select(t => ((string?)t["Path"] ?? "", (IReadOnlyList<string>)Texts(t, "Words"))).ToList() ?? [];

    // The guide for this request: the paths that need a word offered if the request has one.
    public ApplicationGuide ForRequest(string request, IReadOnlyDictionary<string, IReadOnlyList<string>>? nameSets = null)
    {
        var triggers = Triggers;
        if (triggers.Count == 0) return this;
        var asked = RequestGuards.Asked(request, this, nameSets ?? new Dictionary<string, IReadOnlyList<string>>());
        if (asked.Count == 0) return this;
        var kept = Include.Where(path => !triggers.Any(t => path == t.Path || path.StartsWith(t.Path + ".", StringComparison.Ordinal)) ||
                                         triggers.Any(t => asked.Contains(t.Path) && (path == t.Path || path.StartsWith(t.Path + ".", StringComparison.Ordinal)))).ToList();
        var copy = (JsonObject)Root.DeepClone();
        copy["Include"] = new JsonArray(kept.Select(k => (JsonNode)JsonValue.Create(k)!).ToArray());
        return new ApplicationGuide(copy);
    }

    // A line of help for the words in the request, given to the model with the request: what a word of direction changes, in the application's own terms.
    public IReadOnlyList<string> HintsFor(string request)
    {
        var lower = request.ToLowerInvariant();
        var result = new List<string>();
        foreach (var hint in (Root["Hints"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var text = (string?)hint["Text"] ?? "";
            if (text.Length > 0 && Texts(hint, "Words").Any(w => System.Text.RegularExpressions.Regex.IsMatch(lower, @"\b" + System.Text.RegularExpressions.Regex.Escape(w.ToLowerInvariant())))) result.Add(text);
        }
        return result;
    }

    // A REQUEST THAT IS ALWAYS THE SAME COMMAND ("what is in the scene" asks the application for a listing): the phrases and the command, which the AIM completes and checks as it
    // does any other and sends without asking the language model.
    public JsonObject? ShortcutFor(string request)
    {
        var lower = request.ToLowerInvariant().Replace("’", "'");
        foreach (var shortcut in (Root["Shortcuts"] as JsonArray)?.OfType<JsonObject>() ?? [])
            if (shortcut["Command"] is JsonObject command && Texts(shortcut, "Phrases").Any(p => lower.Contains(p.ToLowerInvariant()))) return command;
        return null;
    }

    public IReadOnlyList<string> NeedsNumberPhrases => Texts(Root["NeedsNumber"] as JsonObject, "Phrases");
    public IReadOnlyList<string> NumberWords => Texts(Root["NeedsNumber"] as JsonObject, "NumberWords");
    public string NeedsNumberQuestion => (string?)(Root["NeedsNumber"] as JsonObject)?["Question"] ?? "By how much? Please give me the number.";

    // The commands that ask the application what it holds, one after another (the state kept fresh where an application is connected). Each is the content of an Instance.
    public IReadOnlyList<JsonObject> StateRequests => (Root["StateRequests"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];

    public IReadOnlyList<JsonObject> Examples => (Root["Examples"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];

    private string Text(string name) => (string?)Root[name] ?? "";
    private IReadOnlyList<string> Texts(string name) => Texts(Root, name);
    private static IReadOnlyList<string> Texts(JsonObject? of, string name) =>
        (of?[name] as JsonArray)?.Select(x => (string?)x ?? "").Where(x => x.Length > 0).ToList() ?? [];
}
