using System.Text.Json;
using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Core;

namespace Mpai.Cae.Ami;

// CAE-AMI-V1.0 - Audio Scene Management Interface (the author, 2026/10/26).
//
// THE ONE AIM THAT TOUCHES THE BOUNDARY of the Composite CAE-ASM. Drawn as a vertical AIM at the far left of the Composite, its left-hand side is the boundary - the Ports of the
// Composite - and its right-hand side serves the Editing AIMs exactly as the boundary served them before: the same Data Types, the same Port Numbers on the left; to each Editing
// AIM what the boundary gave it, and from each what it gave the boundary.
//
//   Left-hand Ports      Inputs:  User Command (RQ) 1-5, User PoV 1-5, Audio Object, Audio Scene, Speech Object, Speech Scene, User AJI Input
//                        Outputs: User Command (RS) 1-5, Audio Object, Audio Scene, Speech Object, Speech Scene, Multimodal Scene, User AJI Output
//   Right-hand Ports     Outputs: User Command (RQ) 6-10 and User PoV 1-5 to Audio Object, Audio Scene, Speech Object, Speech Scene and Multimodal Scene Editing (the numbers
//                        are those of the left, and 5 more for a User Command), and the Object or the Scene Port 2 to the Editing AIM that takes it
//                        Inputs:  User Command (RS) 6-10, and the Object or the Scene Port 2 (the Multimodal Scene Port 1) from the Editing AIM that gives it
//
// IT FORWARDS, UNCHANGED, what the boundary forwarded: a User Command arriving at the left-hand Port k goes to the Editing AIM k; its User Command response (RS) comes back from
// that AIM and leaves at the left-hand Port k. It edits nothing and keeps nothing of the Objects and the Scenes.
//
// THE LIST OF ALL. A ListedObjects whose Kind is All is sent to every Editing AIM; their answers are merged into one response - the entries of each, in the order of the Editing
// AIMs, under the one UserCommandID - and that is what leaves, where the command came from.
//
// THE APPLICATION INTERFACE (AJI). An Application JSON Instance (MMC-AJI) arriving is checked against the Access of this AIM (the schema of the User Command, Name CommandSchema):
// its ApplicationSchemaURI has to be that schema's address, and its Instance has to conform to it and to be a request (UserCommandRQ). It is then sent to the Editing AIMs as any
// other User Command, and their response (RS) leaves wrapped in an Application JSON Instance: InReplyTo is the AJIID of the one it answers, ApplicationSchemaURI the same, Instance
// the response. An Instance that does not conform is answered - no exception - by a response that reports it Failed, wrapped the same way.
//
// WHICH EDITING AIMS A COMMAND OF AN AJI REACHES is decided from the command itself (a User Command arriving at a Port is for the AIM of that Port): see Route. A command whose
// answer comes from more than one AIM gets one response, the answers merged (Merge).
//
// A User Command that the Editing AIMs answer only once all have answered (the List of All, and the commands that go to more than one) is held until then. Two things for one Port in one
// run (an AJI's request and a forwarded one for the same Editing AIM) are not lost: the second leaves in the next run.
public sealed class AmiAimProcessor : IAimProcessor
{
    private const string Ucm = "CAE-UCM-V1.0", Opv = "OSD-OPV-V1.5", Auo = "OSD-AUO-V1.5", Asd = "OSD-ASD-V1.5", Spo = "OSD-SPO-V1.5", Ssd = "OSD-SSD-V1.5", Msd = "OSD-MSD-V1.5",
                         AjiType = "MMC-AJI-V2.5";
    public const string AjiHeader = "MMC-AJI-V2.5";

    // The Editing AIMs, in the order of the Ports of the Composite that serve them.
    public static readonly string[] Editors =
        ["Audio Object Editing", "Audio Scene Editing", "Speech Object Editing", "Speech Scene Editing", "Multimodal Scene Editing"];

    private readonly string _rqIn, _povIn;      // the one User Command and the one User PoV the boundary gives
    private readonly string[] _rsIn = new string[6], _rsOut = new string[6], _rqOut = new string[6], _povOut = new string[6];
    private readonly (string In, string Out)[] _toEditors, _fromEditors;
    private readonly string _ajiIn, _ajiOut;
    private readonly CommandSchemaCheck? _schema;

