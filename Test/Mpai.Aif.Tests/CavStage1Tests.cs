using System.Text.Json;
using System.Text.Json.Nodes;

using Mpai.Cav.Ams;
using Mpai.Cav.Map;
using Mpai.Cav.Recordings;

namespace Mpai.Aif.Tests;

// PHASE 11 (M3243): the Connected Autonomous Vehicle, Stage 1.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
[Collection(Timing.Name)]
public class CavStage1Tests
{
    // A CAV standing at the start of the Route of Phase 8, on a free road: the AMS a
    // Module on perfect perception, the MAS a Module moving the vehicle; what HCI
    // would say written to the AMS.
    private sealed class Cav : IDisposable
    {
        public readonly Simulation Sim;
        private readonly MasInTheLoop mas;
        public readonly AmsInTheLoop Ams;
        private IReadOnlyList<(string DataType, string Json)> responses = [];
        private JsonNode? answer;
        public double Hardest { get; private set; }

        public Cav(string? destination = null)
        {
            var map = RoadMap.Grid(3);
            Sim = new Simulation(map, map.FastestRoute("W00", "W21")!, [], seed: 7);
            Sim.Mechanical(seed: 9);
            mas = new MasInTheLoop(Sim.Path.At(0).Heading * 180 / Math.PI);
            Ams = new AmsInTheLoop(Sim, destination);
        }

        public void Step()
        {
            var sensed = Sim.Sense(camera: false);
            mas.Sense(sensed, responses);
            var message = Ams.Step(JsonNode.Parse(TruthBed.Of(Sim, sensed))!, [], answer, sensed.FrameMs);
            List<(string DataType, string Json)> commands = [];
            if (message is not null) (answer, commands) = mas.Answer(message);
            Sim.Actuate(commands);
            responses = Sim.Advance();
            Hardest = Math.Max(Hardest, -Sim.EgoAcceleration);
        }

        // The last Route state the AMS told, and the last Route list.
        public string? Status => Ams.Told.LastOrDefault(t => t["AMSMessage"]?["RouteStatus"] is not null)?["AMSMessage"]!["RouteStatus"]!["Status"]!.GetValue<string>();
        public string? StatusRoute => Ams.Told.LastOrDefault(t => t["AMSMessage"]?["RouteStatus"] is not null)?["AMSMessage"]!["RouteStatus"]!["RouteID"]!.GetValue<string>();
        public JsonArray? Routes => Ams.Told.LastOrDefault(t => t["AMSMessage"]?["RouteList"] is not null)?["AMSMessage"]!["RouteList"]!.AsArray();

        // How far the CAV is from the nearest way point of the Route.
        public double FromWayPoint()
        {
            var ends = new List<double> { 0 };
            foreach (var s in Sim.Path.Segments) ends.Add(ends[^1] + Sim.Map.Length(s));
            return ends.Min(e => Math.Abs(Sim.EgoS - e));
        }

        public void Dispose()
        {
            Ams.Dispose();
            mas.Dispose();
        }
    }

    private static string Request(string wayPoint) => AmsStage1Tests.Destination(wayPoint, execute: false);

    private static string Command(string command, string? routeId = null)
    {
        var hci = new JsonObject { ["RouteCommand"] = command };
        if (routeId is not null) hci["SelectedRouteID"] = routeId;
        return new JsonObject { ["Header"] = AmsTypes.Hci, ["AMSHCIMessageID"] = "AHM-" + command, ["HCIMessage"] = hci }.ToJsonString();
    }

