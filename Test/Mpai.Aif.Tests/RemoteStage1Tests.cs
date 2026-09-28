using System.Text.Json;
using System.Text.Json.Nodes;

using AIF.Channels;
using Mpai.Aif.Api;
using Mpai.Cav.Ams;
using Mpai.Cav.Map;
using Mpai.Cav.Recordings;

namespace Mpai.Aif.Tests;

// PHASE 10 (M3241): the Remote CAVs, Stage 1.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
[Collection(Timing.Name)]
public class RemoteStage1Tests
{
    // THE HIDDEN VEHICLE (M3241 3.5): on the first straight of the Route of Phase 8, a
    // vehicle stopped in the lane at 200 m; CAV A 25 m ahead of CAV B at 12 m/s, in the
    // same lane, hiding it; A changes lane 8 m before it (scripted: Stage 1's Paths
    // keep the lane). B starts at 12 m/s.
    public const double StoppedAt = 200, AStart = 30, Speed = 12, CutOut = 8;

    public static Simulation HiddenVehicle()
    {
        var map = RoadMap.Grid(3);
        var route = map.FastestRoute("W00", "W21")!;
        var cutAt = (StoppedAt - CutOut - AStart) / Speed;
        return new Simulation(map, route,
        [
            new ScenarioVehicle("A", 0, AStart, [(0, Speed)], CameraRenderer.Silver, LaneChange: (cutAt, 1)),
            new ScenarioVehicle("stopped", 0, StoppedAt, [(0, 0.0)], CameraRenderer.DarkRed)
        ], seed: 7, egoSpeed: Speed);
    }

    // B driven on perfect perception as its camera would allow it - what a nearer
    // vehicle in its lane hides, and what is beyond 90 m, unseen - or with the stopped
    // vehicle known, as A would report it. Judged: no collision; with it known, less
    // hard braking than without. Reported: when B first knows the stopped vehicle,
    // its speed when A pulls out, the hardest braking, the lowest time to collision,
    // the gap it stops at.
    [Fact]
    public void Step1WithoutTheExchange()
    {
        var result = new Dictionary<string, string>();
        var report = new Dictionary<string, string>();
        var hardest = new Dictionary<string, double>();
        foreach (var (name, known) in new[] { ("the stopped vehicle unknown until seen", false), ("the stopped vehicle known, as A would report it", true) })
        {
            var sim = HiddenVehicle();
            double brake = 0, lowestTtc = double.PositiveInfinity, speedAtCut = double.NaN;
            double? knownAt = null;
            var cutAt = (StoppedAt - CutOut - AStart) / Speed;
            MasStage1Tests.Loop(sim, s =>
            {
                brake = Math.Max(brake, -sim.EgoAcceleration);
                if (double.IsNaN(speedAtCut) && sim.Time >= cutAt) speedAtCut = sim.EgoSpeed;
                var stopped = sim.Around().First(a => a.Id == "stopped");
                if (sim.Around().All(a => a.Id == "stopped" || a.Lane != 0 || a.Ahead > stopped.Ahead) && stopped.Ahead > 0 && sim.EgoSpeed > 0.1)
                    lowestTtc = Math.Min(lowestTtc, stopped.Ahead / sim.EgoSpeed);
                // Known: from the start when reported; else once within the camera's 90 m
                // with nothing nearer in the lane - as TruthBed sees it.
                var visible = stopped.Ahead < 90 && sim.Around().All(a => a.Id == "stopped" || a.Lane != 0 || a.Ahead > stopped.Ahead);
                if (knownAt is null && (known || visible)) knownAt = known ? 0 : sim.Time;
            }, bedOf: (m, sensed) => TruthBed.Of(m, sensed, occlude: true, known: known ? ["stopped"] : null));
            var gap = sim.Around().First(a => a.Id == "stopped").Ahead;
            hardest[name] = brake;
            result[name] = $"{(sim.Collided ? "collision" : "no collision")}";
            report[name] = result[name] + $"; the stopped vehicle known at {knownAt:0.0} s (A pulls out at {cutAt:0.0} s); speed when A pulls out {speedAtCut:0.0} m/s; " +
                           $"hardest braking {brake:0.0} m/s2; lowest time to collision {(double.IsPositiveInfinity(lowestTtc) ? "-" : lowestTtc.ToString("0.0"))} s; stopped {gap:0.0} m behind it";
        }
        result["known, against unknown"] = hardest.Values.Last() < hardest.Values.First() - 0.5 ? "less hard braking" : "no less hard braking";
        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "remote-stage1-without.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Expected.Match("remote-stage1-without.json", result);
    }

