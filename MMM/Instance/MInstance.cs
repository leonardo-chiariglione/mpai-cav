using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Mpai.Mmm;

// AN M-INSTANCE OF MMM-TEC V2.2. It holds Processes (Humans' Users, Services) and
// Items, and its Services perform the Process Actions the Processes request: each
// request goes to the Service of its Action (Register to RGSrvc, Transact to TRSrvc,
// MM-Add, MM-Move, MM-Animate and Property Change to LCSrvc, ...), which checks the
// Rights of the requesting Process, performs the Action and answers.
//
// RIGHTS (Rights chapter; the author, 2026/10/01). A Rights Item lists Rights, each a
// Deontic Verb, the Process Actions it concerns and a Level - Internal, from
// registration; Acquired, from Process Actions with other Processes; Granted, by a
// User to another, for a time. A Right names its Process Actions by ID (PAIDOrPA):
// the M-Instance keeps those Process Actions, concrete ones (an MM-Move PA,
// MMM-2MP-V2.2, with its Nil and To), and retrieves them to see what the Right
// allows. A Process holds a Rights Item once a Service has made it Final: a Request
// carries it at Status=Model, the Response returns it at Status=Final. May Not
// prevails over May. What a Process owns - its Personae, what it bought, what it
// placed - it may act on without a Right.
public sealed class MInstance
{
    public sealed class Item
    {
        public required string ID { get; init; }
        public required string DataType { get; set; }
        public JsonObject? Body { get; set; }
        public string? Owner { get; set; }
        public string? Location { get; set; }        // the M- or U-Location the Item is at
        public bool Public { get; set; }             // a Location any Process may enter
        public bool Perceptible { get; set; } = true;
        public string Status { get; set; } = "Final";
        public JsonNode? SpatialAttitude { get; set; }
        public string? Animation { get; set; }       // the Animation Stream animating it
        public double[]? Place { get; set; }         // a Location's place, x and z in metres: in the
                                                     // M-Environment, or in the Location it is at
        public double Size { get; set; }             // a Location's side, metres
        public string? Model { get; set; }           // the 3D Model that renders it (a GLB file)
    }

    public sealed class Process
    {
        public required string ID { get; init; }
        public required string Kind { get; init; }   // Human, User, Service
        public string? HumanID { get; init; }
        public string? Token { get; set; }
    }

    public sealed record Activity(long Time, string Source, string Action, string Destination, int Http, string Status);

    private readonly object gate = new();
    private readonly Schemas schemas;
    private readonly Dictionary<string, Item> items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Process> processes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> holdings = new(StringComparer.Ordinal);   // Process -> Rights IDs
    private readonly Dictionary<string, List<string>> inboxes = new(StringComparer.Ordinal);    // Process -> Message IDs
    private readonly List<Activity> activity = [];

    public string ID { get; }
    public string EnvironmentID { get; }

    public static readonly IReadOnlyDictionary<string, string> ServiceOf = new Dictionary<string, string>
    {
        ["Register"] = "RGSrvc", ["Transact"] = "TRSrvc", ["MM-Add"] = "LCSrvc", ["MM-Move"] = "LCSrvc",
        ["MM-Animate"] = "LCSrvc", ["Property Change"] = "LCSrvc", ["MM-Send"] = "COSrvc",
        ["Rights Change"] = "RTSrvc", ["Identify"] = "IDSrvc", ["UM-Capture"] = "EISrvc", ["MU-Actuate"] = "EISrvc"
    };

    public MInstance(string schemasRoot, string id = "MI1", string environmentId = "ME1")
    {
        schemas = new Schemas(schemasRoot);
        ID = id;
        EnvironmentID = environmentId;
        foreach (var s in ServiceOf.Values.Distinct().Append("PRSrvc"))
            processes[s] = new Process { ID = s, Kind = "Service" };
    }

    public IEnumerable<string> Performed => ServiceOf.Keys;

    // What the viewer says about a demonstration in progress: the step just performed, and
    // whether the next one waits for the presenter (Manual). Not part of the M-Instance.
    public volatile string? Caption;
    public volatile string? Note;     // one line under the caption: what the step means
    public volatile string[]? Hidden; // Items the viewer does not draw yet (a demonstration brings them in)
    public volatile bool Manual;
    public Schemas Schemas => schemas;

