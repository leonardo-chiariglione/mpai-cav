using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using AIF.Controller;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Mmc.Jsi;

// MMC-JSI-V2.5 - JSON Interface (the author, 2026/10/26).
//
// BETWEEN WHAT A PERSON SAYS, AS TEXT, AND AN APPLICATION DRIVEN BY JSON INSTANCES. The same AIM serves every application: the application is given to it as the entries of the
// Access of its L3 - the schema of the commands (CommandSchema), the schema of the responses (ResponseSchema) and a guide that says what the schemas do not (ApplicationGuide).
// Nothing in this class is about an application.
//
//   Request  (BTO)            the person's words -> the language model, asked for its answer in the shape of the command schema (structured output), given the state of the
//                             application and the commands it sent before -> the answer is completed and checked against the whole schema, and once more, with the fault told, if
//                             it does not conform -> ApplicationCommand: an Application JSON Instance whose Instance conforms to the schema. A request that cannot become a
//                             command - a number is missing, a name is not in the application, the application has no such thing, the answer will not conform - gives NO
//                             command, and Facts that say, in plain words, what is missing.
//   ApplicationResponse (AJI 1)  a response of the application -> Facts: what was done and what was not, in the words of the guide. The AIM does not decide what is said.
//   ApplicationContext (AJI 2)   the state of the application, kept until the next one: the names the application has, and where its things are.
//
// A COMMAND THAT WAS SENT IS NOT SAID: the person is told when the application answers. Facts leave when a request gave no command, and when a response arrives.
public sealed class JsiAimProcessor : IAimProcessor
{
    public const string AjiHeader = "MMC-AJI-V2.5";
    private const int RemembersCommands = 5;

    private readonly string _requestIn, _responseIn, _contextIn, _commandOut, _factsOut;
    private readonly JsiConfiguration _configuration;
    private readonly StructuredModel? _model;
    private readonly ApplicationGuide? _guide;
    private readonly SchemaCheck? _commandCheck, _responseCheck;
    private readonly SchemaLibrary? _library;
    private readonly SemaphoreSlim _one = new(1, 1);

    private ApplicationState _state = new();

    // THE STATE KEPT FRESH (an application is connected: the setting RefreshState): the commands the guide gives for asking the application what it holds (StateRequests) are sent
    // one after another - each when the answer to the one before has come - at the first request, and again after every response the person is told; a request that comes
    // while the state is not fresh is held until it is, so that it is worked out from the scene as it is. What the application answers to them is the state; it is not told.
    private readonly HashSet<string> _stateAsked = new(StringComparer.Ordinal);
    private bool _stale = true;
    private int _chain = -1;                 // the state request asked now, or -1 when none is
    private DateTime _askedAt;
    private string? _held;                   // the request kept until the state is fresh
    private bool Refreshing => _configuration.RefreshesState && HasApplication && _guide!.StateRequests.Count > 0;
    private readonly List<(string Id, string Request)> _sent = [];
    private readonly Dictionary<string, string> _requestOf = new(StringComparer.Ordinal);

    public string InstanceId { get; }

