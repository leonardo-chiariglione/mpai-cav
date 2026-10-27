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

// MMC-JSI-V2.5 - JSON Interface (the author, 2026/10/26; the User Command as the command and the response, 2026/10/27).
//
// BETWEEN WHAT A PERSON SAYS, AS TEXT, AND AN APPLICATION DRIVEN BY JSON INSTANCES. The same AIM serves every application: the application is given to it as the entries of the
// Access of its L3 - the schema of the commands (CommandSchema), the schema of the responses (ResponseSchema) and a guide that says what the schemas do not (ApplicationContext); they
// are the Application Context, the standing description of the application. Nothing in this class is about an application.
//
//   Request  (BTO)            the person's words -> the language model, asked for its answer in the shape of the command schema (structured output), given what the AIM knows of
//                             the state of the application and the commands it sent before -> the answer is completed and checked against the whole schema, and once more, with the
//                             fault told, if it does not conform -> ApplicationCommand: a User Command request (CAE-UCM #1) that conforms to the schema. A request that cannot become a
//                             command - a number is missing, a name is not in the application, the application has no such thing, the answer will not conform - gives NO command,
//                             and a Response Hint that says, in plain words, what is missing.
//   ApplicationResponse (CAE-UCM #1)  a User Command response, which names the command it answers by its UserCommandID -> the Response Hint: what was done and what was not, in the
//                             words of the guide. The AIM does not decide what is said. When several AIMs answer one command (Responders), their answers are gathered and merged into
//                             one response before it is told. A response that lists is the state of the application, kept until the next one.
//
// A COMMAND THAT WAS SENT IS NOT SAID: the person is told when the application answers. A Response Hint leaves when a request gave no command, and when a response arrives.
public sealed class JsiAimProcessor : IAimProcessor
{
    private const int RemembersCommands = 5;
    private const string Ucm = "CAE-UCM-V1.0";
    private static readonly TimeSpan GatherFor = TimeSpan.FromSeconds(10);

    private readonly string _requestIn, _responseIn, _contextIn, _commandOut, _hintOut;
    private const string ContextDataType = "MMC-APC-V2.5";
    private const string ContextSchemaUri = SchemaCheck.Authority + "MMC/V2.5/data/ApplicationContext.json";
    private readonly string? _schemasRoot;
    private readonly JsiConfiguration _configuration;
    private readonly StructuredModel? _model;
    private ApplicationContext? _guide;   // from the Access of the L3, or from the Application Context Port when one arrives
    private readonly SchemaCheck? _commandCheck, _responseCheck;
    private readonly SchemaLibrary? _library;
    private readonly SemaphoreSlim _one = new(1, 1);

    private ApplicationState _state = new();
    private readonly Dictionary<string, double> _memory = new(StringComparer.Ordinal);      // numbers a rule of the grammar keeps for the next request (the size of the room)

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

    // THE ANSWERS TO ONE COMMAND, when more than one AIM answers it (Responders): kept by the UserCommandID until all have answered - or, if one does not, until GatherFor has passed.
    private readonly Dictionary<string, (DateTime Since, List<UserCommand> Answers)> _gathering = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _requestOf = new(StringComparer.Ordinal);

    public string InstanceId { get; }

    public JsiAimProcessor(string instanceId, AimPortReader ports, JsiConfiguration configuration, StructuredModel? model)
    {
        InstanceId = instanceId;
        _configuration = configuration;
        _model = model;
        _requestIn = ports.Input("OSD-BTO-V1.5");
        _responseIn = ports.InputOrDefault(Ucm, 1, "");
        _contextIn = ports.InputOrDefault(ContextDataType, 1, "");
        _schemasRoot = configuration.SchemasRoot;
        _commandOut = ports.OutputOrDefault(Ucm, 1, "");
        _hintOut = ports.Output("OSD-BTO-V1.5");

        if (configuration.ContextFile is { } guideFile && File.Exists(guideFile))
        {
            try { _guide = ApplicationContext.Parse(File.ReadAllText(guideFile)); }
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
            // 0. An Application Context that arrives on its Port replaces the one read from Access, if it follows the structure of an Application Context.
            if (Input(message, _contextIn) is { } context) AcceptContext(context);

            var hints = new List<string>();
            JsonNode? command = null;

            // 1. A response of the application, told as the Response Hint - or, if it answers a question about the state, taken as the state. A gathering that has waited too long is
            // told with the answers it has.
            var asked = false;
            var responses = new List<(string Json, bool Merged)>();
            if (Input(message, _responseIn) is { } response) responses.Add((response, false));
            foreach (var late in Overdue()) responses.Add((late, true));
            foreach (var (each, merged) in responses)
            {
                var told = Respond(each, out var outcome, merged);
                if (told is not null) hints.Add(told);
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
                                    var (made, said) = await Understand(held).ConfigureAwait(false);
                                    command = made;
                                    if (said is not null) hints.Add(said);
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

            // 2. A request, to become a command.
            if (Input(message, _requestIn) is { } request)
            {
                string text;
                try { text = MpaiJson.FromJson<BasicTextObject>(request)?.GetText() ?? ""; }
                catch (JsonException) { return Message.Error(message.MessageId, InstanceId, "the request is not a Text Object"); }
                if (IsNoise(text)) text = "";           // what a recogniser writes for a noise: nothing is said, nothing is answered
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
                            var (made, said) = await Understand(text.Trim()).ConfigureAwait(false);
                            command = made;
                            if (said is not null) hints.Add(said);
                        }
                        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException)
                        {
                            return Message.Error(message.MessageId, InstanceId, $"The language model could not be reached (is Ollama running?): {e.Message}");
                        }
                }
            }

