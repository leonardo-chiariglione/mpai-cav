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

    public void RegisterRuntime(IAimProcessor processor)
    {
        _processors[processor.InstanceId] = processor;
        _lifecycles[processor.InstanceId] = new AimLifecycle();
    }

    public bool Contains(string instanceId) =>
        _processors.ContainsKey(instanceId);

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

    // MPAI_AIFU_MODULE_Pause: every AIM completes what it is doing and waits.
    public void PauseModule()
    {
        lock (_module)
        {
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
        }
    }

    public bool IsStopped => _stopped.IsCancellationRequested;

    // ── Execution ─────────────────────────────────────────────────────────────

    // Called by MachineExecutor for normal (non-interactive) AIMs.
    // Waits while the Module is paused, refuses once it is stopped, then starts
    // the AIM, which runs to completion.
    public async Task<Message> ProcessAsync(string instanceId, Message message)
    {
        if (!_processors.TryGetValue(instanceId, out var processor))
            throw new InvalidOperationException(
                $"No implementation is registered for {instanceId}.");

        Task gate;
        lock (_module) gate = _running.Task;
        await gate;
        _stopped.Token.ThrowIfCancellationRequested();

        // Embed an AimContext in the message so the processor can honour
        // lifecycle signals without holding a reference to AimLifecycle.
        var context = MPAI_AIFM_AIM_Start(instanceId);
        lock (_module)
            if (!_running.Task.IsCompleted) _lifecycles[instanceId].Pause();   // paused as it started
        var msg     = message with { Context = context };

        return await processor.ProcessAsync(msg);
    }

    // Called by AifAmqSession for interactive AIMs (e.g. CAE-AOA).
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
