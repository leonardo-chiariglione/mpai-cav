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
        ControllerTransport = new ControllerTransport(Clock);
        InProcessTransport  = new InProcessTransport(Clock);
    }

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
    public string DefaultTransport { get; set; } = "Controller";

    private IReadOnlyDictionary<string, IChannelTransport> Transports => new Dictionary<string, IChannelTransport>
    {
        [ControllerTransport.Name] = ControllerTransport,
        [InProcessTransport.Name]  = InProcessTransport
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

    // A running Module (composite AIM): its graph, host, and boundary Ports.
    private sealed class RunningModule
    {
        public required string          Name        { get; init; }
        public required DescriptorGraph Graph       { get; init; }
        public required AimHost         Host        { get; init; }
        public required MachineExecutor Executor    { get; init; }

        // The continuous executor, for a Module whose Metadata declares
        // Execution: Continuous (M3215 3.3); null for an exchange.
        public ContinuousExecutor? Continuous { get; init; }

        // Where the User Agent initialised this Module's Shared Storage; null,
        // the User Agent's root (MPAI_AIFU_SharedStorage_Init, M3203 3.4.1).
        public string? StorageLocation { get; set; }
    }

    // -- 3.1 General: initialise / destroy the Controller ---------------------

    // MPAI_AIFU_Controller_Initialize
    public AifError MPAI_AIFU_Controller_Initialize()
    {
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
            AIF.SharedStorage.ISharedStorage? storage)
        {
            if (kept.TryGetValue(aimName, out var already)) return already;
            var made = inner.Create(aimName, settings, storage);
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
        _controller.Instantiate(graph, new Retaining(provider, _retained), settings, host,
            () => started?.StorageLocation ?? _sharedStorageRoot);

        moduleId = Interlocked.Increment(ref _nextModuleId);

        // Every Module's Channels are planned, so that one whose reader does not
        // accept its writer's transport refuses to load (M3215 3.1); a Continuous
        // Module runs on them from now until Stop.
        var channels = new ContinuousExecutor(graph, host, Transports, $"{name}#{moduleId}", DefaultTransport, Clock);

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

        if (graph.Root.IsContinuous) channels.Start();

        _running[moduleId] = started = new RunningModule
        {
            Name       = name,
            Graph      = graph,
            Host       = host,
            Executor   = new MachineExecutor(host),
            Continuous = graph.Root.IsContinuous ? channels : null
        };
        return AifError.OK;
    }

    // MPAI_AIFU_MODULE_Pause. Every AIM of the Module, at every depth, completes
    // what it is doing and waits, until Resume - across runs.
    public AifError MPAI_AIFU_MODULE_Pause(int moduleId)
    {
        if (!_running.TryGetValue(moduleId, out var module)) return AifError.NotFound;
        module.Host.PauseModule();
        return AifError.OK;
    }

    // MPAI_AIFU_MODULE_Resume
    public AifError MPAI_AIFU_MODULE_Resume(int moduleId)
    {
        if (!_running.TryGetValue(moduleId, out var module)) return AifError.NotFound;
        module.Host.ResumeModule();
        return AifError.OK;
    }

    // MPAI_AIFU_MODULE_Stop
    public AifError MPAI_AIFU_MODULE_Stop(int moduleId)
    {
        if (!_running.TryGetValue(moduleId, out var module)) return AifError.NotFound;
        module.Continuous?.StopAsync().GetAwaiter().GetResult();
        module.Host.Dispose();
        _running.TryRemove(moduleId, out _);
        return AifError.OK;
    }

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
        status = module.Host.StatusOf(name);
        return AifError.OK;
    }

    // Every AIM of the Module: status, why, and the reports of its last run.
    public AifError MPAI_AIFU_MODULE_GetStatus(int moduleId, out IReadOnlyList<AimReport> aims)
    {
        aims = Array.Empty<AimReport>();
        if (!_running.TryGetValue(moduleId, out var module)) return AifError.NotFound;
        aims = module.Host.Status();
        return AifError.OK;
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
        if (!_running.TryGetValue(moduleId, out var module) || module.Continuous is null) return AifError.NotStarted;
        return await module.Continuous.WriteBoundaryAsync(dataType, portNumber, json, timeoutMs);
    }

    public async Task<(AifError, string?)> ContinuousReadAsync(int moduleId, string dataType, int portNumber, int timeoutMs)
    {
        if (!_running.TryGetValue(moduleId, out var module) || module.Continuous is null) return (AifError.NotStarted, null);
        return await module.Continuous.ReadBoundaryAsync(dataType, portNumber, timeoutMs);
    }

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

        var result = await module.Executor.RunAsync(
            module.Graph,
            new Message
            {
                MessageId   = Guid.NewGuid().ToString(),
                // MessageType carries no meaning the framework itself relies on (the
                // only values Message.IsError/IsCancelled compare against are the
                // reserved ErrorType/CancelledType constants). It is the RUNNING
                // Module's own name - never a fixed application name.
                MessageType = module.Name,
                Ports       = new Dictionary<string, string>(boundaryPorts)
            });

        return (AifError.OK, new RunOutcome { Completed = result.Completed });
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
    NotStarted
}
