using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Mpai.Cav.Recordings;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Ua;

namespace Mpai.Cav.Ua;

// WHAT THE PASSENGER SAYS: a line, when the CAV has said something that contains the cue (a null cue: at the start), and not before After seconds of the drive.
public sealed record PassengerLine(string? Cue, string Says, double After = 0);

// THE WORLD OF A SIMULATED CAV, AS THE DEVICES OF ITS USER AGENT (CAV.orch). The workflow binds a device "Vehicle" to the sensors, the Responses and the commands of the vehicle, "Cabin" and "Hearing"
// to the passenger, "Map" to the Offline Map, "Journal" to the decision of the Autonomous Motion Subsystem. It knows none of them but by name. Behind the devices is a simulation, stepped as the CAV
// decides: a frame is given, the simulation waits for the CAV's decision on it and for its commands, acts on them and goes on - nothing is dropped for want of time (M3233). A device in a real
// vehicle would be paced by the clock; the workflow would not change.
public sealed class CavWorld : IDisposable
{
    private const string Attitude = "OSD-OSA-V1.5";        // the simulation's own Spatial Attitude: not a Port of the Module

    private readonly Simulation sim;
    private readonly PassengerLine[] lines;
    private readonly Func<string, string> speak;
    private readonly string avatarModel;
    private readonly int maxSteps;
    private readonly string? endCue;
    private readonly TimeSpan decisionTimeout;
    private readonly Stopwatch wall = Stopwatch.StartNew();
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // The Module has its map and the avatar it answers with before the world starts: what a Module is given first it has first (the order the first Messages are given in is the User Agent's).
    private readonly TaskCompletionSource mapGiven = new(TaskCreationOptions.RunContinuationsAsynchronously), avatarGiven = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly object gate = new();
    private readonly List<(string DataType, string Json)> pending = [];
    private long decidedMs = long.MinValue, wheelMs = long.MinValue;
    private bool commanding, safe;
    private int steps;
    private string? failure;
    private JsonNode? decision;

    private readonly List<(double At, string Text)> said = [];
    private readonly List<string> recognised = [];
    private readonly List<byte[]> spoken = [];
    private readonly List<double> stepMs = [];
    private readonly List<(double At, string Text)> spoke = [];
    private double? movedAt, arrivedAt; private double overLimit;

    // sim: the world; lines: what the passenger says; speak: the passenger's voice (the text as a Basic Audio Object, as a microphone gives it); avatarDatum: the Avatar Model the CAV answers with.
    public CavWorld(Simulation sim, IEnumerable<PassengerLine> lines, Func<string, string> speak, string avatarDatum, int maxSteps = 1500, TimeSpan? decisionTimeout = null, string? endCue = "arrived")
    {
        this.endCue = endCue; this.sim = sim; this.lines = lines.ToArray(); this.speak = speak; avatarModel = avatarDatum; this.maxSteps = maxSteps;
        this.decisionTimeout = decisionTimeout ?? TimeSpan.FromSeconds(60);
        Vehicle = new VehicleDevice(this);
        Cabin = new CabinDevice(this);
        Hearing = new HearingDevice(this);
        Journal = new JournalDevice(this);
        PointOfView = new GivenDevice("OSD-OPV-V1.5", new JsonObject { ["Header"] = "OSD-OPV-V1.5", ["PointOfViewID"] = "POV", ["CartPosition"] = new JsonArray(0.0, 0.0, 1.2), ["Orientation"] = new JsonArray(0.0, 0.0, 0.0) }.ToJsonString());
        Map = new FirstDevice("OSD-BOO-V1.5", sim.Map.ToOfflineMapObject(0), mapGiven);
    }

    public IDevice Vehicle { get; }
    public IDevice Cabin { get; }
    public IDevice Hearing { get; }
    public IDevice Journal { get; }
    public IDevice Map { get; }
    // The point of view the passenger selects: Position and Orientation, as the Point of View Acquisition Unit gives it (here a fixed one, the passenger looking ahead).
    public IDevice PointOfView { get; }

    public Simulation Sim => sim;
    // Completes when the drive is over: arrived, collided, the steps given, the CAV silent, or the vehicle brought to its safe state and stopped.
    public Task Finished => finished.Task;
    public string? Failure => failure;
    public int Steps => steps;
    public bool Safe => safe;
    public IReadOnlyList<(double At, string Text)> Said { get { lock (gate) return said.ToList(); } }
    public IReadOnlyList<string> Recognised { get { lock (gate) return recognised.ToList(); } }
    public IReadOnlyList<byte[]> Spoken { get { lock (gate) return spoken.ToList(); } }
    // How long the CAV took to decide on and command each frame, in milliseconds.
    public IReadOnlyList<double> StepMilliseconds { get { lock (gate) return stepMs.ToList(); } }
    // The decision of the Autonomous Motion Subsystem on the last frame.
    public JsonNode? Decision { get { lock (gate) return decision; } }
    // What the passenger said and when; when the vehicle first moved; the most it went over the speed limit (m/s); when the CAV said it had arrived (the end cue).
    public IReadOnlyList<(double At, string Text)> Spoke { get { lock (gate) return spoke.ToList(); } }
    public double? MovedAt { get { lock (gate) return movedAt; } }
    public double OverLimit { get { lock (gate) return overLimit; } }
    public double? ArrivedAt { get { lock (gate) return arrivedAt; } }

