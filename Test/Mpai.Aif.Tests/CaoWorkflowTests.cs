using System.Text.Json;
using System.Text.Json.Nodes;

using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Cav.Ams;
using Mpai.Cav.Ess;
using Mpai.Cav.Hci;
using Mpai.Cav.Map;
using Mpai.Cav.Mas;
using Mpai.Cav.Recordings;
using Mpai.Cav.Ua;
using Mpai.Core;
using Mpai.Ua;
using Mpai.Wdl;

namespace Mpai.Aif.Tests;

// THE USER AGENT OF THE CAV: CAV.orch, interpreted over the Module 1CAV-CAO-V2.0-I01, with the vehicle, the cabin, the map and the journal as the devices of its Physical Layer - a simulation behind them
// (Apps\CAV\CAO\Ua). It is the drive of CaoTests.Step4OneModule - the passenger asks, in speech, for Central Station, is told the Route and agrees, and is driven there - with no step of the User Agent
// written by hand: the workflow starts the Module, binds a device to every Port of its boundary by Data Type and Port Number, and brings the vehicle to its safe state when it stops. Judged as that test
// judges it, against the same expected result.
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
[Collection(Timing.Name)]
public class CaoWorkflowTests
{
    private static readonly string Workflow = Environment.GetEnvironmentVariable("MPAI_CAV_WORKFLOW") ?? Path.Combine(Repository.Root, "Apps", "CAV", "CAO", "CAV.orch");