    public JsiAimProcessor(string instanceId, AimPortReader ports, JsiConfiguration configuration, StructuredModel? model)
    {
        InstanceId = instanceId;
        _configuration = configuration;
        _model = model;
        _requestIn = ports.Input("OSD-BTO-V1.5");
        _responseIn = ports.InputOrDefault("MMC-AJI-V2.5", 1, "");
        _contextIn = ports.InputOrDefault("MMC-AJI-V2.5", 2, "");
        _commandOut = ports.OutputOrDefault("MMC-AJI-V2.5", "");
        _factsOut = ports.Output("OSD-BTO-V1.5");

        if (configuration.GuideFile is { } guideFile && File.Exists(guideFile))
        {
            try { _guide = ApplicationGuide.Parse(File.ReadAllText(guideFile)); }
            catch (Exception e) when (e is JsonException or FormatException or IOException) { _guide = null; }
        }
        if (configuration.CommandSchemaUri.Length > 0 && !configuration.CommandSchemaUri.TrimStart().StartsWith("/*", StringComparison.Ordinal))
            _commandCheck = new SchemaCheck(configuration.CommandSchemaUri, configuration.CommandSchemaFile, configuration.SchemasRoot);
        if (configuration.ResponseSchemaUri.Length > 0 && !configuration.ResponseSchemaUri.TrimStart().StartsWith("/*", StringComparison.Ordinal))
            _responseCheck = _commandCheck is not null && configuration.ResponseSchemaUri == configuration.CommandSchemaUri
                ? _commandCheck
                : new SchemaCheck(configuration.ResponseSchemaUri, configuration.ResponseSchemaFile, configuration.SchemasRoot);
        if (_commandCheck?.SchemasRoot is { } root)
            _library = configuration.CommandSchemaFile is { } file
                ? new SchemaLibrary(root, configuration.CommandSchemaUri, file)
                : new SchemaLibrary(root);
    }

    // Does this AIM have an application to serve?
    public bool HasApplication => _guide is not null && _commandCheck is not null;