    // ---------------------------------------------------------------- the state an M-Instance starts with
    // An M-Location (in the M-Environment, drawn by the viewer) or, universe: true, a
    // U-Location (in the Universe, where Data are captured and R-Items rendered).
    public void AddLocation(string id, string? owner = null, bool isPublic = false, double x = 0, double z = 0, double size = 4,
                            bool perceptible = true, bool universe = false)
    {
        lock (gate) items[id] = new Item
        {
            ID = id, DataType = universe ? "MMM-ULC" : "MMM-LOC", Owner = owner, Public = isPublic, Place = [x, z], Size = size,
            Perceptible = perceptible
        };
    }

    // The 3D Model that renders an Item - a Persona's, once it exists.
    private readonly Dictionary<string, string> models = new(StringComparer.Ordinal);
    public void SetModel(string itemId, string model)
    {
        lock (gate) { models[itemId] = model; if (items.TryGetValue(itemId, out var it)) it.Model = model; }
    }

    public void AddUser(string id, string humanId, IEnumerable<string>? owns = null)
    {
        lock (gate)
        {
            processes[id] = new Process { ID = id, Kind = "User", HumanID = humanId, Token = NewToken() };
            foreach (var i in owns ?? []) if (items.TryGetValue(i, out var it)) it.Owner = id;
        }
    }

    public string? TokenOf(string process) { lock (gate) return processes.GetValueOrDefault(process)?.Token; }

