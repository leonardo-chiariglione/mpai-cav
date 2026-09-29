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
using Mpai.Core;

namespace Mpai.Aif.Tests;

// THE CAV AS ONE MODULE (M3243 3.4): 1CAV-CAO-V2.0-I01 under one Controller - HCI,
// the ESS, the AMS and the MAS its Sub-AIMs - driven at its boundary by the User
// Agent: each step what the vehicle senses written - the camera, GNSS, the Spatial
// Data, the Weather Data, the Responses of the devices - and the commands of that
// frame read; what the passenger says written to the cabin's audio; what the CAV
// says read. What one subsystem gives another, the Topology carries. The step ends
// when the AMS has decided on the frame (its AMS Data): the simulation waits for the
// CAV.
public sealed class CaoInTheLoop : IDisposable
{
    public const string Cao = "1CAV-CAO-V2.0-I01";
    private readonly ControllerApi api;
    private readonly string settings = Path.Combine(Path.GetTempPath(), "mpai-p11-settings-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly string location = Path.Combine(Path.GetTempPath(), "mpai-p11-cao-" + Guid.NewGuid().ToString("N"));
    private bool commanding;

    public ControllerApi Api => api;

    // What the CAV said: its text, in order; and the AMS Data of each step.
    public List<string> Said { get; } = [];
    public List<string> Recognised { get; } = [];

    public CaoInTheLoop(Simulation sim)
    {
        var all = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Root, "AIMs", "aim-settings.json")))!.AsObject();
        all[MasProvider.Msa] = new JsonObject { ["InitialHeading"] = (sim.Path.At(0).Heading * 180 / Math.PI).ToString(System.Globalization.CultureInfo.InvariantCulture) };
        File.WriteAllText(settings, all.ToJsonString());
        api = new ControllerApi(Repository.Amds, settings, store => new CompositeProvider(new EssProvider(Repository.Root), new AmsProvider(), new MasProvider(), new HciProvider(store, Repository.Root)));
        var started = api.StartFlow(Cao);
        if (started != AifError.OK) throw new InvalidOperationException($"{Cao} did not start: {started}");
        api.SharedStorageInit(Cao, location);
        Write("OSD-BOO-V1.5", 1, sim.Map.ToOfflineMapObject(0));
    }

    private void Write(string dataType, int port, string json, int timeoutMs = 10_000)
    {
        var written = api.InputWrite(Cao, dataType, port, json, timeoutMs);
        if (written != AifError.OK) throw new InvalidOperationException($"{dataType}#{port} not written to the CAV: {written}");
    }

    // What the passenger says, into the cabin's microphone.
    public void Hears(string passengerAudio) => Write("OSD-BAO-V1.5", 2, passengerAudio);

    // A step: what was sensed and the Responses in; the commands of the frame out.
    public List<(string DataType, string Json)> Step(Simulation.Sensed sensed, IEnumerable<(string DataType, string Json)> responses, int timeoutMs = 60_000)
    {
        foreach (var (dataType, json) in responses) Write(dataType, 1, json);
        foreach (var (dataType, json) in sensed.Messages.Where(m => m.DataType != "OSD-OSA-V1.5")) Write(dataType, 1, json);

        // The AMS's decision on the frame.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var read = api.OutputRead(Cao, AmsTypes.Data, 1, Math.Max(1, timeoutMs - (int)clock.ElapsedMilliseconds));
            if (read.Error != AifError.OK) throw new InvalidOperationException($"no AMS Data for {sensed.FrameMs}: {read.Error}");
            if (AmsTypes.Ms(JsonNode.Parse(read.Json!)!["AMSDataTime"]) >= sensed.FrameMs) break;
        }
        // The commands of the frame: once the MAS follows a Message, one Wheel Command
        // for each Spatial Data, the Brake and Motor Commands before it.
        var commands = new List<(string, string)>();
        while (true)
        {
            var read = api.OutputRead(Cao, MasTypes.WheelCommand, 1, commanding ? 10_000 : 0);
            if (read.Error != AifError.OK) break;
            commanding = true;
            foreach (var type in new[] { MasTypes.BrakeCommand, MasTypes.MotorCommand })
                while (api.OutputRead(Cao, type, 1, 0) is { Error: AifError.OK, Json: { } c }) commands.Add((type, c));
            commands.Add((MasTypes.WheelCommand, read.Json!));
            if (MasTypes.Ms(JsonNode.Parse(read.Json!)!["WheelCommandTime"]) >= sensed.FrameMs) break;
            commands.Clear();
        }
        // What the CAV says, and what HCI heard.
        while (api.OutputRead(Cao, "OSD-BTO-V1.5", 1, 0) is { Error: AifError.OK, Json: { } t }) Said.Add(MpaiJson.FromJson<BasicTextObject>(t)?.GetText() ?? "");
        while (api.OutputRead(Cao, "OSD-BTO-V1.5", 2, 0) is { Error: AifError.OK, Json: { } r }) Recognised.Add(MpaiJson.FromJson<BasicTextObject>(r)?.GetText() ?? "");
        foreach (var type in new[] { "OSD-BSO-V1.5", "PAF-FDO-V1.6", "OSD-IID-V1.5" })
            while (api.OutputRead(Cao, type, 1, 0) is { Error: AifError.OK }) { }
        return commands;
    }

    public void Dispose()
    {
        api.StopFlow(Cao);
        api.Dispose();
        try { File.Delete(settings); Directory.Delete(location, recursive: true); } catch { }
    }
}