    private readonly object _lock = new();
    private sealed class Pending
    {
        public required HashSet<int> Expected { get; init; }
        public Dictionary<int, UserCommand> Got { get; } = new();
        public int? OriginPort { get; init; }           // the left-hand Port k the command came by; else it came by the AJI
        public string? AjiId { get; init; }
        public string? SchemaUri { get; init; }
    }
    private readonly Dictionary<string, Pending> _pending = new();
    private readonly Dictionary<string, int[]> _sentTo = new();
    private readonly Queue<string> _sentOrder = new();
    private readonly Queue<(string Port, string Json)> _held = new();
    private readonly HashSet<string> _scenes = new(StringComparer.Ordinal);     // the names of the Scenes that have left an Editing AIM

    public string InstanceId { get; }

    public AmiAimProcessor(string instanceId, AimPortReader ports, CommandSchemaCheck? schema = null)
    {
        InstanceId = instanceId;
        _schema = schema;
        for (var k = 1; k <= 5; k++)
        {
            _rsIn[k] = ports.Input(Ucm, k + 5);
            _rsOut[k] = ports.Output(Ucm, k);
            _rqOut[k] = ports.Output(Ucm, k + 5);
            _povOut[k] = ports.Output(Opv, k);
        }
        _rqIn = ports.Input(Ucm, 1);
        _povIn = ports.Input(Opv, 1);
        // To the Editing AIMs: what arrives at the left-hand Port leaves at the right-hand Port 2.
        _toEditors = [(ports.Input(Auo, 1), ports.Output(Auo, 2)), (ports.Input(Asd, 1), ports.Output(Asd, 2)),
                      (ports.Input(Spo, 1), ports.Output(Spo, 2)), (ports.Input(Ssd, 1), ports.Output(Ssd, 2))];
        // From the Editing AIMs: what arrives at the right-hand Port leaves at the left-hand Port 1.
        _fromEditors = [(ports.Input(Auo, 2), ports.Output(Auo, 1)), (ports.Input(Asd, 2), ports.Output(Asd, 1)),
                        (ports.Input(Spo, 2), ports.Output(Spo, 1)), (ports.Input(Ssd, 2), ports.Output(Ssd, 1)),
                        (ports.Input(Msd, 1), ports.Output(Msd, 1))];
        _ajiIn = ports.InputOrDefault(AjiType, "");
        _ajiOut = ports.OutputOrDefault(AjiType, "");
    }

    public Task<Message> ProcessAsync(Message message)
    {
        lock (_lock)
        {
            var outputs = new Dictionary<string, string>();
            string? In(string port) =>
                port.Length > 0 && message.Ports.TryGetValue(port, out var json) && !string.IsNullOrWhiteSpace(json) ? json : null;

            // What an earlier run could not send.
            for (var left = _held.Count; left > 0; left--)
            {
                var (heldPort, heldJson) = _held.Dequeue();
                Emit(outputs, heldPort, heldJson);
            }

            // 1. what the boundary gives the Editing AIMs: User Commands, User PoVs, Objects and Scenes - forwarded as they are.
            if (In(_rqIn) is { } command) Forward(command, outputs);
            if (In(_povIn) is { } pov)
                for (var k = 1; k <= 5; k++) Emit(outputs, _povOut[k], pov);       // to each Editing AIM, on its own Port
            foreach (var (from, to) in _toEditors)
                if (In(from) is { } data) Emit(outputs, to, data);

            // 2. what the Editing AIMs give the boundary: Objects, Scenes, and the responses to User Commands.
            foreach (var (from, to) in _fromEditors)
                if (In(from) is { } data) { NoteScene(data); Emit(outputs, to, data); }
            for (var k = 1; k <= 5; k++)
                if (In(_rsIn[k]) is { } response) Respond(k, response, outputs);

            // 3. the commands of an application interface.
            if (In(_ajiIn) is { } aji) Instance(aji, outputs);

            return Task.FromResult(new Message
            {
                MessageId = Guid.NewGuid().ToString(),
                MessageType = outputs.Count == 0 ? "Nothing" : "AudioSceneManagement",
                Ports = outputs
            });
        }
    }

    private void NoteScene(string json)
    {
        var node = ParseObject(json);
        foreach (var field in new[] { "AudioSceneDescriptorsID", "SpeechSceneDescriptorsID", "MultimodalSceneDescriptorsID" })
            if (Text(node?[field]) is { Length: > 0 } name) _scenes.Add(name);
    }

