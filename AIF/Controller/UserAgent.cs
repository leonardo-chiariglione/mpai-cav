using System.Text.Json.Nodes;
using System;
using System.Collections.Concurrent;
using AIF.Channels;
using AIF.Store;

using AIF.Trust;

namespace AIF.Controller;

// The User Agent, as defined by MPAI-AIF V3.0 Basic API section 3.
// It is the SOLE boundary between the human/OS world and the AIF.
// The application (the human-facing UI) calls ONLY these MPAI_AIFU_* methods;
// it never touches the Controller internals or any AIM directly.
//
// In V3.0 an AI Workflow (Module) is a composite AIM, so "Module" here means the
// composite AIM (e.g. MMC-AMQ-V2.5).
//
// Error convention follows the standard: methods return AifError.OK on success.
public sealed class UserAgent
{
    private readonly AmdStore   _store;
    private Controller?         _controller;

    // SEVERAL CLIENTS, ONE USER AGENT. A Service runs different Modules for
    // different people at the same time, so the table of running Modules is read
    // by one run while another Module starts or stops. Starting itself is one at
    // a time: it builds the graph and fills the table of retained AIMs.
    private readonly ConcurrentDictionary<int, RunningModule> _running = new();
    private readonly object _startOne = new();
    private int _nextModuleId;

    // Where Shared Storage lives for this User Agent's Modules. Null means no
    // scope is configured and AIMs are handed no storage.
    private string? _sharedStorageRoot;

    public UserAgent(AmdStore store, string? sharedStorageRoot = null, IClock? clock = null)
    {
        _store = store;
        _sharedStorageRoot = sharedStorageRoot;
        Clock = clock ?? SystemClock.Instance;
        ControllerTransport = new ControllerTransport(Clock)
        {
            // What an AIM produces is looked at on the way, as it was by the
            // exchange executor.
            Observer = (spec, message) =>
                ContinuousExecutor.ObjectInspector?.Invoke(spec.Writer.Aim, message.DataType, message.Json)
        };
        InProcessTransport  = new InProcessTransport(Clock);
        BusTransport        = new BusTransport(Clock);
        RemoteTransport     = new RemoteTransport(LinkFor, Clock);
    }

    // The Modules with AIMs on hosts, by the name their hosts know them by.
    private readonly ConcurrentDictionary<string, RunningModule> hostModules = new();
    // The Modules whose AIMs are being placed on hosts: an AIM there reads its Access while it is built, before the Module is running.
    private readonly ConcurrentDictionary<string, (string Name, string? Central)> placingModules = new();

    // The Remote transport's reach: the link to the host an AIM of a Module
    // instance is placed on; null for an AIM here and for the boundary.
    private RemoteLink? LinkFor(string module, string aim) =>
        hostModules.TryGetValue(module, out var running) && running.Placed.TryGetValue(aim, out var client) ? client.Link : null;

    // THE CONTROLLER'S TIME BASE is whatever clock is plugged in (M3215 3.5). Every
    // Message is stamped on it, and an AIM reads it as IAimPorts.Now.
    public IClock Clock { get; }

    // THE SHORTEST PERIOD THIS CONTROLLER CAN KEEP: what its timer can resolve on
    // this machine, measured once (M3215 3.5). Not a number fixed in advance; a
    // Controller that knows better may set it.
    public TimeSpan MinimumPeriod { get; set; } = TimerResolution.Value;