    public async Task<Message> ProcessAsync(Message message)
    {
        await _one.WaitAsync().ConfigureAwait(false);
        try
        {
            var facts = new List<string>();
            JsonNode? command = null;

            // 1. The state of the application, kept.
            if (Input(message, _contextIn) is { } context && _guide is not null)
                try
                {
                    // Only a listing is a state: another response given as the context is ignored, and the state kept is not wiped.
                    var instance = (JsonNode.Parse(context) as JsonObject)?["Instance"];
                    if (ApplicationState.IsListing(instance, _guide)) _state = ApplicationState.Read(instance, _guide).OnTopOf(_state);
                }
                catch (JsonException) { }

            // 2. A response of the application, told as facts - or, if it answers a question about the state, taken as the state.
            var asked = false;
            if (Input(message, _responseIn) is { } response)
            {
                var told = Respond(response, out var outcome);
                if (told is not null) facts.Add(told);
                if (Refreshing)
                {
                    if (outcome == ResponseKind.Told)
                    {
                        _stale = true;
                        if (_chain < 0) { command = StateRequest(_chain = 0); asked = command is not null; if (!asked) _chain = -1; }
                    }
                    else if (outcome == ResponseKind.State && _chain >= 0)
                    {
                        if (++_chain < _guide!.StateRequests.Count) { command = StateRequest(_chain); asked = command is not null; }
                        if (!asked)
                        {
                            _chain = -1; _stale = false;
                            if (_held is { } held)
                            {
                                _held = null;
                                try
                                {
                                    var (aji, said) = await Understand(held).ConfigureAwait(false);
                                    command = aji;
                                    if (said is not null) facts.Add(said);
                                }
                                catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException)
                                {
                                    return Message.Error(message.MessageId, InstanceId, $"The language model could not be reached (is Ollama running?): {e.Message}");
                                }
                            }
                        }
                    }
                }
            }

            // 3. A request, to become a command.
            if (Input(message, _requestIn) is { } request)
            {
                string text;
                try { text = MpaiJson.FromJson<BasicTextObject>(request)?.GetText() ?? ""; }
                catch (JsonException) { return Message.Error(message.MessageId, InstanceId, "the request is not a Text Object"); }
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (Refreshing && (_stale || _chain >= 0))
                    {
                        // The state is not fresh: the request waits for it, and the chain of questions starts (or starts again, if the application has not answered in time).
                        _held = text.Trim();
                        if (!asked && (_chain < 0 || DateTime.UtcNow - _askedAt > TimeSpan.FromSeconds(10)))
                        {
                            command = StateRequest(_chain = 0);
                            if (command is null) { _chain = -1; _stale = false; _held = null; }
                        }
                    }
                    else
                        try
                        {
                            var (aji, said) = await Understand(text.Trim()).ConfigureAwait(false);
                            command = aji;
                            if (said is not null) facts.Add(said);
                        }
                        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException)
                        {
                            return Message.Error(message.MessageId, InstanceId, $"The language model could not be reached (is Ollama running?): {e.Message}");
                        }
                }
            }

            var ports = new Dictionary<string, string>();
            if (command is not null && _commandOut.Length > 0) ports[_commandOut] = command.ToJsonString();
            if (facts.Count > 0) ports[_factsOut] = MpaiJson.ToJson(BasicTextObject.FromText(string.Join(" ", facts)));
            return new Message { MessageId = message.MessageId, MessageType = ports.Count == 0 ? "Nothing" : message.MessageType, Ports = ports };
        }
        finally { _one.Release(); }
    }

    private static string? Input(Message message, string port) =>
        port.Length > 0 && message.Ports.TryGetValue(port, out var json) && !string.IsNullOrWhiteSpace(json) ? json : null;

    private enum ResponseKind { Told, State, Foreign }

    // The question about the state that the guide gives at that place, as a command of the application (an Application JSON Instance), completed and checked as any command is;
    // it is not remembered as one of the person's.
    private JsonObject? StateRequest(int index)
    {
        _askedAt = DateTime.UtcNow;
        if (_guide is null || _commandCheck is null || _library is null || index >= _guide.StateRequests.Count) return null;
        var view = new ModelView(_library, _guide, _state.NameSets);
        if (view.Build(_commandCheck.Uri) is null) return null;
        var completed = view.Complete(_guide.StateRequests[index].DeepClone());
        if (_commandCheck.Violations(completed.ToJsonString()) is not { Count: 0 }) return null;
        var aji = new JsonObject
        {
            ["Header"] = AjiHeader, ["AJIID"] = Guid.NewGuid().ToString(),
            ["ApplicationSchemaURI"] = _configuration.CommandSchemaUri, ["Instance"] = completed
        };
        _stateAsked.Add((string)aji["AJIID"]!);
        return aji;
    }

    // ---- a request ----------------------------------------------------------------------------------------------------------------------

    private async Task<(JsonNode? Aji, string? Facts)> Understand(string text)
    {
        if (!HasApplication)
            return (null, "No command was sent: this JSON Interface has no application to serve (its Access names no schemas and no guide).");
        var guide = _guide!;

        // A change by an amount the person did not give is asked for, not guessed.
        if (NeedsNumber(text, guide)) return (null, "No command was sent. " + guide.NeedsNumberQuestion);

        // A request that is not for the application, or that works on an object and names none that the application has, is answered without the model.
        if (RequestGuards.Refused(text, guide) is { } refusal) return (null, "No command was sent. " + refusal);
        var names = _state.NameSets;
        if (RequestGuards.NamesNothing(text, guide, RequestGuards.Asked(text, guide, names), (names.GetValueOrDefault("Present") ?? []).Concat(names.GetValueOrDefault("Stored") ?? []).ToList()) is { } unnamed)
            return (null, "No command was sent. " + unnamed);

        if (_commandCheck is null || !_commandCheck.Available || _library is null)
            return (null, "No command was sent: the schema of the commands of the application is not available, so a command cannot be checked.");
        if (_model is null)
            return (null, "No command was sent: there is no language model to understand the request.");

        var view = new ModelView(_library, guide.ForRequest(text, _state.NameSets), _state.NameSets);
        if (view.Build(_commandCheck.Uri) is null)
            return (null, "No command was sent: the schema of the commands of the application cannot be read.");

        // A request that is always the same command is that command, whatever the model would make of it.
        if (guide.ShortcutFor(text) is { } shortcut)
        {
            var completed = view.Complete(shortcut.DeepClone());
            if (_commandCheck.Violations(completed.ToJsonString()) is { Count: 0 })
            {
                var aji = new JsonObject
                {
                    ["Header"] = AjiHeader, ["AJIID"] = Guid.NewGuid().ToString(),
                    ["ApplicationSchemaURI"] = _configuration.CommandSchemaUri, ["Instance"] = completed
                };
                Remember(aji, completed, text, guide);
                return (aji, null);
            }
        }

        var system = SystemText(guide, view);
        var turns = new List<(string Role, string Content)>(ExampleTurns(guide)) { ("user", RequestText(guide, text)) };
        var wrapper = Wrapper(view.ForModel(), guide.Working);

        string problem = "";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var raw = await _model(system, turns, wrapper).ConfigureAwait(false);
            var (instance, said, fault) = Check(raw, view, guide);
            if (instance is not null)
            {
                var aji = new JsonObject
                {
                    ["Header"] = AjiHeader,
                    ["AJIID"] = Guid.NewGuid().ToString(),
                    ["ApplicationSchemaURI"] = _configuration.CommandSchemaUri,
                    ["Instance"] = instance
                };
                Remember(aji, instance, text, guide);
                return (aji, null);
            }
            if (said is not null) return (null, "No command was sent. " + said);
            problem = fault!;
            turns.Add(("assistant", raw));
            turns.Add(("user", $"That is not accepted: {fault} Answer again with the corrected JSON object only."));
        }
        return (null, "No command was sent: the request could not be turned into a command that the application accepts (" + problem.TrimEnd('.') + "). Ask the person to say it again, with the names and the numbers.");
    }

    // What the model gave: a command (completed, checked), a problem it states, or the fault.
    private (JsonNode? Instance, string? Problem, string? Fault) Check(string raw, ModelView view, ApplicationGuide guide)
    {
        JsonNode? answer;
        try { answer = JsonNode.Parse(Unfenced(raw)); }
        catch (JsonException e) { return (null, null, "the answer is not JSON (" + e.Message + ")."); }
        if (answer is not JsonObject o) return (null, null, "the answer is not a JSON object.");
        if (o["Problem"] is JsonValue p && p.TryGetValue<string>(out var problem) && !string.IsNullOrWhiteSpace(problem) && o["Command"] is null)
            return (null, problem.Trim(), null);
        if (o["Command"] is not JsonObject command) return (null, null, "the answer has neither a Command nor a Problem.");

        var completed = view.Complete(command);
        // What the guide requires of every command: not empty where it has to have something in it.
        foreach (var path in guide.Require)
            if (ApplicationState.Select(completed, path).FirstOrDefault() is JsonObject { Count: 0 } or JsonArray { Count: 0 })
                return (null, null, $"{path} is empty: a command has to say what is to be done.");
        var violations = _commandCheck!.Violations(completed.ToJsonString());
        if (violations is null) return (null, null, "the schema of the commands is not available.");
        if (violations.Count > 0) return (null, null, "it does not conform to the schema of the commands: " + Readable(violations, view) + ".");
        return (completed, null, null);
    }

    // What is wrong, said briefly: the validator tells every branch of every alternative it tried; what is told to the model is the deepest places that fail, without the
    // generic wrappers and without what the AIM filled in itself.
    private static string Readable(IReadOnlyList<string> violations, ModelView view)
    {
        var filled = (view.Full["x-fill"] as JsonObject)?.Select(p => "/" + p.Key).ToList() ?? [];
        var generic = new[] { "Some properties did not match", "Some items do not match", "failing indexes", "expected to fail", "should be \"object\"", "should be \"string\"" };
        var specific = violations.Where(v => !generic.Any(g => v.Contains(g, StringComparison.Ordinal)) && !filled.Any(f => v.StartsWith(f, StringComparison.Ordinal)))
                                 .Distinct().OrderByDescending(v => v.Count(c => c == '/')).Take(4).ToList();
        return string.Join("; ", (specific.Count > 0 ? specific : violations.Take(3).ToList()).Select(v => v.Trim()));
    }

    private static string Unfenced(string raw)
    {
        raw = raw.Trim();
        if (raw.StartsWith("```", StringComparison.Ordinal))
        {
            var start = raw.IndexOf('\n');
            var end = raw.LastIndexOf("```", StringComparison.Ordinal);
            if (start >= 0 && end > start) raw = raw[(start + 1)..end];
        }
        return raw;
    }

    private void Remember(JsonObject aji, JsonNode instance, string request, ApplicationGuide guide)
    {
        var id = guide.Earlier.Length > 0 ? (string?)(instance as JsonObject)?[guide.Earlier] : null;
        _sent.Add((id ?? (string?)aji["AJIID"] ?? "", request));
        while (_sent.Count > RemembersCommands) _sent.RemoveAt(0);
        _requestOf[(string)aji["AJIID"]!] = request;
        if (_requestOf.Count > 40) _requestOf.Remove(_requestOf.Keys.First());
        // The AJI of an instance that names an identifier is also known by that identifier.
        if (id is not null) _requestOf[id] = request;
    }

    // Words that ask for a change by an amount, and no number with them.
    public static bool NeedsNumber(string text, ApplicationGuide guide)
    {
        var phrases = guide.NeedsNumberPhrases;
        if (phrases.Count == 0) return false;
        var lower = text.ToLowerInvariant();
        if (!phrases.Any(p => Regex.IsMatch(lower, @"\b" + Regex.Escape(p.ToLowerInvariant()) + @"\b"))) return false;
        if (Regex.IsMatch(lower, @"\d")) return false;
        return !guide.NumberWords.Any(w => Regex.IsMatch(lower, @"\b" + Regex.Escape(w.ToLowerInvariant()) + @"\b"));
    }

    // The answer is either a command or a problem, each after a line of working: the model says first, in a sentence, what is asked and the numbers it works from - the
    // order of the properties is the order it writes them - and the command follows from what it has just written.
    private static JsonNode Wrapper(JsonObject command, bool working)
    {
        JsonObject Alternative(string key, JsonNode value)
        {
            var properties = new JsonObject();
            var required = new JsonArray();
            if (working) { properties["Working"] = new JsonObject { ["type"] = "string" }; required.Add("Working"); }
            properties[key] = value;
            required.Add(key);
            return new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["required"] = required, ["properties"] = properties };
        }
        return new JsonObject { ["anyOf"] = new JsonArray(Alternative("Command", command.DeepClone()), Alternative("Problem", new JsonObject { ["type"] = "string" })) };
    }

    // ---- what the model is told -------------------------------------------------------------------------------------------------------------

    // What does not change from one request to the next: what the model is, the shape of the answer, the conventions, the rules. Then the examples, as turns.
    private static string SystemText(ApplicationGuide guide, ModelView view)
    {
        var text = new StringBuilder();
        text.Append("You turn what a person says into a command for an application. ").Append(guide.Application).Append('\n');
        if (guide.Working)
        {
            text.Append("Answer with ONE JSON object and nothing else: {\"Working\": ..., \"Command\": ...} or {\"Working\": ..., \"Problem\": \"...\"}. ");
            text.Append("Working is one short line: the field you use and the numbers you work from and those that result.\n\n");
        }
        else text.Append("Answer with ONE JSON object and nothing else: {\"Command\": ...} or {\"Problem\": \"...\"}.\n\n");
        text.Append("A Command has this shape (no other field exists):\n").Append(view.Outline()).Append("\n\n");
        if (guide.Conventions.Length > 0) text.Append(guide.Conventions.Trim()).Append("\n\n");
        text.Append("Rules:\n");
        text.Append("- Every number in a Command is a number the person said, or one you work out from the state of the application. Never invent a number.\n");
        text.Append("- Use the names exactly as the state of the application gives them. Speech recognition makes errors: read a word as the name it most likely stands for.\n");
        text.Append("- Do only what is asked. If the request needs a number it does not give, names something the application does not have, could mean more than one thing, or is not for the application, answer a Problem: one short plain sentence that says what is missing, or asks for it.\n");
        return text.ToString();
    }

    private static IEnumerable<(string Role, string Content)> ExampleTurns(ApplicationGuide guide)
    {
        foreach (var example in guide.Examples)
        {
            var user = new StringBuilder();
            user.Append(StateText((string?)example["State"], (string?)example["Earlier"])).Append("Request: ").Append((string?)example["Request"]);
            yield return ("user", user.ToString());
            var answer = new JsonObject();
            if (guide.Working) answer["Working"] = (string?)example["Working"] ?? "";
            if (example["Command"] is { } c) answer["Command"] = c.DeepClone(); else answer["Problem"] = (string?)example["Problem"];
            yield return ("assistant", answer.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
    }

    private static string StateText(string? state, string? earlier)
    {
        var text = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(state)) text.Append("State of the application:\n").Append(state.Trim()).Append('\n');
        if (!string.IsNullOrWhiteSpace(earlier)) text.Append("Commands sent before, the last at the end:\n").Append(earlier.Trim()).Append('\n');
        return text.ToString();
    }

    // The request, with the state of the application as it is now and the commands sent before.
    private string RequestText(ApplicationGuide guide, string request)
    {
        string? earlier = null;
        if (_sent.Count > 0 && guide.Earlier.Length > 0)
            earlier = string.Join('\n', _sent.Select(s => "- " + s.Id + ": \"" + s.Request + "\""));
        var hints = string.Concat(guide.HintsFor(request).Select(h => "Hint: " + h + "\n"));
        return StateText(_state.Tell(guide), earlier) + hints + (guide.Reminder.Length > 0 ? "Reminder: " + guide.Reminder + "\n" : "") + "Request: " + request;
    }

    // ---- a response ---------------------------------------------------------------------------------------------------------------------

    // The facts a response tells; none for the answer to a command this interface did not send - the application's user agent asked for it - and, if it lists, it is the state.
    private string? Respond(string json, out ResponseKind outcome)
    {
        outcome = ResponseKind.Told;
        JsonObject? aji;
        try { aji = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { aji = null; }
        if (aji?["Instance"] is not JsonObject instance)
            return "The application sent a response that this interface cannot read.";
        if ((string?)aji["InReplyTo"] is { } stateOf && _stateAsked.Remove(stateOf))
        {
            outcome = ResponseKind.State;
            if (_guide is not null && ApplicationState.IsListing(instance, _guide)) _state = ApplicationState.Read(instance, _guide).OnTopOf(_state);
            return null;
        }
        if (_guide is null) return "The application sent a response, but this interface has no guide to tell it.";

        var uri = (string?)aji["ApplicationSchemaURI"];
        if (_responseCheck is not null && uri is not null && uri != _responseCheck.Uri)
            return "The application sent a response in a form this interface does not know (" + uri + ").";
        if (_responseCheck is { Available: true } && _responseCheck.Violations(instance.ToJsonString()) is { Count: > 0 } violations)
            return "The application sent a response that does not conform to its own schema: " + string.Join("; ", violations.Take(3)) + ".";

        var replyTo = (string?)aji["InReplyTo"];
        // An answer to a command that this interface did not send was asked for by the application's user agent: it is not for the person. A listing in it is the state.
        if (replyTo is not null && !_requestOf.ContainsKey(replyTo) && !_sent.Any(s => s.Id == replyTo))
        {
            outcome = ResponseKind.Foreign;
            if (ApplicationState.IsListing(instance, _guide)) _state = ApplicationState.Read(instance, _guide).OnTopOf(_state);
            return null;
        }
        string? asked = replyTo is not null && _requestOf.TryGetValue(replyTo, out var r) ? r : null;
        if (asked is null && replyTo is not null)
            asked = _sent.FirstOrDefault(s => s.Id == replyTo).Request;
        return ResponseFacts.Tell(instance, _guide, asked);
    }
}