    // STEP 4 (M3241 3.3): FULL ENVIRONMENT DESCRIPTION WITH A REMOTE CAV, on perfect
    // perception, in process. A's FED, given A's view - the truth from A, what the
    // vehicle ahead of it hides left out - sends its Full Environment Descriptors
    // (CAVID set); B's FED hears them and places what A perceives on its own map. B
    // driven by its AMS and its vehicle as in Step 1. Judged: B places the stopped
    // vehicle from A's report before it could see it, where it is; no collision; less
    // hard braking than without the exchange; every Ego-Remote AMS Message valid.
    [Fact]
    public void Step4FromARemoteCav()
    {
        var result = new Dictionary<string, string>();
        var report = new Dictionary<string, string>();
        var messages = new List<string>();
        var hardest = new Dictionary<string, double>();
        foreach (var (name, exchange) in new[] { ("without the exchange", false), ("with A's reports", true) })
        {
            var sim = HiddenVehicle();
            var map = JsonNode.Parse(sim.Map.ToOfflineMapObject(0))!;
            var fedA = new FullEnvironmentDescription(AmsProvider.Fed, new Dictionary<string, string> { ["CAVID"] = "CAV-A" });
            fedA.Know(map);
            var probe = new FullEnvironmentDescription(AmsProvider.Fed);
            probe.Know(map);
            double brake = 0, worstPlace = 0;
            double? knownAt = null, seenAt = null;
            MasStage1Tests.Loop(sim, s =>
            {
                brake = Math.Max(brake, -sim.EgoAcceleration);
                var stopped = sim.Around().First(a => a.Id == "stopped");
                if (seenAt is null && stopped.Ahead < 90 && sim.Around().All(a => a.Id == "stopped" || a.Lane != 0 || a.Ahead > stopped.Ahead)) seenAt = sim.Time;
            }, bedOf: (m, sensed) => TruthBed.Of(m, sensed, occlude: true), remote: (fedB, sensed) =>
            {
                if (!exchange) return;
                var fa = fedA.Describe(JsonNode.Parse(TruthBed.OfVehicle(sim, sensed, "A"))!);
                if (fedA.ToSend(fa) is { } era) { messages.Add(era.ToJsonString()); fedB.Heard(era); probe.Heard(era); }
                // What B's FED makes of it: the stopped vehicle, from A, where it is.
                var described = probe.Describe(JsonNode.Parse(TruthBed.Of(sim, sensed, occlude: true))!);
                var fromA = described["FullEnvironmentObjects"]!.AsArray().FirstOrDefault(o => (string?)o!["BasicEnvironmentObject"]!["BasicEnvironmentObjectID"] == "CAV-A/stopped");
                if (fromA is not null && (int?)fromA["Placement"]?["Lane"] == 0)
                {
                    knownAt ??= sim.Time;
                    var x = (double)fromA["BasicEnvironmentObject"]!["SpatialAttitude"]!["Position"]!["CartPosition"]![0]!;
                    worstPlace = Math.Max(worstPlace, Math.Abs(x - sim.Around().First(a => a.Id == "stopped").Ahead));
                }
            });
            hardest[name] = brake;
            result[name] = sim.Collided ? "collision" : "no collision";
            report[name] = result[name] + $"; hardest braking {brake:0.0} m/s2" + (exchange
                ? $"; the stopped vehicle placed from A's report at {knownAt:0.0} s, B's camera could see it at {seenAt:0.0} s; placed within {worstPlace:0.0} m of where it is"
                : $"; B's camera sees it at {seenAt:0.0} s");
            if (exchange)
                result["the stopped vehicle, from A's report"] = knownAt is { } k && seenAt is { } v && k < v - 1
                    ? $"placed before B could see it, within {(worstPlace <= 1 ? "1 m" : $"{worstPlace:0.0} m")} of where it is" : "not placed before B could see it";
        }
        result["with A's reports, against without"] = hardest["with A's reports"] < hardest["without the exchange"] - 0.5 ? "less hard braking" : "no less hard braking";
        var schema = AIF.Metadata.PublishedSchemas.At(Repository.Schemas)[Path.GetFullPath(Path.Combine(Repository.Schemas, "CAV2", "V2.0", "data", "EgoRemoteAMSMessage.json"))];
        var valid = messages.Count(m => { using var doc = JsonDocument.Parse(m); lock (AIF.Metadata.PublishedSchemas.Lock) return schema.Evaluate(doc.RootElement).IsValid; });
        result["CAV-ERA-V2.0 against its schema"] = valid == messages.Count && messages.Count > 0 ? "every one valid" : $"{valid} of {messages.Count}";
        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "remote-stage1-fed.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Expected.Match("remote-stage1-fed.json", result);
    }