    private static long Ms(JsonNode? simpleTime) =>
        simpleTime?["SimpleTimeData"]?[0]?["StartTime"] is JsonValue v ? v.TryGetValue<long>(out var l) ? l : v.TryGetValue<double>(out var d) ? (long)d : 0 : 0;

    // ---- the vehicle: it acts on the world and answers --------------------------------------------------------------------------------------------------------------------------------

    private sealed class VehicleDevice(CavWorld w) : IDevice
    {
        public Task DeliverAsync(string dataType, string json, CancellationToken cancel)
        {
            lock (w.gate)
            {
                if (w.safe) return Task.CompletedTask;               // at its safe state, a vehicle takes no command
                w.pending.Add((dataType, json));
                if (dataType.StartsWith("CAV-WHC", StringComparison.Ordinal))
                {
                    w.commanding = true;
                    w.wheelMs = Math.Max(w.wheelMs, Ms(JsonNode.Parse(json)?["WheelCommandTime"]));
                }
            }
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<(string DataType, string Json)> ReadAsync([EnumeratorCancellation] CancellationToken cancel)
        {
            IReadOnlyList<(string DataType, string Json)> responses = [];
            try
            {
                await Task.WhenAll(w.mapGiven.Task, w.avatarGiven.Task).WaitAsync(cancel);
                while (!cancel.IsCancellationRequested)
                {
                    Simulation.Sensed sensed;
                    lock (w.sim)
                    {
                        if (w.sim.Collided || w.steps >= w.maxSteps || w.safe || (w.endCue is null ? w.sim.Arrived : w.arrivedAt is not null)) break;
                        sensed = w.sim.Sense();
                        lock (w.gate) w.overLimit = Math.Max(w.overLimit, (double)sensed.Truth["Ego"]!["Speed"]! - (double)sensed.Truth["Ego"]!["SpeedLimit"]!);
                    }
                    foreach (var r in responses) yield return r;
                    foreach (var m in sensed.Messages) if (m.DataType != Attitude) yield return m;

                    var clock = Stopwatch.StartNew();
                    if (!await w.DecidedAsync(sensed.FrameMs, cancel)) { if (!w.safe) w.failure = $"no decision of the Autonomous Motion Subsystem on frame {sensed.FrameMs}"; break; }
                    var commands = await w.TakeCommandsAsync(sensed.FrameMs, cancel);
                    lock (w.sim)
                    {
                        w.sim.Actuate(commands);
                        responses = w.sim.Advance();
                        w.steps++;
                        if (w.movedAt is null && w.sim.EgoS > 0.5) lock (w.gate) w.movedAt = w.sim.Time;
                    }
                    lock (w.gate) w.stepMs.Add(clock.Elapsed.TotalMilliseconds);
                }
                w.StopTheVehicle();
            }
            finally { w.finished.TrySetResult(); }
        }

        // The vehicle's own safe state: the emergency brake, held to a standstill; after it, no command is taken.
        public Task SafeStopAsync()
        {
            lock (w.gate) { w.safe = true; w.pending.Clear(); }
            return Task.CompletedTask;
        }
    }

    private async Task<bool> DecidedAsync(long frameMs, CancellationToken cancel)
    {
        var clock = Stopwatch.StartNew();
        while (!cancel.IsCancellationRequested)
        {
            lock (gate) { if (decidedMs >= frameMs) return true; if (safe) return false; }
            if (clock.Elapsed > decisionTimeout) return false;
            await Task.Delay(2, CancellationToken.None);
        }
        return false;
    }

    // The commands of the frame: once the vehicle has been commanded, the step waits for the Wheel Command of the frame - one for each frame once the Motion Subsystem follows a Message - and takes
    // what came with it; before that it takes what there is.
    private async Task<List<(string DataType, string Json)>> TakeCommandsAsync(long frameMs, CancellationToken cancel)
    {
        var clock = Stopwatch.StartNew();
        bool wait; lock (gate) wait = commanding;
        while (wait && !cancel.IsCancellationRequested && clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            lock (gate) { if (wheelMs >= frameMs || safe) break; }
            await Task.Delay(2, CancellationToken.None);
        }
        if (wait) await Task.Delay(15, CancellationToken.None);     // what the Brake and Motor Ports gave with it
        lock (gate)
        {
            var taken = pending.ToList();
            pending.Clear();
            return taken;
        }
    }

    // Brought to its safe state, the vehicle brakes to a standstill without the CAV.
    private void StopTheVehicle()
    {
        bool stop; lock (gate) stop = safe;
        if (!stop) return;
        lock (sim)
        {
            var at = (sim.Start + TimeSpan.FromSeconds(sim.Time)).ToUnixTimeMilliseconds();
            for (var i = 0; i < 300 && sim.EgoSpeed > 0; i++)
            {
                sim.Actuate([("CAV-BRC-V2.0", BrakeEmergency(at + i * 100L))]);
                sim.Advance();
            }
        }
    }

