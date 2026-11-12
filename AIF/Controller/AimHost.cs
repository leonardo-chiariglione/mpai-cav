namespace AIF.Controller;

// Holds the instantiated AIMs and is the SOLE gateway through which the
// application interacts with them — consistent with the zero-trust principle
// that no entity communicates with another except via the Controller.
//
// Implements the four lifecycle operations from the MPAI-AIF Basic API
// (section 4.4): Start, Pause, Resume, Stop — applied per AIM.
// The AIM itself never holds a lifecycle object; it receives an AimContext
// snapshot for each ProcessAsync call, containing only the signals it needs.
public sealed class AimHost : IDisposable
{
    private readonly Dictionary<string, IAimProcessor> _processors = new();
    private readonly Dictionary<string, AimLifecycle>  _lifecycles = new();

    // THE MODULE'S OWN PAUSE AND STOP (M3213 3.4). This host holds every AIM of
    // one Module, at every depth, so what is done here reaches them all. The gate
    // is held until Resume, across runs: an AIM about to run waits at it, and an
    // AIM already running is paused through its context, if it honours one.
    private readonly object _module = new();
    private TaskCompletionSource _running = Open();
    private readonly CancellationTokenSource _stopped = new();

    private static TaskCompletionSource Open()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.TrySetResult();
        return gate;
    }

    // Each AIM's status, why, and what it reported in the last run (M3213 3.5).
    private readonly Dictionary<string, AimStatus>    _status  = new();
    private readonly Dictionary<string, string>       _reason  = new();
    private readonly Dictionary<string, List<string>> _reports = new();

    public void RegisterRuntime(IAimProcessor processor) => RegisterRuntime(processor, processor.InstanceId);

    // Under the instance the Controller names (DescriptorNode.Instance): two instances of one AIM in one Module
    // are two runtimes, whatever InstanceId their implementation gives itself.
    public void RegisterRuntime(IAimProcessor processor, string instance)
    {
        _processors[instance] = processor;
        _lifecycles[instance] = new AimLifecycle();
        _status[instance]     = AimStatus.Alive;
        _reason[instance]     = string.Empty;
        _reports[instance]    = new List<string>();
    }

    public bool Contains(string instanceId) =>
        _processors.ContainsKey(instanceId);

    // The implementation that runs an AIM here.
    public IAimProcessor? ProcessorOf(string instanceId) =>
        _processors.GetValueOrDefault(instanceId);

    public AimState GetState(string instanceId) =>
        _lifecycles.TryGetValue(instanceId, out var lc)
            ? lc.State
            : AimState.Idle;

    // ── Lifecycle API (MPAI-AIF Basic API section 4.4) ───────────────────────

    // MPAI_AIFM_AIM_Start: prepare the AIM for a new run and return the
    // AimContext the caller should embed in the Message for this invocation.
    public AimContext MPAI_AIFM_AIM_Start(string instanceId)
    {
        if (!_lifecycles.TryGetValue(instanceId, out var lc))
            throw new InvalidOperationException(
                $"No AIM registered for {instanceId}.");
        return lc.Start();
    }

    // MPAI_AIFM_AIM_Stop: signal the AIM to stop at its next yield point.
    public void MPAI_AIFM_AIM_Stop(string instanceId)
    {
        if (_lifecycles.TryGetValue(instanceId, out var lc))
            lc.Stop();
    }

    // MPAI_AIFM_AIM_Pause: close the AIM's pause gate so it blocks.
    public void MPAI_AIFM_AIM_Pause(string instanceId)
    {
        if (_lifecycles.TryGetValue(instanceId, out var lc))
            lc.Pause();
    }

    // MPAI_AIFM_AIM_Resume: open the AIM's pause gate so it continues.
    public void MPAI_AIFM_AIM_Resume(string instanceId)
    {
        if (_lifecycles.TryGetValue(instanceId, out var lc))
            lc.Resume();
    }

    // Whether the Module is Suspended: paused, and not yet resumed (Basic API state).
    private bool _suspended;
    public bool IsSuspended { get { lock (_module) return _suspended; } }

    // MPAI_AIFU_MODULE_Pause: every AIM completes what it is doing and waits.
    public void PauseModule()
    {
        lock (_module)
        {
            _suspended = true;
            if (_running.Task.IsCompleted)
                _running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            foreach (var lc in _lifecycles.Values) lc.Pause();
        }
    }

    // MPAI_AIFU_MODULE_Resume.
    public void ResumeModule()
    {
        lock (_module)
        {
            _suspended = false;
            _running.TrySetResult();
            foreach (var lc in _lifecycles.Values) lc.Resume();
        }
    }

    // MPAI_AIFU_MODULE_Stop: every AIM that is running is signalled, and none
    // runs again.
    public void StopModule()
    {
        lock (_module)
        {
            _stopped.Cancel();
            _running.TrySetResult();
            foreach (var lc in _lifecycles.Values) lc.Stop();
            foreach (var aim in _status.Keys.ToList())
                if (_status[aim] != AimStatus.Dead) { _status[aim] = AimStatus.Dead; _reason[aim] = "its Module was stopped"; }
        }
    }

    public bool IsStopped => _stopped.IsCancellationRequested;

    // ── Status, degradation and reporting (M3213 3.5) ────────────────────────

    // A run begins: the reports of the last one are discarded.
    public void BeginRun()
    {
        lock (_module)
            foreach (var reports in _reports.Values) reports.Clear();
    }

    // MPAI_AIFM_AIM_Report, conveyed: logged, kept for Status, and the AIM is
    // DEGRADED for the run. Nothing else is done with it.
    public void Report(string aim, string text)
    {
        Console.WriteLine($"[AIF] {aim} reports: {text}");
        lock (_module)
        {
            if (!_reports.TryGetValue(aim, out var reports)) return;
            reports.Add(text);
            if (_status[aim] != AimStatus.Dead) { _status[aim] = AimStatus.Degraded; _reason[aim] = "it reported"; }
        }
    }

    // The AIM failed in this run: it threw, or returned an error.
    public void Degrade(string aim, string reason)
    {
        lock (_module)
            if (_status.TryGetValue(aim, out var status) && status != AimStatus.Dead)
            { _status[aim] = AimStatus.Degraded; _reason[aim] = reason; }
    }

    // The AIM completed a run without failing or reporting: ALIVE again.
    public void Succeeded(string aim)
    {
        lock (_module)
            if (_status.TryGetValue(aim, out var status) && status == AimStatus.Degraded && _reports[aim].Count == 0)
            { _status[aim] = AimStatus.Alive; _reason[aim] = string.Empty; }
    }

    // StopAIM, MPAI_AIFM_AIM_Stop and MPAI_AIFU_AIM_Stop: the AIM is signalled,
    // is DEAD, and does not run again in this Module. False if there is no such AIM.
    public bool StopAim(string aim, string reason = "stopped")
    {
        lock (_module)
        {
            if (!_lifecycles.TryGetValue(aim, out var lc)) return false;
            if (_status[aim] != AimStatus.Dead) { _status[aim] = AimStatus.Dead; _reason[aim] = reason; }
            lc.Stop();
        }
        AimStopped?.Invoke(aim, reason);
        return true;
    }

    // AN AIM ON ANOTHER MACHINE (M3217 3.3). Told of every AIM stopped here: the
    // Controller passes it on to the host the AIM runs on. And where an AIM here
    // stops another, who does it: on a host, the Controller, which holds the Module.
    public Action<string, string>? AimStopped { get; set; }
    public Func<string, string, bool>? StopAimBy { get; set; }

    // The Controller's Access, which the AIMs held here read (AIF V3.0, Storage 6):
    // its store, or - on an AIM host - the Controller's, read over the link.
    public IAccessReader? Access { get; set; }

    // What obtains the items of a set of Access when an AIM first reads them (the Controller's, never on an AIM host).
    public AccessFetcher? Fetcher { get; set; }

    // The Module this host holds the AIMs of and the AIM that is its central control (Metadata StorageControl): who may read
    // a datum of Access is decided as it is in Shared Storage, from the holder of the handle.
    public string? ModuleName { get; set; }
    public string? CentralControl { get; set; }

    // WHAT EACH AIM MAY READ OF IT: the Access the AIM declares in its Metadata (AccessSources). An AIM that declares
    // nothing reads none; the entries are those of its Instance, so what it may read is what its L3 says.
    private readonly Dictionary<string, IReadOnlyList<AccessEntry>> _accessEntries = new(StringComparer.Ordinal);

    public void BindAccess(string aim, IReadOnlyList<AccessEntry> entries)
    {
        lock (_module) _accessEntries[aim] = entries;
    }

    // The Access of one AIM, narrowed to what its Metadata declares; given to the AIM when it is built.
    public IAccessReader? AccessFor(string aim) => AccessOf(aim);

    private IAccessReader? AccessOf(string aim)
    {
        if (Access is null) return null;
        IReadOnlyList<AccessEntry> entries;
        lock (_module) entries = _accessEntries.GetValueOrDefault(aim) ?? [];
        IAccessReader scoped = Access is RemoteAccess remote ? remote.ForAim(aim) : Access is AccessStore store
            ? store.As(new AIF.SharedStorage.StorageHolder(ModuleName, aim),
                       CentralControl is { } c ? new AIF.SharedStorage.StorageHolder(ModuleName, c) : null)
            : Access;
        var bound = new BoundAccess(scoped, entries, Fetcher, aim);
        lock (_module) { if (!_handedOut.TryGetValue(aim, out var list)) _handedOut[aim] = list = []; list.Add(bound); }
        return bound;
    }

    private readonly Dictionary<string, List<BoundAccess>> _handedOut = new(StringComparer.Ordinal);

    // MPAI_AIFM_MODEL_UPDATE (Basic API 4.12.1), the Controller's part: the URI names a model of the AIM - one of its AccessSources, as the Metadata the Controller holds now states it. The Controller obtains
    // what the Metadata pins for it, from where it states (a Source takes a new Version of the same URI when its SHA-256 is new), and what is obtained is checked against that SHA-256 before it is kept:
    // the model the AIM was reading stays where it is until the new one is in Access. NOT_AUTHORISED where the Metadata of the AIM names no model at that URI; NOT_TRUSTED where what is found is not
    // the SHA-256 it states, or is a model the Source held before; FAILED where it cannot be obtained.
    public AifError ModelUpdate(string aim, string uri, IReadOnlyList<AccessEntry> now, out string why)
    {
        why = "";
        if (Fetcher is null || Access is null) { why = "the Controller has no Access"; return AifError.Failed; }
        var entry = now.FirstOrDefault(e => !e.IsPlaceholder && !e.IsSet && e.Locations.Contains(uri, StringComparer.Ordinal));
        if (entry is null) { why = $"the Metadata of {aim} names no model at {uri}"; return AifError.NotAuthorised; }
        switch (Fetcher.Ensure(entry, aim, out why))
        {
            case AccessOutcome.OK: break;
            case AccessOutcome.Refused: return AifError.NotTrusted;
            default: return AifError.Failed;
        }
        BindAccess(aim, now);
        lock (_module) foreach (var bound in _handedOut.GetValueOrDefault(aim) ?? []) bound.Rebind(now);
        return AifError.OK;
    }

    public bool IsDead(string aim)
    {
        lock (_module)
            return _status.TryGetValue(aim, out var status) && status == AimStatus.Dead;
    }

    public AimStatus StatusOf(string aim)
    {
        lock (_module)
            return _status.TryGetValue(aim, out var status) ? status : AimStatus.Dead;
    }

    public IReadOnlyList<AimReport> Status()
    {
        lock (_module)
            return _status.Keys.OrderBy(a => a, StringComparer.Ordinal)
                .Select(a => new AimReport(a, _status[a], _reason[a], _reports[a].ToList()))
                .ToList();
    }

    // ── Execution ─────────────────────────────────────────────────────────────

    // Called by the executor for normal (non-interactive) AIMs.
    // Waits while the Module is paused, refuses once it is stopped, then starts
    // the AIM, which runs to completion.
    public async Task<Message> ProcessAsync(string instanceId, Message message)
    {
        if (!_processors.TryGetValue(instanceId, out var processor))
            throw new InvalidOperationException(
                $"No implementation is registered for {instanceId}.");

        // Embed an AimContext in the message so the processor can honour
        // lifecycle signals without holding a reference to AimLifecycle.
        var context = await ContextAsync(instanceId);
        if (!TurnTrace.Enabled) return await processor.ProcessAsync(message with { Context = context });
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try { return await processor.ProcessAsync(message with { Context = context }); }
        finally { TurnTrace.Line($"aim {instanceId} {clock.ElapsedMilliseconds} ms"); }
    }

    // The implementation registered for an AIM.
    public IAimProcessor? Processor(string instanceId) =>
        _processors.TryGetValue(instanceId, out var processor) ? processor : null;

    // An AIM about to run: waits while the Module is paused, refuses once the
    // Module or the AIM is stopped, then starts the AIM's lifecycle and gives the
    // context it runs with - Pause, Stop, Report, StopAim.
    public async Task<AimContext> ContextAsync(string instanceId)
    {
        await WhilePausedAsync();
        _stopped.Token.ThrowIfCancellationRequested();
        if (IsDead(instanceId))
            throw new OperationCanceledException($"{instanceId} is stopped.");

        var context = MPAI_AIFM_AIM_Start(instanceId);
        lock (_module)
            if (!_running.Task.IsCompleted) _lifecycles[instanceId].Pause();   // paused as it started
        return context.WithHost(text => Report(instanceId, text),
            aim => StopAimBy?.Invoke(aim, instanceId) ?? StopAim(aim, $"stopped by {instanceId}"), AccessOf(instanceId));
    }

    // Completes when the Module is not paused.
    public Task WhilePausedAsync()
    {
        lock (_module) return _running.Task;
    }

    // Cancelled when the Module is stopped.
    public CancellationToken Stopping => _stopped.Token;

    // Called by AifAmqSession for interactive AIMs.
    // The caller starts the AIM, lets it run, then calls StopAim when ready.
    public Task<Message> ProcessWithContextAsync(
        string     instanceId,
        Message    message,
        AimContext context)
    {
        if (!_processors.TryGetValue(instanceId, out var processor))
            throw new InvalidOperationException(
                $"No implementation is registered for {instanceId}.");

        var msg = message with { Context = context };
        return processor.ProcessAsync(msg);
    }

    public void Dispose()
    {
        StopModule();
        foreach (var lc in _lifecycles.Values)
            lc.Dispose();
    }
}
