using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Core;
using Mpai.Providers;
using Mpai.Rca;
using Mpai.Wdl;

namespace Mpai.AsmStudio;

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
// interpreter, which is the only thing that deals with the Controller - starts the Module, asks it to keep the storage where the User says, streams to the Ports and
// delivers from them, stops it. The UA holds devices, one to a Port, and addresses them by Data Type and Port Number; it holds no AIM and does not read or write the
// storage. It uses only the Controller API (and so may run remotely, through MPAI-MAS, as the other Apps do).
public sealed class ModuleLink : IDisposable
{
    public const string Module = "1CAE-ASM-V1.0-I01";

    // Data Types of the Module's boundary, by Port Number (CAE-ASM-V1.0, schemas/CAE3/V1.0/AIMs/AudioSceneManager.json).
    public const string Bao = "OSD-BAO-V1.5", Bas = "OSD-BAS-V1.5", Bso = "OSD-BSO-V1.5", Bto = "OSD-BTO-V1.5", Ucm = "CAE-UCM-V1.0", Tfu = "MMC-TFU-V1.5", Auo = "OSD-AUO-V1.5";

    // The device of each Port the User Agent gives to, and of each it takes from, as the workflow names them.
    private static readonly (string Device, string Type, int Number)[] Streams =
    [
        ("Voice", Bso, 2), ("Typed", Bto, 1), ("TextToSpeak", Bto, 2), ("ObjectIn", Bao, 1), ("AudioObjectIn", Auo, 1), ("ObjectCommand", Ucm, 1),
        ("SceneIn", Bas, 1), ("SceneCommand", Ucm, 2), ("VoiceScene", Bas, 2)
    ];

    private static readonly (string Device, string Type, int Number)[] Delivered =
    [
        ("Heard", Bto, 1), ("ReplyText", Bto, 2), ("Reply", Bso, 2), ("Said", Tfu, 1), ("VoiceCommand", Ucm, 6), ("ObjectOut", Bao, 1), ("AudioObjectOut", Auo, 1),
        ("ObjectReport", Ucm, 1), ("Scene", Bas, 1), ("SceneReport", Ucm, 2)
    ];

    private readonly IControllerApi api;
    private readonly IDisposable? owner;
    private readonly Dictionary<(string, int), PortDevice> into = [], from = [];
    private readonly CancellationTokenSource stopping = new();
    private readonly HashSet<string> bound = [];
    private Task? running;
    private string storage = "";

    public ModuleLink(IControllerApi api, IDisposable? owner = null) { this.api = api; this.owner = owner; }

    // The Controller of this machine, with the AIMs it can make for this Module: Audio Scene Management's, and the voice's.
    public static ModuleLink InProcess()
    {
        var controller = new ControllerApi(Paths.Amds, Paths.Settings, store => new CompositeProvider(new Mpai.Cae.Asm.AsmProvider(Paths.Amds), new MadProvider(store), new MatProvider(store)));
        return new ModuleLink(controller, controller);
    }

    // The workflow is run; it starts the Module and asks the Controller to keep the storage where the User says.
    public void Start(string storageLocation)
    {
        storage = storageLocation;
        var devices = new DeviceRegistry();
        foreach (var (name, type, number) in Streams) devices.RegisterDevice(name, into[(type, number)] = new PortDevice(type));
        foreach (var (name, type, number) in Delivered) devices.RegisterDevice(name, from[(type, number)] = new PortDevice(type));
        devices.RegisterDevice("Unused", new DiscardDevice());
        // What the User chose: where the storage is.
        devices.RegisterAcquire(Bto, (_, _) => Task.FromResult<string?>(MpaiJson.ToJson(BasicTextObject.FromText(storage))));

        var workflow = new WorkflowReader().Read(File.ReadAllText(Path.Combine(Paths.Root, "Apps", "ASM", "ASM.orch")));
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

        // Ready when the Module is started and every device is bound.
        var expected = Streams.Length + Delivered.Length > 0 ? Streams.Select(s => s.Device).Concat(Delivered.Select(d => d.Device)).Distinct().Count() + 1 : 0;
        var until = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < until)
        {
            if (failure.Task.IsCompleted) throw new InvalidOperationException("The workflow of Audio Scene Management stopped: " + failure.Task.Result?.Message, failure.Task.Result);
            int count; lock (bound) count = bound.Count;
            if (count >= expected && Status() == AifError.OK) { Thread.Sleep(200); return; }
            Thread.Sleep(100);
        }
        throw new InvalidOperationException("The workflow of Audio Scene Management did not start the Module.");
    }

    // The ports the editing exchanges leave a datum at that nobody asks for, and those of the voice: read away before an exchange, so that what is read after it is of it.
    private static readonly (string, int)[] EditingOutputs = [(Bao, 1), (Ucm, 1), (Ucm, 2), (Bas, 1), (Auo, 1)];
    private static readonly (string, int)[] VoiceOutputs = [(Ucm, 6), (Bto, 1), (Bto, 2), (Tfu, 1), (Bso, 2)];

    public void DrainEditing() => Drain(EditingOutputs);
    public void DrainVoice() => Drain(VoiceOutputs);
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

    public AifError Status() => api.Status(Module).Error;

    public void Dispose()
    {
        try { stopping.Cancel(); running?.Wait(TimeSpan.FromSeconds(30)); } catch { }
        owner?.Dispose();
    }
}
