using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using AIF.Controller;   // the codes of the Controller API (AifError)
using Mpai.Aif.Api;
using Mpai.Core;
using Mpai.Ua;
using Mpai.Wdl;

namespace Mpai.Cae.AsmApp;

// A DEVICE OF THE ASM USER AGENT BOUND TO ONE PORT OF THE MODULE. The workflow (Apps/ASM/ASM.orch) binds it for the life of the workflow: what the User Agent sends to it is
// streamed to the Port it is streamed to; what the Port it is delivered to gives is delivered to it, and waits here until the User Agent takes it.
public sealed class PortDevice(string dataType) : IDevice
{
    private readonly Channel<string> toModule = Channel.CreateUnbounded<string>();
    private readonly BlockingCollection<string> fromModule = [];

    public void Send(string json) => toModule.Writer.TryWrite(json);

    // What the Module gave, the oldest first, or nothing within the time.
    public string? Receive(int milliseconds) => fromModule.TryTake(out var json, milliseconds) ? json : null;

    public void Drain() { while (fromModule.TryTake(out _)) { } }

    public Task DeliverAsync(string type, string json, CancellationToken cancel) { fromModule.Add(json, cancel); return Task.CompletedTask; }

    public async IAsyncEnumerable<(string DataType, string Json)> ReadAsync([EnumeratorCancellation] CancellationToken cancel)
    {
        await foreach (var json in toModule.Reader.ReadAllAsync(cancel)) yield return (dataType, json);
    }

    public Task SafeStopAsync() => Task.CompletedTask;
}

// THE DEVICE FOR WHAT THE USER AGENT HAS NO USE FOR: what a Port gives is taken, so that it does not wait on the Port, and dropped.
public sealed class DiscardDevice : IDevice
{
    public Task DeliverAsync(string type, string json, CancellationToken cancel) => Task.CompletedTask;
    public async IAsyncEnumerable<(string DataType, string Json)> ReadAsync([EnumeratorCancellation] CancellationToken cancel) { await Task.Delay(Timeout.Infinite, cancel).ConfigureAwait(false); yield break; }
    public Task SafeStopAsync() => Task.CompletedTask;
}

// THE USER AGENT'S LINK TO THE MODULE. The ASM UA is a User Agent: it runs the Workflow Description of Audio Scene Management (Apps/ASM/ASM.orch) with the workflow
// interpreter, which is the only thing that deals with the Controller - starts the Module, streams to the Ports and
// delivers from them, stops it. The UA holds devices, one to a Port, and addresses them by Data Type and Port Number; it holds no AIM and does not read or write the
// storage. It uses only the Controller API.
public sealed class ModuleLink : IDisposable
{
    public const string Module = "1CAE-ASM-V1.0-I01";

    // Data Types of the Module's boundary, by Port Number (CAE-ASM-V1.0, schemas/CAE3/V1.0/AIMs/AudioSceneManager.json).
    public const string Bao = "OSD-BAO-V1.5", Bas = "OSD-BAS-V1.5", Bso = "OSD-BSO-V1.5", Bto = "OSD-BTO-V1.5", Ucm = "CAE-UCM-V1.0", Tfu = "MMC-TFU-V1.5", Auo = "OSD-AUO-V1.5";

    // The device of each Port the User Agent gives to, and of each it takes from, as the workflow names them.
    private static readonly (string Device, string Type, int Number)[] Streams =
    [
        ("ObjectIn", Bao, 1), ("AudioObjectIn", Auo, 1), ("ObjectCommand", Ucm, 1),
        ("SceneIn", Bas, 1), ("SceneCommand", Ucm, 2)
    ];

    private static readonly (string Device, string Type, int Number)[] Delivered =
    [
        ("ObjectOut", Bao, 1), ("AudioObjectOut", Auo, 1),
        ("ObjectReport", Ucm, 1), ("Scene", Bas, 1), ("SceneReport", Ucm, 2)
    ];

