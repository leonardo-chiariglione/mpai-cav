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
    private readonly string workflowFile, storage;
    private readonly string[] names;
    private Task? running;

    // devices: the name of each device the workflow binds, and the Data Type of what it streams to the Module (its Data Type matters only for those).
    public ModuleSession(IControllerApi api, string workflowFile, string storageLocation, params (string Device, string DataType)[] devices)
    {
        this.api = api; this.workflowFile = workflowFile; storage = storageLocation;
        names = devices.Select(d => d.Device).ToArray();
        foreach (var (name, type) in devices) this.devices[name] = new ChannelDevice(type);
    }

    public void Start(int seconds = 90)
    {
        var registry = new DeviceRegistry();
        foreach (var (name, device) in devices) registry.RegisterDevice(name, device);
        // What the owner chose: where the storage is.
        registry.RegisterAcquire("OSD-BTO-V1.5", (_, _) => Task.FromResult<string?>(MpaiJson.ToJson(BasicTextObject.FromText(storage))));

        var workflow = new WorkflowReader().Read(File.ReadAllText(workflowFile));
        var failure = new TaskCompletionSource<Exception?>();
        var interpreter = new WorkflowInterpreter(api.Async(), registry, line => { lock (bound) if (line.StartsWith("device \"")) bound.Add(line); });
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
        throw new InvalidOperationException($"The workflow {Path.GetFileName(workflowFile)} did not start the Module.");
    }

    public void Send(string device, string json) => devices[device].Send(json);

    public string? Receive(string device, int milliseconds = 15_000) => devices[device].Receive(milliseconds);

    public void Drain() { foreach (var d in devices.Values) d.Drain(); }

    public void Dispose()
    {
        try { stopping.Cancel(); running?.Wait(TimeSpan.FromSeconds(20)); } catch { }
    }
}
