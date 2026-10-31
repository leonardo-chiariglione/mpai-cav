using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using AIF.Controller;
using AIF.Store;

namespace Mpai.Aif.Api;

// ControllerApi - the MPAI-AIF Controller API. The UA identifies data ONLY by
// (DataType, PortNumber). The boundary contract with the Controller is the
// typed key "DataType#PortNumber": ControllerApi neither knows nor uses any port
// NAME. Outcomes are the standard AifError, surfaced faithfully; no application
// semantics, no content, no state (memory lives in the Module).
public sealed class ControllerApi : IControllerApi, IDisposable
{
    // THE FRAMEWORK OFFERS THE PLACE; THIS KNOWS WHAT TO LOOK FOR. AIF.Controller
    // routes Data Types and payloads and does not know what an MPAI Object is.
    // The Controller API knows both, so the inspector is installed here - once, for every
    // application and both servers, since all of them construct a ControllerApi.
    static ControllerApi()
    {
        AIF.Controller.ContinuousExecutor.ObjectInspector = Mpai.Core.QualifierCheck.Inspect;
    }

    // THE CODECS RESOLVE A REFERENCE THIS CONTROLLER ISSUED (M3215 3.6), through the
    // User Agents of the Controller APIs in this process.
    private static readonly List<WeakReference<UserAgent>> Resolvers = new();

    private static byte[]? ResolvePayload(string reference)
    {
        lock (Resolvers)
            foreach (var weak in Resolvers)
                if (weak.TryGetTarget(out var ua) && ua.ResolvePayload(reference) is { } data) return data;
        return null;
    }

    private void OfferPayloads()
    {
        lock (Resolvers) Resolvers.Add(new WeakReference<UserAgent>(_ua));
        Mpai.Aif.PortData.PayloadReferences.Resolve = ResolvePayload;
    }

    private readonly UserAgent    _ua;
    private readonly IAimProvider _provider;
    private readonly AimSettings  _settings;
    private readonly AmdStore     _amds;

    // THE CONTROLLER'S REPOSITORY OF AIMs (see AimRepository): its folder is the AIM Metadata folder this Controller reads. With it and a Source (the MPAI Store) the Controller,
    // when it starts a Module, obtains the AIMs of the Module it does not hold - and only those - and, if FollowNewerVersions, the newer version of one the Store has.
    public AimRepository? Repository { get; set; }
    // The Source is also who verifies: the Controller measures what it runs and asks the Store (MPAI_AIFS_VerifyImplementation); it never reads the fingerprint itself.
    private IAimSource? _source;
    public IAimSource? Source { get => _source; set { _source = value; if (value is not null) _amds.Verifier = value; } }
    public bool FollowNewerVersions { get; set; }

    // Whether this Controller obtains the Implementations and models of the AIMs it obtains. A Controller whose AIMs all run on AIM hosts does not: the hosts do, from the places the
    // Metadata gives, and the Controller hands them the Metadata, the fingerprints and the AIM Licence (UserAgent.PackageForHost).
    public bool FetchImplementations { get; set; } = true;

    // A Service calls this from many requests at once, for different Modules.
    // The table is touched only under _tables; a run itself happens outside it,
    // so one Module's run never waits for another's.
    private readonly Dictionary<string, Started> _running = new();
    private readonly object _tables = new();

    // A started Module: its id, what has been written to it since its last run,
    // and that run.
    private sealed class Started
    {
        public required int Id { get; init; }

        // Its policy stopped it (OnDegraded StopModule): nothing more is written
        // to it, and its status can still be asked for, until it is started again.
        public bool Stopped { get; set; }
        public Dictionary<string, string> Written { get; } = new();
        public Task<Result>? Run { get; set; }

        // The exchange on the Module's Channels, when it runs there: a boundary
        // Output Port settles before the whole run completes (M3215 3.4).
        public ContinuousExecutor.ExchangeRun? Exchange { get; set; }
    }

    public ControllerApi(string amdDir, string settingsPath, IAimProvider provider)
    {
        _settings = AimSettings.Load(settingsPath);
        _provider = provider;
        var store = new AmdStore(amdDir); store.Scan(); _amds = store;
        _ua = new UserAgent(store, Mpai.Core.MpaiPaths.SharedStorage);
        _ua.MPAI_AIFU_Controller_Initialize();
        _ua.Access = new AccessStore(Mpai.Core.MpaiPaths.Access);
        _ua.PackageForHost = aim => Repository?.PackageForHost(aim);
        OfferPayloads();
    }

    // Overload: caller supplies a provider FACTORY, so ControllerApi builds ONE AmdStore
    // and hands it to the factory (e.g. store => new MacProvider(store, galleryJson)).
    public ControllerApi(string amdDir, string settingsPath, Func<AmdStore, IAimProvider> providerFactory)
    {
        _settings = AimSettings.Load(settingsPath);
        var store = new AmdStore(amdDir); store.Scan(); _amds = store;
        _provider = providerFactory(store);
        _ua = new UserAgent(store, Mpai.Core.MpaiPaths.SharedStorage);
        _ua.MPAI_AIFU_Controller_Initialize();
        _ua.Access = new AccessStore(Mpai.Core.MpaiPaths.Access);
        _ua.PackageForHost = aim => Repository?.PackageForHost(aim);
        OfferPayloads();
    }