    // The workflow reads and its Ports are all ones of the Module's boundary: no Module, no models needed.
    [Fact]
    public void TheWorkflowReadsAndNamesOnlyPortsOfTheBoundary()
    {
        var workflow = new WorkflowReader().Read(File.ReadAllText(Workflow));
        Assert.Equal(["1CAV-CAO-V2.0-I01"], workflow.Modules);

        var amd = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Amds, "1CAV-CAO-V2.0-I01.json")))!;
        var ports = amd["ExternalPorts"]!.AsArray().Select(p => (Direction: (string)p!["Direction"]!, DataType: (string)p["DataType"]!, Number: p["PortNumber"] is { } n ? (int)n : 1)).ToList();
        var named = workflow.OnStart.Where(step => step.Kind is StepKind.Stream or StepKind.Deliver)
            .Select(step => (Direction: step.Kind == StepKind.Deliver ? "Output" : "Input", step.Port!.DataType, Number: step.Port.PortNumber)).ToList();
        Assert.NotEmpty(named);

        // Every datum the workflow streams is a boundary Input Port of the Module, and every datum it delivers a boundary Output Port.
        foreach (var port in named)
            Assert.Contains(ports, p => p.Direction == port.Direction && p.DataType == port.DataType && p.Number == port.Number);
        // The boundary Ports it leaves out are optional ones, or the External Ports of the Module (between Controllers, no device).
        foreach (var p in amd["ExternalPorts"]!.AsArray().Select(x => x!).Where(x => !named.Contains(((string)x["Direction"]!, (string)x["DataType"]!, x["PortNumber"] is { } n ? (int)n : 1))))
            Assert.True(p["IsOptional"]?.GetValue<bool>() == true || (string)p["DataType"]! is "CAV-ERA-V2.0" or "CAV-ERH-V2.0", $"the boundary Port {p["Name"]} is left out of the workflow and is not optional");
    }

    [SkippableFact]
    [Trait("Duration", "Long")]
    public async Task Step4TheDriveThroughTheWorkflow()
    {
        Skip.IfNot(TestModels.Can("yolox_s.onnx"), "yolox_s.onnx cannot be had: the L3 declares it, and Access holds it or cannot obtain it.");
        var result = new Dictionary<string, string>();
        var report = new Dictionary<string, object>();
        var map = RoadMap.Grid(3).Named(CavDialogueTests.Places);
        var sim = new Simulation(map, map.FastestRoute("W00", "W21")!, [], seed: 7);
        sim.Mechanical(seed: 9);
        const string voice = "en_GB-alan-medium";

        var all = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Root, "AIMs", "aim-settings.json")))!.AsObject();
        all[MasProvider.Msa] = new JsonObject { ["InitialHeading"] = (sim.Path.At(0).Heading * 180 / Math.PI).ToString(System.Globalization.CultureInfo.InvariantCulture) };
        var settings = Path.Combine(Path.GetTempPath(), "mpai-cav-ua-" + Guid.NewGuid().ToString("N") + ".json");
        var storage = Path.Combine(Path.GetTempPath(), "mpai-cav-ua-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(settings, all.ToJsonString());
        try
        {
            using var world = new CavWorld(sim,
                [new(null, "Please take me to the central station."), new("Shall we", "Yes, let's go.")],
                text => HciSpeechTests.Passenger(text, voice), AvatarUtterance.AvatarDatum(TestAvatar.Model),
                maxSteps: int.TryParse(Environment.GetEnvironmentVariable("MPAI_CAV_MAXSTEPS"), out var cap) ? cap : 1500);
            using var api = new ControllerApi(Repository.Amds, settings, store => new CompositeProvider(new EssProvider(Repository.Root), new AmsProvider(), new MasProvider(), new HciProvider(store, Repository.Root)));

            // The Physical Layer of this User Agent: the devices the workflow names, and the place where the person decided the Module keeps its storage.
            var devices = new DeviceRegistry()
                .RegisterDevice("Vehicle", world.Vehicle).RegisterDevice("Cabin", world.Cabin).RegisterDevice("Hearing", world.Hearing)
                .RegisterDevice("Map", world.Map).RegisterDevice("PointOfView", world.PointOfView).RegisterDevice("Journal", world.Journal)
                .RegisterAcquire("OSD-BTO-V1.5", (bool _, string? _) => Task.FromResult<string?>(MpaiJson.ToJson(BasicTextObject.FromText(storage))));
            var told = new List<string>();
            var interpreter = new WorkflowInterpreter(api.Async(), devices, line => { lock (told) told.Add(line); });
            using var stop = new CancellationTokenSource();
            var run = Task.Run(() => interpreter.RunAsync(new WorkflowReader().Read(File.ReadAllText(Workflow)), stop.Token));

            var degraded = new List<string>();
            var watch = Task.Run(async () =>
            {
                while (!world.Finished.IsCompleted && !run.IsCompleted)
                {
                    foreach (var x in api.Status("1CAV-CAO-V2.0-I01").Aims.Where(x => x.Status.ToString() != "Alive" && x.Status.ToString() != "Running" || x.Reports.Count > 0))
                    { var line = $"{x.Aim}: {x.Status} {x.Reason} {string.Join("; ", x.Reports.TakeLast(3))}"; lock (degraded) if (!degraded.Contains(line)) degraded.Add(line); }
                    await Task.Delay(100);
                }
            });
            await Task.WhenAny(world.Finished, run, Task.Delay(TimeSpan.FromMinutes(20)));
            stop.Cancel();
            try { await run.WaitAsync(TimeSpan.FromSeconds(60)); } catch (OperationCanceledException) { }

            await watch; report["the Sub-AIMs"] = degraded.ToList();
            Assert.Null(world.Failure);
            double? askedAt = world.Spoke.Select(s => (double?)s.At).FirstOrDefault();
            double? agreedAt = world.Spoke.Count > 1 ? world.Spoke[1].At : null;
            var said = world.Said;
            var latencies = world.StepMilliseconds.OrderBy(x => x).ToList();
            result["the passenger's Destination"] = agreedAt is not null ? "understood; the Route told" : "not understood";
            result["before the passenger agrees"] = world.MovedAt is { } m && agreedAt is { } a && m >= a ? "the CAV stands" : $"moved at {world.MovedAt:0.0} s, agreed at {agreedAt:0.0} s";
            result["the drive"] = $"{(sim.Collided ? "collision" : "no collision")}; {(sim.Path.Length - sim.EgoS < 15 ? "Destination reached" : "Destination not reached")}; {(world.OverLimit <= 0.5 ? "within the speed limit" : "above the speed limit")}";
            result["the arrival"] = world.ArrivedAt is not null ? "told" : "not told";
            report["heard"] = world.Recognised;
            report["said"] = said.Select(s => $"{s.At:0.0} s: {s.Text}").ToList();
            report["the workflow"] = told.ToList();
            report["the drive"] = $"asked at {askedAt:0.0} s, agreed at {agreedAt:0.0} s, moved at {world.MovedAt:0.0} s, arrived at {world.ArrivedAt:0.0} s, {sim.Path.Length - sim.EgoS:0.0} m from the end; {world.Steps} steps" +
                                  (latencies.Count > 0 ? $"; a step of the CAV median {latencies[latencies.Count / 2]:0} ms, 95% {latencies[(int)(latencies.Count * 0.95)]:0} ms" : "");
            File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "cav-ua-cao.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + Environment.NewLine);
            Expected.Match("cav-stage1-cao.json", result);
        }
        finally { try { File.Delete(settings); Directory.Delete(storage, recursive: true); } catch { } }
    }
}