    private void Emit(Dictionary<string, string> outputs, string port, string json)
    {
        if (port.Length == 0) return;
        if (outputs.ContainsKey(port)) _held.Enqueue((port, json));
        else outputs[port] = json;
    }

    private void Remember(string id, int[] editors)
    {
        if (id.Length == 0) return;
        if (!_sentTo.ContainsKey(id)) _sentOrder.Enqueue(id);
        _sentTo[id] = editors;
        while (_sentOrder.Count > 500) _sentTo.Remove(_sentOrder.Dequeue());
    }

    private static JsonObject? ParseObject(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // ---- the commands of the boundary --------------------------------------------------------------------------------------------------

    // THE ONE USER COMMAND OF THE BOUNDARY goes to the Editing AIMs that can act on it (Route), each on its own Port. Each response leaves, unchanged, at the response Port of its
    // AIM; the list of all alone is held until every AIM has answered and leaves as one, at the first response Port.
    private void Forward(string json, Dictionary<string, string> outputs)
    {
        var node = ParseObject(json);
        var id = Text(node?["UserCommandID"]) ?? "";
        UserCommand? command = null;
        try { command = MpaiJson.FromJson<UserCommand>(json); } catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or NotSupportedException) { }
        var editors = command?.UserCommandRQ is { } rq ? Route(rq, i => _sentTo.GetValueOrDefault(i), _scenes.Contains) : [1];     // one that cannot be read goes to the first, which reports it
        Remember(id, editors);
        // Only the list of all is one answer; the others leave as the Editing AIMs gave them, each at its own response Port.
        if (IsListOfAll(node) && id.Length > 0 && !_pending.ContainsKey(id))
            _pending[id] = new Pending { Expected = editors.ToHashSet(), OriginPort = 1 };
        foreach (var e in editors) Emit(outputs, _rqOut[e], json);
    }

    private static bool IsListOfAll(JsonObject? command) =>
        Text(command?["UserCommandRQ"]?["ListedObjects"]?["Kind"]) == ListKinds.All;

    private void Respond(int k, string json, Dictionary<string, string> outputs)
    {
        var node = ParseObject(json);
        var id = Text(node?["UserCommandID"]) ?? "";
        if (id.Length == 0 || !_pending.TryGetValue(id, out var pending) || !pending.Expected.Contains(k))
        {
            Emit(outputs, _rsOut[k], json);        // an answer to a command that was forwarded: back, unchanged, where it was asked
            return;
        }
        UserCommand? answer = null;
        try { answer = MpaiJson.FromJson<UserCommand>(json); } catch (Exception e) when (e is JsonException or InvalidOperationException) { }
        pending.Got[k] = answer ?? Unreadable(id, Editors[k - 1]);
        if (pending.Got.Count < pending.Expected.Count) return;

        _pending.Remove(id);
        var merged = Merge(id, pending.Expected.Order().Select(e => pending.Got[e]).ToList());
        if (pending.OriginPort is { } origin) Emit(outputs, _rsOut[origin], MpaiJson.ToJson(merged));
        else Emit(outputs, _ajiOut, Wrap(pending.AjiId, pending.SchemaUri ?? _schema?.Uri ?? "", merged));
    }

    private static UserCommand Unreadable(string id, string editor)
    {
        var report = new CommandReport(new UserCommand { UserCommandID = id });
        report.Failed("AcquiredObject", null, $"the response of {editor} cannot be read as a User Command");
        return report.Report();
    }

    // ---- many answers, one -------------------------------------------------------------------------------------------------------------

