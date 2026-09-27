using System.Text.Json;
using System.Text.Json.Nodes;

using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Cav.Ams;
using Mpai.Cav.Ess;
using Mpai.Cav.Map;
using Mpai.Cav.Mas;
using Mpai.Cav.Recordings;
using Mpai.Rca;
using Mpai.Wdl;

namespace Mpai.Aif.Tests;

// PHASE 9 (M3237): the Motion Actuation Subsystem, Stage 1. Step 4 runs a workflow
// against the clock (deliver): with the tests that time.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
[Collection(Timing.Name)]
public class MasStage1Tests
{
    private static readonly RoadMap Map = RoadMap.Grid(3);

    // A run: the vehicle on a Route, the same commands at every step, until stopped
    // or for a time. Every command, Response and Spatial Data kept, to be validated.
    private sealed record Run(Simulation Sim, List<(string DataType, string Json)> Objects, double Start, int Steps);

    private static Run Drive(string to, double speed, double seconds, Func<Simulation, long, int, IEnumerable<(string, string)>> commands,
                             double friction = Simulation.DryFriction, bool untilStopped = false)
    {
        var route = Map.FastestRoute("W00", to)!;
        var sim = new Simulation(Map, route, [], seed: 9, egoSpeed: speed)
        {
            Surface = friction == Simulation.DryFriction ? [] : [(0, 10_000, friction)]
        };
        sim.Mechanical(seed: 9);
        var objects = new List<(string, string)>();
        var steps = 0;
        while (steps < seconds / Simulation.Step && !(untilStopped && steps > 0 && sim.EgoSpeed <= 0))
        {
            var ms = (sim.Start + TimeSpan.FromSeconds(sim.Time)).ToUnixTimeMilliseconds();
            var given = commands(sim, ms, steps).ToList();
            objects.AddRange(given);
            sim.Actuate(given);
            objects.AddRange(sim.Sense(camera: false).Messages.Where(m => m.DataType == "CAV-SPD-V2.0"));
            objects.AddRange(sim.Advance());
            steps++;
        }
        return new Run(sim, objects, 0, steps);
    }

