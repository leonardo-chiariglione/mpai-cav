using System.Text.Json.Nodes;
using System;
using System.Collections.Concurrent;
using AIF.Channels;
using AIF.Store;

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
        RemoteTransport     = new RemoteTransport(LinkFor, Clock);
    }

    // The Modules with AIMs on hosts, by the name their hosts know them by.
    private readonly ConcurrentDictionary<string, RunningModule> hostModules = new();

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

    // THE TRANSPORTS OF THIS CONTROLLER (M3205 3.6.2, M3215 3.1): Controller, which
    // relays and can observe, and InProcess. The Channels of a Module use the one
    // its Output Ports declare, DefaultTransport where they declare none.
    public ControllerTransport ControllerTransport { get; }
    public InProcessTransport  InProcessTransport  { get; }
    public RemoteTransport     RemoteTransport     { get; }
    public string DefaultTransport { get; set; } = "Controller";

    // WHERE AIMs PLACED ON OTHER MACHINES RUN (M3217 3.4): the AIM host named for
    // an AIM Instance - or for a composite that contains it - as host:port, and the
    // key the hosts were started with. The L3's Relation says an AIM runs
    // elsewhere; this says where.
    public Dictionary<string, string> AimHosts { get; } = new(StringComparer.Ordinal);
    public string AimHostKey { get; set; } = "";

    private readonly Dictionary<string, AimHostClient> hostClients = new(StringComparer.Ordinal);

    private AimHostClient HostAt(string address)
    {
        lock (hostClients)
        {
            if (hostClients.TryGetValue(address, out var client) && !client.Link.IsClosed) return client;
            client = AimHostClient.ConnectAsync(address, AimHostKey).GetAwaiter().GetResult();

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
                var away = elsewhere || (child.Relation.Length > 0 && child.Relation != "Internal");
                var at = AimHosts.GetValueOrDefault(child.AIMName) ?? host;
                if (child.IsComposite) { Walk(child, away, at); continue; }
                if (!away) continue;
                placed[child.AIMName] = at ?? throw new InvalidOperationException(
                    $"{module}: {child.AIMName} runs on another machine (its Relation, or a composite's containing it, is not Internal), and no AIM host is named for it.");
            }
        }
        Walk(root, false, null);
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
        [RemoteTransport.Name]     = RemoteTransport
    };

    // MPAI_AIFU_SharedStorage_Init(MODULE_ID, location) (M3203 3.4.1): the scope
    // of one Module is held at location. Its AIMs' handles follow at their next
    // call. The User Agent says where, and nothing about who writes.
    public AifError MPAI_AIFU_SharedStorage_Init(int moduleId, string location)
    {
        if (!_running.TryGetValue(moduleId, out var module)) return AifError.NotFound;
        if (string.IsNullOrWhiteSpace(location)) return AifError.Failed;
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
    }

    // -- 3.1 General: initialise / destroy the Controller ---------------------

    // MPAI_AIFU_Controller_Initialize
    // The session of this User Agent with this Controller: data kept until the
    // session ends are this session's (M3219 3.1).
    private string _session = Guid.NewGuid().ToString("N");

    public AifError MPAI_AIFU_Controller_Initialize()
    {
        _session = Guid.NewGuid().ToString("N");
        _controller = new Controller(_store);
        _controller.SetSharedStorageRoot(_sharedStorageRoot);
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
            if (kept.TryGetValue(aimName, out var already)) return already;
            var made = inner.Create(aimName, settings, storage, privateStorage, moduleStorage);
            kept[aimName] = made;
            return made;
        }
    }

    // MPAI_AIFU_MODULE_Start(name, out MODULE_ID)
    public AifError MPAI_AIFU_MODULE_Start(
        string name, IAimProvider provider, AimSettings settings, out int moduleId)
    {
        lock (_startOne)
            return StartOne(name, provider, settings, out moduleId);
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
        var host  = new AimHost();
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
        if (graph.Root.StorageControl is { } control && !Leaves(graph.Root).Contains(control))
            throw new InvalidOperationException(
                $"{name}: its StorageControl names {control}, which is not an AIM of the Module.");
        var moduleStore = _stores[name] = new AIF.SharedStorage.RuledStore(
            () => (started?.StorageLocation ?? _sharedStorageRoot) is { } scope
                ? Path.Combine(scope, "private", Uri.EscapeDataString(name)) : null,
            () => Clock.Now, hostModule, _session, graph.Root.StorageControl);
        var placement = Placement(graph.Root, name);
        var hosts = new List<AimHostClient>();
        IAimProcessor? OnItsHost(DescriptorNode leaf)
        {
            if (!placement.TryGetValue(leaf.AIMName, out var address)) return null;
            var client = HostAt(address);
            client.PlaceAsync(hostModule, leaf.AIMName).GetAwaiter().GetResult();
            if (!hosts.Contains(client)) hosts.Add(client);
            return new RemoteProcessor(leaf.AIMName, client, hostModule, host.Report);
        }

        // A Module refused once some of its AIMs are placed releases them.
        try
        {
            _controller.Instantiate(graph, new Retaining(provider, _retained), settings, host,
                () => started?.StorageLocation ?? _sharedStorageRoot, OnItsHost,
                aim => new CurrentStorage(this, name, aim));

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

            if (graph.Root.IsContinuous) channels.Start();

            _running[moduleId] = started;
            return AifError.OK;
        }
        catch
        {
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
        return AifError.OK;
    }

    // MPAI_AIFU_MODULE_Resume
    public AifError MPAI_AIFU_MODULE_Resume(int moduleId)
    {
        if (!_running.TryGetValue(moduleId, out var module)) return AifError.NotFound;
        module.Host.ResumeModule();
        OnHosts(module, "Resume");
        return AifError.OK;
    }

    // MPAI_AIFU_MODULE_Stop
    public AifError MPAI_AIFU_MODULE_Stop(int moduleId)
    {
        if (!_running.TryGetValue(moduleId, out var module)) return AifError.NotFound;
        module.Continuous?.StopAsync().GetAwaiter().GetResult();
        OnHosts(module, "Stop");
        OnHosts(module, "Release");
        hostModules.TryRemove(module.HostModule, out _);
        module.Host.Dispose();
        module.Storage?.EndOfModule();
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

    private sealed class CurrentStorage(UserAgent ua, string module, string holder) : AIF.SharedStorage.IRuledStorage
    {
        private AIF.SharedStorage.IRuledStorage Now() =>
            (ua._stores.TryGetValue(module, out var store) ? store : throw new InvalidOperationException($"{module} is not running."))
            .For(holder);

        public string Holder => holder;
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
        _running.TryGetValue(moduleId, out var module) ? module.Storage?.For(AIF.SharedStorage.RuledStore.UserAgent) : null;

    public static AifError Outcome(AIF.SharedStorage.StorageOutcome outcome) => outcome switch
    {
        AIF.SharedStorage.StorageOutcome.OK       => AifError.OK,
        AIF.SharedStorage.StorageOutcome.NotFound => AifError.NotFound,
        _                                         => AifError.NotAuthorised
    };

    private static IEnumerable<string> Leaves(DescriptorNode node) =>
        node.Children.SelectMany(c => c.IsComposite ? Leaves(c) : [c.AIMName]);

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

    public async Task<AifError> ContinuousWriteAsync(int moduleId, string dataType, int portNumber, string json, int timeoutMs)
    {
        // A Module its policy stopped (OnDegraded StopModule) takes no more, as in
        // an exchange.
        if (!_running.TryGetValue(moduleId, out var module) || module.Continuous is null || module.Host.IsStopped) return AifError.NotStarted;
        return await module.Continuous.WriteBoundaryAsync(dataType, portNumber, json, timeoutMs);
    }

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

// Standard-style error codes, and the outcomes of M3213 3.2 (the codes of
// M3203's API Conventions: MPAI_AIF_NOT_PRODUCED, MPAI_AIF_TIMEOUT,
// MPAI_AIF_NO_SUCH_PORT, MPAI_AIF_TYPE_NOT_ACCEPTED).
public enum AifError
{
    OK = 0,
    NotInitialized,
    NotFound,
    Failed,

    // The Port is declared, and produced nothing in the run just completed. An
    // outcome, not an error.
    NotProduced,

    // The call waited as long as it was allowed to and could not complete.
    Timeout,

    // The Module declares no boundary Port of that Data Type and Port Number, in
    // that Direction.
    NoSuchPort,

    // The datum's Data Type is not among those the Port accepts.
    TypeNotAccepted,

    // The Module is not started.
    NotStarted,

    // Not allowed by the rules of the storage (M3219 3.2).
    NotAuthorised
}