    public string? ProcessOf(string token)
    {
        lock (gate) return processes.Values.FirstOrDefault(p => p.Token is not null && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(p.Token), System.Text.Encoding.UTF8.GetBytes(token)))?.ID;
    }

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    // ---------------------------------------------------------------- reading
    public Item? Get(string id) { lock (gate) return items.GetValueOrDefault(id); }

    public IReadOnlyList<Activity> ActivityData { get { lock (gate) return activity.ToList(); } }

    public IReadOnlyList<string> Inbox(string process) { lock (gate) return inboxes.GetValueOrDefault(process)?.ToList() ?? []; }

    public IReadOnlyList<JsonObject> RightsOf(string process)
    {
        lock (gate) return (holdings.GetValueOrDefault(process) ?? []).Select(r => items[r].Body!).ToList();
    }

    public IReadOnlyList<string> HoldersOf(string itemId)
    {
        lock (gate) return holdings.Where(h => h.Value.Any(r => Targets(items[r].Body!).Contains(itemId))).Select(h => h.Key).ToList();
    }

    public IReadOnlyList<(string ID, string DataType)> List(string? dataType = null)
    {
        lock (gate) return items.Values.Where(i => dataType is null || i.DataType.Contains(dataType, StringComparison.Ordinal))
                                       .Select(i => (i.ID, i.DataType)).OrderBy(i => i.ID, StringComparer.Ordinal).ToList();
    }

    // May this Process read this Item: its owner, a Public Location, a Process holding a
    // Right on it, the recipient of a Message.
    public bool MayRead(string process, string itemId)
    {
        lock (gate)
        {
            if (!items.TryGetValue(itemId, out var it)) return false;
            return it.Owner == process || it.Public || (inboxes.GetValueOrDefault(process)?.Contains(itemId) ?? false)
                   || (holdings.GetValueOrDefault(process) ?? []).Any(r => r == itemId || Targets(items[r].Body!).Contains(itemId));
        }
    }

    public JsonObject AsAnyItem(Item it) => new()
    {
        ["ItemName"] = it.ID,
        ["ItemValueOrRef"] = new JsonArray(it.Body?.DeepClone() ?? new JsonObject
        {
            ["ItemID"] = it.ID, ["DataType"] = it.DataType, ["Owner"] = it.Owner, ["Location"] = it.Location,
            ["Perceptible"] = it.Perceptible, ["Animation"] = it.Animation
        }),
        ["MInstanceID"] = ID, ["MEnvironmentID"] = EnvironmentID
    };

    // ---------------------------------------------------------------- what can be seen
    // THE M-ENVIRONMENT AS IT CAN BE SEEN: every Location and every Item that is at
    // one, where it is in metres - a Location at another (a Room on its Parcel) placed
    // within it - and whether it is perceptible. What the viewer draws; what the
    // tests compare.
    public JsonObject Snapshot()
    {
        lock (gate)
        {
            // Where an Item stands within its Location when its Spatial Attitude says so
            // (x and z of its Position, in metres); an Item without, stands at the Location's centre.
            double[]? Spot(Item it) =>
                it.SpatialAttitude?["Position"]?["CartPosition"] is JsonArray a && a.Count >= 3
                && a[0] is not null && a[2] is not null && (a[0]!.GetValue<double>() != 0 || a[2]!.GetValue<double>() != 0)
                    ? [a[0]!.GetValue<double>(), a[2]!.GetValue<double>()] : null;
            double[] Where(Item it, int depth = 0)
            {
                var own = it.Place ?? Spot(it) ?? [0, 0];
                if (depth < 8 && it.Location is { } l && items.TryGetValue(l, out var parent))
                {
                    var p = Where(parent, depth + 1);
                    return [p[0] + own[0], p[1] + own[1]];
                }
                return own;
            }
            var located = items.Values.Where(i => i.DataType == "MMM-LOC" ||
                                                  (i.Location is not null && items.TryGetValue(i.Location, out var at) && at.DataType == "MMM-LOC"))
                                      .OrderBy(i => i.ID, StringComparer.Ordinal).ToList();
            // Items at the same Location stand side by side.
            var side = located.Where(i => i.DataType != "MMM-LOC").GroupBy(i => i.Location!)
                              .SelectMany(g => g.Select((it, k) => (it.ID, Offset: (k - (g.Count() - 1) / 2.0) * 0.9)))
                              .ToDictionary(x => x.ID, x => x.Offset);
            return new JsonObject
            {
                ["MInstanceID"] = ID,
                ["Recent"] = new JsonArray(activity.TakeLast(6).Select(a => (JsonNode)new JsonObject
                {
                    ["Source"] = a.Source, ["Action"] = a.Action, ["Destination"] = a.Destination, ["Http"] = a.Http, ["Status"] = a.Status
                }).ToArray()),
                ["Performed"] = activity.Count,
                ["Caption"] = Caption, ["Note"] = Note, ["Manual"] = Manual,
                ["Hidden"] = new JsonArray((Hidden ?? []).Select(h => (JsonNode)h).ToArray()),
                ["Things"] = new JsonArray(located.Select(i =>
                {
                    var w = Where(i);
                    var o = new JsonObject
                    {
                        ["ID"] = i.ID, ["Kind"] = i.DataType == "MMM-LOC" ? "Location" : i.DataType == "OSD-3DO-V1.5" ? "Persona" : "Item",
                        ["At"] = i.Location, ["X"] = w[0] + (Spot(i) is null ? side.GetValueOrDefault(i.ID) : 0), ["Z"] = w[1],
                        ["Perceptible"] = i.Perceptible, ["Owner"] = i.Owner, ["Public"] = i.Public
                    };
                    if (i.DataType == "MMM-LOC") o["Size"] = i.Size;
                    if (i.Model is not null) o["Model"] = i.Model;
                    if (i.Animation is not null) o["Animation"] = i.Animation;
                    return (JsonNode)o;
                }).ToArray())
            };
        }
    }

    // The snapshot in one line: where each Persona is, which Locations are perceptible.
    public string View()
    {
        lock (gate)
        {
            var personae = items.Values.Where(i => i.DataType == "OSD-3DO-V1.5").OrderBy(i => i.ID, StringComparer.Ordinal)
                                .Select(i => $"{i.ID}@{i.Location ?? "-"}{(i.Animation is null ? "" : "~")}");
            var places = items.Values.Where(i => i.DataType == "MMM-LOC" && i.Location is not null).OrderBy(i => i.ID, StringComparer.Ordinal)
                              .Select(i => $"{i.ID}@{i.Location}{(i.Perceptible ? "" : "(not perceptible)")}");
            return string.Join(", ", personae.Concat(places));
        }
    }

    // ---------------------------------------------------------------- performing a Process Action
    public Outcome Perform(string? caller, ProcessActionRequest rq)
    {
        lock (gate)
        {
            var outcome = PerformLocked(caller, rq);
            activity.Add(new Activity(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), rq.SourceProcessID, rq.Action,
                                      outcome.Response.DestinationProcessID, outcome.Http, outcome.Response.PaStatus.Code));
            return outcome;
        }
    }

    private Outcome PerformLocked(string? caller, ProcessActionRequest rq)
    {
        if (!ServiceOf.TryGetValue(rq.Action, out var service))
            return Fail(422, "", "FaultyPA", $"{rq.Action} is not performed by this M-Instance");
        if (rq.Action != "Register")
        {
            if (caller is null) return Fail(403, service, "InsRights", "no Process: the bearer token is missing or unknown");
            if (caller != rq.SourceProcessID) return Fail(403, service, "InsRights", $"the token is {caller}'s, the Request {rq.SourceProcessID}'s");
        }
        if (rq.DeonticVerb != "May") return Fail(422, service, "FaultyPA", "a Process requests what it May do");

        var c = rq.Complements;
        var with = new List<JsonObject>();     // the Items the Request carries, at Status=Model
        var refs = new List<string>();         // the Items it names
        foreach (var w in c.With ?? [])
        {
            switch (w)
            {
                case JsonValue v when v.TryGetValue<string>(out var id): refs.Add(id); break;
                case JsonObject o when o["itemID"] is not null:
                    var rid = o["itemID"]!.GetValue<string>();
                    if (!items.ContainsKey(rid)) return Fail(404, service, "IncID", $"no Item {rid}");
                    refs.Add(rid); break;
                case JsonObject o:
                    var item = o["ItemValueOrRef"] is JsonArray a && a.Count == 1 && a[0] is JsonObject inner ? inner : o;
                    if (schemas.Check(item) is { } fault) return Fail(422, service, "FaultyPA", fault);
                    with.Add((JsonObject)item.DeepClone()); break;
                default: return Fail(422, service, "FaultyPA", "a With Complement is an identifier, a Statused Reference or an Item");
            }
        }

        return rq.Action switch
        {
            "Register" => Register(service, with),
            "Transact" => Transact(service, rq.SourceProcessID, with),
            "MM-Add" => MMAdd(service, rq.SourceProcessID, c, with),
            "UM-Capture" => UMCapture(service, rq.SourceProcessID, c, refs),
            "Identify" => Identify(service, rq.SourceProcessID, c, refs),
            "MM-Animate" => MMAnimate(service, rq.SourceProcessID, c, refs, with),
            "MM-Send" => MMSend(service, rq.SourceProcessID, c, with),
            "MM-Move" => MMMove(service, rq.SourceProcessID, c, with),
            "Property Change" => PropertyChange(service, rq.SourceProcessID, c, with),
            "MU-Actuate" => MUActuate(service, rq.SourceProcessID, c, refs, with),
            "Rights Change" => RightsChange(service, rq.SourceProcessID, c, with),
            _ => Fail(422, service, "FaultyPA", rq.Action)
        };
    }

    // ---------------------------------------------------------------- the Services
    // RGSrvc. The Personal Profile lists the User Processes the human asks for and
    // their Personae: an Account is opened, the Users created, the Personae theirs.
    private Outcome Register(string service, List<JsonObject> with)
    {
        var profile = with.FirstOrDefault(i => Header(i) == "MMM-PPR-V2.2");
        var human = profile?["humanID"]?.GetValue<string>();
        if (profile is null || human is null) return Fail(422, service, "FaultyPA", "Register takes a Personal Profile with its humanID");
        if (processes.ContainsKey(human)) return Fail(409, service, "Clash", $"{human} is registered already");

        processes[human] = new Process { ID = human, Kind = "Human" };
        var account = $"Account-{human}";
        var users = new List<string>();
        var tokens = new Dictionary<string, string>();
        foreach (var p in profile["PersonalProfile"]?["Processes"]?.AsArray() ?? [])
        {
            var ids = Ids(p?["ProcessIDOrProcess"], "ProcessID");
            var personae = Ids(p?["PersonaIDOrPersona"], "PersonaID");
            foreach (var u in ids)
            {
                if (processes.ContainsKey(u)) return Fail(409, service, "Clash", $"Process {u} exists already");
                var proc = new Process { ID = u, Kind = "User", HumanID = human, Token = NewToken() };
                processes[u] = proc; tokens[u] = proc.Token!; users.Add(u);
                foreach (var pid in personae)
                    items[pid] = new Item { ID = pid, DataType = "OSD-3DO-V1.5", Owner = u, Model = models.GetValueOrDefault(pid) };
            }
        }
        items[profile["PersonalProfileID"]?.GetValue<string>() ?? $"Profile-{human}"] =
            new Item { ID = profile["PersonalProfileID"]?.GetValue<string>() ?? $"Profile-{human}", DataType = "MMM-PPR-V2.2", Body = profile, Owner = human };
        items[account] = new Item
        {
            ID = account, DataType = "MMM-ACC-V2.2", Owner = human,
            Body = new JsonObject
            {
                ["Header"] = "MMM-ACC-V2.2", ["MInstanceID"] = ID, ["MEnvironmentID"] = EnvironmentID, ["HumanID"] = human, ["AccountID"] = account,
                ["Processes"] = new JsonArray(users.Select(u => (JsonNode)new JsonObject { ["ProcessID"] = u }).ToArray())
            }
        };
        return Ok(201, service, new Complements { With = [JsonValue.Create(account)] }) with { Tokens = tokens };
    }

    // TRSrvc. A Transaction between its Sender (the requesting Process) and its
    // Receiver, of the Assets it names: the Assets pass to the Sender, the Transaction
    // becomes Final, and so do the Rights the Sender acquires with it.
    private Outcome Transact(string service, string source, List<JsonObject> with)
    {
        var tr = with.FirstOrDefault(i => Header(i) == "MMM-TRA-V2.2");
        if (tr is null) return Fail(422, service, "FaultyPA", "Transact takes a Transaction");
        var trId = tr["TransactionID"]!.GetValue<string>();
        if (tr["SenderData"]?["SenderID"]?.GetValue<string>() != source) return Fail(403, service, "InsRights", "the Sender of a Transaction is the Process requesting it");
        var receiver = tr["ReceiverData"]?["ReceiverID"]?.GetValue<string>();
        if (receiver is null || !processes.ContainsKey(receiver)) return Fail(404, service, "IncID", $"no Receiver {receiver}");
        var assets = (tr["AssetID"]?.AsArray() ?? []).Select(a => a?["ItemID"]?.GetValue<string>()).OfType<string>().ToList();
        foreach (var a in assets)
        {
            if (!items.TryGetValue(a, out var it)) return Fail(404, service, "IncID", $"no Asset {a}");
            if (it.Owner != receiver) return Fail(409, service, "Clash", $"{a} is not {receiver}'s to sell");
        }
        foreach (var a in assets) items[a].Owner = source;
        tr["TransactionStatus"] = "Final";
        items[trId] = new Item { ID = trId, DataType = "MMM-TRA-V2.2", Body = tr, Owner = source };
        var rs = new List<JsonNode?> { Wire.Ref(trId, "Final") };
        rs.AddRange(GrantAll(with, source));
        return Ok(200, service, new Complements { With = rs });
    }

    // LCSrvc. MM-Add places an Item its Process owns at an M-Location it may enter.
    private Outcome MMAdd(string service, string source, Complements c, List<JsonObject> with)
    {
        if (Need(service, c.Nil, "Nil") is { } f1) return f1;
        if (Need(service, c.At, "At") is { } f2) return f2;
        if (!items.TryGetValue(c.Nil!, out var it)) return Fail(404, service, "IncID", $"no Item {c.Nil}");
        if (!items.ContainsKey(c.At!)) return Fail(404, service, "IncID", $"no M-Location {c.At}");
        if (it.Owner != source && !Allowed(source, "MM-Add", c.Nil, c.At)) return Fail(403, service, "InsRights", $"{c.Nil} is not {source}'s");
        if (!MayEnter(source, "MM-Add", c.Nil!, c.At!)) return Fail(403, service, "InsRights", $"{source} may not place Items at {c.At}");
        it.Location = c.At; it.SpatialAttitude = SpatialAttitude(with);
        return Ok(200, service, new Complements { With = GrantAll(with, source) });
    }

    // LCSrvc. MM-Move moves an Item from where it is to an M-Location the Process may enter.
    private Outcome MMMove(string service, string source, Complements c, List<JsonObject> with)
    {
        if (Need(service, c.Nil, "Nil") is { } f1) return f1;
        if (Need(service, c.To, "To") is { } f2) return f2;
        if (!items.TryGetValue(c.Nil!, out var it)) return Fail(404, service, "IncID", $"no Item {c.Nil}");
        if (!items.ContainsKey(c.To!)) return Fail(404, service, "IncID", $"no M-Location {c.To}");
        if (it.Owner != source) return Fail(403, service, "InsRights", $"{c.Nil} is not {source}'s");
        if (c.From is not null && it.Location != c.From) return Fail(409, service, "Clash", $"{c.Nil} is at {it.Location}, not {c.From}");
        if (!MayEnter(source, "MM-Move", c.Nil!, c.To!)) return Fail(403, service, "InsRights", $"{source} may not move {c.Nil} to {c.To}");
        it.Location = c.To; it.SpatialAttitude = SpatialAttitude(with) ?? it.SpatialAttitude;
        return Ok(200, service, new Complements { With = GrantAll(with, source) });
    }

    // LCSrvc. MM-Animate animates an Item its Process owns with an Animation Stream it owns.
    private Outcome MMAnimate(string service, string source, Complements c, List<string> refs, List<JsonObject> with)
    {
        if (Need(service, c.Nil, "Nil") is { } f1) return f1;
        if (!items.TryGetValue(c.Nil!, out var it)) return Fail(404, service, "IncID", $"no Item {c.Nil}");
        var stream = refs.FirstOrDefault(r => items.TryGetValue(r, out var s) && s.DataType == "PAF-AMO-V1.6");
        if (stream is null) return Fail(422, service, "FaultyPA", "MM-Animate takes an Animation Stream");
        if (it.Owner != source || items[stream].Owner != source) return Fail(403, service, "InsRights", $"{c.Nil} or {stream} is not {source}'s");
        it.Animation = stream;
        return Ok(200, service, new Complements { With = GrantAll(with, source) });
    }

    // LCSrvc. Property Change makes an Item its Process owns perceptible.
    private Outcome PropertyChange(string service, string source, Complements c, List<JsonObject> with)
    {
        if (Need(service, c.Nil, "Nil") is { } f1) return f1;
        if (!items.TryGetValue(c.Nil!, out var it)) return Fail(404, service, "IncID", $"no Item {c.Nil}");
        if (it.Owner != source) return Fail(403, service, "InsRights", $"{c.Nil} is not {source}'s");
        it.Perceptible = true;
        return Ok(200, service, new Complements { With = GrantAll(with, source) });
    }

    // EISrvc. UM-Capture captures Data at a U-Location: the Data becomes an Item of the
    // requesting Process.
    private Outcome UMCapture(string service, string source, Complements c, List<string> refs)
    {
        if (Need(service, c.Nil, "Nil") is { } f1) return f1;
        if (Need(service, c.At, "At") is { } f2) return f2;
        if (items.ContainsKey(c.Nil!)) return Fail(409, service, "Clash", $"{c.Nil} exists already");
        items[c.Nil!] = new Item { ID = c.Nil!, DataType = "MMM-DAT", Owner = source, Location = c.At };
        return Ok(201, service, new Complements { Nil = c.Nil, With = refs.Select(r => (JsonNode?)JsonValue.Create(r)).ToList() });
    }

    // IDSrvc. Identify says what captured Data is: here, the Animation Stream of the
    // human captured.
    private Outcome Identify(string service, string source, Complements c, List<string> refs)
    {
        if (Need(service, c.Nil, "Nil") is { } f1) return f1;
        if (!items.TryGetValue(c.Nil!, out var data)) return Fail(404, service, "IncID", $"no Data {c.Nil}");
        if (data.Owner != source) return Fail(403, service, "InsRights", $"{c.Nil} is not {source}'s");
        var stream = $"{c.Nil}-Stream";
        items[stream] = new Item { ID = stream, DataType = "PAF-AMO-V1.6", Owner = source };
        return Ok(201, service, new Complements { Nil = stream });
    }

    // COSrvc. MM-Send conveys a Message to a Process: the Message reaches its inbox and
    // the Rights to Use it are the recipient's.
    private Outcome MMSend(string service, string source, Complements c, List<JsonObject> with)
    {
        if (Need(service, c.Nil, "Nil") is { } f1) return f1;
        if (Need(service, c.To, "To") is { } f2) return f2;
        if (!processes.ContainsKey(c.To!)) return Fail(404, service, "IncID", $"no Process {c.To}");
        var message = with.FirstOrDefault(i => Header(i) == "MMM-MSG-V2.2");
        items[c.Nil!] = new Item { ID = c.Nil!, DataType = "MMM-MSG-V2.2", Body = message, Owner = source };
        (inboxes.TryGetValue(c.To!, out var box) ? box : inboxes[c.To!] = []).Add(c.Nil!);
        return Ok(200, service, new Complements { With = GrantAll(with, c.To!) });
    }

    // EISrvc. MU-Actuate renders an Item its Process owns at a U-Location, as an R-Item.
    private Outcome MUActuate(string service, string source, Complements c, List<string> refs, List<JsonObject> with)
    {
        if (Need(service, c.Nil, "Nil") is { } f1) return f1;
        if (Need(service, c.At, "At") is { } f2) return f2;
        if (!items.TryGetValue(c.Nil!, out var it)) return Fail(404, service, "IncID", $"no Item {c.Nil}");
        if (it.Owner != source) return Fail(403, service, "InsRights", $"{c.Nil} is not {source}'s");
        var ritem = $"RItem-{c.Nil}";
        items[ritem] = new Item
        {
            ID = ritem, DataType = "MMM-RIT-V2.2", Owner = source, Location = c.At,
            Body = new JsonObject { ["Header"] = "MMM-RIT-V2.2", ["MInstanceID"] = ID, ["RItemID"] = ritem, ["RItemType"] = refs.FirstOrDefault() }
        };
        var rs = new List<JsonNode?>(GrantAll(with, source));
        return Ok(201, service, new Complements { Nil = ritem, With = rs });
    }

    // RTSrvc. Rights Change gives a Process Rights (May) or takes them away (May Not),
    // on Locations the requesting Process owns.
    private Outcome RightsChange(string service, string source, Complements c, List<JsonObject> with)
    {
        if (Need(service, c.Nil, "Nil") is { } f1) return f1;
        if (!processes.ContainsKey(c.Nil!)) return Fail(404, service, "IncID", $"no Process {c.Nil}");
        var rights = with.Where(i => Header(i) == "MMM-RGT-V2.2").ToList();
        if (rights.Count == 0) return Fail(422, service, "FaultyPA", "Rights Change takes Rights");
        Store(with);
        // Rights on a place are its owner's to give: the targets (To, At) of the Process
        // Actions the Rights name - the Room - not what they act on (the Persona moving in).
        foreach (var r in rights)
            foreach (var t in Entries(r).SelectMany(e => e.PAs).Select(Template).OfType<PA>().Select(t => t.Target).OfType<string>())
                if (items.TryGetValue(t, out var it) && it.Owner != source && !it.Public)
                    return Fail(403, service, "InsRights", $"{t} is not {source}'s to give Rights on");

        var rs = new List<JsonNode?>();
        foreach (var r in rights)
        {
            var id = r["RightsID"]!.GetValue<string>();
            var withdrawn = Entries(r).Where(e => e.Verb == "May Not").SelectMany(e => e.PAs).ToHashSet();
            if (withdrawn.Count > 0 && holdings.TryGetValue(c.Nil!, out var held))
                held.RemoveAll(h => Entries(items[h].Body!).Any(e => e.Verb == "May" && e.PAs.Any(p => withdrawn.Any(w => SamePA(p, w)))));
            rs.Add(Grant(r, c.Nil!));
        }
        return Ok(200, service, new Complements { With = rs });
    }

    // ---------------------------------------------------------------- Rights
    private sealed record Entry(string Verb, IReadOnlyList<string> PAs);
    private sealed record PA(string Action, string? Nil, string? Target);

    private static IEnumerable<Entry> Entries(JsonObject rights) =>
        (rights["RightsData"]?.AsArray() ?? []).OfType<JsonObject>().Select(e => new Entry(
            e["DeonticVerb"]?.GetValue<string>() ?? "",
            (e["PAIDOrPA"]?.AsArray() ?? []).Select(p => p?["ProcessActionID"]?.GetValue<string>()).OfType<string>().ToList()));

    // A Process Action the M-Instance keeps, as what it allows: its Action, by its
    // Header; its Nil; its target - To, At, or the Point of View of To.
    private PA? Template(string id)
    {
        if (!items.TryGetValue(id, out var it) || it.Body is null || schemas.ActionOf(it.DataType) is not { } action) return null;
        string? nil = null, target = null;
        foreach (var part in it.Body.Where(p => p.Key.EndsWith("PARQ", StringComparison.Ordinal)).SelectMany(p => p.Value?.AsObject() ?? []))
        {
            if (part.Value is not JsonObject rq) continue;
            var complement = rq["Complement"]?.GetValue<string>();
            var value = rq.Where(p => p.Key != "Complement").Select(p => p.Value switch
            {
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                JsonObject o => o.Where(q => q.Key.EndsWith("ID", StringComparison.Ordinal)).Select(q => q.Value?.GetValue<string>()).FirstOrDefault(),
                _ => null
            }).FirstOrDefault(v => v is not null);
            if (complement == "Nil") nil = value;
            else if (complement is "To" or "At") target = value;
        }
        return new PA(action, nil, target);
    }

    private bool SamePA(string a, string b) => a == b || (Template(a) is { } x && Template(b) is { } y && x == y);

    private static bool Matches(PA t, string action, string? nil, string? target) =>
        t.Action == action && (t.Nil is null || t.Nil == nil) && (t.Target is null || t.Target == target);

    // Does a Right of the Process allow it? A May Not prevails.
    private bool Allowed(string process, string action, string? nil, string? target)
    {
        var held = (holdings.GetValueOrDefault(process) ?? []).Select(r => items[r].Body!).ToList();
        bool Any(string verb) => held.SelectMany(Entries).Where(e => e.Verb == verb)
                                     .SelectMany(e => e.PAs).Select(Template).OfType<PA>().Any(t => Matches(t, action, nil, target));
        return !Any("May Not") && Any("May");
    }

    // An M-Location a Process may enter: its own, a Public one, one inside its own (a
    // Room on its Parcel), or one a Right lets it enter.
    private bool MayEnter(string process, string action, string item, string location)
    {
        if (Allowed(process, action, item, location)) return true;
        var denied = (holdings.GetValueOrDefault(process) ?? []).Select(r => items[r].Body!).SelectMany(Entries)
                     .Where(e => e.Verb == "May Not").SelectMany(e => e.PAs).Select(Template).OfType<PA>()
                     .Any(t => Matches(t, action, item, location));
        if (denied) return false;
        for (var l = location; l is not null && items.TryGetValue(l, out var it); l = it.Location)
            if (it.Owner == process || it.Public) return true;
        return false;
    }

    // The Items a Rights Item concerns: the targets and Nils of its Process Actions.
    private IEnumerable<string> Targets(JsonObject rights) =>
        Entries(rights).SelectMany(e => e.PAs).Select(Template).OfType<PA>().SelectMany(t => new[] { t.Target, t.Nil }).OfType<string>().Distinct();

    // Keep the Process Actions a Request carries, so that the Rights naming them can be read.
    private void Store(List<JsonObject> with)
    {
        foreach (var i in with)
            if (Header(i) is { } h && schemas.ActionOf(h) is not null && IdOf(i) is { } id)
                items[id] = new Item { ID = id, DataType = h, Body = i };
    }

    // Every Rights Item of a Request, made Final and held by the Process: Statused
    // References at Status=Final for the Response.
    private List<JsonNode?> GrantAll(List<JsonObject> with, string holder)
    {
        Store(with);
        return with.Where(i => Header(i) == "MMM-RGT-V2.2").Select(r => Grant(r, holder)).ToList();
    }

    private JsonNode Grant(JsonObject rights, string holder)
    {
        var id = rights["RightsID"]!.GetValue<string>();
        items[id] = new Item { ID = id, DataType = "MMM-RGT-V2.2", Body = rights, Owner = holder, Status = "Final" };
        var held = holdings.TryGetValue(holder, out var h) ? h : holdings[holder] = [];
        if (!held.Contains(id)) held.Add(id);
        return Wire.Ref(id, "Final");
    }

    // ---------------------------------------------------------------- helpers
    private static string? Header(JsonObject i) => i["Header"]?.GetValue<string>();

    private static readonly HashSet<string> NotTheId = ["MInstanceID", "UEnvironmentID", "MEnvironmentID", "humanID", "HumanID", "AssetID"];
    private static string? IdOf(JsonObject i) =>
        i.Where(p => p.Key.EndsWith("ID", StringComparison.Ordinal) && !NotTheId.Contains(p.Key) && p.Value is JsonValue)
         .Select(p => p.Value!.GetValue<string>()).FirstOrDefault();

    private static IEnumerable<string> Ids(JsonNode? array, string key) =>
        (array as JsonArray ?? []).Select(x => x?[key]?.GetValue<string>()).OfType<string>();

    private static JsonNode? SpatialAttitude(List<JsonObject> with) =>
        with.FirstOrDefault(i => Header(i) == "OSD-OSA-V1.5")?.DeepClone();

    private Outcome? Need(string service, string? value, string complement) =>
        value is null ? Fail(422, service, "FaultyPA", $"the Request has no {complement} Complement") : null;

    private static Outcome Ok(int http, string service, Complements c) =>
        new(http, new ProcessActionResponse { DestinationProcessID = service, Complements = c, PaStatus = new PAStatus { Code = "Ack" } });

    private static Outcome Fail(int http, string service, string code, string detail) =>
        new(http, new ProcessActionResponse { DestinationProcessID = service, PaStatus = new PAStatus { Code = code, Detail = detail } });
}