    // STEP 1 (M3237 3.1): the mechanical subsystems and the vehicle, open loop.
    [Fact]
    public void Step1MechanicsOpenLoop()
    {
        var result = new Dictionary<string, string>();
        var everything = new List<(string DataType, string Json)>();
        const double g = Vehicle.G;

        // Braking from 20 m/s at more than the road allows: ABS holds the tyres near
        // their peak. With the rolling resistance and the drag, a deceleration
        // a0 + k v^2 stops in ln(1 + k v^2 / a0) / 2k; the brakes' lag adds v tau.
        foreach (var (name, friction, abs) in new[] { ("dry, ABS", Simulation.DryFriction, true), ("icy, ABS", 0.15, true), ("icy, no ABS", 0.15, false) })
        {
            var sawAbs = false; var sawLock = false;
            var run = Drive("W20", 20, 60, (sim, ms, k) =>
            {
                sawAbs |= sim.Mechanics!.AbsActive; sawLock |= sim.Mechanics.WheelLocked;
                return [("CAV-BRC-V2.0", MasTypes.Brake($"BRC{k}", ms, 9, abs: abs))];
            }, friction, untilStopped: true);
            var deceleration = Math.Min(Vehicle.MaxBrakeDeceleration, (abs ? Vehicle.AbsGrip : Vehicle.LockedGrip) * friction * g);
            var a0 = deceleration + Vehicle.RollingResistance * g;
            var k = Vehicle.AirDensity * Vehicle.DragArea / (2 * Vehicle.Mass);
            var expected = Math.Log(1 + k * 20 * 20 / a0) / (2 * k) + 20 * Vehicle.BrakeLag;
            var distance = run.Sim.Mechanics!.Distance;
            everything.AddRange(run.Objects);
            result[$"braking from 20 m/s, {name}"] =
                $"stopped in {Near(distance, expected, 0.05)} of {expected:0} m; {(abs ? (sawAbs ? "ABS active" : "ABS never active") : (sawLock ? "wheels locked" : "wheels never locked"))}; offset {Math.Abs(run.Sim.EgoOffset):0.00} m";
        }

        // Full torque from standstill: never more than the tyres or the motor's power
        // allow, at the speed of the step's start (the speed changes within a step);
        // the speed reached in 10 s.
        {
            double worst = 0, v0 = 0;
            var run = Drive("W20", 0, 10, (sim, ms, k) =>
            {
                var v = sim.Mechanics!.Speed; var a = sim.Mechanics.Acceleration;
                if (k > 0) worst = Math.Max(worst, a - Math.Min(Simulation.DryFriction * g, Vehicle.MaxPower / (Vehicle.Mass * Math.Max(v0, 1))));
                v0 = v;
                return [("CAV-MRC-V2.0", MasTypes.Motor($"MRC{k}", ms, "torque", Vehicle.MaxMotorTorque))];
            });
            everything.AddRange(run.Objects);
            result["full torque from standstill, 10 s"] =
                $"{(worst <= 0.01 ? "within grip and power" : $"beyond them by {worst:0.00} m/s2")}; speed reached {Bucket(run.Sim.Mechanics!.Speed, 2)} m/s";
        }

        // A steady turn: at 10 m/s, the road wheels at 5 degrees, the heading turns at
        // v tan(delta) / L once the steering has reached its angle.
        {
            double h0 = 0;
            var run = Drive("W20", 10, 6, (sim, ms, k) =>
            {
                if (k == 10) h0 = sim.Mechanics!.Heading;
                return [("CAV-MRC-V2.0", MasTypes.Motor($"MRC{k}", ms, "velocity", 10)), ("CAV-WHC-V2.0", MasTypes.Wheel($"WHC{k}", ms, 5))];
            });
            var rate = (run.Sim.Mechanics!.Heading - h0) / ((run.Steps - 10) * Simulation.Step);
            var expected = 10 * Math.Tan(5 * Math.PI / 180) / Vehicle.Wheelbase;
            everything.AddRange(run.Objects);
            result["a steady turn, 10 m/s, 5 degrees, dry"] = $"yaw rate {Near(rate, expected, 0.05)} of {expected:0.000} rad/s";
        }

        // Too sharp a turn on ice: the tyres cannot give it; the vehicle slides, and
        // turns only as far as the grip lets it.
        {
            var skid = false;
            var run = Drive("W20", 15, 3, (sim, ms, k) =>
            {
                skid |= sim.Mechanics!.Skidding;
                return [("CAV-MRC-V2.0", MasTypes.Motor($"MRC{k}", ms, "velocity", 15)), ("CAV-WHC-V2.0", MasTypes.Wheel($"WHC{k}", ms, 20))];
            }, friction: 0.15);
            everything.AddRange(run.Objects);
            result["too sharp a turn on ice, 15 m/s, 20 degrees"] = skid ? "the vehicle slides" : "no slide";
        }

        // The sensors and the Objects.
        {
            // Spatial Data is read before the step moves the vehicle: compare it with
            // the distance travelled at that moment.
            double atReading = 0;
            var run = Drive("W20", 15, 10, (sim, ms, k) =>
            {
                atReading = sim.Mechanics!.Distance;
                return [("CAV-MRC-V2.0", MasTypes.Motor($"MRC{k}", ms, "velocity", 15))];
            });
            var last = JsonNode.Parse(run.Objects.Last(o => o.DataType == "CAV-SPD-V2.0").Json)!["SpatialData"]!;
            var odometer = (double)last["OdometerData"]!;
            everything.AddRange(run.Objects);
            result["the odometer against the distance travelled"] = $"{Bucket((odometer / atReading - 1) * 100, 0.5)} % long";
        }

        foreach (var (key, value) in Validity(everything)) result[key] = value;
        Expected.Match("mas-stage1-mechanics.json", result);
    }

    // ---- Step 4 (M3237 3.4, 3.2): AMS-MAS Message Interpretation, and deliver --------

    // An AMS-MAS Message whose Trajectory starts at (east, north) heading east at v0
    // m/s, accelerating at a m/s2 (never below a standstill): 6 s of points 0.1 s
    // apart, as Motion Selection Planning gives them.
    public static JsonNode Straight(long ms, double east, double north, double v0, double a, string command = "Execute")
    {
        var points = new JsonArray();
        double v = v0, x = east;
        for (var k = 0; k <= 60; k++)
        {
            var t = ms + k * 100;
            var point = EssJson.Attitude($"T-{k}", t, (x, north, 0), (0.5, 0.5, 0.1), (v, 0, 0));
            point["Position"]!["CartAccel"] = EssJson.Triple((v > 0 || a > 0 ? a : 0, 0, 0));
            points.Add(new JsonObject { ["ExpectedSpaceTime"] = EssJson.SpaceTime($"T-{k}-ST", t, point) });
            var next = Math.Max(0, v + a * 0.1);
            x += (v + next) / 2 * 0.1;
            v = next;
        }
        return new JsonObject
        {
            ["Header"] = MasTypes.Message, ["AMSMASMessageID"] = $"AMM{ms}", ["AMSMASMessageTime"] = EssJson.SimpleTime($"AMM{ms}-T", ms),
            ["AMSMessage"] = new JsonObject
            {
                ["Trajectory"] = new JsonObject { ["Header"] = "OSD-TRJ-V1.5", ["TrajectoryID"] = $"TRJ{ms}", ["TrajectoryTime"] = EssJson.SimpleTime($"TRJ{ms}-T", ms), ["Trajectory"] = points },
                ["Command"] = command
            }
        };
    }