    // A typed datum: DataType (+ PortNumber where a type repeats) + JSON payload.
    public readonly record struct Datum(string DataType, int PortNumber, string Json)
    {
        public Datum(string dataType, string json) : this(dataType, 1, json) { }
    }

    // What one exchange returned.
    public readonly record struct Result(AifError Error, IReadOnlyList<Datum> Outputs)
    {
        public bool Ok => Error == AifError.OK;
        public string? ByType(string dataType, int portNumber = 1) =>
            Outputs.FirstOrDefault(o => o.DataType == dataType && o.PortNumber == portNumber).Json;
    }

    // What Status returned: an outcome, and each AIM's status and reports.
    public readonly record struct ModuleStatus(AifError Error, IReadOnlyList<AimReport> Aims)
    {
        public bool Ok => Error == AifError.OK;

        // The state of the Module: Running, or Suspended between Pause and Resume.
        public ModuleState State { get; init; } = ModuleState.Running;

        // While the Module is recorded: per Port, what is recorded and what is not
        // (M3219 3.4).
        public IReadOnlyDictionary<BoundaryRecord.PortKey, (long Recorded, long NotRecorded)>? Record { get; init; }
    }

    // What one OutputRead returned: an outcome, and the datum when it is OK.
    public readonly record struct Read(AifError Error, string? Json)
    {
        public bool Ok => Error == AifError.OK;
    }

    public AifError StartFlow(string moduleName)
    {
        lock (_tables)
        {
            if (_running.TryGetValue(moduleName, out var already))
            {
                if (!already.Stopped) return AifError.OK;
                _ua.MPAI_AIFU_MODULE_Stop(already.Id);
                _running.Remove(moduleName);
            }
            var prepared = PrepareAims(moduleName);
            if (prepared != AifError.OK) return prepared;
            var err = _ua.MPAI_AIFU_MODULE_Start(moduleName, _provider, _settings, out var id);
            if (err == AifError.OK) _running[moduleName] = new Started { Id = id };
            return err;
        }
    }