    private static string BrakeEmergency(long ms) => new JsonObject
    {
        ["Header"] = "CAV-BRC-V2.0", ["BrakeCommandID"] = "SAFE", ["BrakeID"] = "B1",
        ["BrakeCommand"] = new JsonArray(new JsonObject
        {
            ["TargetVelocity"] = 0.0,
            ["BrakeCommandTime"] = new JsonObject { ["Header"] = "OSD-STM-V1.5", ["SimpleTimeID"] = "SAFE-T", ["SimpleTimeData"] = new JsonArray(new JsonObject { ["StartTime"] = ms }) },
            ["DecelerationTarget"] = 9.0, ["ABSAllow"] = true, ["EmergencyBrakeFlag"] = true
        })
    }.ToJsonString();

    // ---- the journal: what the Autonomous Motion Subsystem decided --------------------------------------------------------------------------------------------------------------------

    private sealed class JournalDevice(CavWorld w) : IDevice
    {
        public Task DeliverAsync(string dataType, string json, CancellationToken cancel)
        {
            var node = JsonNode.Parse(json);
            lock (w.gate) { w.decision = node; w.decidedMs = Math.Max(w.decidedMs, Ms(node?["AMSDataTime"])); }
            return Task.CompletedTask;
        }
        public async IAsyncEnumerable<(string DataType, string Json)> ReadAsync([EnumeratorCancellation] CancellationToken cancel) { await Task.Delay(-1, cancel); yield break; }
        public Task SafeStopAsync() => Task.CompletedTask;
    }

    // ---- the cabin: the passenger, and what the CAV says to them ------------------------------------------------------------------------------------------------------------------

    private sealed class CabinDevice(CavWorld w) : IDevice
    {
        public Task DeliverAsync(string dataType, string json, CancellationToken cancel)
        {
            if (dataType == "OSD-BTO-V1.5")
            {
                var text = MpaiJson.FromJson<BasicTextObject>(json)?.GetText() ?? "";
                lock (w.gate) { w.said.Add((w.sim.Time, text)); if (w.endCue is not null && w.arrivedAt is null && text.Contains(w.endCue)) w.arrivedAt = w.sim.Time; }
            }
            else if (dataType == "PAF-PAV-V1.6")
            {
                var speech = MpaiJson.FromJson<PortableAvatar>(json)?.Speech()?.Data;
                if (speech is not null) lock (w.gate) w.spoken.Add(speech);
            }
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<(string DataType, string Json)> ReadAsync([EnumeratorCancellation] CancellationToken cancel)
        {
            yield return ("OSD-B3O-V1.5", w.avatarModel);
            w.avatarGiven.TrySetResult();                       // the consumer asked for the next: the avatar is written

            // The passenger says a line when its cue is heard, and its time has come.
            var next = 0; var seen = 0; var cued = false;
            while (!cancel.IsCancellationRequested && !w.finished.Task.IsCompleted)
            {
                string? line = null;
                lock (w.gate)
                {
                    if (next < w.lines.Length && w.lines[next].Cue is null && w.sim.StepNumber >= 5) line = w.lines[next++].Says;
                    else if (cued && next < w.lines.Length && w.sim.Time >= w.lines[next].After) { line = w.lines[next++].Says; cued = false; }
                    for (; seen < w.said.Count; seen++)
                        if (!cued && next < w.lines.Length && w.lines[next].Cue is { } cue && w.said[seen].Text.Contains(cue)) cued = true;
                }
                if (line is not null) { lock (w.gate) w.spoke.Add((w.sim.Time, line)); yield return ("OSD-BAO-V1.5", w.speak(line)); }
                else await Task.Delay(20, CancellationToken.None);
            }
        }

        public Task SafeStopAsync() => Task.CompletedTask;
    }

    // What the CAV heard, in words: the Port that gives it.
    private sealed class HearingDevice(CavWorld w) : IDevice
    {
        public Task DeliverAsync(string dataType, string json, CancellationToken cancel)
        {
            lock (w.gate) w.recognised.Add(MpaiJson.FromJson<BasicTextObject>(json)?.GetText() ?? "");
            return Task.CompletedTask;
        }
        public async IAsyncEnumerable<(string DataType, string Json)> ReadAsync([EnumeratorCancellation] CancellationToken cancel) { await Task.Delay(-1, cancel); yield break; }
        public Task SafeStopAsync() => Task.CompletedTask;
    }

    // A device that gives one datum - the Offline Map - and says when the Module has it.
    private sealed class FirstDevice(string dataType, string json, TaskCompletionSource given) : IDevice
    {
        public Task DeliverAsync(string type, string data, CancellationToken cancel) => Task.CompletedTask;
        public async IAsyncEnumerable<(string DataType, string Json)> ReadAsync([EnumeratorCancellation] CancellationToken cancel)
        {
            await Task.CompletedTask;
            yield return (dataType, json);
            given.TrySetResult();
        }
        public Task SafeStopAsync() => Task.CompletedTask;
    }

    public void Dispose() { }
}