    private static readonly Lazy<TimeSpan> TimerResolution = new(() =>
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 5; i++) Thread.Sleep(1);
        return TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerMillisecond, clock.Elapsed.Ticks / 5));
    });

    // MPAI_AIFU_SharedStorage_Init
    //
    // The specification has the User Agent ask the Controller to initialise the
    // storage interface, and that is the whole of the User Agent's part in it: it
    // says where, and never what identity a write will carry. The Controller binds
    // each AIM a handle stamped with the Module and the AIM, so that the record of
    // who wrote is made by the framework and not by the writer.
    public AifError MPAI_AIFU_SharedStorage_Init(string root)
    {
        _sharedStorageRoot = root;
        _controller?.SetSharedStorageRoot(root);
        return AifError.OK;
    }

    // ACCESS (AIF V3.0, Storage 6): the static or slowly changing data the Modules of
    // this Controller read. Where it is kept is the deployment's; null: there is none.
    public AccessStore? Access { get; set; }

    // THE USER AGENT HAS NO FUNCTION ON ACCESS (AIF V3.0, Storage 6.4): Access is infrastructure that serves the AIMs. What an AIM reads it declares in
    // its Metadata and the Controller obtains it; what the User Agent keeps for itself it keeps in Shared Storage (SharedStorage_Init, _Keep) or outside the
    // AIF, and gives to the Controller as data on the Ports of a Module.

    // The Access of an AIM placed on a host: the rules of each datum decide, as they do for an AIM here, for the AIM the request
    // names, which is of the Module the link was opened for.
    private IAccessReader? AccessOfAimOnHost(string module, string? central, string aim) =>
        Access is AccessStore store
            ? store.As(new AIF.SharedStorage.StorageHolder(module, aim),
                       central is { } c ? new AIF.SharedStorage.StorageHolder(module, c) : null)
            : Access;

    private static AifError ErrorOf(AccessOutcome outcome) => outcome switch
    {
        AccessOutcome.OK            => AifError.OK,
        AccessOutcome.NotFound      => AifError.NotFound,
        AccessOutcome.NotAuthorised => AifError.NotAuthorised,
        _                           => AifError.Failed               // Exists, Refused
    };

    // THE DATA OF PROVIDERS (AIF V3.0, Storage 6.3): only the Controller writes Access. It obtains what the Metadata of an AIM names
    // from the URI the Metadata gives; the data of a Provider, over a link the Trust Protocol admitted. AccessTrust is that
    // protocol (null: every Uri is obtained as it always was); OpenAccessHosts, the hosts whose data is published openly, obtained
    // by its SHA-256 alone.
    public AIF.Trust.TrustProtocol? AccessTrust { get; set; }
    public ISet<string> OpenAccessHosts { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private AccessFetcher? NewFetcher()
    {
        if (Access is null) return null;
        var fetcher = new AccessFetcher(Access) { Trust = AccessTrust };
        foreach (var host in OpenAccessHosts) fetcher.OpenHosts.Add(host);
        return fetcher;
    }

    private IEnumerable<DescriptorNode> LeavesOf(string aimName)
    {
        var selected = _store.GetCatalog().FirstOrDefault(c => c.AIMName == aimName);
        return selected is null || _controller is null ? [] : AimsOf(_controller.RegisterAim(new Identifier
        { AIMName = selected.AIMName, ImplementerID = selected.ImplementerID, ImplementationID = selected.ImplementationID }).Root);
    }

    // UPDATING ACCESS: the Controller reads the Metadata of a Module again and obtains what it names that Access does not hold - a new
    // SHA-256 for a Uri is a new Version of its Source. It is done each time a Module starts, and when this is called; nothing
    // watches a Uri. What was done for each entry of each AIM of the Module.
    public IReadOnlyList<AccessUpdate> RefreshAccess(string moduleName)
    {
        var fetcher = NewFetcher();
        return fetcher is null ? [] : LeavesOf(moduleName).SelectMany(l => fetcher.Refresh(l.AccessSources, l.AIMName)).ToList();
    }

    // MPAI_AIFM_MODEL_UPDATE: the Controller reads the Metadata of the Module again - as the Store gave it - and obtains the model the AIM names, as ModelUpdate of the host says.
    private Task<AifError> ModelUpdateAsync(string moduleName, AimHost host, string aim, string uri) => Task.Run(() =>
    {
        _store.Scan();
        var leaf = LeavesOf(moduleName).FirstOrDefault(l => l.AIMName == aim);
        return leaf is null ? AifError.NotFound : host.ModelUpdate(aim, uri, leaf.AccessSources, out _);
    });

    // The same, for a caller that is the Controller's own (a test, a tool): the AIM of a running Module.
    public Task<AifError> ModelUpdate(int moduleId, string aim, string uri) =>
        _running.TryGetValue(moduleId, out var module) && module.Continuous?.Models is { } update ? update(aim, uri) : Task.FromResult(AifError.NotStarted);

    public double? ModelDrift(int moduleId, string aim) => _running.TryGetValue(moduleId, out var module) ? module.Continuous?.Drift?.Invoke(aim) : null;

    // REMOVING FROM ACCESS: the Controller removes the Sources that the Metadata of no Module in its catalogue declares, and
    // keeps those that one does. An AIM, the User Agent and a Provider have no such call.
    public (IReadOnlyList<string> Removed, IReadOnlyList<string> Kept) PruneAccess()
    {
        var fetcher = NewFetcher();
        if (fetcher is null) return ([], []);
        var declared = _store.GetCatalog().SelectMany(c => LeavesOf(c.AIMName)).SelectMany(l => l.AccessSources);
        return fetcher.Prune(AccessFetcher.Declared(declared));
    }

    // THE TRANSPORTS OF THIS CONTROLLER (M3205 3.6.2, M3215 3.1): Controller, which
    // relays and can observe, and InProcess. The Channels of a Module use the one
    // its Output Ports declare, DefaultTransport where they declare none.
    public ControllerTransport ControllerTransport { get; }
    public InProcessTransport  InProcessTransport  { get; }
    public BusTransport        BusTransport        { get; }
    public RemoteTransport     RemoteTransport     { get; }
    public string DefaultTransport { get; set; } = "Controller";

    // WHERE AIMs PLACED ON OTHER MACHINES RUN (M3217 3.4): the AIM host named for
    // an AIM Instance - or for a composite that contains it - as host:port, and the
    // key the hosts were started with. The L3's Relation says an AIM runs
    // elsewhere; this says where.
    public Dictionary<string, string> AimHosts { get; } = new(StringComparer.Ordinal);

    // A DEPLOYMENT THAT RUNS EVERY AIM OF A MODULE ON ITS AIM HOST - the Controller holds none - says so here, and names the host for the Module (AimHosts[module]). It need not know the AIMs
    // of the Module, or rewrite the Relation in the Metadata: the Metadata is authoritative and stays as the Implementer wrote it.
    public bool AllAimsApart { get; set; }

    // WHAT THIS CONTROLLER HANDS A HOST WHEN IT PLACES AN AIM there: the AIM's Metadata, the fingerprints the MPAI Store approved and the AIM Licence it holds for it (null for an AIM
    // that has none to hand over). A host with a repository of its own takes the AIM in from it.
    public Func<string, JsonObject?>? PackageForHost { get; set; }

    // TRUST (Phase 13, M3223 3.1): where the deployment has configured its Trust
    // Anchor, every AIM Instance started is a PTF Process Instance - its key, its
    // CII, the Instance Credential this Controller issues it, and a Process
    // Lifecycle Credential that follows its state. Null: nothing verified, as before.
    public AIF.Trust.TrustDomain? Trust { get; set; }

    // THE ROOT OF TRUST OF THIS CONTROLLER (M3223 3.5): where it has one, its code is
    // measured into it now, each Implementation before it is built, and a host it
    // links to is given the quote. Its identity key is the key of Trust's anchor.
    public IRootOfTrust? RootOfTrust { get; private set; }

    public void Attest(IRootOfTrust root)
    {
        PartyCode.MeasureInto(root, Path.GetDirectoryName(typeof(UserAgent).Assembly.Location)!);
        RootOfTrust = root;
    }

    // WHAT THIS CONTROLLER REQUIRES OF A HOST'S ROOT OF TRUST: the manufacturer it
    // trusts, and the code a host may run - its reference values, file by file. Null
    // manufacturer: a host is not required to be attested.
    public System.Security.Cryptography.X509Certificates.X509Certificate2? Manufacturer { get; set; }
    public Dictionary<string, string> HostCode { get; } = new(StringComparer.Ordinal);

    private Attestation.Policy? HostAttestation => Manufacturer is { } manufacturer
        ? new Attestation.Policy(manufacturer, m => m.Register == Attestation.CodeRegister
            ? PartyCode.Approve(HostCode, m)
            : ImplementationEvidence.Approve(_store.Verifier, m))
        : null;

    // Why the last Module refused was refused, where it was not trusted.
    public string? LastRefusal { get; private set; }

    // The instance of a Module that runs, as its AIM Instances are named by it
    // (<Module instance>/<AIM>), and the AIM hosts it uses.
    public string? InstanceName(string module) => _instances.GetValueOrDefault(module);
    public IReadOnlyList<AimHostClient> HostsOf(string module) =>
        _running.Values.FirstOrDefault(m => m.Name == module)?.Hosts ?? (IReadOnlyList<AimHostClient>)[];
    public string AimHostKey { get; set; } = "";

    // THE ANCHORS OF THE AIM HOSTS this Controller uses (M3223 3.4): where Trust is
    // configured, a link to a host opens with the Trust Protocol, and a host that is
    // not one of these, or does not prove it, is not used. The key is not.
    public List<JsonObject> HostAnchors { get; } = new();

    private readonly Dictionary<string, AimHostClient> hostClients = new(StringComparer.Ordinal);
    private TrustedLink? _trustedLink;

    // How long the last link took to open, the Trust Protocol included.
    public TimeSpan LastLinkOpened { get; private set; }

    private AimHostClient HostAt(string address)
    {
        lock (hostClients)
        {
            if (hostClients.TryGetValue(address, out var client) && !client.Link.IsClosed) return client;
            AIF.Channels.ILinkAdmission admission = Trust is { } trust
                ? _trustedLink ??= new TrustedLink(trust.LinkWith(HostAnchors, RootOfTrust, HostAttestation))
                : new AIF.Channels.KeyAdmission(AimHostKey);
            var opening = System.Diagnostics.Stopwatch.StartNew();
            try { client = AimHostClient.ConnectAsync(address, admission).GetAwaiter().GetResult(); }
            catch (UnauthorizedAccessException refused) when (Trust is not null)
            {
                throw new TrustRefusedException($"the AIM host at {address} not trusted - {refused.Message[(refused.Message.IndexOf(": ", StringComparison.Ordinal) + 2)..].TrimEnd('.')}.");
            }
            LastLinkOpened = opening.Elapsed;

            // What a host sends (M3217 3.3): Messages its AIMs write to readers
            // here; an AIM of its stopping another - the Module is held here; an AIM
            // of its DEGRADED - the composite's policy applies here.
            client.Link.OnRequest = async frame =>
            {
                var module = hostModules.GetValueOrDefault(frame["Module"]?.GetValue<string>() ?? "");
                switch (frame["Kind"]?.GetValue<string>())
                {
                    case "Message":
                        return await RemoteTransport.ReceiveAsync(frame);
                    case "Time":                                   // the time base a host stamps on
                        return new JsonObject { ["Ok"] = true, ["Now"] = Clock.Now.ToString("O") };
                    case "Access" when module is not null:          // an AIM there reading Access
                        return RemoteAccess.Answer(AccessOfAimOnHost(module.Name, module.Graph.Root.StorageControl, frame["Aim"]?.GetValue<string>() ?? ""), frame);
                    case "Access" when placingModules.TryGetValue(frame["Module"]?.GetValue<string>() ?? "", out var placing):   // ... while it is being placed
                        return RemoteAccess.Answer(AccessOfAimOnHost(placing.Name, placing.Central, frame["Aim"]?.GetValue<string>() ?? ""), frame);
                    case "StopAim" when module is not null:
                        return new JsonObject
                        {
                            ["Ok"] = module.Host.StopAim(frame["Aim"]!.GetValue<string>(), $"stopped by {frame["By"]}")
                        };
                    default:
                        return new JsonObject { ["Ok"] = false, ["Error"] = "not asked of this Controller" };
                }
            };
            client.Link.OnNotice = frame =>
            {
                if (frame["Kind"]?.GetValue<string>() == "Degraded"
                    && hostModules.TryGetValue(frame["Module"]!.GetValue<string>(), out var module))
                    module.Continuous?.DegradedElsewhere(frame["Aim"]!.GetValue<string>(), frame["Reason"]!.GetValue<string>());
                return Task.CompletedTask;
            };

            // THE HOST GONE (M3217 3.2): each AIM of it is DEGRADED, and the
            // policy of its composite applies - at once in a Continuous Module; in
            // an exchange, when it is next fired and fails.
            var lost = client;
            client.Link.Lost += reason =>
            {
                var why = $"the link to its host {lost.Address} was lost: {reason}";
                foreach (var module in hostModules.Values)
                    foreach (var (aim, _) in module.Placed.Where(p => p.Value == lost))
                        if (module.Continuous is { } executor) executor.DegradedElsewhere(aim, why);
                        else module.Host.Degrade(aim, why);
            };
            hostClients[address] = client;
            return client;
        }
    }

    // Which AIMs of a Module are placed, and on which host: an AIM whose Relation,
    // or that of a composite containing it, is not Internal, on the host named for
    // it or for the nearest such composite.
    private Dictionary<string, string> Placement(DescriptorNode root, string module)
    {
        var placed = new Dictionary<string, string>(StringComparer.Ordinal);
        void Walk(DescriptorNode node, bool elsewhere, string? host)
        {
            foreach (var child in node.Children)
            {
                var away = elsewhere || AllAimsApart || (child.Relation.Length > 0 && child.Relation != "Internal");
                var at = AimHosts.GetValueOrDefault(child.AIMName) ?? host;
                if (child.IsComposite) { Walk(child, away, at); continue; }
                if (!away) continue;
                placed[child.AIMName] = at ?? throw new InvalidOperationException(
                    $"{module}: {child.AIMName} runs on another machine (its Relation, or a composite's containing it, is not Internal), and no AIM host is named for it.");
            }
        }
        Walk(root, false, AimHosts.GetValueOrDefault(root.AIMName));      // a host named for the Module itself holds all its AIMs that are not Internal
        return placed;
    }

    // True when this Module runs in exchanges - on its Channels (M3215 3.4) - and
    // not continuously.
    public bool ExchangesOnChannels(int moduleId) =>
        _running.TryGetValue(moduleId, out var module) && module.Continuous is null;

    // An exchange on the Module's Channels: each boundary Output Port settles as
    // soon as it can (OI-12).
    public ContinuousExecutor.ExchangeRun? StartExchange(int moduleId, IReadOnlyDictionary<string, string> boundaryPorts) =>
        _running.TryGetValue(moduleId, out var module)
            ? module.Channels.Exchange(boundaryPorts, Guid.NewGuid().ToString())
            : null;

    private IReadOnlyDictionary<string, IChannelTransport> Transports => new Dictionary<string, IChannelTransport>
    {
        [ControllerTransport.Name] = ControllerTransport,
        [InProcessTransport.Name]  = InProcessTransport,
        [BusTransport.Name]        = BusTransport,
        [RemoteTransport.Name]     = RemoteTransport
    };

    // MPAI_AIFU_SharedStorage_Init(MODULE_ID, location) (M3203 3.4.1): the scope
    // of one Module is held at location. Its AIMs' handles follow at their next
    // call. The User Agent says where, and nothing about who writes.
    public AifError MPAI_AIFU_SharedStorage_Init(int moduleId, string location) =>
        MPAI_AIFU_SharedStorage_Init(moduleId, location, false);

    // Several Modules given one location share its Shared Storage (M3219 3.3).
    // governs: this Module's StorageControl holds the central control of that
    // Shared Storage - refused where the Module names none, or another holds it.
    public AifError MPAI_AIFU_SharedStorage_Init(int moduleId, string location, bool governs)
    {
        if (!_running.TryGetValue(moduleId, out var module)) return AifError.NotFound;
        if (string.IsNullOrWhiteSpace(location)) return AifError.Failed;
        if (governs)
        {
            if (module.Graph.Root.StorageControl is not { } control) return AifError.NotAuthorised;
            if (!AIF.SharedStorage.RuledStore.SharedAt(location, () => Clock.Now)
                    .Govern(new AIF.SharedStorage.StorageHolder(module.Name, control)))
                return AifError.NotAuthorised;
        }
        module.StorageLocation = location;
        return AifError.OK;
    }

    // What the Module's AIMs on other machines are to do as well; a host gone
    // does not stop the rest.
    private static void OnHosts(RunningModule module, string kind)
    {
        foreach (var client in module.Hosts)
            try { client.AskAsync(kind, module.HostModule).Wait(TimeSpan.FromSeconds(5)); }
            catch { /* its AIMs are DEGRADED when next fired */ }
    }

    // A running Module (composite AIM): its graph, host, and boundary Ports.
    private sealed class RunningModule
    {
        public required string          Name        { get; init; }
        public required DescriptorGraph Graph       { get; init; }
        public required AimHost         Host        { get; init; }

        // The continuous executor, for a Module whose Metadata declares
        // Execution: Continuous (M3215 3.3); null for an exchange.
        public ContinuousExecutor? Continuous { get; init; }

        // The Module's Channels, planned for every Module: an exchange runs on
        // them (M3215 3.4).
        public required ContinuousExecutor Channels { get; init; }

        // Where the User Agent initialised this Module's Shared Storage; null,
        // the User Agent's root (MPAI_AIFU_SharedStorage_Init, M3203 3.4.1).
        public string? StorageLocation { get; set; }

        // The AIM hosts its placed AIMs run on, each placed AIM's, and the name
        // the Module has there.
        public List<AimHostClient> Hosts { get; } = new();
        public Dictionary<string, AimHostClient> Placed { get; } = new(StringComparer.Ordinal);
        public string HostModule { get; init; } = "";

        // Its Private Storage, under the rules of its writers or its central
        // control (M3219 3.1).
        public AIF.SharedStorage.RuledStore? Storage { get; init; }

        // Its record, while there is one (M3219 3.4).
        public BoundaryRecord? Record { get; set; }
    }

    // THE RECORD OF THE BOUNDARY (M3219 3.4). Its queue, and - to see a record that
    // cannot keep up - a delay before each write.
    public int RecordQueueDepth { get; set; } = 4096;
    public TimeSpan RecordWriteDelay { get; set; } = TimeSpan.Zero;

    private BoundaryRecord NewRecord(RunningModule module, bool always)
    {
        var header = new System.Text.Json.Nodes.JsonObject
        {
            ["Module"] = module.Name, ["ModuleInstance"] = module.HostModule,
            ["L3"] = module.Graph.Root.AIMName, ["TimeBase"] = Clock.GetType().Name, ["Always"] = always
        };
        var record = new BoundaryRecord(
            module.Storage!.For(new AIF.SharedStorage.StorageHolder(module.Name, AIF.SharedStorage.RuledStore.Controller), module.HostModule, _session),
            module.Channels.Payloads, Clock, header, always, RecordQueueDepth, RecordWriteDelay);
        module.Record = record;
        module.Channels.Record = record;
        return record;
    }

    // Record_Start (M3219 3.4): the Controller records the Module's boundary into
    // its Private Storage, until Record_Stop or the Module stops.
    public AifError MPAI_AIFU_Record_Start(int moduleId, out string? recordId)
    {
        recordId = null;
        if (!_running.TryGetValue(moduleId, out var module)) return AifError.NotFound;
        if (module.StorageLocation is null) return AifError.NotInitialized;    // the Module's scope, first
        if (module.Record is not null) return AifError.Failed;
        recordId = NewRecord(module, false).Id;
        return AifError.OK;
    }

    // Record_Stop: the record ends, and says what it recorded and what it could
    // not, per Port. A record the Metadata declares always on is not stopped.
    public AifError MPAI_AIFU_Record_Stop(int moduleId, out IReadOnlyDictionary<BoundaryRecord.PortKey, (long Recorded, long NotRecorded)>? totals)
    {
        totals = null;
        if (!_running.TryGetValue(moduleId, out var module)) return AifError.NotFound;
        if (module.Record is not { } record) return AifError.Failed;
        if (record.Always) return AifError.NotAuthorised;
        module.Channels.Record = null;
        module.Record = null;
        totals = record.StopAsync().GetAwaiter().GetResult();
        return AifError.OK;
    }

    // The record of a running Module, as it stands: its identity, and per Port
    // what is recorded and what is not.
    public (string Id, IReadOnlyDictionary<BoundaryRecord.PortKey, (long Recorded, long NotRecorded)> Totals)? RecordStatus(int moduleId) =>
        _running.TryGetValue(moduleId, out var module) && module.Record is { } record ? (record.Id, record.Totals) : null;

    // -- 3.1 General: initialise / destroy the Controller ---------------------

    // MPAI_AIFU_Controller_Initialize
    // The session of this User Agent with this Controller: data kept until the
    // session ends are this session's (M3219 3.1).
    private string _session = Guid.NewGuid().ToString("N");

    // THE SESSIONS OF THE CLIENTS OF A SERVICE (M3245 3.3). A Service's Controller
    // serves many clients, each in a session of its own: the User Agent says, for
    // an exchange of a Module, whose session it serves; the data that exchange
    // keeps for its session are that client's. Without one, the User Agent's own.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _sessions = new(StringComparer.Ordinal);

    public void MPAI_AIFU_Session_Serve(string moduleName, string? session)
    {
        if (session is null) { _sessions.TryRemove(moduleName, out _); return; }
        AIF.SharedStorage.RuledStore.Started(session);
        _sessions[moduleName] = session;
    }

    // A client's session ended: what its exchanges kept for the session is
    // deleted - now, not when next read.
    public void MPAI_AIFU_Session_End(string session)
    {
        AIF.SharedStorage.RuledStore.Ended(session);
        AIF.SharedStorage.RuledStore.SweepAll();
    }

    // HOW LONG DATA OF A CATEGORY IS KEPT AT A LOCATION (M3245 3.3), where its writer
    // does not say: a deployment's decision - on a server, what the persons of a
    // session register, as long as the session.
    public AifError MPAI_AIFU_SharedStorage_Keep(string location, string category, AIF.SharedStorage.StorageTime time)
    {
        if (string.IsNullOrWhiteSpace(location)) return AifError.Failed;
        AIF.SharedStorage.RuledStore.SharedAt(location, () => Clock.Now).Keep(category, time);
        return AifError.OK;
    }

    public AifError MPAI_AIFU_Controller_Initialize()
    {
        AIF.SharedStorage.RuledStore.Ended(_session);
        _session = Guid.NewGuid().ToString("N");
        AIF.SharedStorage.RuledStore.Started(_session);
        _controller = new Controller(_store);
        _controller.SetSharedStorageRoot(_sharedStorageRoot);
        _controller.InstanceOf = module => _instances.GetValueOrDefault(module, "");
        _controller.SessionOf  = module => _sessions.TryGetValue(module, out var s) ? s : _session;
        _controller.ClientOf   = module => _sessions.TryGetValue(module, out var s) ? s : "";
        _controller.Now        = () => Clock.Now;
        return AifError.OK;
    }

    // MPAI_AIFU_Controller_Destroy
    public AifError MPAI_AIFU_Controller_Destroy()
    {
        foreach (var module in _running.Values)
            module.Host.Dispose();
        _running.Clear();
        _controller = null;
        return AifError.OK;
    }

    // -- 3.2 Start/Pause/Resume/Stop the Module (composite AIM) ------------------

    private readonly Dictionary<string, IAimProcessor> _retained = new();

    private sealed class Retaining : IAimProvider
    {
        private readonly IAimProvider inner;
        private readonly Dictionary<string, IAimProcessor> kept;

        public Retaining(IAimProvider inner, Dictionary<string, IAimProcessor> kept)
        { this.inner = inner; this.kept = kept; }

        public bool CanCreate(string aimName) => inner.CanCreate(aimName);
        public string? ImplementationOf(string aimName) => inner.ImplementationOf(aimName);

        public IAimProcessor Create(string aimName,
            IReadOnlyDictionary<string, string> settings,
            AIF.SharedStorage.ISharedStorage? storage) =>
            Create(aimName, settings, storage, null);

        public IAimProcessor Create(string aimName,
            IReadOnlyDictionary<string, string> settings,
            AIF.SharedStorage.ISharedStorage? storage,
            AIF.SharedStorage.ISharedStorage? privateStorage) =>
            Create(aimName, settings, storage, privateStorage, null);

        public IAimProcessor Create(string aimName,
            IReadOnlyDictionary<string, string> settings,
            AIF.SharedStorage.ISharedStorage? storage,
            AIF.SharedStorage.ISharedStorage? privateStorage,
            AIF.SharedStorage.IRuledStorage? moduleStorage)
        {
            var key = Controller.CreatingInstance ?? aimName;                // each instance of an AIM its own (M3194)
            if (kept.TryGetValue(key, out var already)) return already;
            var made = inner.Create(aimName, settings, storage, privateStorage, moduleStorage);
            kept[key] = made;
            return made;
        }

        public IAimProcessor Create(string aimName,
            IReadOnlyDictionary<string, string> settings,
            AIF.SharedStorage.ISharedStorage? storage,
            AIF.SharedStorage.ISharedStorage? privateStorage,
            AIF.SharedStorage.IRuledStorage? moduleStorage,
            IAccessReader? access)
        {
            var key = Controller.CreatingInstance ?? aimName;
            if (kept.TryGetValue(key, out var already)) return already;
            var made = inner.Create(aimName, settings, storage, privateStorage, moduleStorage, access);
            kept[key] = made;
            return made;
        }
    }

    // MPAI_AIFU_MODULE_Start(name, out MODULE_ID)
    public AifError MPAI_AIFU_MODULE_Start(
        string name, IAimProvider provider, AimSettings settings, out int moduleId)
    {
        lock (_startOne)
        {
            try { LastRefusal = null; return StartOne(name, provider, settings, out moduleId); }
            catch (TrustRefusedException refused)
            {
                // An AIM not trusted (M3223 3.8): the outcome NOT_TRUSTED, and why.
                LastRefusal = refused.Message;
                moduleId = -1;
                return AifError.NotTrusted;
            }
        }
    }

    private AifError StartOne(
        string name, IAimProvider provider, AimSettings settings, out int moduleId)
    {
        moduleId = -1;
        if (_controller is null) return AifError.NotInitialized;

        var selected = _store.GetCatalog().FirstOrDefault(c => c.AIMName == name);
        if (selected is null) return AifError.NotFound;

        var identifier = new Identifier
        {
            AIMName          = selected.AIMName,
            ImplementerID    = selected.ImplementerID,
            ImplementationID = selected.ImplementationID
        };

        var graph = _controller.RegisterAim(identifier);
        var fetcher = NewFetcher();
        var host  = new AimHost { Access = Access, Fetcher = fetcher, ModuleName = name, CentralControl = graph.Root.StorageControl };
        RunningModule? started = null;

        // THE CONTROLLER KEEPS WHAT IT HAS BUILT. Stopping a Module releases the
        // Module; it does not throw away the models its AIMs loaded. A person who
        // tries one App, then another, then returns to the first should not wait
        // for the same models to load twice.
        //
        // What is retained is the AIM implementation, so anything it holds is
        // retained with it. An AIM that keeps state between runs - a dialogue
        // memory, say - will carry that state into the next Module that uses it.
        // AIMs PLACED ON OTHER MACHINES are placed on their hosts first: the Module
        // is refused, before anything is built, if one has no host named.
        var hostModule = $"{name}#{Guid.NewGuid():N}";

        // THE PRIVATE STORAGE OF THE MODULE (M3219 3.1), below its scope beside its
        // AIMs' own; its central control, where the Metadata names one, an AIM of
        // the Module.
        // THE ACCESS A MODULE NEEDS (AIM Metadata, AccessSources): what each AIM declares is obtained, checked against the SHA-256
        // its Metadata gives; an entry an AIM does not mark optional that cannot be obtained refuses the Module.
        foreach (var leaf in AimsOf(graph.Root))
        {
            if (AccessEntry.Check(leaf.AccessSources) is { Length: > 0 } wrong)
                throw new InvalidOperationException($"{name}: {leaf.AIMName} declares its Access wrongly: {wrong}");
            var got = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in leaf.AccessSources.Where(e => !e.IsPlaceholder || !e.IsOptional))
            {
                var why = "the Controller has no Access";
                if (fetcher is not null && fetcher.Ensure(entry, leaf.AIMName, out why) == AccessOutcome.OK) got.Add(entry.Sha256);
                else if (!entry.IsOptional)
                    throw new InvalidOperationException($"{name}: {leaf.AIMName} needs the Access '{entry.Name}' and there is no such Source: {why}");
            }
            // Data that is only valid with other data is not given without it.
            foreach (var entry in leaf.AccessSources.ToList())
                if (got.Contains(entry.Sha256) && entry.ValidWith?.FirstOrDefault(h => !got.Contains(h)) is { } missing)
                {
                    if (!entry.IsOptional)
                        throw new InvalidOperationException($"{name}: {leaf.AIMName}: the Access '{entry.Name}' is valid only with data {missing}, which could not be obtained");
                    leaf.AccessSources.Remove(entry);
                    fetcher?.Withhold(entry, leaf.AIMName);   // and what was obtained for it is not read by it (STO-10)
                }
        }

        if (graph.Root.StorageControl is { } control && !Leaves(graph.Root).Contains(control))
            throw new InvalidOperationException(
                $"{name}: its StorageControl names {control}, which is not an AIM of the Module.");
        var moduleStore = _stores[name] = new AIF.SharedStorage.RuledStore(
            () => (started?.StorageLocation ?? _sharedStorageRoot) is { } scope
                ? Path.Combine(scope, "private", Uri.EscapeDataString(name)) : null,
            () => Clock.Now, everyoneReads: false,
            graph.Root.StorageControl is { } central ? new AIF.SharedStorage.StorageHolder(name, central) : null);
        _instances[name] = hostModule;
        AIF.SharedStorage.RuledStore.Started(hostModule);
        var placement = Placement(graph.Root, name);
        if (placement.Count > 0) placingModules[hostModule] = (name, graph.Root.StorageControl);
        var hosts = new List<AimHostClient>();
        var placedIdentities = new Dictionary<string, JsonObject?>(StringComparer.Ordinal);
        var placedEvidence = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var placedDeclared = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);

        // THE CONTROLLER IS THE VERIFIER (M3223 3.1-3.3). Before an AIM it builds is
        // built: its identity and credentials issued; what it will run measured - the
        // binary its provider names, the models its settings name - as evidence; the
        // policy it is bound by derived from its approved Metadata; and the
        // Verification Pipeline run on all of it. An AIM not trusted refuses the
        // Module before anything runs.
        if (Trust is { } verifier)
            foreach (var leaf in AimsOf(graph.Root).Where(l => !placement.ContainsKey(l.AIMName)))
            {
                var instanceId = $"{hostModule}/{leaf.Instance}";
                var instance = verifier.IssueLocal(instanceId, leaf.AIMName, leaf.AIMName, issuer: leaf.Packaged.Count > 0);
                // The models Access gave the AIM are those its settings name when it runs, and are measured as such.
                var aimSettings = AccessEntry.Overlay(settings.For(leaf.AIMName), leaf.AccessSources, host.AccessFor(leaf.AIMName), leaf.AIMName);
                var measured = ImplementationEvidence.Of(provider.ImplementationOf(leaf.AIMName), aimSettings);
                ImplementationEvidence.Record(RootOfTrust, leaf.AIMName, measured);
                instance.Evidence = verifier.SignEvidence(instanceId, measured);
                Verify(verifier, instance, leaf, aimSettings, name);
            }
        IAimProcessor? OnItsHost(DescriptorNode leaf)
        {
            if (!placement.TryGetValue(leaf.AIMName, out var address)) return null;
            var client = HostAt(address);
            var reply = client.PlaceAsync(hostModule, leaf.AIMName, PackageForHost?.Invoke(leaf.AIMName)).GetAwaiter().GetResult();
            placedIdentities[leaf.AIMName] = reply["CII"] as JsonObject;

            // THE CONTROLLER VERIFIES, not the host: what the host measured of the files it obtained is put to the Store, which says whether it is what it approved.
            if (reply["Measured"] is JsonObject measuredByHost)
                foreach (var (file, fingerprint) in measuredByHost)
                    if (_store.Verifier.Verify(leaf.AIMName, file, (string?)fingerprint ?? "") != VerifyResult.Same)
                        throw new TrustRefusedException($"{name} not started: NOT_TRUSTED - {file} of {leaf.AIMName}, obtained on the AIM host at {address}, is not what the Store approved.");

            // What it runs there, measured by the host and signed by the key the AIM
            // holds there (M3223 3.2); verified with the rest once it is placed.
            if (reply["Evidence"] is JsonObject evidence) placedEvidence[leaf.AIMName] = evidence;
            placedDeclared[leaf.AIMName] = SettingsOnHost(reply);
            if (!hosts.Contains(client)) hosts.Add(client);
            return new RemoteProcessor(leaf.AIMName, client, hostModule, host.Report);
        }

        // A Module refused once some of its AIMs are placed releases them.
        try
        {
            var made = _controller.Instantiate(graph, new Retaining(provider, _retained), settings, host,
                () => started?.StorageLocation ?? _sharedStorageRoot, OnItsHost,
                aim => new CurrentStorage(this, name, aim));
            placingModules.TryRemove(hostModule, out _);

            // AN AIM ON A HOST (M3223 3.1-3.3): its key made there, its CII sent here;
            // issued its credential only if the CII shows the host holds that key; then
            // verified like the others, before its Module starts.
            if (Trust is { } trust)
            {
                foreach (var leaf in AimsOf(graph.Root).Where(l => placement.ContainsKey(l.AIMName)))
                {
                    var aim = leaf.AIMName;
                    var address = placement[aim];
                    var instanceId = $"{hostModule}/{aim}";
                    var cii = placedIdentities.GetValueOrDefault(aim)
                        ?? throw new TrustRefusedException($"{name} not started: the AIM host at {address} gave {aim} no identity.");
                    AIF.Trust.TrustDomain.Instance issued;
                    try { issued = trust.IssueHeld(instanceId, aim, cii); }
                    catch (InvalidOperationException e) { throw new TrustRefusedException($"{name} not started: on the AIM host at {address}, {e.Message}"); }
                    issued.Evidence = placedEvidence.GetValueOrDefault(aim);
                    Verify(trust, issued, leaf, placedDeclared.GetValueOrDefault(aim) ?? new Dictionary<string, string>(), name, address);
                    var taken = HostAt(address).AskAsync("Credential", hostModule, new JsonObject
                    {
                        ["Aim"] = aim, ["Credential"] = issued.Credential.DeepClone(), ["Lifecycle"] = issued.Lifecycle.DeepClone()
                    }).GetAwaiter().GetResult();
                    if (taken["Ok"]?.GetValue<bool>() != true)
                        throw new InvalidOperationException($"{name}: the AIM host at {address} did not take the credential of {aim}: {taken["Error"]}.");
                }

                // A PACKAGE (M3223 3.3): each AIM its L3 says it contains, presented by
                // it and verified through the chain to this Controller.
                foreach (var leaf in AimsOf(graph.Root).Where(l => l.Packaged.Count > 0 && !placement.ContainsKey(l.AIMName)))
                    VerifyPackage(trust, leaf, host, $"{hostModule}/{leaf.AIMName}", name);
            }

            moduleId = Interlocked.Increment(ref _nextModuleId);

            // Every Module's Channels are planned, so that one whose reader does not
            // accept its writer's transport refuses to load (M3215 3.1); a Continuous
            // Module runs on them from now until Stop.
            //
            // A Continuous Module with AIMs on hosts runs there too: the Channels that
            // touch them cross the Remote transport, and each host runs its AIMs on
            // them (M3217 3.2, 3.4). Its instance has the name its hosts know it by.
            var placedHere = graph.Root.IsContinuous && placement.Count > 0;
            var channels = placedHere
                ? new ContinuousExecutor(graph, host, Transports, hostModule, DefaultTransport, Clock, aim => !placement.ContainsKey(aim))
                : new ContinuousExecutor(graph, host, Transports, $"{name}#{moduleId}", DefaultTransport, Clock);

            // THE CHANNELS PLANNED, AGAINST THE POLICIES (M3223 3.3): every end an AIM is
            // given is one its policy allows. The Controller binds nothing else.
            if (Trust is { } bound) CheckChannels(bound, channels, hostModule, name);

            // A PERIOD OR A DEADLINE the Controller cannot honour refuses the Module
            // (M3215 3.5): in an exchange, which runs when the User Agent asks; or a
            // Period shorter than this Controller can keep.
            foreach (var aim in channels.Aims)
            {
                if ((aim.Period is not null || aim.Deadline is not null) && !graph.Root.IsContinuous)
                    throw new InvalidOperationException(
                        $"{name}: {aim.AIMName} declares a {(aim.Period is not null ? "Period" : "Deadline")}, and {name} is not Execution: Continuous.");
                if (aim.Period is { } period && TimeSpan.FromMilliseconds(period) < MinimumPeriod)
                    throw new InvalidOperationException(
                        $"{name}: {aim.AIMName} declares a Period of {period} ms; the shortest this Controller can keep is {MinimumPeriod.TotalMilliseconds:0.#} ms.");
            }

            started = new RunningModule
            {
                Name       = name,
                Graph      = graph,
                Host       = host,
                Continuous = graph.Root.IsContinuous ? channels : null,
                Channels   = channels,
                HostModule = hostModule,
                Storage    = moduleStore
            };
            started.Hosts.AddRange(hosts);
            // MACHINE LEARNING (Basic API 4.12): what an AIM of this Module asks of the Controller about its models.
            channels.Models = (aim, uri) => ModelUpdateAsync(name, host, aim, uri);
            channels.Drift = aim => host.Processor(aim) is IModelDrift drifting ? drifting.Drift : null;
            foreach (var (aim, address) in placement) started.Placed[aim] = HostAt(address);
            if (placement.Count > 0)
            {
                hostModules[hostModule] = started;

                // An AIM here stopped - by the User Agent, by an AIM, by a policy -
                // that runs on a host is stopped there.
                var placedOnes = started.Placed;
                host.AimStopped = (aim, reason) =>
                {
                    if (placedOnes.TryGetValue(aim, out var client) && !client.Link.IsClosed)
                        _ = client.AskAsync("StopAim", hostModule, new JsonObject { ["Aim"] = aim }).ContinueWith(_ => { });
                };
            }

            if (placedHere)
            {
                foreach (var client in hosts)
                {
                    var (specs, aims) = channels.PlacedPart(placement.Where(p => p.Value == client.Address).Select(p => p.Key).ToHashSet());
                    var reply = client.AskAsync("Start", hostModule, new JsonObject
                    {
                        ["Channels"] = new JsonArray(specs.Select(s => (JsonNode)RemoteTransport.SpecJson(s)).ToArray()),
                        ["Aims"]     = new JsonArray(aims.Select(a => (JsonNode)new JsonObject { ["Aim"] = a.Aim, ["OnDegraded"] = a.OnDegraded }).ToArray())
                    }).GetAwaiter().GetResult();
                    if (reply["Ok"]?.GetValue<bool>() != true)
                        throw new InvalidOperationException($"{name}: the AIM host at {client.Address} did not start its AIMs: {reply["Error"]}.");
                }
            }

            // A Module whose Metadata declares its record always on is recorded from
            // its Start, whatever the User Agent does (M3219 3.4).
            if (graph.Root.Record == "Always") NewRecord(started, true);

            if (graph.Root.IsContinuous) channels.Start();

            _running[moduleId] = started;
            Trust?.TransitionModule(hostModule, "Running");
            return AifError.OK;
        }
        catch
        {
            Trust?.TransitionModule(hostModule, "Terminated");
            placingModules.TryRemove(hostModule, out _);
            hostModules.TryRemove(hostModule, out _);
            foreach (var client in hosts)
                try { client.AskAsync("Release", hostModule).Wait(TimeSpan.FromSeconds(5)); } catch { }
            throw;
        }
    }

    // MPAI_AIFU_MODULE_Pause. Every AIM of the Module, at every depth, completes
    // what it is doing and waits, until Resume - across runs.
    public AifError MPAI_AIFU_MODULE_Pause(int moduleId)
    {
        if (!_running.TryGetValue(moduleId, out var module)) return AifError.NotFound;
        module.Host.PauseModule();
        OnHosts(module, "Pause");
        Trust?.TransitionModule(module.HostModule, "Suspended");
        return AifError.OK;
    }

    // MPAI_AIFU_MODULE_Resume
    public AifError MPAI_AIFU_MODULE_Resume(int moduleId)
    {
        if (!_running.TryGetValue(moduleId, out var module)) return AifError.NotFound;
        module.Host.ResumeModule();
        OnHosts(module, "Resume");
        Trust?.TransitionModule(module.HostModule, "Running");
        return AifError.OK;
    }

    // MPAI_AIFU_MODULE_Stop
    public AifError MPAI_AIFU_MODULE_Stop(int moduleId)
    {
        if (!_running.TryGetValue(moduleId, out var module)) return AifError.NotFound;
        module.Continuous?.StopAsync().GetAwaiter().GetResult();
        if (module.Record is { } record)
        {
            module.Channels.Record = null;
            module.Record = null;
            record.StopAsync().GetAwaiter().GetResult();
        }
        OnHosts(module, "Stop");
        OnHosts(module, "Release");
        hostModules.TryRemove(module.HostModule, out _);
        module.Host.Dispose();
        AIF.SharedStorage.RuledStore.Ended(module.HostModule);
        AIF.SharedStorage.RuledStore.SweepAll();   // what was kept as long as the Module ran goes with it (Storage 3)
        Trust?.TransitionModule(module.HostModule, "Terminated");
        _running.TryRemove(moduleId, out _);
        return AifError.OK;
    }

    // THE PRIVATE STORAGE OF THE MODULE, THE USER AGENT'S FACE (M3219 3.1): the
    // User Agent holds a handle as any AIM of the Module does, bound to it, and
    // reaches what the rules let it reach. Never the central control.
    // AN AIM IS RETAINED ACROSS MODULE INSTANCES, AND ITS HANDLE WITH IT: the
    // handle reaches, at each call, the store of the instance of its Module that
    // runs now, as the Shared Storage handle asks where its scope now is.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AIF.SharedStorage.RuledStore> _stores = new(StringComparer.Ordinal);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _instances = new(StringComparer.Ordinal);

    private sealed class CurrentStorage(UserAgent ua, string module, string aim) : AIF.SharedStorage.IRuledStorage
    {
        private AIF.SharedStorage.IRuledStorage Now() =>
            (ua._stores.TryGetValue(module, out var store) ? store : throw new InvalidOperationException($"{module} is not running."))
            .For(new AIF.SharedStorage.StorageHolder(module, aim), ua._instances.GetValueOrDefault(module, ""), ua._sessions.TryGetValue(module, out var served) ? served : ua._session);   // the session the exchange serves (Basic API 3.7.1)

        public string Holder => $"{module}/{aim}";
        public AIF.SharedStorage.StorageOutcome MPAI_AIFM_RuledStorage_Put(string key, byte[] data, string category, IReadOnlyCollection<string>? readers = null, AIF.SharedStorage.StorageTime? time = null) =>
            Now().MPAI_AIFM_RuledStorage_Put(key, data, category, readers, time);
        public AIF.SharedStorage.StorageOutcome MPAI_AIFM_RuledStorage_Get(string key, out byte[] data) => Now().MPAI_AIFM_RuledStorage_Get(key, out data);
        public AIF.SharedStorage.StorageOutcome MPAI_AIFM_RuledStorage_Delete(string key) => Now().MPAI_AIFM_RuledStorage_Delete(key);
        public IReadOnlyList<string> MPAI_AIFM_RuledStorage_List(string? category = null, string prefix = "") => Now().MPAI_AIFM_RuledStorage_List(category, prefix);
        public bool MPAI_AIFM_RuledStorage_Exists(string key) => Now().MPAI_AIFM_RuledStorage_Exists(key);
        public AIF.SharedStorage.StorageOutcome MPAI_AIFM_RuledStorage_Trace(string key, out AIF.SharedStorage.StorageTrace? trace) => Now().MPAI_AIFM_RuledStorage_Trace(key, out trace);
        public AIF.SharedStorage.StorageOutcome MPAI_AIFM_RuledStorage_SetRule(string category, AIF.SharedStorage.StorageRule rule) => Now().MPAI_AIFM_RuledStorage_SetRule(category, rule);
    }

    public AIF.SharedStorage.IRuledStorage? ModuleStorage(int moduleId) =>
        _running.TryGetValue(moduleId, out var module)
            ? module.Storage?.For(AIF.SharedStorage.StorageHolder.UserAgent, module.HostModule, _session) : null;

    // THE PRIVATE STORAGE OF A MODULE AT A LOCATION, running or not - a record
    // is read after its Module stops, or from another instance (M3219 3.5): the
    // User Agent reaches what it is a reader of. Rules of a central control no
    // longer running do not reach it; its data read as their writers named.
    public AIF.SharedStorage.IRuledStorage ModuleStorageAt(string moduleName, string location) =>
        new AIF.SharedStorage.RuledStore(() => Path.Combine(location, "private", Uri.EscapeDataString(moduleName)),
                                         () => Clock.Now, everyoneReads: false, centralControl: null)
            .For(AIF.SharedStorage.StorageHolder.UserAgent, "", _session);

    // The Shared Storage at a running Module's location, as the User Agent may
    // reach it (M3219 3.3).
    public AIF.SharedStorage.IRuledStorage? SharedStorage(int moduleId) =>
        _running.TryGetValue(moduleId, out var module) && (module.StorageLocation ?? _sharedStorageRoot) is { } at
            ? (AIF.SharedStorage.IRuledStorage)AIF.SharedStorage.RuledStore.SharedAt(at, () => Clock.Now)
                .PlainFor(AIF.SharedStorage.StorageHolder.UserAgent, module.HostModule, _session, "local")
            : null;

    public static AifError Outcome(AIF.SharedStorage.StorageOutcome outcome) => outcome switch
    {
        AIF.SharedStorage.StorageOutcome.OK       => AifError.OK,
        AIF.SharedStorage.StorageOutcome.NotFound => AifError.NotFound,
        _                                         => AifError.NotAuthorised
    };

    // ---- the Verifier (M3223 3.3) ----------------------------------------------------

    // The policy of an AIM, from its approved Metadata: the Ports it may read and
    // write, and the storage it is given.
    private static List<(string Name, string Value)> PolicyOf(DescriptorNode leaf) =>
        leaf.Ports.Select(p => ("Port", $"{p.Direction} {string.Join("|", p.DataTypes.Count > 0 ? p.DataTypes : [p.DataType])}#{p.PortNumber ?? 1}"))
            .Concat([("Storage", "Private"), ("Storage", "Module"), ("Storage", "Shared")])
            .ToList();

    private void Verify(AIF.Trust.TrustDomain trust, AIF.Trust.TrustDomain.Instance instance, DescriptorNode leaf,
                        IReadOnlyDictionary<string, string> settings, string module, string? onHost = null)
    {
        var allowed = PolicyOf(leaf);
        var policy = trust.BindPolicy(instance.Id, allowed);
        var binary = _store.BinaryNameOf(IdentifierOf(leaf));
        var decision = trust.Verify(instance, new AIF.Trust.VerificationPipeline.Expectation(
            instance.Id, LifecycleState: "Created", EvidenceRequired: true,
            CheckEvidence: items => ImplementationEvidence.Check(leaf.AIMName, items, binary, _store.Verifier, settings),
            Policy: policy,
            CheckPolicy: bound =>
            {
                var constraints = (bound["Constraints"] as JsonArray ?? []).Select(c => ((string?)c?["Name"] ?? "", (string?)c?["Value"] ?? "")).ToList();
                return constraints.OrderBy(c => c).SequenceEqual(allowed.OrderBy(c => c)) ? [] : ["its policy is not what its approved Metadata allows"];
            }));
        if (!decision.Trusted)
            throw new TrustRefusedException($"{module} not started: {leaf.AIMName}{(onHost is null ? "" : $" on the AIM host at {onHost}")} not trusted - {decision.Reason.Replace(leaf.AIMName + ": ", "")}.");
    }

    private void VerifyPackage(AIF.Trust.TrustDomain trust, DescriptorNode leaf, AimHost host, string packageId, string module)
    {
        if (host.ProcessorOf(leaf.AIMName) is not IAimPackage package)
            throw new TrustRefusedException($"{module} not started: {leaf.AIMName} contains AIMs, and does not present them.");
        var packageInstance = trust.Of(packageId)!;
        var asIssuer = new AIF.Trust.PtfIssuer(packageId, trust.KeyOf(packageId)!, packageId);
        var presented = package.Present(asIssuer, packageId, Clock.Now);
        var names = presented.Select(p => p.AimName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (!names.SequenceEqual(leaf.Packaged.OrderBy(n => n, StringComparer.Ordinal)))
            throw new TrustRefusedException($"{module} not started: {leaf.AIMName} presents {string.Join(", ", names)}; its L3 says it contains {string.Join(", ", leaf.Packaged)}.");
        var chain = new[] { new AIF.Trust.PtfCredential.Link(packageInstance.Cii, packageInstance.Credential) };
        var packageCode = AIF.Trust.PtfEvidence.Items(packageInstance.Evidence!).FirstOrDefault(i => i.Type == AIF.Trust.PtfEvidence.CodeHash);
        foreach (var inner in presented)
        {
            var innerId = $"{packageId}/{inner.AimName}";
            var instance = trust.RecordPackaged(innerId, packageId, inner.Cii, inner.Credential, inner.Evidence);
            var decision = trust.Verify(instance, new AIF.Trust.VerificationPipeline.Expectation(
                innerId,
                // What runs inside a package is the package's binary.
                CheckEvidence: items => items.Where(i => i.Type == AIF.Trust.PtfEvidence.CodeHash)
                                             .Any(i => packageCode is null || i.Hash != packageCode.Hash)
                    ? [$"{inner.AimName} is not run by the package's binary"] : []), chain);
            if (!decision.Trusted)
                throw new TrustRefusedException($"{module} not started: {inner.AimName} inside {leaf.AIMName} not trusted - {decision.Reason}.");
        }
    }

    private static void CheckChannels(AIF.Trust.TrustDomain trust, ContinuousExecutor channels, string hostModule, string module)
    {
        bool Allows(AIF.Channels.PortEnd end, string direction) =>
            trust.Of($"{hostModule}/{end.Aim}")?.Policy?["Constraints"] is JsonArray constraints &&
            constraints.Any(c => (string?)c?["Name"] == "Port" && (string?)c?["Value"] is { } v &&
                                 v.StartsWith(direction + " ", StringComparison.Ordinal) && v.EndsWith($"#{end.PortNumber}", StringComparison.Ordinal) &&
                                 v[(direction.Length + 1)..v.LastIndexOf('#')].Split('|').Contains(end.DataType));
        foreach (var spec in channels.Channels)
        {
            if (!spec.Writer.IsBoundary && !Allows(spec.Writer, "Output"))
                throw new TrustRefusedException($"{module} not started: the Channel {spec.Writer} is not one its policy allows.");
            foreach (var reader in spec.Readers.Where(r => !r.Reader.IsBoundary))
                if (!Allows(reader.Reader, "Input"))
                    throw new TrustRefusedException($"{module} not started: the Channel to {reader.Reader} is not one its policy allows.");
        }
    }

    // The AIMs the Controller builds: the leaves, or the Module itself where it is one AIM.
    private static IEnumerable<DescriptorNode> AimsOf(DescriptorNode root) => root.IsComposite ? LeafNodes(root) : [root];

    private static IEnumerable<DescriptorNode> LeafNodes(DescriptorNode node) =>
        node.Children.SelectMany(c => c.IsComposite ? LeafNodes(c) : [c]);

    private static Identifier IdentifierOf(DescriptorNode node) => new()
    {
        AIMName = node.AIMName, ImplementerID = node.ImplementerID, ImplementationID = node.ImplementationID
    };

    // The settings of an AIM on a host, as the host reports it measured them.
    private static IReadOnlyDictionary<string, string> SettingsOnHost(JsonObject reply) =>
        (reply["Declared"] as JsonObject)?.ToDictionary(p => p.Key, p => (string?)p.Value ?? "", StringComparer.Ordinal)
        ?? new Dictionary<string, string>(StringComparer.Ordinal);

    private static IEnumerable<string> Leaves(DescriptorNode node) =>
        node.Children.SelectMany(c => c.IsComposite ? Leaves(c) : [c.AIMName]);

    // The External Ports of a running Continuous Module, as its AIMs call them (Basic API 5); and what the Controller of the Module asks of it to serve them.
    public void SetExternal(int moduleId, AIF.Channels.IExternalPorts? external) { if (_running.TryGetValue(moduleId, out var module) && module.Continuous is { } run) run.External = external; }
    public bool HasAim(int moduleId, string name) => _running.TryGetValue(moduleId, out var module) && module.Continuous is { } run && run.HasAim(name);
    public async Task<bool> ContinuousReadableAsync(int moduleId, string dataType, int portNumber, int timeoutMs) =>
        _running.TryGetValue(moduleId, out var module) && module.Continuous is { } run && await run.BoundaryReadableAsync(dataType, portNumber, timeoutMs);

    // The boundary Ports of a running Module, as its Metadata declares them.
    public IReadOnlyList<RuntimePort>? BoundaryPorts(int moduleId) =>
        _running.TryGetValue(moduleId, out var module) ? module.Graph.Root.Ports : null;

    // -- 3.3 Inquire about AIM state ------------------------------------------

    // MPAI_AIFU_AIM_GetStatus(MODULE_ID, name, out status): ALIVE, DEGRADED or
    // DEAD (M3203 3.7).
    public AifError MPAI_AIFU_AIM_GetStatus(int moduleId, string name, out AimStatus status)
    {
        status = AimStatus.Dead;
        if (!_running.TryGetValue(moduleId, out var module)) return AifError.NotFound;
        if (!module.Host.Contains(name)) return AifError.NotFound;
        status = StatusOf(module).First(a => a.Aim == name).Status;
        return AifError.OK;
    }

    // Every AIM of the Module: status, why, and the reports of its last run.
    // The state of the Module: Running, or Suspended between Pause and Resume.
    public AifError MPAI_AIFU_MODULE_GetState(int moduleId, out ModuleState state)
    {
        state = ModuleState.Running;
        if (!_running.TryGetValue(moduleId, out var module)) return AifError.NotFound;
        state = module.Host.IsSuspended ? ModuleState.Suspended : ModuleState.Running;
        return AifError.OK;
    }

    public AifError MPAI_AIFU_MODULE_GetStatus(int moduleId, out IReadOnlyList<AimReport> aims)
    {
        aims = Array.Empty<AimReport>();
        if (!_running.TryGetValue(moduleId, out var module)) return AifError.NotFound;
        aims = StatusOf(module);
        return AifError.OK;
    }

    // THE STATUS OF A MODULE WITH AIMs ON HOSTS (M3217 3.3). In an exchange this
    // Controller fires every AIM and keeps its status. In a Continuous Module an
    // AIM on a host runs there, and there its status and reports are kept: they
    // are asked of the host - but an AIM this Controller holds DEAD is DEAD, and
    // a host that does not answer leaves what this Controller knows.
    private static IReadOnlyList<AimReport> StatusOf(RunningModule module)
    {
        var here = module.Host.Status();
        if (module.Continuous is null || module.Placed.Count == 0) return here;

        var there = new Dictionary<string, AimReport>(StringComparer.Ordinal);
        foreach (var client in module.Hosts.Where(c => !c.Link.IsClosed))
            try
            {
                var reply = client.AskAsync("Status", module.HostModule).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                foreach (var a in reply["Aims"]?.AsArray() ?? new JsonArray())
                    there[a!["Aim"]!.GetValue<string>()] = new AimReport(
                        a["Aim"]!.GetValue<string>(),
                        Enum.Parse<AimStatus>(a["Status"]!.GetValue<string>()),
                        a["Reason"]?.GetValue<string>() ?? "",
                        a["Reports"]!.AsArray().Select(r => r!.GetValue<string>()).ToList());
            }
            catch { /* what this Controller knows */ }

        return here.Select(a => a.Status != AimStatus.Dead && module.Placed.ContainsKey(a.Aim) && there.TryGetValue(a.Aim, out var t) ? t : a).ToList();
    }

    // MPAI_AIFU_AIM_Stop (M3213 3.5): the User Agent stops one AIM of the Module,
    // which is DEAD for the rest of the Module's life; the others go on.
    public AifError MPAI_AIFU_AIM_Stop(int moduleId, string name)
    {
        if (!_running.TryGetValue(moduleId, out var module)) return AifError.NotFound;
        return module.Host.StopAim(name, "stopped by the User Agent") ? AifError.OK : AifError.NotFound;
    }

    // A Continuous Module's boundary (M3215 3.3): a write under its Port's
    // behaviour, a read of the oldest Message pending, each within its timeout.
    public bool IsContinuous(int moduleId) =>
        _running.TryGetValue(moduleId, out var module) && module.Continuous is not null;

    public async Task<AifError> ContinuousWriteAsync(int moduleId, string dataType, int portNumber, string json, int timeoutMs,
                                                     string? from = null, DateTimeOffset? fromStamp = null)
    {
        // A Module its policy stopped (OnDegraded StopModule) takes no more, as in
        // an exchange.
        if (!_running.TryGetValue(moduleId, out var module) || module.Continuous is null || module.Host.IsStopped) return AifError.NotStarted;
        return await module.Continuous.WriteBoundaryAsync(dataType, portNumber, json, timeoutMs, from, fromStamp);
    }

    public async Task<(AifError, string?, DateTimeOffset)> ContinuousReadStampedAsync(int moduleId, string dataType, int portNumber, int timeoutMs)
    {
        if (!_running.TryGetValue(moduleId, out var module) || module.Continuous is null) return (AifError.NotStarted, null, default);
        return await module.Continuous.ReadBoundaryStampedAsync(dataType, portNumber, timeoutMs);
    }

    // For the payload channel of an External Port (Communication 7): the Message with the
    // references of its payloads kept, the store that holds them, and the adoption of the
    // payloads of a Message that stay on the Controller that wrote it.
    public async Task<(AifError, string?, DateTimeOffset)> ContinuousReadApartAsync(int moduleId, string dataType, int portNumber, int timeoutMs)
    {
        if (!_running.TryGetValue(moduleId, out var module) || module.Continuous is null) return (AifError.NotStarted, null, default);
        return await module.Continuous.ReadBoundaryApartAsync(dataType, portNumber, timeoutMs);
    }

    public AIF.Channels.IPayloadSource? PayloadSourceOf(int moduleId) =>
        _running.TryGetValue(moduleId, out var module) ? module.Continuous?.Payloads : null;

    public string AdoptPayloads(int moduleId, string dataType, int portNumber, string json, IEnumerable<(string Reference, Func<byte[]?> Fetch)> payloads) =>
        _running.TryGetValue(moduleId, out var module) && module.Continuous is { } run ? run.AdoptBoundaryPayloads(dataType, portNumber, json, payloads) : json;

    public async Task<(AifError, string?)> ContinuousReadAsync(int moduleId, string dataType, int portNumber, int timeoutMs)
    {
        if (!_running.TryGetValue(moduleId, out var module) || module.Continuous is null) return (AifError.NotStarted, null);
        return await module.Continuous.ReadBoundaryAsync(dataType, portNumber, timeoutMs);
    }

    // MPAI_AIFU_Payload_Put (M3215 3.6).
    public (AifError, string?) PayloadPut(int moduleId, string dataType, int portNumber, ReadOnlyMemory<byte> data)
    {
        if (!_running.TryGetValue(moduleId, out var module)) return (AifError.NotStarted, null);
        if (module.Continuous is null) return (AifError.Failed, null);   // an exchange carries its payloads inline
        return module.Continuous.PutBoundaryPayload(dataType, portNumber, data);
    }

    // The data of a reference this Controller issued, in whichever of its Modules.
    public byte[]? ResolvePayload(string reference)
    {
        foreach (var module in _running.Values)
            if (module.Continuous is { } run && run.Payloads.TryGet(reference, out var data)) return data.ToArray();
        return null;
    }

    // How many payloads a Continuous Module holds.
    public int PayloadsHeld(int moduleId) =>
        _running.TryGetValue(moduleId, out var module) && module.Continuous is not null ? module.Continuous.Payloads.Held : 0;

    // How many times an AIM of a Continuous Module missed its Deadline.
    public int DeadlinesMissed(int moduleId, string aim) =>
        _running.TryGetValue(moduleId, out var module) && module.Continuous is not null
            ? module.Continuous.DeadlinesMissed(aim) : 0;

    // What each Channel of a Continuous Module carried.
    public IReadOnlyList<string> ChannelAccounts(int moduleId) =>
        _running.TryGetValue(moduleId, out var module) && module.Continuous is not null
            ? module.Continuous.Accounts() : Array.Empty<string>();

    // True when the Module's own policy stopped it (OnDegraded StopModule).
    public bool ModuleStopped(int moduleId) =>
        _running.TryGetValue(moduleId, out var module) && module.Host.IsStopped;

    // -- The run: the User Agent writes boundary Ports, the Module runs --------
    // The UA supplies data on the composite's boundary input Ports, keyed by Data
    // Type and Port Number. It never names an AIM nor orders execution - the
    // executor runs the AIMs per the Topology.

    public sealed class RunOutcome
    {
        public required Message Completed { get; init; }
    }

    public async Task<(AifError, RunOutcome?)> RunAsync(
        int moduleId, IReadOnlyDictionary<string, string> boundaryPorts)
    {
        if (!_running.TryGetValue(moduleId, out var module)) return (AifError.NotFound, null);

        if (module.Continuous is not null) return (AifError.Failed, null);   // it runs by itself
        return (AifError.OK, new RunOutcome { Completed = await module.Channels.Exchange(boundaryPorts, Guid.NewGuid().ToString()).Completed });
    }

    // TryGetRuntime USED to live here, handing an Module's AimHost and its Ports
    // to whoever asked. Its own comment said "not part of the public MPAI_AIFU_*
    // surface", which was the warning: it let a User Agent register an AIM into a
    // running Module and invoke it outside the Topology that governs it. Nothing in
    // the AMD would mention that AIM, and nothing could refuse it.
    //
    // Its one caller, AmqWorkflow, needed MMC-OCR - which is not a SubAIM of
    // MMC-AMQ. That is now an Module of the User Agent's own, UAG-OCR-V1.0, started
    // and run through this same public API. Removing the method is what makes the
    // guarantee real: an escape hatch that exists is an escape hatch that will be
    // used.
}

// AifError (the error_t of the Basic and Security APIs) is in AIF.Trust\AifError.cs, so that the Security API, below the Controller, returns the same values.