            var ports = new Dictionary<string, string>();
            if (command is not null && _commandOut.Length > 0) ports[_commandOut] = command.ToJsonString();
            if (hints.Count > 0) ports[_hintOut] = MpaiJson.ToJson(BasicTextObject.FromText(string.Join(" ", hints)));
            return new Message { MessageId = message.MessageId, MessageType = ports.Count == 0 ? "Nothing" : message.MessageType, Ports = ports };
        }
        finally { _one.Release(); }
    }

    // An Application Context given on the Port: used for the requests that follow, unless it does not follow the structure of an Application Context (then it is ignored and the
    // one in use stays). Where the schema cannot be found the context is taken as it comes.
    private void AcceptContext(string json)
    {
        try
        {
            var violations = new SchemaCheck(ContextSchemaUri, null, _schemasRoot).Violations(json);
            if (violations is { Count: > 0 }) return;
            var parsed = ApplicationContext.Parse(json);
            if (parsed.Root["Header"]?.GetValue<string>() != "MMC-APC-V2.5") return;
            _guide = parsed;
            _stale = true;
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException) { }
    }

    private static readonly HashSet<string> Fillers = new(StringComparer.OrdinalIgnoreCase)
    { "you", "thank you", "thanks", "thank you for watching", "thanks for watching", "bye", "bye bye", "okay", "ok", "um", "uh", "hmm", "mm", "the", "so", "yeah" };

    // A NOISE THE RECOGNISER WROTE WORDS FOR: a lone word or a filler it makes up of silence ("you", "thank you for watching"), a lone word of fewer than four letters. A longer lone word ("undo", "stop") is a command.
    public static bool IsNoise(string text)
    {
        var plain = new string(text.Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == (char)39).ToArray()).Trim();
        if (plain.Length == 0) return true;
        if (Fillers.Contains(plain)) return true;
        var words = plain.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length < 2 && plain.Length < 4;
    }

    // What the interface itself says to the person, as it is: said as it is where the dialogue is not asked (SayPlainly).
    private string Said(string what) => _configuration.SaysPlainly ? ResponseFacts.SayAsItIs + what : "No command was formed. " + what;

    private static string? Input(Message message, string port) =>
        port.Length > 0 && message.Ports.TryGetValue(port, out var json) && !string.IsNullOrWhiteSpace(json) ? json : null;

    private enum ResponseKind { Told, State, Foreign, Gathering }

    // The identifier the application knows a command by (the property the guide names as Earlier: the UserCommandID).
    private string? IdOf(JsonNode? command) => (command as JsonObject)?[_guide?.Earlier is { Length: > 0 } key ? key : "UserCommandID"] is JsonValue v && v.TryGetValue<string>(out var id) && id.Length > 0 ? id : null;

    // The question about the state that the guide gives at that place, as a command of the application, completed and checked as any command is;
    // it is not remembered as one of the person's.
    private JsonObject? StateRequest(int index)
    {
        _askedAt = DateTime.UtcNow;
        if (_guide is null || _commandCheck is null || _library is null || index >= _guide.StateRequests.Count) return null;
        var view = new ModelView(_library, _guide, _state.NameSets);
        if (view.Build(_commandCheck.Uri) is null) return null;
        var completed = view.Complete(_guide.StateRequests[index].DeepClone());
        if (_commandCheck.Violations(completed.ToJsonString()) is not { Count: 0 }) return null;
        if (IdOf(completed) is not { } id) return null;
        _stateAsked.Add(id);
        return completed as JsonObject;
    }

    // ---- a request ----------------------------------------------------------------------------------------------------------------------

    private async Task<(JsonNode? Command, string? Hint)> Understand(string text)
    {
        if (!HasApplication)
            return (null, "No command was formed: this JSON Interface has no application to serve (its Access names no schemas and no guide).");
        var guide = _guide!;

        // A change by an amount the person did not give is asked for, not guessed.
        if (NeedsNumber(text, guide)) return (null, "No command was formed. " + guide.NeedsNumberQuestion);

        // A request that is not for the application, or that works on an object and names none that the application has, is answered without the model.
        if (RequestGuards.Refused(text, guide) is { } refusal) return (null, "No command was formed. " + refusal);
        var names = _state.NameSets;
        if (RequestGuards.NamesNothing(text, guide, RequestGuards.Asked(text, guide, names), (names.GetValueOrDefault("Present") ?? []).Concat(names.GetValueOrDefault("Stored") ?? []).ToList()) is { } unnamed)
            return (null, "No command was formed. " + unnamed);

        if (_commandCheck is null || !_commandCheck.Available || _library is null)
            return (null, "No command was formed: the schema of the commands of the application is not available, so a command cannot be checked.");
        if (_model is null)
            return (null, "No command was formed: there is no language model to understand the request.");

        var view = new ModelView(_library, guide.ForRequest(text, _state.NameSets), _state.NameSets);
        if (view.Build(_commandCheck.Uri) is null)
            return (null, "No command was formed: the schema of the commands of the application cannot be read.");

        // A request that is always the same command is that command, whatever the model would make of it.
        if (guide.ShortcutFor(text) is { } shortcut)
        {
            var completed = view.Complete(shortcut.DeepClone());
            if (_commandCheck.Violations(completed.ToJsonString()) is { Count: 0 })
            {
                if (completed["UserCommandRQ"]?["ListedObjects"] is null) Remember(completed, text);
                else if (IdOf(completed) is { Length: > 0 } listingId) _requestOf[listingId] = text;      // its answer is told, but a listing is not one of the person's commands to be undone
                return (completed, null);
            }
        }

        // THE GRAMMAR OF THE APPLICATION FIRST (no language model): a request one of its rules matches is a command at once, checked against the schema as any is.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var last = _sent.LastOrDefault(x => x.Id.Length > 0).Id;
        if (_configuration.UsesGrammar && guide.Rules.Count > 0 && RuleGrammar.Match(text, guide.Rules, _state.NameSets, _state, string.IsNullOrEmpty(last) ? null : last, _memory) is { } ruled)
        {
            var completed = view.Complete(ruled);
            if (_commandCheck.Violations(completed.ToJsonString()) is { Count: 0 })
            {
                AimLog.Write(InstanceId, $"[timing] understood by the grammar: {watch.ElapsedMilliseconds} ms");
                Remember(completed, text);
                return (completed, null);
            }
        }

        // WHAT THE GRAMMAR DID NOT MATCH, AND IS NOT FOR THE MODEL: a request that needs something it does not give is asked for, and one that says nothing the application knows is not guessed at.
        if (_configuration.UsesGrammar && guide.Rules.Count > 0)
        {
            var lower = text.ToLowerInvariant();
            if (guide.Asks.FirstOrDefault(a => a.Words.Any(w => lower.Contains(w.ToLowerInvariant()))) is { Say.Length: > 0 } ask) return (null, Said(ask.Say));
            var known = guide.Triggers.SelectMany(t => t.Words).Concat(_state.NameSets.Values.SelectMany(v => v)).Where(w => w.Length > 2 && !w.StartsWith("{"))
                             .Any(w => Regex.IsMatch(lower, @"\b" + Regex.Escape(w.Trim().ToLowerInvariant())));
            if (!known && guide.Try.Length > 0) return (null, Said($"I did not understand: {text.Trim().TrimEnd('.')}. {guide.Try}"));
        }

        var system = SystemText(guide, view);
        var turns = new List<(string Role, string Content)>(ExampleTurns(guide)) { ("user", RequestText(guide, text)) };
        var wrapper = Wrapper(view.ForModel(), guide.Working);

        string problem = "";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var raw = await _model(system, turns, wrapper).ConfigureAwait(false);
            AimLog.Write(InstanceId, $"[timing] language model, attempt {attempt + 1}: {clock.ElapsedMilliseconds} ms");
            var (instance, said, fault) = Check(raw, view, guide);
            if (instance is not null)
            {
                Remember(instance, text);
                return (instance, null);
            }
            if (said is not null) return (null, "No command was formed. " + said);
            problem = fault!;
            turns.Add(("assistant", raw));
            turns.Add(("user", $"That is not accepted: {fault} Answer again with the corrected JSON object only."));
        }
        return (null, "No command was formed: the request could not be turned into a command that the application accepts (" + problem.TrimEnd('.') + "). Ask the person to say it again, with the names and the numbers.");
    }

    // What the model gave: a command (completed, checked), a problem it states, or the fault.
    private (JsonNode? Instance, string? Problem, string? Fault) Check(string raw, ModelView view, ApplicationContext guide)
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

    private void Remember(JsonNode command, string request)
    {
        var id = IdOf(command) ?? "";
        _sent.Add((id, request));
        while (_sent.Count > RemembersCommands) _sent.RemoveAt(0);
        if (id.Length > 0)
        {
            _requestOf[id] = request;
            if (_requestOf.Count > 40) _requestOf.Remove(_requestOf.Keys.First());
        }
    }

    // Words that ask for a change by an amount, and no number with them.
    public static bool NeedsNumber(string text, ApplicationContext guide)
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
    private static string SystemText(ApplicationContext guide, ModelView view)
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

    private static IEnumerable<(string Role, string Content)> ExampleTurns(ApplicationContext guide)
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
    private string RequestText(ApplicationContext guide, string request)
    {
        string? earlier = null;
        if (_sent.Count > 0 && guide.Earlier.Length > 0)
            earlier = string.Join('\n', _sent.Select(s => "- " + s.Id + ": \"" + s.Request + "\""));
        var hints = string.Concat(guide.HintsFor(request).Select(h => "Hint: " + h + "\n"));
        return StateText(_state.Tell(guide), earlier) + hints + (guide.Reminder.Length > 0 ? "Reminder: " + guide.Reminder + "\n" : "") + "Request: " + request;
    }

    // ---- a response ---------------------------------------------------------------------------------------------------------------------

    // The Response Hint a response gives; none for the answer to a command this interface did not send - the application's user agent asked for it - and, if it lists, it is the
    // state. When more than one AIM answers a command, nothing until all have: the answers are one response.
    private string? Respond(string json, out ResponseKind outcome, bool merged = false)
    {
        outcome = ResponseKind.Told;
        JsonObject? instance;
        try { instance = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { instance = null; }
        if (instance is null || IdOf(instance) is not { } id)
            return "The application sent a response that this interface cannot read.";

        var known = _stateAsked.Contains(id) || _requestOf.ContainsKey(id) || _sent.Any(s => s.Id == id);
        if (!known)
        {
            // An answer to a command that this interface did not send was asked for by the application's user agent: it is not for the person. A listing in it is the state.
            outcome = ResponseKind.Foreign;
            if (_guide is not null && ApplicationState.IsListing(instance, _guide)) _state = ApplicationState.Read(instance, _guide).OnTopOf(_state);
            return null;
        }

        if (!merged && _configuration.Responders > 1)
        {
            UserCommand? answer = null;
            try { answer = MpaiJson.FromJson<UserCommand>(json); } catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or NotSupportedException) { }
            if (answer is not null)
            {
                if (!_gathering.TryGetValue(id, out var gathered)) _gathering[id] = gathered = (DateTime.UtcNow, []);
                gathered.Answers.Add(answer);
                if (gathered.Answers.Count < _configuration.Responders) { outcome = ResponseKind.Gathering; return null; }
                _gathering.Remove(id);
                instance = (JsonObject)JsonNode.Parse(MpaiJson.ToJson(UserCommandMerge.Merge(id, gathered.Answers)))!;
            }
        }
        return Tell(instance, id, out outcome);
    }

    // The gatherings that have waited too long: what has been answered, merged, as the response.
    private IEnumerable<string> Overdue()
    {
        foreach (var (id, gathered) in _gathering.Where(g => DateTime.UtcNow - g.Value.Since > GatherFor).ToList())
        {
            _gathering.Remove(id);
            yield return MpaiJson.ToJson(UserCommandMerge.Merge(id, gathered.Answers));
        }
    }

    private string? Tell(JsonObject instance, string id, out ResponseKind outcome)
    {
        outcome = ResponseKind.Told;
        if (_stateAsked.Remove(id))
        {
            outcome = ResponseKind.State;
            if (_guide is not null && ApplicationState.IsListing(instance, _guide)) _state = ApplicationState.Read(instance, _guide).OnTopOf(_state);
            return null;
        }
        if (_guide is null) return "The application sent a response, but this interface has no guide to tell it.";
        if (_responseCheck is { Available: true } && _responseCheck.Violations(instance.ToJsonString()) is { Count: > 0 } violations)
            return "The application sent a response that does not conform to its own schema: " + string.Join("; ", violations.Take(3)) + ".";

        var asked = _requestOf.TryGetValue(id, out var r) ? r : _sent.FirstOrDefault(s => s.Id == id).Request;
        return ResponseFacts.Tell(instance, _guide, asked, _configuration.SaysPlainly);
    }
}