    // The MAS's Spatial Attitude of a CAV at (east, north), its heading (degrees), its speed.
    public static JsonNode Pose(long ms, double east, double north, double heading, double speed) =>
        EssJson.Attitude($"P{ms}", ms, (east, north, 0), (0.1, 0.1, 0.1), (speed * Math.Cos(heading * Math.PI / 180), speed * Math.Sin(heading * Math.PI / 180), 0),
            new JsonObject { ["Header"] = "OSD-OOR-V1.5", ["OrientationID"] = $"P{ms}-O", ["Orientation"] = new JsonArray(0.0, 0.0, heading) });

    // What one set of commands says: each device, what it is told.
    private static string Told(IReadOnlyList<(string DataType, string Json)> commands) => commands.Count == 0 ? "nothing" : string.Join("; ", commands.Select(c =>
    {
        var j = JsonNode.Parse(c.Json)!;
        return c.DataType switch
        {
            MasTypes.MotorCommand => $"motor {(string)j["MotorCommand"]!["ControlMode"]!} {Bucket((double?)j["MotorCommand"]!["TargetAcceleration"] ?? (double?)j["MotorCommand"]!["TargetTorque"] ?? 0, 0.5)}",
            MasTypes.BrakeCommand => (bool)j["BrakeCommand"]![0]!["EmergencyBrakeFlag"]! ? "emergency brake"
                                     : $"brake {Bucket((double)j["BrakeCommand"]![0]!["DecelerationTarget"]!, 0.5)} m/s2",
            _ => (double)j["WheelCommand"]!["Angle"]! is var d && Math.Abs(d) < 0.5 ? "wheels straight" : d > 0 ? "wheels left" : "wheels right"
        };
    }));

    // AMI on its own: what each situation makes it command.
    [Fact]
    public void Step4Interpretation()
    {
        var result = new Dictionary<string, string>();
        var all = new List<(string, string)>();
        const long t0 = 1_767_254_400_000;
        var ami = new AmsMasMessageInterpretation(MasProvider.Ami, new Dictionary<string, string>());
        string At(JsonNode pose) { var c = ami.Commands(pose); all.AddRange(c); return Told(c); }

        result["before any AMS-MAS Message"] = At(Pose(t0, 0, 0, 0, 10));
        ami.Accept(Straight(t0, 0, 0, 10, 0));
        result["on the Trajectory, at its speed"] = At(Pose(t0, 0, 0, 0, 10));
        result["1 m to the right of it"] = At(Pose(t0 + 100, 1, -1, 0, 10));
        result["1 m to the left of it"] = At(Pose(t0 + 200, 2, 1, 0, 10));
        result["2 m/s slower than it"] = At(Pose(t0 + 300, 3, 0, 0, 8));
        ami.Accept(Straight(t0 + 400, 4, 0, 10, -3));
        result["it brakes at 3 m/s2"] = At(Pose(t0 + 400, 4, 0, 0, 10));
        ami.Accept(Straight(t0 + 500, 5, 0, 10, -7));
        result["it brakes at 7 m/s2"] = At(Pose(t0 + 500, 5, 0, 0, 10));
        ami.Accept(Straight(t0 + 600, 6, 0, 10, 0));
        ami.Accept(Straight(t0 + 600, 6, 0, 10, 0, "Suspend"));
        result["Suspend"] = At(Pose(t0 + 600, 6, 0, 0, 10));
        ami.Accept(Straight(t0 + 700, 7, 0, 10, 0, "Resume"));
        result["Resume"] = At(Pose(t0 + 700, 7, 0, 0, 10));
        ami.Accept(Straight(t0 + 800, 8, 0, 0, 0));
        result["stopped, to stay stopped"] = At(Pose(t0 + 800, 8, 0, 0, 0));
        result["an AMS-MAS Message with no AMS Message"] = ami.Accept(new JsonObject { ["Header"] = MasTypes.Message, ["AMSMASMessageID"] = "AMM-X" }) ?? "accepted";
        result["Execute with no Trajectory"] = ami.Accept(new JsonObject
        {
            ["Header"] = MasTypes.Message, ["AMSMASMessageID"] = "AMM-Y", ["AMSMessage"] = new JsonObject { ["Command"] = "Execute" }
        }) ?? "accepted";
        foreach (var (key, value) in Validity(all)) result[key] = value;
        Expected.Match("mas-stage1-ami.json", result);
    }