    // STEP 4, AS MODULES: two AMSs, each a Module under its own Controller, each CAV
    // admitted by Turin, their External Ports joined by the External transport over
    // the machine's network. Each given its view, step by step (the simulation moved
    // by its model: this is the exchange, not the drive). Judged: linked; B's AMS
    // places the stopped vehicle A reports; what arrived signed and verified, none
    // refused; a signed Ego-Remote AMS Message valid against its schema.
    [Fact]
    public async Task Step4AsModules()
    {
        const string ams = CavInTheLoop.Ams;
        var result = new Dictionary<string, string>();
        var turin = new AIF.Trust.TrustAuthority("Turin");
        var port = 43000 + Environment.ProcessId % 1000;
        var sim = HiddenVehicle();
        var all = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Root, "AIMs", "aim-settings.json")))!.AsObject();
        var cavs = new List<(string Id, ControllerApi Api, ExternalHub Hub, UdpDiscovery Discovery, string Settings, string Storage)>();
        var fromA = false;
        try
        {
            foreach (var id in new[] { "CAV-A", "CAV-B" })
            {
                var settings = Path.Combine(Path.GetTempPath(), $"mpai-p10-{id}-{Guid.NewGuid():N}.json");
                var mine = all.DeepClone().AsObject();
                mine[AmsProvider.Fed] = new JsonObject { ["CAVID"] = id };
                File.WriteAllText(settings, mine.ToJsonString());
                var api = new ControllerApi(Repository.Amds, settings, new AmsProvider());
                if (api.StartFlow(ams) != AIF.Controller.AifError.OK) throw new InvalidOperationException($"{id}: the AMS did not start");
                var storage = Path.Combine(Path.GetTempPath(), $"mpai-p10-{id}-{Guid.NewGuid():N}");
                api.SharedStorageInit(ams, storage);
                api.InputWrite(ams, AmsTypes.Map, 1, sim.Map.ToOfflineMapObject(0), 5000);
                api.InputWrite(ams, AmsTypes.Hci, 1, AmsStage1Tests.Destination("W21"), 5000);
                var trust = new AIF.Controller.CityTrust(turin, turin.Admit(id));
                var discovery = new UdpDiscovery(port);
                var hub = api.StartExternal(ams, new ExternalHub.Options { ControllerId = id, ModuleType = "CAV-AMS-V2.0", Discovery = discovery, Admission = trust.Admission },
                                            trust.Sign, trust.Verify);
                cavs.Add((id, api, hub, discovery, settings, storage));
            }
            var (a, b) = (cavs[0], cavs[1]);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!(a.Hub.Linked.Contains("CAV-B") && b.Hub.Linked.Contains("CAV-A")) && clock.ElapsedMilliseconds < 5000) await Task.Delay(20);
            result["two AMSs of CAVs admitted by Turin"] = a.Hub.Linked.Contains("CAV-B") ? "linked" : "not linked";

            for (var step = 0; step < 100 && !fromA; step++)
            {
                var sensed = sim.Sense(camera: false);
                foreach (var (id, api, _, _, _, _) in cavs)
                {
                    var bed = id == "CAV-A" ? TruthBed.OfVehicle(sim, sensed, "A") : TruthBed.Of(sim, sensed, occlude: true);
                    api.InputWrite(ams, AmsTypes.Bed, 1, bed, 5000);
                    while (api.OutputRead(ams, AmsTypes.Message, 1, 5000) is { Error: AIF.Controller.AifError.OK, Json: { } json })
                        if (AmsTypes.Ms(JsonNode.Parse(json)!["AMSMASMessageTime"]) == sensed.FrameMs) break;
                    while (api.OutputRead(ams, AmsTypes.Data, 1, id == "CAV-B" ? 100 : 0) is { Error: AIF.Controller.AifError.OK, Json: { } data })
                        if (id == "CAV-B" && data.Contains("\"CAV-A/stopped\"") && data.Contains("\"Remote\"")) fromA = true;
                }
                sim.Advance(0);
            }
            result["B's AMS, the stopped vehicle A reports"] = fromA ? "placed on its map" : "not placed";
            result["what B received"] = b.Hub.Received > 0 && b.Hub.Refused == 0 ? "signed by A, verified, none refused" : $"{b.Hub.Received} received, {b.Hub.Refused} refused";
            var era = new FullEnvironmentDescription(AmsProvider.Fed, new Dictionary<string, string> { ["CAVID"] = "CAV-X" }).ToSend(new JsonObject
            {
                ["Header"] = "CAV-FED-V2.0", ["FullEnvironmentDescriptorsID"] = "FED1",
                ["FullEnvironmentDescriptorsTime"] = Mpai.Cav.Ess.EssJson.SimpleTime("T", 1),
                ["EgoSpatialAttitude"] = Mpai.Cav.Ess.EssJson.Attitude("E", 1, (0, 0, 0), (1, 1, 1), (0, 0, 0)),
                ["FullEnvironmentObjects"] = new JsonArray()
            })!;
            var signed = new AIF.Controller.CityTrust(turin, turin.Admit("CAV-X")).Sign(era.ToJsonString());
            var schema = AIF.Metadata.PublishedSchemas.At(Repository.Schemas)[Path.GetFullPath(Path.Combine(Repository.Schemas, "CAV2", "V2.0", "data", "EgoRemoteAMSMessage.json"))];
            using (var doc = JsonDocument.Parse(signed))
                lock (AIF.Metadata.PublishedSchemas.Lock)
                    result["a signed CAV-ERA-V2.0 against its schema"] = schema.Evaluate(doc.RootElement).IsValid ? "valid" : "invalid";
        }
        finally
        {
            foreach (var (_, api, _, discovery, settings, storage) in cavs)
            {
                api.StopFlow(ams);
                api.Dispose();
                await discovery.DisposeAsync();
                try { File.Delete(settings); Directory.Delete(storage, recursive: true); } catch { }
            }
        }
        Expected.Match("remote-stage1-modules.json", result);
    }
}