// STEP 4 (M3243 3.4, 3.7): THE CAV AS ONE MODULE, ON THE DRIVE OF PHASE 9. The CAV
// standing at the start of the Route of Phase 8, on the free road, the map's places
// named; the passenger asks, in speech, for Central Station - the Route's end - and,
// told the Route, agrees. Judged: the CAV stands until the passenger agrees; no
// collision; the Destination reached; within the speed limit; the arrival told.
// Reported: what HCI heard, what the CAV said and when, the drive's steps, a step.
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
[Collection(Timing.Name)]
public class CaoTests
{
    [SkippableFact]
    [Trait("Duration", "Long")]
    public void Step4OneModule()
    {
        Skip.IfNot(File.Exists(Path.Combine(Repository.Root, "Models", "yolox_s.onnx")), "Models/yolox_s.onnx is absent: the model files are obtained separately.");
        var result = new Dictionary<string, string>();
        var report = new Dictionary<string, object>();
        var map = RoadMap.Grid(3).Named(CavDialogueTests.Places);
        var sim = new Simulation(map, map.FastestRoute("W00", "W21")!, [], seed: 7);
        sim.Mechanical(seed: 9);
        using var cav = new CaoInTheLoop(sim);

        const string voice = "en_GB-alan-medium";
        double? askedAt = null, agreedAt = null, movedAt = null, arrivedAt = null;
        var when = new List<string>();
        var said = 0;
        double overLimit = 0;
        var latencies = new List<double>();
        IReadOnlyList<(string DataType, string Json)> responses = [];
        var steps = 0;
        for (; steps < 1500 && !sim.Collided && arrivedAt is null && (agreedAt is not null || steps < 300); steps++)
        {
            if (steps == 5) { cav.Hears(HciSpeechTests.Passenger("Please take me to the central station.", voice)); askedAt = sim.Time; }
            var sensed = sim.Sense();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var commands = cav.Step(sensed, responses);
            latencies.Add(clock.Elapsed.TotalMilliseconds);
            for (; said < cav.Said.Count; said++)
            {
                when.Add($"{sim.Time:0.0} s: {cav.Said[said]}");
                if (agreedAt is null && cav.Said[said].Contains("Shall we")) { cav.Hears(HciSpeechTests.Passenger("Yes, let's go.", voice)); agreedAt = sim.Time; }
                if (cav.Said[said].Contains("arrived")) arrivedAt = sim.Time;
            }
            if (movedAt is null && sim.EgoS > 0.5) movedAt = sim.Time;
            overLimit = Math.Max(overLimit, (double)sensed.Truth["Ego"]!["Speed"]! - (double)sensed.Truth["Ego"]!["SpeedLimit"]!);
            sim.Actuate(commands);
            responses = sim.Advance();
        }
        result["the passenger's Destination"] = agreedAt is not null ? "understood; the Route told" : "not understood";
        result["before the passenger agrees"] = movedAt is { } m && agreedAt is { } a && m >= a ? "the CAV stands" : $"moved at {movedAt:0.0} s, agreed at {agreedAt:0.0} s";
        result["the drive"] = $"{(sim.Collided ? "collision" : "no collision")}; {(sim.Path.Length - sim.EgoS < 15 ? "Destination reached" : "Destination not reached")}; {(overLimit <= 0.5 ? "within the speed limit" : "above the speed limit")}";
        result["the arrival"] = arrivedAt is not null ? "told" : "not told";
        latencies.Sort();
        report["the Sub-AIMs"] = cav.Api.Status(CaoInTheLoop.Cao).Aims.Where(x => x.Status.ToString() != "Running" || x.Reports.Count > 0)
                                    .Select(x => $"{x.Aim}: {x.Status} {x.Reason} {string.Join("; ", x.Reports.TakeLast(3))}").ToList();
        report["heard"] = cav.Recognised;
        report["said"] = when;
        report["the drive"] = $"asked at {askedAt:0.0} s, agreed at {agreedAt:0.0} s, moved at {movedAt:0.0} s, arrived at {arrivedAt:0.0} s, {sim.Path.Length - sim.EgoS:0.0} m from the end; {steps} steps; " +
                              $"a step of the CAV median {latencies[latencies.Count / 2]:0} ms, 95% {latencies[(int)(latencies.Count * 0.95)]:0} ms";
        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "cav-stage1-cao.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + Environment.NewLine);
        Expected.Match("cav-stage1-cao.json", result);
    }
}