    // STEP 1 (M3243 3.1): THE AMS IN DIALOGUE WITH HCI. A place the map does not have:
    // no Route. A Destination: the Routes proposed - the one of least time first, an
    // alternative - the CAV standing; Execute: it drives the one selected; Suspend: it
    // halts at a way point; Resume: it drives on; Arrived told where it stands at the
    // Destination. Changed while driving: the new Routes proposed, the CAV driving on;
    // the new one executed and arrived at. Stop: it halts, and Resume does not move
    // it. Judged: each of these; no collision; every AMS-HCI Message valid. Reported:
    // the Routes and their times; where the CAV halted; the steps.
    [Fact]
    public void Step1AmsInDialogue()
    {
        var result = new Dictionary<string, string>();
        var report = new Dictionary<string, string>();
        var told = new List<JsonNode>();

        // The dialogue to arrival.
        using (var cav = new Cav())
        {
            var sim = cav.Sim;
            cav.Ams.Say(Request("W99"));
            for (var i = 0; i < 10; i++) cav.Step();
            var unknown = cav.Routes;
            result["a place the map does not have"] = unknown is { Count: 0 } && sim.EgoS < 0.5 ? "no Route proposed; the CAV stands" : $"{unknown?.Count.ToString() ?? "no"} Routes told; moved {sim.EgoS:0.0} m";

            cav.Ams.Say(Request("W21"));
            var steps = 0;
            while (cav.Routes is not { Count: > 0 } && steps++ < 20) cav.Step();
            var routes = cav.Routes!;
            for (var i = 0; i < 30; i++) cav.Step();
            var stood = sim.EgoS;
            var first = routes[0]!;
            var fastest = sim.Path.Segments.Select(s => s.To).ToList();
            var proposed = first["RouteSegments"]!.AsArray().Select(s => s!["WayPoint2ID"]!.GetValue<string>()).ToList();
            var timed = routes.All(r => r!["RouteSegments"]!.AsArray().All(s => s!["EstimatedArrDepSpaceTime"] is not null));
            result["the Routes proposed"] = routes.Count == 2 && proposed.SequenceEqual(fastest) && timed
                ? "the one of least time first, an alternative; each segment timed" : $"{routes.Count} Routes; first {string.Join(">", proposed)}; timed {timed}";
            result["before Execute"] = stood < 0.5 ? "the CAV stands" : $"moved {stood:0.0} m";
            report["the Routes proposed"] = string.Join("; ", routes.Select(r => $"{r!["RouteID"]} {string.Join(">", r["RouteSegments"]!.AsArray().Select(s => s!["WayPoint2ID"]))} " +
                $"{(AmsTypes.Ms(r["RouteSegments"]!.AsArray().Last()!["EstimatedArrDepSpaceTime"]) - AmsTypes.Ms(r["RouteTime"])) / 1000.0:0} s"));

            var id = first["RouteID"]!.GetValue<string>();
            cav.Ams.Say(Command("Execute", id));
            var executedAt = sim.Time;
            double? suspendAt = null, haltedAt = null, resumedAt = null, haltedFrom = null;
            string? whenSuspended = null, whenResumed = null;
            for (steps = 0; steps < 2500 && !sim.Collided && cav.Status != "Arrived"; steps++)
            {
                cav.Step();
                if (suspendAt is null && sim.Time >= executedAt + 20) { cav.Ams.Say(Command("Suspend")); suspendAt = sim.Time; }
                if (suspendAt is not null && haltedAt is null && sim.Time > suspendAt + 1 && sim.EgoSpeed < 0.2) { haltedAt = sim.Time; haltedFrom = cav.FromWayPoint(); whenSuspended = cav.Status; }
                if (haltedAt is not null && resumedAt is null && sim.Time >= haltedAt + 3) { cav.Ams.Say(Command("Resume")); resumedAt = sim.Time; }
                if (resumedAt is not null && whenResumed is null && sim.Time >= resumedAt + 3) whenResumed = $"{cav.Status}, {sim.EgoSpeed:0.0} m/s";
            }
            var toEnd = sim.Path.Length - sim.EgoS;
            result["Execute"] = cav.StatusRoute == id && executedAt < (suspendAt ?? double.MaxValue) ? "the Route selected driven" : $"{cav.StatusRoute} driven";
            result["Suspend"] = whenSuspended == "Suspended" && haltedFrom < 15 ? "Suspended; the CAV halts at a way point" : $"{whenSuspended}; halted {haltedFrom:0.0} m from a way point";
            result["Resume"] = whenResumed is { } w && w.StartsWith("Executing") && !w.StartsWith("Executing, 0.0") ? "Executing; the CAV drives on" : whenResumed ?? "not resumed";
            result["arrival"] = cav.Status == "Arrived" && toEnd < 15 && !sim.Collided ? "Arrived told; the CAV stands at the Destination; no collision" : $"{cav.Status}; {toEnd:0.0} m from the Destination; {(sim.Collided ? "collision" : "no collision")}";
            report["the dialogue"] = $"executed at {executedAt:0.0} s; Suspend at {suspendAt:0.0} s, halted at {haltedAt:0.0} s {haltedFrom:0.0} m from a way point; resumed at {resumedAt:0.0} s; " +
                                     $"{cav.Status} at {sim.Time:0.0} s {toEnd:0.0} m from the Destination; hardest braking {cav.Hardest:0.0} m/s2; {steps} steps";
            told.AddRange(cav.Ams.Told);
        }

        // Changed while driving: to the end of the Route's second segment.
        using (var cav = new Cav("W21"))
        {
            var sim = cav.Sim;
            var nearer = sim.Path.Segments[1].To;
            while (sim.Time < 8) cav.Step();
            cav.Ams.Say(Request(nearer));
            var before = cav.Ams.Told.Count;
            var steps = 0;
            while (cav.Ams.Told.Skip(before).All(t => t["AMSMessage"]?["RouteList"] is null) && steps++ < 20) cav.Step();
            var routes = cav.Routes!;
            var driving = sim.EgoSpeed;
            var id = routes[0]!["RouteID"]!.GetValue<string>();
            cav.Ams.Say(Command("Execute", id));
            for (steps = 0; steps < 1500 && !sim.Collided && cav.Status != "Arrived"; steps++) cav.Step();
            var end = sim.Map.Length(sim.Path.Segments[0]) + sim.Map.Length(sim.Path.Segments[1]);
            result["a Destination changed while driving"] = driving > 5 && cav.StatusRoute == id && cav.Status == "Arrived" && Math.Abs(sim.EgoS - end) < 15
                ? "the new Routes proposed, the CAV driving on; the one selected driven to arrival" : $"{driving:0.0} m/s when proposed; {cav.Status} on {cav.StatusRoute}; {sim.EgoS - end:0.0} m from it";
            report["a Destination changed while driving"] = $"{nearer} requested at 8.0 s at {driving:0.0} m/s; {cav.Status} at {sim.Time:0.0} s, {sim.EgoS - end:0.0} m from it";
            told.AddRange(cav.Ams.Told);
        }

        // Stop.
        using (var cav = new Cav("W21"))
        {
            var sim = cav.Sim;
            while (sim.Time < 12) cav.Step();
            cav.Ams.Say(Command("Stop"));
            var steps = 0;
            while ((sim.EgoSpeed > 0.1 || sim.Time < 14) && steps++ < 600) cav.Step();
            var halted = (cav.Status, From: cav.FromWayPoint(), At: sim.EgoS, Time: sim.Time);
            cav.Ams.Say(Command("Resume"));
            for (var i = 0; i < 50; i++) cav.Step();
            result["Stop"] = halted.Status == "Stopped" && halted.From < 15 && sim.EgoS - halted.At < 0.5 && cav.Status == "Stopped"
                ? "Stopped; the CAV halts at a way point; Resume does not move it" : $"{halted.Status}; halted {halted.From:0.0} m from a way point; moved {sim.EgoS - halted.At:0.0} m after Resume; {cav.Status}";
            report["Stop"] = $"Stop at 12.0 s; halted at {halted.Time:0.0} s, {halted.From:0.0} m from a way point";
            told.AddRange(cav.Ams.Told);
        }

        var schemas = AIF.Metadata.PublishedSchemas.At(Repository.Schemas);
        var schema = schemas[Path.GetFullPath(Path.Combine(Repository.Schemas, "CAV2", "V2.0", "data", "AMSHCIMessage.json"))];
        var valid = told.Count(t => { using var doc = JsonDocument.Parse(t.ToJsonString()); lock (AIF.Metadata.PublishedSchemas.Lock) return schema.Evaluate(doc.RootElement).IsValid; });
        result["every AMS-HCI Message against its schema"] = valid == told.Count ? "valid" : $"{valid} of {told.Count} valid";
        report["AMS-HCI Messages"] = $"{told.Count}";
        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "cav-stage1-ams-dialogue.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Expected.Match("cav-stage1-ams-dialogue.json", result);
    }
}