    // THE LOOP CLOSED THROUGH THE MECHANICAL SUBSYSTEMS (M3237 3.8): the scenarios of
    // Phase 8, and the first of them on an icy stretch the AMS does not know of (ICA:
    // Step 6), on perfect perception - the AMS of Phase 8 in-process, its AMS-MAS
    // Message interpreted by AMI, whose commands move the vehicle. The Spatial
    // Attitude AMI follows the Trajectory from is the truth, on the map's frame, until
    // MSA gives it (Step 5). Judged: no collision; the Destination reached; within the
    // speed limit; in its lane on the straights - not within 25 m of a corner of the
    // Route; not sliding. Reported: how far from the lane's centre, on the straights
    // and at the corners; the speed against the Trajectory's; the hardest braking; the
    // Emergency Brake Commands; the steps.
    public static IReadOnlyDictionary<string, Func<Simulation>> Scenarios()
    {
        var scenarios = new Dictionary<string, Func<Simulation>>(AmsStage1Tests.Scenarios());
        var map = RoadMap.Grid(3);
        var route = map.FastestRoute("W00", "W21")!;
        scenarios["a vehicle ahead slows and stops, on ice"] = () => new Simulation(map, route,
            [new ScenarioVehicle("ahead", 0, 45, [(0, 13.0), (8, 6.0), (14, 0.0)], CameraRenderer.Silver)], seed: 7, egoSpeed: 12) { Surface = [(100, 300, 0.15)] };
        return scenarios;
    }

    // ONE STEP OF THE LOOP: what was sensed, the AMS-MAS Message, the commands, the
    // Responses to them.
    private sealed record Stepped(Simulation.Sensed Sensed, JsonNode Message, IReadOnlyList<(string DataType, string Json)> Commands,
                                  IReadOnlyList<(string DataType, string Json)> Responses,
                                  JsonNode? Attitude = null, JsonNode? Road = null, JsonNode? Answer = null);