    // THE ANSWERS OF SEVERAL EDITING AIMS, ONE RESPONSE under the one UserCommandID: the actions of each, in the order of the AIMs. Where several AIMs answer one action (the
    // same Action on the same Object), what one did stands over what the others could not: the action is the ones that were Done if any, else the first. The same action twice
    // with the same outcome is said once. The Outcome is what the actions say, as in any response; the lists of ListedObjects are one list.
    public static UserCommand Merge(string id, IReadOnlyList<UserCommand> answers)
    {
        var first = answers.FirstOrDefault();
        var actions = new List<ReportAction>();
        foreach (var group in answers.SelectMany(a => a.UserCommandRS?.Actions ?? []).GroupBy(a => (a.Action, a.ObjectID)))
        {
            var chosen = group.Any(a => a.Outcome == "Done") ? group.Where(a => a.Outcome == "Done") : group.Take(1);
            actions.AddRange(chosen.DistinctBy(a => (a.Outcome, a.ResultID, a.Reason)));
        }
        var listed = answers.SelectMany(a => a.UserCommandRS?.ListedObjects ?? []).ToList();
        return new UserCommand
        {
            MInstanceID = first?.MInstanceID, UEnvironmentID = first?.UEnvironmentID,
            UserCommandID = id, UserCommandTime = first?.UserCommandTime ?? SimpleTime.At(DateTimeOffset.UtcNow),
            UserCommandRS = new UserCommandRS
            {
                ReportTime = SimpleTime.At(DateTimeOffset.UtcNow),
                Outcome = actions.Count == 0 ? "Ignored" : actions.All(a => a.Outcome == "Done") ? "Done" : actions.Any(a => a.Outcome == "Done") ? "Partly" : "Failed",
                Actions = actions,
                ListedObjects = listed.Count > 0 ? listed : null
            }
        };
    }

    // ---- the Editing AIMs a command reaches ---------------------------------------------------------------------------------------------

    // WHICH EDITING AIMS A COMMAND OF AN AJI REACHES (1 Audio Object, 2 Audio Scene, 3 Speech Object, 4 Speech Scene, 5 Multimodal Scene Editing), from what the command asks:
    //   ListedObjects         the AIM that keeps the Kind; All, every AIM
    //   RevokedUserCommandID  the AIMs the command revoked was sent to
    //   TranslatedObjects     Speech Object Editing
    //   ClosedSpace, ModifiedScene      Audio Scene Editing
    //   ModifiedObjects, LUFS           Audio Object Editing
    //   ChangedObjects        Audio Object Editing for the features of an Object; the Scene Editing AIMs for its place (SpatialAttitude); Speech Object Editing for a Qualifier
    //   AddedObjects, MovedObjects, RemovedObjects    Audio and Speech Scene Editing (the one that keeps the Objects named does it, the other
    //                         reports it keeps none such); Multimodal Scene Editing when a member is a Scene that has left an Editing AIM
    //   AcquiredObject, DeliveredObject     Audio Object Editing
    //   nothing else (a User PoV alone, a command of nothing known)   every AIM
    public static int[] Route(UserCommandRQ rq, Func<string, int[]?>? sentTo = null, Func<string, bool>? isScene = null)
    {
        int[] every = [1, 2, 3, 4, 5];
        if (rq.RevokedUserCommandID is { } revoked) return sentTo?.Invoke(revoked) ?? every;
        if (rq.ListedObjects is { } listing)
        {
            if (listing.Kind is null || listing.Kind == ListKinds.All) return every;
            var by = ListKinds.ListedBy(listing.Kind);
            var at = Array.IndexOf(Editors, by);
            return at >= 0 ? [at + 1] : every;
        }
        var set = new SortedSet<int>();
        if (rq.TranslatedObjects is { Objects.Count: > 0 }) set.Add(3);
        if (rq.ClosedSpace is { Count: > 0 } || rq.ModifiedScene is not null) set.Add(2);
        if (rq.ModifiedObjects is { Objects.Count: > 0 } || rq.LUFS is not null) set.Add(1);
        if (rq.ChangedObjects is { Objects.Count: > 0 } changed)
            foreach (var entry in changed.Objects)
            {
                if (entry.HasFeature && entry.Qualifier is null) set.Add(1);
                if (entry.Qualifier is not null) set.Add(3);
                if (entry.SpatialAttitude is not null) AddSceneEditors(set, [entry.ObjectID], isScene);
                if (!entry.HasFeature && entry.SpatialAttitude is null) set.Add(1);
            }
        if (rq.AddedObjects is { Objects.Count: > 0 } added) AddSceneEditors(set, added.Objects.Select(o => o.ObjectID), isScene);
        if (rq.MovedObjects is { Objects.Count: > 0 } moved) AddSceneEditors(set, moved.Objects.Select(o => o.ObjectID), isScene);
        if (rq.RemovedObjects is { Objects.Count: > 0 } removed) AddSceneEditors(set, removed.Objects.Select(o => o.ObjectID), isScene);
        if (rq.AcquiredObject is not null || rq.DeliveredObject is not null) set.Add(1);
        return set.Count > 0 ? set.ToArray() : every;
    }

