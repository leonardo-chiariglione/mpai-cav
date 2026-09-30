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
    // What the CAV said, as the cabin's loudspeaker gives it: its speech, in order.
    public List<byte[]> Spoken { get; } = [];

    // The AMS Data of the last step: the decision, and the Full Environment
    // Descriptors it was taken on.
    public JsonNode? Data { get; private set; }

    // cavId: the CAV's identity in its city (M3241), which FED sends its Full
    // Environment Descriptors with.
    public CaoInTheLoop(Simulation sim, string? cavId = null)
    {
        var all = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Root, "AIMs", "aim-settings.json")))!.AsObject();
        if (cavId is not null) all[AmsProvider.Fed] = new JsonObject { ["CAVID"] = cavId };
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
            Data = JsonNode.Parse(read.Json!)!;
            if (AmsTypes.Ms(Data["AMSDataTime"]) >= sensed.FrameMs) break;
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
        while (api.OutputRead(Cao, "OSD-BSO-V1.5", 1, 0) is { Error: AifError.OK, Json: { } sp }) Spoken.Add(MpaiJson.FromJson<BasicSpeechObject>(sp)?.Data ?? []);
        foreach (var type in new[] { "PAF-FDO-V1.6", "OSD-IID-V1.5" })
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

    // THE PASSENGER'S SIDE OF A DIALOGUE: when the CAV has said something containing
    // the cue, and not before After seconds, the passenger says the line (a null cue:
    // at the first step).
    private sealed record Line(string? Cue, string Says, double After = 0);

    // A drive of the CAV as one Module on the free road, the passenger saying the
    // lines in turn; until the CAV arrives, collides, or maxSeconds pass.
    private static (Simulation Sim, List<string> Said, List<string> Heard, double? ArrivedAt, double? MovedAt, List<double> Latencies) Drive(Line[] lines, double maxSeconds)
    {
        var map = RoadMap.Grid(3).Named(CavDialogueTests.Places);
        var sim = new Simulation(map, map.FastestRoute("W00", "W21")!, [], seed: 7);
        sim.Mechanical(seed: 9);
        using var cav = new CaoInTheLoop(sim);
        const string voice = "en_GB-alan-medium";
        var said = new List<string>();
        var next = 0;
        var seen = 0;
        var cued = false;
        double? arrivedAt = null, movedAt = null;
        var latencies = new List<double>();
        IReadOnlyList<(string DataType, string Json)> responses = [];
        for (var steps = 0; sim.Time < maxSeconds && !sim.Collided && arrivedAt is null; steps++)
        {
            if (next < lines.Length && lines[next].Cue is null && steps == 5) cav.Hears(HciSpeechTests.Passenger(lines[next++].Says, voice));
            if (cued && sim.Time >= lines[next].After) { cav.Hears(HciSpeechTests.Passenger(lines[next++].Says, voice)); cued = false; }
            var sensed = sim.Sense();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var commands = cav.Step(sensed, responses);
            latencies.Add(clock.Elapsed.TotalMilliseconds);
            for (; seen < cav.Said.Count; seen++)
            {
                said.Add($"{sim.Time:0.0} s: {cav.Said[seen]}");
                if (!cued && next < lines.Length && lines[next].Cue is { } cue && cav.Said[seen].Contains(cue)) cued = true;
                if (cav.Said[seen].Contains("arrived")) arrivedAt = sim.Time;
            }
            if (movedAt is null && sim.EgoS > 0.5) movedAt = sim.Time;
            sim.Actuate(commands);
            responses = sim.Advance();
        }
        return (sim, said, cav.Recognised, arrivedAt, movedAt, latencies);
    }

    // STEP 5 (M3243 3.5): THE CAV END TO END, the other cases. A place the map does not
    // have, one named ambiguously, a Route declined - the CAV standing - then another
    // place asked for, agreed, driven to and arrived at; and a Destination changed while
    // driving: the new Route proposed, the CAV driving on, agreed, driven to, arrived at.
    // Judged: each; no collision; where it stops. Reported: the dialogue, the drive.
    [SkippableFact]
    [Trait("Duration", "Long")]
    public void Step5TheOtherCases()
    {
        Skip.IfNot(File.Exists(Path.Combine(Repository.Root, "Models", "yolox_s.onnx")), "Models/yolox_s.onnx is absent: the model files are obtained separately.");
        var result = new Dictionary<string, string>();
        var report = new Dictionary<string, object>();
        double Hospital(Simulation sim) => sim.Map.Length(sim.Path.Segments[0]) + sim.Map.Length(sim.Path.Segments[1]);

        // Unknown, ambiguous, declined, then the hospital.
        var (sim, said, heard, arrivedAt, movedAt, _) = Drive(
        [
            new(null, "Take me to the airport."),
            new("do not know", "Then take me to the station."),
            new("Which one", "The north one."),
            new("Shall we", "No, thanks."),
            new("we stay here", "Take me to the hospital, please."),
            new("Shall we", "Yes, let's go."),
        ], 120);
        var text = string.Join(" | ", said);
        result["a place the map does not have"] = text.Contains("do not know \"airport\"") ? "told the places the CAV knows" : "not told";
        result["a place named ambiguously"] = text.Contains("There are 2") ? "asked which" : "not asked";
        result["a Route declined"] = said.Any(x => x.Contains("we stay here")) && movedAt is { } m && said.Last(x => x.Contains("we stay here")) is { } stay && m > double.Parse(stay[..stay.IndexOf(' ')], System.Globalization.CultureInfo.InvariantCulture)
            ? "the CAV stays until another is agreed" : $"moved at {movedAt:0.0} s";
        result["then the hospital"] = arrivedAt is not null && !sim.Collided && Math.Abs(sim.EgoS - Hospital(sim)) < 15 ? "driven to, arrived at, told" : $"{(sim.Collided ? "collision" : "no collision")}; {sim.EgoS - Hospital(sim):0.0} m from it";
        report["unknown, ambiguous, declined, the hospital"] = new { heard, said, drive = $"moved at {movedAt:0.0} s, arrived at {arrivedAt:0.0} s, {sim.EgoS - Hospital(sim):0.0} m from the hospital" };

        // Changed while driving.
        (sim, said, heard, arrivedAt, movedAt, _) = Drive(
        [
            new(null, "Take me to the central station."),
            new("Shall we", "Yes."),
            new("on our way", "Actually, take me to the hospital instead.", After: 15),
            new("Hospital: about", "Yes, go."),
        ], 120);
        var changedAt = said.FirstOrDefault(x => x.Contains("Hospital: about"));
        var proposedAt = changedAt is null ? (double?)null : double.Parse(changedAt[..changedAt.IndexOf(' ')], System.Globalization.CultureInfo.InvariantCulture);
        result["a Destination changed while driving"] = proposedAt is { } t && movedAt is { } moved && t > moved + 5 && arrivedAt is not null && !sim.Collided && Math.Abs(sim.EgoS - Hospital(sim)) < 15
            ? "the new Route proposed, the CAV driving on; agreed, driven to, arrived at" : $"{changedAt ?? "not proposed"}; {(sim.Collided ? "collision" : "no collision")}; {sim.EgoS - Hospital(sim):0.0} m from the hospital";
        report["changed while driving"] = new { heard, said, drive = $"moved at {movedAt:0.0} s, the new Route proposed at {proposedAt:0.0} s, arrived at {arrivedAt:0.0} s, {sim.EgoS - Hospital(sim):0.0} m from the hospital" };

        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "cav-stage1-cao-cases.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + Environment.NewLine);
        Expected.Match("cav-stage1-cao-cases.json", result);
    }

    // STEP 5 (M3243 3.6; the author: both CAOs, A drives): THE TWO CAVs OF PHASE 10,
    // EACH A CAO. On the first straight of the Route, a vehicle stopped in the lane at
    // 200 m; CAV A standing 30 m ahead of CAV B, in the same lane. Each a whole CAO -
    // HCI, the ESS with the detector, the AMS, the MAS - in a simulation of its own:
    // A's with the stopped vehicle; B's with it and with A, placed at each step where
    // A's simulation has it. Both admitted by Turin, their External Ports joined in
    // range; each passenger asks for Central Station and agrees. A drives itself and
    // stops behind the stopped vehicle; B, behind A, cannot see it. Judged: B places
    // the stopped vehicle, from A's report, where it is; what B received signed and
    // verified, none refused; no collision. Reported: when B placed it, where each
    // stopped, the Messages exchanged.
    [SkippableFact]
    [Trait("Duration", "Long")]
    public async Task Step5TwoCaos()
    {
        Skip.IfNot(File.Exists(Path.Combine(Repository.Root, "Models", "yolox_s.onnx")), "Models/yolox_s.onnx is absent: the model files are obtained separately.");
        const double StoppedAt = RemoteStage1Tests.StoppedAt, AStart = RemoteStage1Tests.AStart;
        var result = new Dictionary<string, string>();
        var map = RoadMap.Grid(3).Named(CavDialogueTests.Places);
        var route = map.FastestRoute("W00", "W21")!;
        var simA = new Simulation(map, route, [new ScenarioVehicle("stopped", 0, StoppedAt, [(0, 0.0)], CameraRenderer.DarkRed)], seed: 7);
        simA.StartAt(AStart);
        simA.Mechanical(seed: 9);
        var simB = new Simulation(map, route,
            [new ScenarioVehicle("A", 0, AStart, [(0, 0.0)], CameraRenderer.Silver), new ScenarioVehicle("stopped", 0, StoppedAt, [(0, 0.0)], CameraRenderer.DarkRed)], seed: 8);
        simB.Mechanical(seed: 10);
        using var a = new CaoInTheLoop(simA, "CAV-A");
        using var b = new CaoInTheLoop(simB, "CAV-B");

        var turin = new AIF.Trust.TrustAuthority("Turin");
        var port = 45000 + Environment.ProcessId % 1000;
        var discoveries = new List<AIF.Channels.UdpDiscovery>();
        AIF.Channels.ExternalHub? hubB = null;
        foreach (var (id, cav) in new[] { ("CAV-A", a), ("CAV-B", b) })
        {
            var trust = new AIF.Controller.CityTrust(turin, turin.Admit(id));
            var discovery = new AIF.Channels.UdpDiscovery(port);
            discoveries.Add(discovery);
            var hub = cav.Api.StartExternal(CaoInTheLoop.Cao, new AIF.Channels.ExternalHub.Options
            {
                ControllerId = id, ModuleType = "CAV-CAO-V2.0", Discovery = discovery, Admission = trust.Admission,
                InRange = _ => Math.Abs(simA.EgoS - simB.EgoS) <= 300
            }, trust.Sign, trust.Verify);
            if (id == "CAV-B") hubB = hub;
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (hubB!.Linked.Count == 0 && clock.ElapsedMilliseconds < 5000) await Task.Delay(20);

        const string voice = "en_GB-alan-medium";
        var agreed = new HashSet<string>();
        var seen = new Dictionary<string, int> { ["A"] = 0, ["B"] = 0 };
        double? placedAt = null, placedError = null, seenAt = null;
        IReadOnlyList<(string DataType, string Json)> respA = [], respB = [];
        var steps = 0;
        for (; steps < 900 && !simA.Collided && !simB.Collided; steps++)
        {
            if (steps == 5)
                foreach (var cav in new[] { a, b }) cav.Hears(HciSpeechTests.Passenger("Please take me to the central station.", voice));
            simB.Place("A", simA.EgoS, simA.EgoSpeed);
            var sensedA = simA.Sense();
            var sensedB = simB.Sense();
            var cmdA = a.Step(sensedA, respA);
            var cmdB = b.Step(sensedB, respB);
            foreach (var (name, cav) in new[] { ("A", a), ("B", b) })
                for (; seen[name] < cav.Said.Count; seen[name]++)
                    if (!agreed.Contains(name) && cav.Said[seen[name]].Contains("Shall we")) { cav.Hears(HciSpeechTests.Passenger("Yes, let's go.", voice)); agreed.Add(name); }

            // B's map: a Remote object in its lane, where the stopped vehicle is.
            var stopped = simB.Around().First(x => x.Id == "stopped");
            if (placedAt is null && b.Data?["FED"]?["FullEnvironmentObjects"] is JsonArray objects)
                foreach (var o in objects)
                {
                    if ((string?)o?["Source"] != "Remote" || (int?)o["Placement"]?["Lane"] != 0) continue;
                    if (EssJson.Vector(o["BasicEnvironmentObject"]?["SpatialAttitude"]?["Position"]?["CartPosition"]) is not { } p) continue;
                    if (Math.Abs(p.X - stopped.Ahead) > 10) continue;
                    placedAt = simB.Time; placedError = p.X - stopped.Ahead;
                }
            if (seenAt is null && stopped.Ahead < 90 && simB.Around().All(x => x.Id == "stopped" || x.Lane != 0 || x.Ahead > stopped.Ahead)) seenAt = simB.Time;

            simA.Actuate(cmdA);
            simB.Actuate(cmdB);
            respA = simA.Advance();
            respB = simB.Advance();
            if (agreed.Count == 2 && simA.Time > 15 && simA.EgoSpeed < 0.05 && simB.EgoSpeed < 0.05) break;
        }
        var gapA = StoppedAt - simA.EgoS - Simulation.VehicleLength;
        var gapB = simA.EgoS - simB.EgoS - Simulation.VehicleLength;
        result["the stopped vehicle, from A's report"] = placedAt is not null && (seenAt is null || placedAt < seenAt - 1)
            ? "placed where it is before B's camera could see it" : placedAt is null ? "not placed" : "placed only when B could see it";
        result["what B received"] = hubB.Received > 0 && hubB.Refused == 0 ? "signed by A, verified, none refused" : $"{hubB.Received} received, {hubB.Refused} refused";
        result["the drive"] = !simA.Collided && !simB.Collided && agreed.Count == 2 && simA.EgoS > AStart + 50 ? "both drove; no collision" : $"{(simA.Collided || simB.Collided ? "collision" : "no collision")}; agreed {agreed.Count}";
        var report = new Dictionary<string, string>
        {
            ["the stopped vehicle"] = $"placed by B at {placedAt:0.0} s, {placedError:0.0} m from where it is; B's camera could see it at {(seenAt is { } v ? v.ToString("0.0") + " s" : "no time: A hid it")}",
            ["where they stopped"] = $"A {gapA:0.0} m behind the stopped vehicle, B {gapB:0.0} m behind A, at {simA.Time:0.0} s; {steps} steps",
            ["the exchange"] = $"B received {hubB.Received} Messages, refused {hubB.Refused}"
        };
        foreach (var d in discoveries) await d.DisposeAsync();
        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "cav-stage1-two-caos.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Expected.Match("cav-stage1-two-caos.json", result);
    }
}