    // The loop of Step 4 on a scenario, moved by its mechanics, until the CAV arrives,
    // collides or 90 s have gone; each step given to see. With the MAS (Step 6): AMI
    // follows from MSA's Spatial Attitude, on the MAS's frame; ICA reads the tyres and
    // the weather; MRA answers each AMS-MAS Message, and TOA hears the answer at the
    // next step - the Road State, and the MAS's frame.
    private static Simulation Loop(Simulation sim, Action<Stepped> each, bool mas = false)
    {
        sim.Mechanical(seed: 9);
        var fed = new FullEnvironmentDescription(AmsProvider.Fed);
        fed.Know(JsonNode.Parse(sim.Map.ToOfflineMapObject(0))!);
        var msp = new MotionSelectionPlanning(AmsProvider.Msp, new Dictionary<string, string>());
        msp.Follow(AmsStage1Tests.PathOf(sim));
        var toa = new TrafficObstacleAvoidance(AmsProvider.Toa, new Dictionary<string, string>());
        var ami = new AmsMasMessageInterpretation(MasProvider.Ami, new Dictionary<string, string>());
        var msa = new MasSpatialAttitudeGeneration(MasProvider.Msa, new Dictionary<string, string>
        {
            ["InitialHeading"] = (sim.Path.At(0).Heading * 180 / Math.PI).ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
        var ica = new IceConditionAnalysis(MasProvider.Ica, new Dictionary<string, string>());
        var mra = new MasResponseAnalysis(MasProvider.Mra);
        IReadOnlyList<(string DataType, string Json)> responses = [];
        for (var steps = 0; steps < 900 && !sim.Arrived && !sim.Collided; steps++)
        {
            var sensed = sim.Sense(camera: false);
            var bed = JsonNode.Parse(TruthBed.Of(sim, sensed))!;
            JsonNode attitude = bed["EgoSpatialAttitude"]!;
            JsonNode? road = null;
            if (mas)
            {
                foreach (var (dataType, json) in responses)
                {
                    var response = JsonNode.Parse(json)!;
                    if (dataType == MasTypes.WheelResponse) msa.Steered(response);
                    ica.Responded(dataType, response);
                }
                foreach (var (_, json) in sensed.Messages.Where(m => m.DataType == MasTypes.Weather)) ica.Weather(JsonNode.Parse(json)!);
                attitude = msa.Attitude(JsonNode.Parse(sensed.Messages.First(m => m.DataType == MasTypes.SpatialData).Json)!)!;
                road = ica.State(attitude);
                mra.Observe(attitude);
                mra.Road(road);
            }
            var described = fed.Describe(bed);
            toa.Observe(described);
            var message = toa.Refine(msp.Plan(described));
            ami.Accept(message);
            var commands = ami.Commands(attitude);
            JsonNode? answer = null;
            if (mas)
            {
                answer = mra.Answer(message);
                toa.Answered(answer!);
            }
            sim.Actuate(commands);
            responses = sim.Advance();
            each(new Stepped(sensed, message, commands, responses, mas ? attitude : null, road, answer));
        }
        return sim;
    }

    [Fact]
    public void Step4ClosedLoop()
    {
        var result = new Dictionary<string, string>();
        var report = new Dictionary<string, string>();
        var everything = new List<(string DataType, string Json)>();
        foreach (var (name, make) in Scenarios())
        {
            var sim = make();
            var corners = Corners(sim);
            double straight = 0, corner = 0, overLimit = 0, hardest = 0, sumSquares = 0, worstSpeed = 0;
            int steps = 0, emergencies = 0;
            var skidded = false;
            Loop(sim, s =>
            {
                steps++;
                var ego = s.Sensed.Truth["Ego"]!;
                overLimit = Math.Max(overLimit, (double)ego["Speed"]! - (double)ego["SpeedLimit"]!);
                emergencies += s.Commands.Count(c => c.DataType == MasTypes.BrakeCommand && c.Json.Contains("\"EmergencyBrakeFlag\":true"));
                everything.AddRange(s.Commands);
                everything.AddRange(s.Responses);
                var error = sim.EgoSpeed - TrafficObstacleAvoidance.Speed(s.Message["AMSMessage"]!["Trajectory"]!["Trajectory"]![1]!);
                sumSquares += error * error;
                worstSpeed = Math.Max(worstSpeed, Math.Abs(error));
                hardest = Math.Max(hardest, -sim.EgoAcceleration);
                skidded |= sim.Mechanics!.Skidding;
                if (corners.Any(c => Math.Abs(sim.EgoS - c) < 25)) corner = Math.Max(corner, Math.Abs(sim.EgoOffset));
                else straight = Math.Max(straight, Math.Abs(sim.EgoOffset));
            });
            var inLane = straight <= (RoadMap.LaneWidth - 1.8) / 2;
            result[name] = $"{(sim.Collided ? "collision" : "no collision")}; {(sim.Arrived ? "Destination reached" : "Destination not reached")}; " +
                           $"{(overLimit <= 0.5 ? "within the speed limit" : "above the speed limit")}; " +
                           $"{(inLane ? "in its lane on the straights" : "out of its lane on a straight")}; {(skidded ? "slid" : "did not slide")}";
            report[name] = result[name] + $"; off the lane's centre at most {straight:0.00} m on the straights, {corner:0.00} m at the corners; " +
                           $"speed against the Trajectory's RMS {Math.Sqrt(sumSquares / Math.Max(1, steps)):0.00} m/s, at most {worstSpeed:0.00} m/s; " +
                           $"hardest braking {hardest:0.0} m/s2; {emergencies} Emergency Brake Command(s); {steps} steps";
        }
        foreach (var (key, value) in Validity(everything)) result[key] = value;
        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "mas-stage1-closed-loop.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Expected.Match("mas-stage1-closed-loop.json", result);
    }

    // STEP 6 (M3237 3.6, 3.7): the loop closed through the whole MAS - AMI following
    // from MSA's Spatial Attitude, the Trajectory on the MAS's frame; ICA's Road State
    // and MRA's answer heard by TOA - on the scenarios of Step 4, and the one on ice in
    // freezing snow, which the weather sensors report. Judged as Step 4, and on ice:
    // whether ICA found it, and how. Reported as Step 4, and: where ICA first said the
    // road was icy, the lowest friction it estimated, where it said so on a dry road;
    // how far MSA's Spatial Attitude was from the truth.
    [Fact]
    public void Step6ClosedLoop()
    {
        var result = new Dictionary<string, string>();
        var report = new Dictionary<string, string>();
        var everything = new List<(string DataType, string Json)>();
        var scenarios = new Dictionary<string, Func<Simulation>>(Scenarios());
        var map = RoadMap.Grid(3);
        var route = map.FastestRoute("W00", "W21")!;
        scenarios["a vehicle ahead slows and stops, on ice, in freezing snow"] = () => new Simulation(map, route,
            [new ScenarioVehicle("ahead", 0, 45, [(0, 13.0), (8, 6.0), (14, 0.0)], CameraRenderer.Silver)], seed: 7, egoSpeed: 12)
            { Surface = [(100, 300, 0.15)], Weather = (-3, 2) };
        foreach (var (name, make) in scenarios)
        {
            var sim = make();
            var corners = Corners(sim);
            double straight = 0, corner = 0, overLimit = 0, hardest = 0, lowest = 1, msaError = 0;
            double? iceAt = null, falseIceAt = null;
            int steps = 0, emergencies = 0;
            var skidded = false;
            string? how = null;
            Loop(sim, s =>
            {
                steps++;
                var ego = s.Sensed.Truth["Ego"]!;
                overLimit = Math.Max(overLimit, (double)ego["Speed"]! - (double)ego["SpeedLimit"]!);
                emergencies += s.Commands.Count(c => c.DataType == MasTypes.BrakeCommand && c.Json.Contains("\"EmergencyBrakeFlag\":true"));
                everything.AddRange(s.Commands);
                everything.AddRange(s.Responses);
                everything.AddRange(s.Sensed.Messages.Where(m => m.DataType == MasTypes.Weather));
                everything.Add((MasTypes.RoadState, s.Road!.ToJsonString()));
                everything.Add((MasTypes.Message, s.Answer!.ToJsonString()));
                var surface = s.Road!["SurfaceCondition"]!;
                lowest = Math.Min(lowest, (double)surface["FrictionCoefficientEstimate"]!);
                if ((bool)surface["IcePresence"]!)
                {
                    if ((double)ego["Friction"]! < 0.3) { iceAt ??= (double)ego["S"]!; how ??= (string?)s.Road["DescrMetadata"]; }
                    else falseIceAt ??= (double)ego["S"]!;
                }
                var pose = MasTypes.Pose(s.Attitude!);
                msaError = Math.Max(msaError, Math.Sqrt(Math.Pow(pose.East - (double)ego["East"]!, 2) + Math.Pow(pose.North - (double)ego["North"]!, 2)));
                hardest = Math.Max(hardest, -sim.EgoAcceleration);
                skidded |= sim.Mechanics!.Skidding;
                if (corners.Any(c => Math.Abs(sim.EgoS - c) < 25)) corner = Math.Max(corner, Math.Abs(sim.EgoOffset));
                else straight = Math.Max(straight, Math.Abs(sim.EgoOffset));
            }, mas: true);
            var inLane = straight <= (RoadMap.LaneWidth - 1.8) / 2;
            var icy = sim.Surface.Count > 0;
            result[name] = $"{(sim.Collided ? "collision" : "no collision")}; {(sim.Arrived ? "Destination reached" : "Destination not reached")}; " +
                           $"{(overLimit <= 0.5 ? "within the speed limit" : "above the speed limit")}; " +
                           $"{(inLane ? "in its lane on the straights" : "out of its lane on a straight")}; {(skidded ? "slid" : "did not slide")}" +
                           (icy ? $"; {(iceAt is null ? "the ice not found" : how!.Contains("Weather") ? "the ice expected from the weather" : "the ice found from the tyres")}" : "");
            report[name] = result[name] + $"; off the lane's centre at most {straight:0.00} m on the straights, {corner:0.00} m at the corners; " +
                           $"hardest braking {hardest:0.0} m/s2; {emergencies} Emergency Brake Command(s); {steps} steps; " +
                           $"lowest friction estimated {lowest:0.00}" + (iceAt is { } at ? $", ice first said at {at:0} m (the ice from {sim.Surface[0].From:0} m)" : "") +
                           (falseIceAt is { } f ? $", ice said on a dry road at {f:0} m" : "") +
                           $"; MSA at most {msaError:0.0} m from the truth; corners at {string.Join(", ", corners.Select(c => c.ToString("0")))} m";
        }
        foreach (var (key, value) in Validity(everything)) result[key] = value;
        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "mas-stage1-mas.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Expected.Match("mas-stage1-mas.json", result);
    }

    // Where the Route turns: the distance along it of each way point between segments.
    private static List<double> Corners(Simulation sim) =>
        Enumerable.Range(1, sim.Path.Segments.Count - 1).Select(i => sim.Path.Segments.Take(i).Sum(sim.Map.Length)).ToList();

    // STEP 5 (M3237 3.5): MSA - the Spatial Attitude of the MAS - on the drives of Step
    // 4, from the Spatial Data and the Wheel Responses of each, its heading at Start
    // the Route's; and the ESS's Spatial Attitude Generation fed by it and by GNSS, as
    // the ESS now is. Each against the truth. Judged: MSA's error within the accuracy
    // it states, at every Spatial Data; SAG's within twice its. Reported: the errors,
    // MSA's heading error, the distance run.
    [Fact]
    public async Task Step5SpatialAttitude()
    {
        const double earth = 6_371_000;
        var result = new Dictionary<string, string>();
        var report = new Dictionary<string, string>();
        var outputs = new List<(string DataType, string Json)>();
        foreach (var name in new[] { "a free road to the Destination", "a slower vehicle cuts in", "a vehicle ahead slows and stops, on ice" })
        {
            var sim = Scenarios()[name]();
            var drive = new List<(TimeSpan At, string DataType, int PortNumber, string Json)>();
            var gnss = new List<(TimeSpan At, string DataType, int PortNumber, string Json)>();
            var truth = new Dictionary<TimeSpan, (double East, double North, double Heading)>();
            TimeSpan At(long ms) => TimeSpan.FromMilliseconds(ms - sim.Start.ToUnixTimeMilliseconds());
            Loop(sim, s =>
            {
                var at = At(s.Sensed.FrameMs);
                var ego = s.Sensed.Truth["Ego"]!;
                truth[at] = ((double)ego["East"]!, (double)ego["North"]!, (double)ego["Heading"]!);
                foreach (var (dataType, json) in s.Sensed.Messages)
                    if (dataType == MasTypes.SpatialData) drive.Add((at, dataType, 1, json));
                    else if (dataType == SpatialAttitudeGeneration.Gnss) gnss.Add((at + TimeSpan.FromTicks(1), dataType, 1, json));
                // The Responses of this step answer at the next: before its Spatial Data.
                foreach (var (dataType, json) in s.Responses.Where(r => r.DataType == MasTypes.WheelResponse))
                    drive.Add((At(MasTypes.Ms(JsonNode.Parse(json)!["WheelResponseTime"])), dataType, 1, json));
            });

            // MSA on the drive.
            var start = sim.Path.At(0).Heading * 180 / Math.PI;
            var msaPorts = new DrivePorts(drive);
            await new MasSpatialAttitudeGeneration(MasProvider.Msa, new Dictionary<string, string> { ["InitialHeading"] = start.ToString(System.Globalization.CultureInfo.InvariantCulture) })
                .RunAsync(msaPorts, AimContext.None);
            outputs.AddRange(msaPorts.Written.Select(w => (w.DataType, w.Json)));
            var msa = new List<double>(); var headings = new List<double>(); var covered = 0; var run = 0.0;
            foreach (var (at, _, _, json) in msaPorts.Written)
            {
                var a = JsonNode.Parse(json)!;
                var pose = MasTypes.Pose(a);
                var t = truth[at];
                var error = Math.Sqrt(Math.Pow(pose.East - t.East, 2) + Math.Pow(pose.North - t.North, 2));
                msa.Add(error);
                headings.Add(Math.Abs(Math.IEEERemainder(pose.Heading * 180 / Math.PI - t.Heading, 360)));
                if (error <= (double)a["Position"]!["CartPositionAccuracy"]![0]!) covered++;
            }
            run = sim.Mechanics!.Distance;

            // SAG on MSA's Spatial Attitudes and the GNSS fixes: its frame anchored at
            // the first fix, so the truth is moved there.
            var sagPorts = new DrivePorts(msaPorts.Written.Concat(gnss));
            await new SpatialAttitudeGeneration(EssProvider.Sag, new Dictionary<string, string> { ["GnssWeight"] = "0.1" }).RunAsync(sagPorts, AimContext.None);
            var (lat0, lon0) = SpatialAttitudeGeneration.Position(JsonNode.Parse(gnss[0].Json)!)!.Value;
            var anchor = ((lon0 - sim.Map.OriginLon) * Math.PI / 180 * earth * Math.Cos(lat0 * Math.PI / 180), (lat0 - sim.Map.OriginLat) * Math.PI / 180 * earth);
            var sag = new List<double>(); var sagCovered = 0;
            foreach (var (at, _, _, json) in sagPorts.Written)
            {
                if (!truth.TryGetValue(at, out var t)) continue;
                var p = JsonNode.Parse(json)!["Position"]!;
                var c = p["CartPosition"]!.AsArray();
                var error = Math.Sqrt(Math.Pow((double)c[0]! - (t.East - anchor.Item1), 2) + Math.Pow((double)c[1]! - (t.North - anchor.Item2), 2));
                sag.Add(error);
                if (error <= 2 * (double)p["CartPositionAccuracy"]![0]!) sagCovered++;
            }
            string Stats(List<double> e, string unit) { var s = e.Order().ToList(); return $"median {s[s.Count / 2]:0.00} {unit}, 95th percentile {s[(int)(s.Count * 0.95)]:0.00} {unit}, max {s[^1]:0.00} {unit}"; }
            result[name] = $"MSA within its stated accuracy at {(covered == msa.Count ? "every" : $"{covered} of {msa.Count}")} Spatial Data; " +
                           $"SAG within twice its stated accuracy at {(sagCovered == sag.Count ? "every" : $"{sagCovered} of {sag.Count}")} Spatial Attitude";
            report[name] = result[name] + $"; {run:0} m run; MSA position error {Stats(msa, "m")}, at the end {msa[^1]:0.00} m; " +
                           $"MSA heading error {Stats(headings, "degrees")}; SAG position error {Stats(sag, "m")}";
        }
        var schema = AIF.Metadata.PublishedSchemas.At(Repository.Schemas)[Path.GetFullPath(Path.Combine(Repository.Schemas, "OSD", "V1.5", "data", "SpatialAttitude.json"))];
        var valid = outputs.Count(o => { using var doc = JsonDocument.Parse(o.Json); lock (AIF.Metadata.PublishedSchemas.Lock) return schema.Evaluate(doc.RootElement).IsValid; });
        result["OSD-OSA-V1.5 of MSA against its schema"] = valid == outputs.Count ? "every one valid" : $"{valid} of {outputs.Count}";
        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "mas-stage1-msa.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Expected.Match("mas-stage1-msa.json", result);
    }

    // DELIVER (M3237 3.2), on the Module of Phase 6 that echoes its inputs: a device
    // produces five data, streamed to the Module; what the Module gives is delivered
    // back to it. The interlock: the device brought to its safe state once, when the
    // workflow ends, and at once on Stop. A device the User Agent does not know stops
    // the workflow before anything is bound; a deliver not in its form is refused.
    private sealed class EchoDevice : IDevice
    {
        public readonly List<string> Delivered = [];
        public readonly List<long> SafeStops = [];
        public readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

        public Task DeliverAsync(string dataType, string json, CancellationToken cancel)
        {
            lock (Delivered) Delivered.Add(json);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<(string DataType, string Json)> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancel)
        {
            for (var i = 1; i <= 5; i++)
            {
                await Task.Delay(50, cancel);
                yield return (StorageTests.Text, $"d{i}");
            }
            await Task.Delay(-1, cancel);
        }

        public Task SafeStopAsync()
        {
            lock (SafeStops) SafeStops.Add(Clock.ElapsedMilliseconds);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Step4Deliver()
    {
        const string rec = "1TST-REC-V1.0-I01";
        const string body = "    stream In (TST-TXT-V1.0:1) from device \"Echo\"\n    deliver Out (TST-TXT-V1.0:1) to device \"Echo\"\n";
        var result = new Dictionary<string, string>();
        using var api = new ControllerApi(StorageTests.Amds, Path.Combine(StorageTests.Amds, "no-settings.json"), new StorageAims());

        async Task<(EchoDevice Device, List<string> Said, string Outcome)> Run(string tail, int stopAfterMs = -1, string device = "Echo")
        {
            api.StartFlow(rec);
            var echo = new EchoDevice();
            var said = new List<string>();
            var interpreter = new WorkflowInterpreter(api.Async(), new DeviceRegistry().RegisterDevice(device, echo), line => { lock (said) said.Add(line); });
            using var stop = new CancellationTokenSource();
            if (stopAfterMs >= 0) stop.CancelAfter(stopAfterMs);
            string outcome;
            try { await interpreter.RunAsync(new WorkflowReader().Read($"workflow DELIVER over {rec}\non Start:\n{body}{tail}\n"), stop.Token); outcome = "ended"; }
            catch (OperationCanceledException) { outcome = "stopped"; }
            catch (InvalidOperationException e) { outcome = e.Message; }
            api.StopFlow(rec);
            return (echo, said, outcome);
        }

        var (ends, endsSaid, endsOutcome) = await Run("    wait 1s");
        result["the workflow ends: what the device produced, delivered back to it"] = $"{endsOutcome}; {string.Join(",", ends.Delivered)}";
        result["the workflow ends: the interlock"] = $"{ends.SafeStops.Count} safe stop(s); " + string.Join(" | ", endsSaid.Where(l => l.StartsWith("device")));

        var (stopped, stoppedSaid, stoppedOutcome) = await Run("    wait 10s", stopAfterMs: 300);
        result["Stop: the interlock"] = $"{stoppedOutcome}; {stopped.SafeStops.Count} safe stop(s) " +
                                       $"{(stopped.SafeStops.Count > 0 && stopped.SafeStops[0] < 1500 ? "within 1.5 s of the start" : "late or none")}; " +
                                       string.Join(" | ", stoppedSaid.Where(l => l.Contains("safe state")));

        var (_, _, unknown) = await Run("    wait 1s", device: "Pedal");
        result["a device the User Agent does not know"] = unknown;
        try { new WorkflowReader().Read($"workflow W over {rec}\non Start:\n    deliver Out (TST-TXT-V1.0:1) to \"Echo\"\n"); result["deliver not in its form"] = "accepted"; }
        catch (WorkflowReader.WorkflowSyntaxError e) { result["deliver not in its form"] = e.Message; }
        Expected.Match("mas-stage1-deliver.json", result);
    }

    // Each Data Type of the commands, Responses and Spatial Data against its schema.
    private static Dictionary<string, string> Validity(IEnumerable<(string DataType, string Json)> objects)
    {
        var schemas = AIF.Metadata.PublishedSchemas.At(Repository.Schemas);
        var byType = new Dictionary<string, string>
        {
            [MasTypes.BrakeCommand] = "BrakeCommand", [MasTypes.BrakeResponse] = "BrakeResponse", [MasTypes.MotorCommand] = "MotorCommand",
            [MasTypes.MotorResponse] = "MotorResponse", [MasTypes.WheelCommand] = "WheelCommand", [MasTypes.WheelResponse] = "WheelResponse",
            [MasTypes.SpatialData] = "SpatialData", [MasTypes.RoadState] = "RoadState", [MasTypes.Message] = "AMSMASMessage",
            [MasTypes.Weather] = "WeatherData"
        };
        var result = new Dictionary<string, string>();
        foreach (var group in objects.GroupBy(o => o.DataType).OrderBy(g => g.Key))
        {
            var schema = schemas[Path.GetFullPath(Path.Combine(Repository.Schemas, "CAV2", "V2.0", "data", byType[group.Key] + ".json"))];
            var valid = group.Count(o => { using var doc = JsonDocument.Parse(o.Json); lock (AIF.Metadata.PublishedSchemas.Lock) return schema.Evaluate(doc.RootElement).IsValid; });
            result[$"{group.Key} against its schema"] = valid == group.Count() ? "every one valid" : $"{valid} of {group.Count()}";
        }
        return result;
    }

    private static string Near(double value, double expected, double tolerance) =>
        Math.Abs(value - expected) <= tolerance * expected ? $"within {tolerance * 100:0} %" : $"{value:0.000}, outside {tolerance * 100:0} %";

    private static string Bucket(double value, double size) => (Math.Round(value / size) * size).ToString("0.##");
}