    // The Scene Editing AIMs that a command about the members of a Scene reaches: if a member is a Scene the Interface has seen leave an Editing AIM, the command makes or edits a
    // Multimodal Scene (Multimodal Scene Editing); otherwise it is about the Objects of one medium, and Audio and Speech Scene Editing, the one that keeps them, do it.
    private static void AddSceneEditors(SortedSet<int> set, IEnumerable<ManagedObject?> members, Func<string, bool>? isScene)
    {
        var scenes = isScene is not null && members.Any(m => m?.ObjectID is { } id && isScene(id));
        if (scenes) set.Add(5);
        else { set.Add(2); set.Add(4); }
    }

    // ---- the Application JSON Instance ----------------------------------------------------------------------------------------------------

    private void Instance(string json, Dictionary<string, string> outputs)
    {
        var aji = ParseObject(json);
        var ajiId = Text(aji?["AJIID"]);
        var uri = Text(aji?["ApplicationSchemaURI"]);
        var mine = _schema?.Uri ?? "";

        void Fail(string reason, JsonObject? instance)
        {
            var report = new CommandReport(new UserCommand { UserCommandID = Text(instance?["UserCommandID"]) is { Length: > 0 } c ? c : Guid.NewGuid().ToString() });
            var operations = (instance?["UserCommandRQ"] as JsonObject)?.Select(p => p.Key).Where(CommandReader.Operations.Contains).ToList() ?? [];
            if (operations.Count == 0) operations = ["AcquiredObject"];
            reason = reason.Length > 1000 ? reason[..1000] + " ..." : reason;
            foreach (var operation in operations) report.Failed(operation, null, reason);
            Emit(outputs, _ajiOut, Wrap(ajiId, uri is { Length: > 0 } ? uri : mine, report.Report()));
        }

        if (aji is null || Text(aji["Header"]) != AjiHeader || ajiId is null || uri is null || aji["Instance"] is not JsonObject instance)
        { Fail("the Application JSON Instance has to carry its Header (MMC-AJI-V2.5), AJIID, ApplicationSchemaURI and Instance", aji?["Instance"] as JsonObject); return; }
        if (mine.Length == 0 || !string.Equals(uri, mine, StringComparison.Ordinal))
        { Fail($"the ApplicationSchemaURI is not the address of the schema of the User Commands of this Module{(mine.Length > 0 ? " (" + mine + ")" : ", which names none")}", instance); return; }
        var text = instance.ToJsonString();
        var violations = _schema?.Violations(text);
        if (violations is null) { Fail("the schema of the User Commands is not available, so the Instance cannot be checked", instance); return; }
        if (violations.Count > 0) { Fail("the Instance does not conform to the schema of the User Commands: " + string.Join("; ", violations), instance); return; }
        var command = CommandReader.Read(text, out var rejection);
        if (command is null) { Fail(rejection?.UserCommandRS?.Actions.FirstOrDefault()?.Reason ?? "the Instance cannot be read as a User Command", instance); return; }
        if (command.UserCommandRQ is null) { Fail("the Instance is a response, not a request: it carries no UserCommandRQ", instance); return; }
        if (command.UserCommandID.Length == 0 || _pending.ContainsKey(command.UserCommandID)) { Fail("the User Command has no UserCommandID, or one that an earlier command still waits an answer for", instance); return; }

        var editors = Route(command.UserCommandRQ, id => _sentTo.GetValueOrDefault(id), _scenes.Contains);
        Remember(command.UserCommandID, editors);
        _pending[command.UserCommandID] = new Pending { Expected = editors.ToHashSet(), AjiId = ajiId, SchemaUri = uri };
        foreach (var e in editors) Emit(outputs, _rqOut[e], text);
    }

    // The response in an Application JSON Instance that names the one it answers.
    private static string Wrap(string? inReplyTo, string schemaUri, UserCommand response)
    {
        var wrapped = new JsonObject
        {
            ["Header"] = AjiHeader,
            ["AJIID"] = Guid.NewGuid().ToString(),
            ["AJITime"] = JsonNode.Parse(MpaiJson.ToJson(SimpleTime.At(DateTimeOffset.UtcNow))),
            ["ApplicationSchemaURI"] = schemaUri
        };
        if (inReplyTo is not null) wrapped["InReplyTo"] = inReplyTo;
        wrapped["Instance"] = JsonNode.Parse(MpaiJson.ToJson(response));
        return wrapped.ToJsonString();
    }
}