    private readonly IControllerApi api;
    private readonly IDisposable? owner;
    private readonly Dictionary<(string, int), PortDevice> into = [], from = [];
    private readonly CancellationTokenSource stopping = new();
    private readonly HashSet<string> bound = [];
    private Task? running;

    public ModuleLink(IControllerApi api, IDisposable? owner = null) { this.api = api; this.owner = owner; }

    // The Controller of this App and the Module's AIMs on an AIM host of their own (Mpai.Cae.Apps.ModuleHost). The Controller keeps the Module's Shared Storage in `storage`, its own business: the User Agent
    // names no place. Everything the User Agent does with the Module goes through the Controller API.
    public static ModuleLink Local(string storage, Action<string>? say = null)
    {
        var host = Mpai.Cae.Apps.ModuleHost.Start(Module, null, say,
            [("Mpai.Cae.Asm.dll", "Mpai.Cae.Asm.AsmProvider"), ("Mpai.Providers.dll", "Mpai.Providers.MatProvider")], storage);
        return new ModuleLink(host.Api, host);
    }
    // The workflow is run; it starts the Module (which the Controller already holds, with its storage where it keeps it).
    public void Start()
    {
        var devices = new DeviceRegistry();
        foreach (var (name, type, number) in Streams) devices.RegisterDevice(name, into[(type, number)] = new PortDevice(type));
        foreach (var (name, type, number) in Delivered) devices.RegisterDevice(name, from[(type, number)] = new PortDevice(type));
        devices.RegisterDevice("Unused", new DiscardDevice());

        var workflow = new WorkflowReader().Read(File.ReadAllText(Path.Combine(Paths.Root, "Apps", "CAE", "ASM", "ASM.orch")));
        var failure = new TaskCompletionSource<Exception?>();
        var interpreter = new WorkflowInterpreter(api.Async(), devices, line =>
        {
            lock (bound) if (line.StartsWith("device \"")) bound.Add(line);
        });
        running = Task.Run(async () =>
        {
            try { await interpreter.RunAsync(workflow, stopping.Token); }
            catch (Exception e) { failure.TrySetResult(e); }
        });

        // Ready when every device is bound (the workflow binds them once it has started the Module).
        var expected = Streams.Length + Delivered.Length > 0 ? Streams.Select(s => s.Device).Concat(Delivered.Select(d => d.Device)).Distinct().Count() + 1 : 0;
        var until = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < until)
        {
            if (failure.Task.IsCompleted) throw new InvalidOperationException("The workflow of Audio Scene Management stopped: " + failure.Task.Result?.Message, failure.Task.Result);
            int count; lock (bound) count = bound.Count;
            if (count >= expected) { Thread.Sleep(200); return; }
            Thread.Sleep(100);
        }
        throw new InvalidOperationException("The workflow of Audio Scene Management did not start the Module.");
    }

    // The ports the editing exchanges leave a datum at that nobody asks for: read away before an exchange, so that what is read after it is of it.
    private static readonly (string, int)[] EditingOutputs = [(Bao, 1), (Ucm, 1), (Ucm, 2), (Bas, 1), (Auo, 1)];

    public void DrainEditing() => Drain(EditingOutputs);
    private void Drain((string, int)[] ports) { foreach (var key in ports) from[key].Drain(); }

    // Gives a datum to the Port: the device streams it.
    public void Write(string dataType, int portNumber, string json)
    {
        if (!into.TryGetValue((dataType, portNumber), out var device)) throw new InvalidOperationException($"The workflow binds no device to {dataType}:{portNumber}.");
        device.Send(json);
    }

    // What the Module gave at that Port, or nothing within the time.
    public string? Read(string dataType, int portNumber, int milliseconds = 60_000)
    {
        if (!from.TryGetValue((dataType, portNumber), out var device)) throw new InvalidOperationException($"The workflow delivers {dataType}:{portNumber} to no device.");
        return device.Receive(milliseconds);
    }
    public void Dispose()
    {
        try { stopping.Cancel(); running?.Wait(TimeSpan.FromSeconds(30)); } catch { }
        owner?.Dispose();
    }
}
