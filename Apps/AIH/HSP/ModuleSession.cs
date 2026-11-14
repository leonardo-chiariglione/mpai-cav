using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Mpai.Aif.Api;
using Mpai.Core;
using Mpai.Rca;
using Mpai.Ua;
using Mpai.Wdl;

namespace Mpai.Aih.Hsp;

// A DEVICE OF A USER AGENT BOUND TO ONE PORT OF THE MODULE. The workflow binds it for the life of the workflow: what the User Agent sends to it is streamed to the
// Port it is streamed to; what the Port it is delivered to gives is delivered to it, and waits here until the User Agent takes it.
public sealed class ChannelDevice(string dataType) : IDevice
{
    private readonly Channel<(string Json, TaskCompletionSource Taken)> toModule = Channel.CreateUnbounded<(string, TaskCompletionSource)>();
    private readonly BlockingCollection<string> fromModule = [];

    // Sends to the Port the device is bound to, and returns when the workflow has written it to the Module: what is sent to one Port is then in the Module before what is sent
    // to another afterwards - the User Agent orders its Ports (AIH Data for Auditing, then the audit request).
    public void Send(string json)
    {
        var taken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!toModule.Writer.TryWrite((json, taken))) return;
        taken.Task.Wait(TimeSpan.FromSeconds(10));
    }

    public string? Receive(int milliseconds) => fromModule.TryTake(out var json, milliseconds) ? json : null;

    public void Drain() { while (fromModule.TryTake(out _)) { } }

    public Task DeliverAsync(string type, string json, CancellationToken cancel) { fromModule.Add(json, cancel); return Task.CompletedTask; }

    public async IAsyncEnumerable<(string DataType, string Json)> ReadAsync([EnumeratorCancellation] CancellationToken cancel)
    {
        await foreach (var (json, taken) in toModule.Reader.ReadAllAsync(cancel))
        {
            yield return (dataType, json);
            taken.TrySetResult();                                                   // the consumer has the item written and asks for the next
        }
    }

    public Task SafeStopAsync() => Task.CompletedTask;
}

// A MODULE UNDER A CONTROLLER, as its User Agent runs it: the Workflow Description is read and interpreted, which starts the Module, asks the Controller to keep the
// storage where the owner says, and binds a device to every Port named; the User Agent then sends to the devices that stream to the Module and takes from those
// that the Module delivers to. It uses only the Controller API.
public sealed class ModuleSession : IDisposable
{
    private readonly IControllerApi api;
    private readonly Dictionary<string, ChannelDevice> devices = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stopping = new();
    private readonly List<string> bound = [];
    private readonly List<string> log = [];
    private readonly string workflowFile, storage;
    private readonly (string Service, string Anchor, string Rendezvous, string KnownAs)? trusted;
    private readonly string[] names;
    private Task? running;

    // The Module the workflow runs (its 'over' clause): an Implementation of the AIM - the Basic one (I01) or the Secure one (I02) - is the workflow's choice.
    public string Module { get; }

    // The MODULE_ID the Controller returned when the workflow started the Module (Basic API 2.2): the User Agent addresses it by this, -1 before it is started.
    public int ModuleId => interpreter?.ModuleId ?? -1;
    private WorkflowInterpreter? interpreter;

    // devices: the name of each device the workflow binds, and the Data Type of what it streams to the Module (its Data Type matters only for those).
    // trusted: a Trust Authority the User decided to trust - its service, its anchor, where it is reached - and the name this User Agent's Controller is known by there; a workflow joins it by name.
    public ModuleSession(IControllerApi api, string workflowFile, string storageLocation, (string Service, string Anchor, string Rendezvous, string KnownAs)? trusted, params (string Device, string DataType)[] devices)
    {
        this.api = api; this.workflowFile = workflowFile; storage = storageLocation; this.trusted = trusted;
        Module = new WorkflowReader().Read(File.ReadAllText(workflowFile)).Modules.Single();
        names = devices.Select(d => d.Device).ToArray();
        foreach (var (name, type) in devices) this.devices[name] = new ChannelDevice(type);
    }

    public void Start(int seconds = 90)
    {
        var registry = new DeviceRegistry();
        if (trusted is { } t) registry.RegisterService(t.Service, t.Anchor, t.Rendezvous, t.KnownAs);
        foreach (var (name, device) in devices) registry.RegisterDevice(name, device);
        // What the owner chose: where the storage is.
        registry.RegisterAcquire("OSD-BTO-V1.5", (_, _) => Task.FromResult<string?>(MpaiJson.ToJson(BasicTextObject.FromText(storage))));

        var workflow = new WorkflowReader().Read(File.ReadAllText(workflowFile));
        var failure = new TaskCompletionSource<Exception?>();
        var interpreter = this.interpreter = new WorkflowInterpreter(api.Async(), registry, line => { lock (bound) { log.Add(line); if (line.StartsWith("device \"")) bound.Add(line); } });
        running = Task.Run(async () =>
        {
            try { await interpreter.RunAsync(workflow, stopping.Token); }
            catch (OperationCanceledException) { }
            catch (Exception e) { failure.TrySetResult(e); }
        });

        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (failure.Task.IsCompleted) throw new InvalidOperationException($"The workflow {Path.GetFileName(workflowFile)} stopped: " + failure.Task.Result?.Message, failure.Task.Result);
            int count; lock (bound) count = bound.Count;
            if (count >= names.Length) { Thread.Sleep(200); return; }
            Thread.Sleep(100);
        }
        string said; lock (bound) said = string.Join(" | ", log.TakeLast(12));
        throw new InvalidOperationException($"The workflow {Path.GetFileName(workflowFile)} did not start the Module: {bound.Count} of {names.Length} devices bound; it said: {said}");
    }

    public void Send(string device, string json) => devices[device].Send(json);

    public string? Receive(string device, int milliseconds = 15_000) => devices[device].Receive(milliseconds);

    // The devices a User Agent serves all the time (what the Module asks of the Blockchain) are not drained with the answers it waits for.
    private readonly HashSet<string> served = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource serving = new();
    private readonly List<Task> servers = [];

    public void Drain() { foreach (var (name, device) in devices) if (!served.Contains(name)) device.Drain(); }

    // SERVES A DEVICE for the life of the session: what the Module gives it is handled as it comes - not within a request of the User Agent's, which does not know when the Module will ask.
    public void Serve(string device, Action<string> handle)
    {
        served.Add(device);
        servers.Add(Task.Run(() =>
        {
            while (!serving.IsCancellationRequested)
            {
                var asked = devices[device].Receive(200);
                if (asked is null) continue;
                try { handle(asked); } catch (Exception e) when (!serving.IsCancellationRequested) { Errors.Add($"{device}: {e.Message}"); }
            }
        }));
    }

    // What went wrong in what a served device was asked.
    public ConcurrentBag<string> Errors { get; } = [];

    public void Dispose()
    {
        try { serving.Cancel(); Task.WaitAll(servers.ToArray(), TimeSpan.FromSeconds(15)); } catch { }
        try { stopping.Cancel(); running?.Wait(TimeSpan.FromSeconds(20)); } catch { }
    }
}