    // OBTAINS THE AIMs OF A MODULE from the Store when the repository does not hold them (or holds an older version and the Controller follows the Store), and reads the
    // Metadata again. What it holds is not obtained again: it was paid for.
    private AifError PrepareAims(string moduleName)
    {
        if (Repository is null || Source is null) return AifError.OK;
        var changed = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Resolve(string aim)
        {
            if (!seen.Add(aim)) return;
            if (Repository.VersionOf(aim) is null)
            { if (Source.CurrentVersion(aim) is not null) { Repository.Obtain(aim, Source, FetchImplementations); changed = true; } }
            else if (FollowNewerVersions && Repository.NewerVersion(aim, Source) is not null) { Repository.Update(aim, Source, FetchImplementations); changed = true; }
            var path = System.IO.Path.Combine(Repository.Folder, aim + ".json");
            if (!System.IO.File.Exists(path)) return;
            using var document = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("SubAIMs", out var subs) && subs.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var sub in subs.EnumerateArray())
                    if (sub.TryGetProperty("Identifier", out var i) && i.TryGetProperty("AIMName", out var n) && n.GetString() is { Length: > 0 } name) Resolve(name);
        }
        try
        {
            Resolve(moduleName);
            if (changed) _amds.Scan();
            // The AIM Licence of every AIM of the Module that has one: good, and - the Store being asked - not revoked.
            foreach (var aim in seen) Repository.CheckLicence(aim, Source);
        }
        catch (InvalidOperationException) { return AifError.NotTrusted; }         // NOT_TRUSTED, or NOT_LICENSED
        return AifError.OK;
    }
    public void StopFlow(string moduleName)
    {
        List<AIF.Channels.ExternalHub>? hubs;
        lock (_tables)
        {
            if (_running.Remove(moduleName, out var started))
                _ua.MPAI_AIFU_MODULE_Stop(started.Id);
            _external.Remove(moduleName, out hubs);
        }
        foreach (var hub in hubs ?? []) hub.DisposeAsync().AsTask().GetAwaiter().GetResult();
        ExternalLeave(moduleName);
    }

    // THE EXTERNAL PORTS OF A MODULE JOINED TO OTHER CONTROLLERS (M3205 3.3; M3241 3.2):
    // its boundary Ports marked IsRemote. What its External Output Ports give is sent,
    // with its stamp, to the Controllers in range the hub links; what they send is
    // written to its External Input Ports with their controllerID and stamp. sign and
    // verify: the Controller's trust (CityTrust). The hub ends when the Module stops.
    private readonly Dictionary<string, List<AIF.Channels.ExternalHub>> _external = new();
    private readonly Dictionary<string, System.Collections.Concurrent.BlockingCollection<string>> _unrouted = new();

    private System.Collections.Concurrent.BlockingCollection<string> UnroutedQueue(string moduleName, string dataType, int portNumber)
    {
        lock (_unrouted)
        {
            var key = moduleName + "|" + Key(dataType, portNumber);
            if (!_unrouted.TryGetValue(key, out var queue)) _unrouted[key] = queue = new();
            return queue;
        }
    }

    public AIF.Channels.ExternalHub StartExternal(string moduleName, AIF.Channels.ExternalHub.Options options,
                                                  Func<string, string>? sign = null, Func<string, string, string?>? verify = null,
                                                  Func<string, IReadOnlyList<AIF.Channels.PayloadDescriptor>, string>? signPayloads = null)
    {
        int id;
        IReadOnlyList<RuntimePort> ports;
        lock (_tables)
        {
            if (!_running.TryGetValue(moduleName, out var started) || !_ua.IsContinuous(started.Id))
                throw new InvalidOperationException($"{moduleName} is not a continuous Module started here.");
            id = started.Id;
            ports = (_ua.BoundaryPorts(id) ?? Array.Empty<RuntimePort>()).Where(p => p.IsRemote).ToList();
        }
        var hub = new AIF.Channels.ExternalHub(options)
        {
            Outputs = ports.Where(p => p.Direction == "Output").Select(p => (p.DataType, p.PortNumber ?? 1)).ToList(),
            ReadOutput = async (dataType, portNumber, timeoutMs) =>
            {
                var (error, json, stamp) = await _ua.ContinuousReadStampedAsync(id, dataType, portNumber, timeoutMs);
                return error == AifError.OK && json is not null ? (json, stamp) : null;
            },
            // The payload channel (Communication 4, 7): the references of the payloads of what an
            // External Output Port gives are kept, and sent apart; those of what arrives stay
            // on the Controller that wrote them until a reader asks.
            ReadOutputApart = async (dataType, portNumber, timeoutMs) =>
            {
                var (error, json, stamp) = await _ua.ContinuousReadApartAsync(id, dataType, portNumber, timeoutMs);
                return error == AifError.OK && json is not null ? (json, stamp) : null;
            },
            PayloadSource = _ua.PayloadSourceOf(id),
            SignPayloads = signPayloads,
            Unrouted = options.DirectedReplies ? (dataType, portNumber, json) => { UnroutedQueue(moduleName, dataType, portNumber).Add(json); return Task.CompletedTask; } : null,
            Deliver = async m =>
            {
                if (!ports.Any(p => p.Direction == "Input" && p.Accepts(m.DataType))) return;
                var json = m.Payloads is { Count: > 0 } && m.Fetch is { } fetch
                    ? _ua.AdoptPayloads(id, m.DataType, m.PortNumber, m.Json, m.Payloads.Select(d => (d.Reference, (Func<byte[]?>)(() => fetch(d)))))
                    : m.Json;
                await _ua.ContinuousWriteAsync(id, m.DataType, m.PortNumber, json, 15_000, m.From, m.Stamp);
            },
            Sign = sign, Verify = verify
        };
        lock (_tables)
        {
            if (!_external.TryGetValue(moduleName, out var list)) _external[moduleName] = list = new();
            list.Add(hub);
        }
        hub.Start();
        return hub;
    }

    // ---- BETWEEN CONTROLLERS (Basic API 3.9; Communication 9) ---------------------------
    // The User Agent passes its User's decision to trust a Trust Authority to the Controller
    // (External_Join), or makes the Controller the Authority of a service (External_Authority).
    // The Controller trusts and admits only at that request, and has no initiative of its own.

    private readonly Dictionary<string, AIF.Controller.ExternalAuthorityService> _authorities = new();
    private readonly Dictionary<string, AIF.Controller.ExternalMember> _members = new();

    // The root of trust of this Controller, where it has one: a Controller that joins presents
    // the Attestation Evidence a service requires of its role.
    public AIF.Trust.IRootOfTrust? ExternalAttestor { get; set; }

    // What a Controller that is an Authority judges the Attestation Evidence of the Controllers it
    // admits by (the manufacturers it trusts and the code it approves).
    public AIF.Trust.Attestation.Policy? ExternalAttestationPolicy { get; set; }

    // Where the anchor of this Controller as an Authority is kept between runs (its identity and
    // key); made there if absent. Without it, the anchor lasts as long as the Controller.
    public string? ExternalAnchorFile { get; set; }

    // Why the last External_Join or External_Authority did not succeed.
    public string? ExternalError { get; private set; }

    // MPAI_AIFU_External_Authority(MODULE_ID, policy): the Controller becomes the Trust Authority
    // for the External links of the Module, admitting Controllers under policy. It is admitted to
    // its own service in the role the policy gives it. Its anchor, the one its User Agent publishes
    // with the service, is ExternalAnchor(moduleName); the rendezvous, ExternalRendezvous(moduleName).
    public AifError ExternalAuthority(string moduleName, string policy, string host = "127.0.0.1", int port = 0)
    {
        ExternalError = null;
        lock (_tables)
        {
            if (!_running.ContainsKey(moduleName)) return AifError.NotStarted;
            if (_authorities.ContainsKey(moduleName) || _members.ContainsKey(moduleName)) { ExternalError = $"{moduleName} has External links already"; return AifError.Failed; }
        }
        try
        {
            var parsed = AIF.Controller.ExternalPolicy.Parse(policy);
            AIF.Trust.TrustAnchorKey anchor;
            System.Security.Cryptography.ECDsa key;
            if (ExternalAnchorFile is { } file && System.IO.File.Exists(file)) (anchor, key) = AIF.Trust.TrustAnchorKey.Load(file);
            else
            {
                key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
                var t = DateTimeOffset.UtcNow;
                anchor = new AIF.Trust.TrustAnchorKey($"{moduleName}-Authority-{Guid.NewGuid():N}", key, t.AddMinutes(-5), t.AddDays(365));
                if (ExternalAnchorFile is { } save) AIF.Trust.TrustAnchorKey.Save(save, anchor.AnchorId, key, t.AddMinutes(-5), t.AddDays(365));
            }
            var service = new AIF.Controller.ExternalAuthorityService(anchor, key, parsed, ExternalAttestationPolicy, host: host, port: port);
            var (credential, discovery) = service.AdmitSelf(anchor.AnchorId);
            var trust = new AIF.Controller.ExternalTrust(anchor, key, credential, anchor.Object(), parsed.AuthorityRole, parsed);
            var hub = StartExternal(moduleName, new AIF.Channels.ExternalHub.Options
            {
                ControllerId = anchor.AnchorId, ModuleType = anchor.AnchorId, Discovery = discovery, Admission = trust.Admission, Host = host,
                DirectedReplies = parsed.Directed, CorrelateBy = parsed.Correlate ?? [], AddressedBy = parsed.Address ?? []
            }, trust.Sign, trust.Verify, trust.SignPayloads);
            lock (_tables) _authorities[moduleName] = service;
            return AifError.OK;
        }
        catch (InvalidOperationException e) { ExternalError = e.Message; return AifError.Failed; }
        catch (Exception e) when (e is System.Text.Json.JsonException or ArgumentException or FormatException) { ExternalError = "the policy is not valid: " + e.Message; return AifError.Failed; }
    }

    // The Trust Anchor object this Controller is the Authority by, to be published by its User
    // Agent with the service; and the rendezvous where Controllers present themselves.
    public string? ExternalAnchor(string moduleName) { lock (_tables) return _authorities.TryGetValue(moduleName, out var a) ? a.AnchorObject.ToJsonString() : null; }
    public string? ExternalRendezvous(string moduleName) { lock (_tables) return _authorities.TryGetValue(moduleName, out var a) ? a.Rendezvous : null; }

    // MPAI_AIFU_External_Join(MODULE_ID, authority, rendezvous): the External Ports of the Module are
    // linked to the Controllers admitted by the Trust Authority whose anchor is authority (its Trust
    // Anchor object, JSON) - the Authority the User decided to trust. The Controller presents itself
    // at rendezvous (host:port) with its attestation and what the service requires, and receives its
    // credential. NOT_TRUSTED where the Authority does not prove that it holds authority; NOT_AUTHORISED
    // where it refuses admission. role: the role asked for (null: the Authority's default); controllerId:
    // the name this Controller is known by in the service (null: one is made), which other Controllers may
    // name in a Message to address it; rendezvous
    // null (announcement in range) is not offered by this Controller.
    public AifError ExternalJoin(string moduleName, string authority, string? rendezvous, string? role = null, string? controllerId = null)
    {
        ExternalError = null;
        if (rendezvous is null) { ExternalError = "an Authority is reached at a rendezvous; admission by announcement in range is not offered"; return AifError.Failed; }
        lock (_tables)
        {
            if (!_running.ContainsKey(moduleName)) return AifError.NotStarted;
            if (_authorities.ContainsKey(moduleName) || _members.ContainsKey(moduleName)) { ExternalError = $"{moduleName} has External links already"; return AifError.Failed; }
        }
        AIF.Controller.ExternalMember member;
        try
        {
            var anchor = System.Text.Json.Nodes.JsonNode.Parse(authority)?.AsObject() ?? throw new FormatException("the authority is not a Trust Anchor object");
            member = AIF.Controller.ExternalMember.JoinAsync(controllerId ?? $"{moduleName}-{Guid.NewGuid():N}", anchor, rendezvous, role, ExternalAttestor).GetAwaiter().GetResult();
        }
        catch (AIF.Controller.ExternalJoinException e)
        {
            ExternalError = e.Message;
            return e.Kind switch { AIF.Controller.AifErrorKind.NotTrusted => AifError.NotTrusted, AIF.Controller.AifErrorKind.NotAuthorised => AifError.NotAuthorised, _ => AifError.Failed };
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException) { ExternalError = e.Message; return AifError.Failed; }
        try
        {
            var hub = StartExternal(moduleName, new AIF.Channels.ExternalHub.Options
            {
                ControllerId = member.Trust.ControllerId, ModuleType = AuthorityIdOf(authority), Discovery = member.Discovery, Admission = member.Trust.Admission,
                DirectedReplies = false, HoldUntilLinked = true     // what its Module gives waits until a Controller of the service is linked
            }, member.Trust.Sign, member.Trust.Verify, member.Trust.SignPayloads);
            // Joined is admitted and reachable: the Controller waits, a while, for the Controller of the Authority to be linked.
            var linking = System.Diagnostics.Stopwatch.StartNew();
            while (hub.Linked.Count == 0 && linking.Elapsed < TimeSpan.FromSeconds(20)) System.Threading.Thread.Sleep(20);
        }
        catch (InvalidOperationException e)
        {
            member.DisposeAsync().AsTask().GetAwaiter().GetResult();
            ExternalError = e.Message;
            return AifError.Failed;
        }
        lock (_tables) _members[moduleName] = member;
        return AifError.OK;
    }

    // The hubs of the External Ports of a Module (what they linked, sent, received, refused).
    public IReadOnlyList<AIF.Channels.ExternalHub> ExternalHubsOf(string moduleName)
    {
        lock (_tables) return _external.TryGetValue(moduleName, out var list) ? list.ToList() : [];
    }

    // The Controllers of a service are of one type - the service, named by its Authority - whatever Modules they run: a Back End and a Front End link.
    private static string AuthorityIdOf(string authorityAnchor) => (string?)System.Text.Json.Nodes.JsonNode.Parse(authorityAnchor)?["AnchorID"] ?? "";

    // MPAI_AIFU_External_Leave(MODULE_ID): the links of the Module are closed; its credential is discarded.
    public AifError ExternalLeave(string moduleName)
    {
        List<AIF.Channels.ExternalHub>? hubs;
        AIF.Controller.ExternalMember? member;
        AIF.Controller.ExternalAuthorityService? service;
        lock (_tables)
        {
            _external.Remove(moduleName, out hubs);
            _members.Remove(moduleName, out member);
            _authorities.Remove(moduleName, out service);
        }
        if (hubs is null && member is null && service is null) return AifError.NotFound;
        foreach (var hub in hubs ?? []) hub.DisposeAsync().AsTask().GetAwaiter().GetResult();
        member?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        service?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return AifError.OK;
    }

    // The boundary key the Controller routes on: DataType + PortNumber. Ports of
    // the same type are told apart only by number; a single occurrence is #1.
    private static string Key(string dataType, int portNumber) => dataType + "#" + portNumber;

    // ---- The User Agent's data path (M3203 3.3, M3213 3.2) -------------------

    // MPAI_AIFU_MODULE_Input_Write. Supplies a datum at a boundary input Port,
    // for the Module's next run. Writing never waits here, so timeoutMs is
    // honoured trivially; it is in the signature because a Port with a Depth
    // (Phase 4) may have to.
    public AifError InputWrite(string moduleName, string dataType, int portNumber, string json, int timeoutMs = -1)
    {
        Started? continuous = null;
        lock (_tables)
        {
            if (!_running.TryGetValue(moduleName, out var started) || started.Stopped) return AifError.NotStarted;

            var port = PortOf(started, "Input", dataType, portNumber);
            if (port is null) return AifError.NoSuchPort;
            if (HeaderOf(json) is { } header && !port.Accepts(header)) return AifError.TypeNotAccepted;

            if (_ua.IsContinuous(started.Id)) continuous = started;
            else
            {
                started.Written[Key(dataType, portNumber)] = json;
                return AifError.OK;
            }
        }

        // A Continuous Module: onto its boundary Channel, under its Port's
        // behaviour, within the timeout - outside the table's lock.
        return _ua.ContinuousWriteAsync(continuous.Id, dataType, portNumber, json, timeoutMs).GetAwaiter().GetResult();
    }

    // MPAI_AIFU_MODULE_Output_Read. Collects the datum of a boundary output Port.
    // The first read after one or more writes runs the Module on what was
    // written; later reads return what that run produced. timeoutMs: 0, do not
    // wait; negative, wait without limit. A run a TIMEOUT leaves behind
    // continues, and a later read returns its result.
    public Read OutputRead(string moduleName, string dataType, int portNumber, int timeoutMs = -1)
    {
        Task<Result>? run;
        int? continuous = null;
        System.Collections.Concurrent.BlockingCollection<string>? unroutedQueue = null;
        lock (_tables)
        {
            if (!_running.TryGetValue(moduleName, out var started)) return new Read(AifError.NotStarted, null);

            // AN EXTERNAL OUTPUT PORT IS READ BY THE CONTROLLER'S HUB, which sends what the Module gives to the Controllers it concerns. The User Agent that serves clients which are not
            // Controllers of the service reads what the hub could not route to one.
            if (_external.ContainsKey(moduleName) && (_ua.BoundaryPorts(started.Id) ?? Array.Empty<RuntimePort>())
                    .Any(p => p.IsRemote && p.Direction == "Output" && p.DataType == dataType && (p.PortNumber ?? 1) == portNumber))
            {
                unroutedQueue = UnroutedQueue(moduleName, dataType, portNumber);
                run = null;
            }
            else
            {
                if (PortOf(started, "Output", dataType, portNumber) is null) return new Read(AifError.NoSuchPort, null);
                if (_ua.IsContinuous(started.Id)) { continuous = started.Id; run = null; }
                else run = RunIfWritten(moduleName, started);
            }
        }

        // Waited for outside the table's lock: other calls of the User Agent are not held while it waits.
        if (unroutedQueue is not null)
            return unroutedQueue.TryTake(out var unrouted, timeoutMs < 0 ? System.Threading.Timeout.Infinite : timeoutMs) ? new Read(AifError.OK, unrouted) : new Read(AifError.NotProduced, null);

        // A Continuous Module: the oldest Message pending, within the timeout;
        // NOT_PRODUCED when none was (M3203 3.3).
        if (continuous is int id)
        {
            var (error, pending) = _ua.ContinuousReadAsync(id, dataType, portNumber, timeoutMs).GetAwaiter().GetResult();
            return new Read(error, pending);
        }

        if (run is null) return new Read(AifError.NotProduced, null);

        // THE EXCHANGE IS FINISHED FOR WHAT THE USER AGENT ASKS (OI-12): on the
        // Module's Channels, this Port settles as soon as it has its Message or can
        // no longer get one, whatever the rest of the run is doing.
        ContinuousExecutor.ExchangeRun? exchange;
        lock (_tables) exchange = _running.TryGetValue(moduleName, out var s) ? s.Exchange : null;
        if (exchange is not null)
        {
            var port = exchange.Port(dataType, portNumber);
            if (!Completes(port, timeoutMs)) return new Read(AifError.Timeout, null);
            if (port.Result is { } settled) return new Read(AifError.OK, settled);
            if (!Completes(run, 0) || run.Result.Error == AifError.OK) return new Read(AifError.NotProduced, null);
            return new Read(run.Result.Error, null);
        }

        if (!Completes(run, timeoutMs)) return new Read(AifError.Timeout, null);

        var result = run.Result;
        if (result.Error != AifError.OK) return new Read(result.Error, null);
        return result.ByType(dataType, portNumber) is { } json
            ? new Read(AifError.OK, json)
            : new Read(AifError.NotProduced, null);
    }

    // MPAI_AIFU_MODULE_Pause and _Resume. A run started while the Module is
    // paused waits, and a read observes its timeout.
    public AifError Pause(string moduleName)
    {
        lock (_tables)
            return _running.TryGetValue(moduleName, out var started)
                ? _ua.MPAI_AIFU_MODULE_Pause(started.Id)
                : AifError.NotStarted;
    }

    public AifError Resume(string moduleName)
    {
        lock (_tables)
            return _running.TryGetValue(moduleName, out var started)
                ? _ua.MPAI_AIFU_MODULE_Resume(started.Id)
                : AifError.NotStarted;
    }

    public ModuleStatus Status(string moduleName)
    {
        lock (_tables)
        {
            if (!_running.TryGetValue(moduleName, out var started)) return new ModuleStatus(AifError.NotStarted, Array.Empty<AimReport>());
            var err = _ua.MPAI_AIFU_MODULE_GetStatus(started.Id, out var aims);
            _ua.MPAI_AIFU_MODULE_GetState(started.Id, out var state);
            return new ModuleStatus(err, aims) { State = state, Record = _ua.RecordStatus(started.Id)?.Totals };
        }
    }

    public AifError StopAim(string moduleName, string aimName)
    {
        lock (_tables)
            return _running.TryGetValue(moduleName, out var started) && !started.Stopped
                ? _ua.MPAI_AIFU_AIM_Stop(started.Id, aimName)
                : AifError.NotStarted;
    }

    // MPAI_AIFU_Payload_Put (M3215 3.6): a payload placed for a boundary Input
    // Port of a Continuous Module; its Object is then written with the reference.
    public (AifError Error, string? Reference) PayloadPut(string moduleName, string dataType, int portNumber, ReadOnlyMemory<byte> data)
    {
        lock (_tables)
            return _running.TryGetValue(moduleName, out var started)
                ? _ua.PayloadPut(started.Id, dataType, portNumber, data)
                : (AifError.NotStarted, null);
    }

    public int PayloadsHeld(string moduleName)
    {
        lock (_tables) return _running.TryGetValue(moduleName, out var started) ? _ua.PayloadsHeld(started.Id) : 0;
    }

    // What each Channel of a Continuous Module carried (M3215 3.8).
    public IReadOnlyList<string> ChannelAccounts(string moduleName)
    {
        lock (_tables)
            return _running.TryGetValue(moduleName, out var started) ? _ua.ChannelAccounts(started.Id) : Array.Empty<string>();
    }

    // The Controller's transports and the one a Channel uses when its Output Port
    // declares none (M3215 3.1).
    public UserAgent Controller => _ua;

    // THE RECORD OF THE BOUNDARY (M3219 3.4).
    public AifError RecordStart(string moduleName, out string? recordId)
    {
        recordId = null;
        lock (_tables)
            return _running.TryGetValue(moduleName, out var started) ? _ua.MPAI_AIFU_Record_Start(started.Id, out recordId) : AifError.NotStarted;
    }

    public AifError RecordStop(string moduleName, out IReadOnlyDictionary<BoundaryRecord.PortKey, (long Recorded, long NotRecorded)>? totals)
    {
        totals = null;
        lock (_tables)
            return _running.TryGetValue(moduleName, out var started) ? _ua.MPAI_AIFU_Record_Stop(started.Id, out totals) : AifError.NotStarted;
    }

    public AIF.SharedStorage.IRuledStorage ModuleStorageAt(string moduleName, string location) =>
        _ua.ModuleStorageAt(moduleName, location);

    public string? RecordId(string moduleName)
    {
        lock (_tables)
            return _running.TryGetValue(moduleName, out var started) ? _ua.RecordStatus(started.Id)?.Id : null;
    }

    // The Private Storage of a running Module, as the User Agent may reach it:
    // under the rules of its writers or its central control (M3219 3.1).
    public AIF.SharedStorage.IRuledStorage? ModuleStorage(string moduleName)
    {
        lock (_tables)
            return _running.TryGetValue(moduleName, out var started) ? _ua.ModuleStorage(started.Id) : null;
    }

    public AifError SharedStorageInit(string moduleName, string location) => SharedStorageInit(moduleName, location, false);

    // THE USER AGENT'S STORE, READ, DELETE AND LIST on the Module's Shared Storage, through the handle the Controller binds to the User Agent: the rules and the Grants are the storage's.
    private static AifError Outcome(AIF.SharedStorage.StorageOutcome o) => o switch
    {
        AIF.SharedStorage.StorageOutcome.OK => AifError.OK,
        AIF.SharedStorage.StorageOutcome.NotFound => AifError.NotFound,
        _ => AifError.NotAuthorised
    };

    public AifError SharedStorageStore(string moduleName, string key, byte[] data, string category = "Data") =>
        SharedStorage(moduleName) is { } storage ? Outcome(storage.MPAI_AIFM_RuledStorage_Put(key, data, category)) : AifError.NotStarted;

    public AifError SharedStorageRead(string moduleName, string key, out byte[] data)
    {
        data = [];
        if (SharedStorage(moduleName) is not { } storage) return AifError.NotStarted;
        var outcome = storage.MPAI_AIFM_RuledStorage_Get(key, out var got);
        data = got ?? [];
        return Outcome(outcome);
    }

    public AifError SharedStorageDelete(string moduleName, string key) =>
        SharedStorage(moduleName) is { } storage ? Outcome(storage.MPAI_AIFM_RuledStorage_Delete(key)) : AifError.NotStarted;

    public IReadOnlyList<string> SharedStorageList(string moduleName, string prefix = "") =>
        SharedStorage(moduleName) is { } storage ? storage.MPAI_AIFM_RuledStorage_List(null, prefix) : [];

    // WHOSE SESSION AN EXCHANGE SERVES (M3245 3.3): a Service's Controller serves
    // many clients; the data an exchange keeps for its session are that client's,
    // and deleted when the client's session ends.
    public Result Advance(string moduleName, IEnumerable<Datum> inputs, string session)
    {
        _ua.MPAI_AIFU_Session_Serve(moduleName, session);
        try { return Advance(moduleName, inputs); }
        finally { _ua.MPAI_AIFU_Session_Serve(moduleName, null); }
    }

    public void SessionEnded(string session) => _ua.MPAI_AIFU_Session_End(session);

    // How long data of a category is kept at a location, where the writer does not say.
    public AifError SharedStorageKeep(string location, string category, AIF.SharedStorage.StorageTime time) =>
        _ua.MPAI_AIFU_SharedStorage_Keep(location, category, time);

    public AifError SharedStorageInit(string moduleName, string location, bool governs)
    {
        lock (_tables)
            return _running.TryGetValue(moduleName, out var started)
                ? _ua.MPAI_AIFU_SharedStorage_Init(started.Id, location, governs)
                : AifError.NotStarted;
    }

    // The Shared Storage at a running Module's location, as the User Agent may
    // reach it (M3219 3.3).
    public AIF.SharedStorage.IRuledStorage? SharedStorage(string moduleName)
    {
        lock (_tables)
            return _running.TryGetValue(moduleName, out var started) ? _ua.SharedStorage(started.Id) : null;
    }

    // Write every input, read every output: one exchange, built on the data path.
    // It is lenient where InputWrite is not - a datum for a Port the Module does
    // not declare is passed on, and meets no connection - because every client
    // and workflow was written against it.
    public Result Advance(string moduleName, IEnumerable<Datum> inputs)
    {
        bool ephemeral;
        lock (_tables) ephemeral = !_running.ContainsKey(moduleName);
        if (ephemeral)
        {
            var e = StartFlow(moduleName);
            if (e != AifError.OK) return new Result(e, Array.Empty<Datum>());
        }

        // A Continuous Module: the inputs written, and what is pending at its
        // boundary outputs now, one Message of each; nothing waits for more.
        int? continuousId = null;
        lock (_tables)
            if (_running.TryGetValue(moduleName, out var s) && _ua.IsContinuous(s.Id)) continuousId = s.Id;
        if (continuousId is int cid)
        {
            foreach (var d in inputs)
                _ua.ContinuousWriteAsync(cid, d.DataType, d.PortNumber, d.Json, -1).GetAwaiter().GetResult();
            var pending = new List<Datum>();
            foreach (var port in (_ua.BoundaryPorts(cid) ?? Array.Empty<RuntimePort>()).Where(p => p.Direction == "Output"))
            {
                var number = port.PortNumber ?? 1;
                var (error, json) = _ua.ContinuousReadAsync(cid, port.DataType, number, 0).GetAwaiter().GetResult();
                if (error == AifError.OK && json is not null) pending.Add(new Datum(port.DataType, number, json));
            }
            if (ephemeral) StopFlow(moduleName);
            return new Result(AifError.OK, pending);
        }

        Task<Result>? run;
        lock (_tables)
        {
            if (!_running.TryGetValue(moduleName, out var started) || started.Stopped)
                return new Result(AifError.NotStarted, Array.Empty<Datum>());
            foreach (var d in inputs)
                started.Written[Key(d.DataType, d.PortNumber)] = d.Json;
            run = RunIfWritten(moduleName, started, always: true)!;
        }

        var result = run.GetAwaiter().GetResult();
        if (ephemeral) StopFlow(moduleName);
        return result;
    }

    // Starts a run on what was written, if anything was written since the last
    // one; returns the Module's latest run. Called under _tables.
    private Task<Result>? RunIfWritten(string moduleName, Started started, bool always = false)
    {
        if (started.Written.Count > 0 || always)
        {
            var boundary = new Dictionary<string, string>(started.Written);
            started.Written.Clear();
            var exchange = _ua.StartExchange(started.Id, boundary);
            started.Exchange = exchange;
            started.Run = exchange is null
                ? Task.FromResult(new Result(AifError.NotStarted, Array.Empty<Datum>()))
                : Completed(started, exchange);
        }
        return started.Run;
    }

    private async Task<Result> Completed(Started started, ContinuousExecutor.ExchangeRun exchange)
    {
        Message message;
        try { message = await exchange.Completed; }
        catch { return new Result(AifError.Failed, Array.Empty<Datum>()); }
        return ToResult(started, AifError.OK, message);
    }

    private Result ToResult(Started started, AifError err, Message? completed)
    {
        if (_ua.ModuleStopped(started.Id))
            lock (_tables) started.Stopped = true;
        if (err != AifError.OK) return new Result(err, Array.Empty<Datum>());

        // An AIM's error does not end the run (every Module continues, until
        // Step 5 gives it its policy): the run's message is marked an error when
        // the last AIM to run failed, and still carries what the others produced.
        // A cancelled run - its Module stopped - produced nothing.
        var message = completed;
        if (message is null || message.IsCancelled)
            return new Result(AifError.Failed, Array.Empty<Datum>());

        // Outputs come back keyed by (DataType, PortNumber) too - parse the key.
        var outs = new List<Datum>();
        foreach (var kv in message.Ports)
        {
            var hash = kv.Key.LastIndexOf('#');
            if (hash <= 0) continue;
            var dt = kv.Key.Substring(0, hash);
            var pn = int.TryParse(kv.Key.Substring(hash + 1), out var n) ? n : 1;
            outs.Add(new Datum(dt, pn, kv.Value));
        }
        return new Result(AifError.OK, outs);
    }

    private static bool Completes(Task task, int timeoutMs) =>
        timeoutMs < 0 ? WaitedFor(task) : task.Wait(timeoutMs);

    private static bool WaitedFor(Task task) { task.Wait(); return true; }

    // The boundary Port of that Direction, Data Type and Port Number: the one
    // declaring that number, else the n-th of its type (1 where it occurs once).
    private RuntimePort? PortOf(Started started, string direction, string dataType, int portNumber)
    {
        var ports = (_ua.BoundaryPorts(started.Id) ?? Array.Empty<RuntimePort>())
            .Where(p => p.Direction == direction && p.Accepts(dataType))
            .ToList();
        return ports.FirstOrDefault(p => p.PortNumber == portNumber)
            ?? (ports.All(p => p.PortNumber is null) && portNumber >= 1 && portNumber <= ports.Count
                ? ports[portNumber - 1]
                : null);
    }

    // The Data Type an MPAI Object says it is: its Header. Null for a datum that
    // is not an Object, which says nothing about its type.
    private static string? HeaderOf(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("Header", out var h) && h.ValueKind == JsonValueKind.String
                ? h.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    public void Dispose()
    {
        List<AIF.Channels.ExternalHub> hubs;
        lock (_tables)
        {
            foreach (var started in _running.Values) _ua.MPAI_AIFU_MODULE_Stop(started.Id);
            _running.Clear();
            hubs = _external.Values.SelectMany(h => h).ToList();
            _external.Clear();
        }
        foreach (var hub in hubs) hub.DisposeAsync().AsTask().GetAwaiter().GetResult();
        (_provider as IDisposable)?.Dispose();
    }
}
