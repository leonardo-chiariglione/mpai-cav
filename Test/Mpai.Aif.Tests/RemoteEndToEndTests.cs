using System.Text.Json;
using System.Text.Json.Nodes;

using AIF.Channels;
using Mpai.Cav.Mas;
using Mpai.Cav.Recordings;

namespace Mpai.Aif.Tests;

// PHASE 10, STEP 5 (M3241 3.6): TWO CAVs END TO END. CAV B whole - the ESS with the
// detector, the AMS, the MAS: three Modules, moved by its mechanical subsystems, as in
// Phase 9 - behind CAV A, whose AMS runs as a Module on its view (the truth from A)
// and whose motion is scripted: it pulls out of the lane 8 m before a vehicle stopped
// in it, which it hides from B's camera until then. Both admitted by Turin, their
// AMSs joined by the External transport, in range (300 m). The same drive without the
// exchange. Judged: with it, no collision; B places the stopped vehicle from A's
// report before its camera could see it; B is slower when A pulls out than without
// it - it has slowed for what it knows; what B received signed and verified, none
// refused. Reported: when B placed it and could see it, its speed when A pulls out,
// the hardest braking (three Modules at their own pace: it varies from run to run),
// the smallest gap, the Messages exchanged.
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
[Collection(Timing.Name)]
public class RemoteEndToEndTests
{
    private const double Range = 300;

    // LONG (Run-Matrix.ps1): run at a step only where it changed what this exercises.
    [SkippableFact]
    [Trait("Duration", "Long")]
    public async Task Step5TwoCavs()
    {
        Skip.IfNot(File.Exists(Path.Combine(Repository.Root, "Models", "yolox_s.onnx")), "Models/yolox_s.onnx is absent: the model files are obtained separately.");
        var result = new Dictionary<string, string>();
        var report = new Dictionary<string, string>();
        var hardest = new Dictionary<string, double>();
        var atPullOut = new Dictionary<string, double>();
        var pullOut = (RemoteStage1Tests.StoppedAt - RemoteStage1Tests.CutOut - RemoteStage1Tests.AStart) / RemoteStage1Tests.Speed;
        foreach (var (name, exchange) in new[] { ("without the exchange", false), ("with the exchange", true) })
        {
            var sim = RemoteStage1Tests.HiddenVehicle();
            sim.Mechanical(seed: 9);
            using var ess = new EssInTheLoop();
            ess.Know(sim.Map.ToOfflineMapObject(0));
            using var mas = new MasInTheLoop(sim.Path.At(0).Heading * 180 / Math.PI);
            using var amsB = new AmsInTheLoop(sim, "W21", exchange ? "CAV-B" : null);
            using var amsA = exchange ? new AmsInTheLoop(sim, "W21", "CAV-A") : null;
            var discoveries = new List<UdpDiscovery>();
            ExternalHub? hubB = null;
            var distance = 0.0;                                             // A to B, updated each step
            if (exchange)
            {
                var turin = new AIF.Trust.TrustAuthority("Turin");
                var port = 44000 + Environment.ProcessId % 1000;
                foreach (var (id, ams) in new[] { ("CAV-A", amsA!), ("CAV-B", amsB) })
                {
                    var trust = new AIF.Controller.CityTrust(turin, turin.Admit(id));
                    var discovery = new UdpDiscovery(port);
                    discoveries.Add(discovery);
                    var hub = ams.Api.StartExternal(CavInTheLoop.Ams, new ExternalHub.Options
                    {
                        ControllerId = id, ModuleType = "CAV-AMS-V2.0", Discovery = discovery, Admission = trust.Admission,
                        InRange = _ => Volatile.Read(ref distance) <= Range
                    }, trust.Sign, trust.Verify);
                    if (id == "CAV-B") hubB = hub;
                }
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (hubB!.Linked.Count == 0 && clock.ElapsedMilliseconds < 5000) await Task.Delay(20);
            }

            double brake = 0, minGap = double.PositiveInfinity, speedAtPullOut = double.NaN;
            double? knownAt = null, seenAt = null;
            IReadOnlyList<(string DataType, string Json)> responses = [];
            JsonNode? answer = null;
            var steps = 0;
            for (; steps < 400 && !sim.Collided; steps++)
            {
                var sensed = sim.Sense();
                var a = sim.Around().First(x => x.Id == "A");
                Volatile.Write(ref distance, Math.Abs(a.Ahead));
                amsA?.Step(JsonNode.Parse(TruthBed.OfVehicle(sim, sensed, "A"))!, [], null, sensed.FrameMs);

                var attitude = mas.Sense(sensed, responses);
                var toEss = sensed.Messages.Where(m => m.DataType is not MasTypes.SpatialData).Prepend((MasTypes.Attitude, attitude.ToJsonString())).ToList();
                var (bed, alerts) = ess.Step(new Simulation.Sensed(toEss, sensed.Truth, sensed.FrameMs));
                var time = sim.Time;
                var message = amsB.Step(bed!, alerts, answer, sensed.FrameMs,
                    data: d => { if (knownAt is null && d.Contains("\"CAV-A/stopped\"") && d.Contains("\"Remote\"")) knownAt = time; });
                List<(string DataType, string Json)> commands = [];
                if (message is not null) (answer, commands) = mas.Answer(message);

                if (double.IsNaN(speedAtPullOut) && sim.Time >= pullOut) speedAtPullOut = sim.EgoSpeed;
                var stopped = sim.Around().First(x => x.Id == "stopped");
                if (seenAt is null && stopped.Ahead < 90 && sim.Around().All(x => x.Id == "stopped" || x.Lane != 0 || x.Ahead > stopped.Ahead)) seenAt = sim.Time;
                sim.Actuate(commands);
                responses = sim.Advance();
                brake = Math.Max(brake, -sim.EgoAcceleration);
                if (sim.GapAhead() is { } g) minGap = Math.Min(minGap, g);
            }
            hardest[name] = brake;
            atPullOut[name] = speedAtPullOut;
            result[name] = sim.Collided ? "collision" : "no collision";
            report[name] = result[name] + $"; {speedAtPullOut:0.0} m/s when A pulls out; hardest braking {brake:0.0} m/s2; smallest gap {minGap:0.0} m; {steps} steps; B's camera could see the stopped vehicle at {seenAt:0.0} s" +
                           (exchange ? $"; B placed it from A's report at {(knownAt is { } k ? k.ToString("0.0") + " s" : "-")}; B received {hubB!.Received} Messages, refused {hubB.Refused}" : "");
            if (exchange)
            {
                result["the stopped vehicle, from A's report"] = knownAt is { } placed && seenAt is { } v && placed < v - 1 ? "placed before B's camera could see it" : "not placed before B's camera could see it";
                result["what B received"] = hubB!.Received > 0 && hubB.Refused == 0 ? "signed by A, verified, none refused" : $"{hubB.Received} received, {hubB.Refused} refused";
            }
            amsA?.Api.StopFlow(CavInTheLoop.Ams);
            amsB.Api.StopFlow(CavInTheLoop.Ams);
            foreach (var d in discoveries) await d.DisposeAsync();
        }
        result["with the exchange, against without"] = atPullOut["with the exchange"] < atPullOut["without the exchange"] - 1 ? "slower when A pulls out" : "no slower when A pulls out";
        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "remote-stage1-end-to-end.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Expected.Match("remote-stage1-end-to-end.json", result);
    }
}
