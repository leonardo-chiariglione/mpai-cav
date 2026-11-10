using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Mpai.Xrv.Acg;

public enum ShowState { NotStarted, Running, Held, Ended }

// What the engine asks the AIM to write: the Cue Point ID (Port Number 3), an RE Action ID (1) or a VE Action ID (2).
public enum EventKind { CuePoint, REAction, VEAction }
public sealed record EngineEvent(EventKind Kind, string Id);

// THE CUE ENGINE OF ACTION GENERATION (XRV-ACG). The show goes by the Cue List of the Script, one Cue at a time: the Cue Point is the current Cue, and the Cue Point ID is told to the
// Status Interpretation AIMs. The Statuses they send are kept (the latest of each kind) and the groups of the current Cue are taken in the order of the Cue List: the first group whose
// Conditions are all met (every Status target of the group is met by the latest Status of its kind) has its Actions executed - the RE and the VE Action IDs come out, in order - and the
// show goes to the NextCue of the group, or, if it names none, to the next Cue of the Cue List; after the last Cue the show has ended. A new Cue forgets the Statuses held.
//
// THE SHOW IS in one of four states: not started, running, held, ended. A Status of the operator may carry a ShowControl: Go starts the show (at the first Cue) or resumes a held one;
// Hold holds it (no Cue Point ID and no Action until Go); Skip leaves the current Cue for the next one of the Cue List; Back returns to the Cue before the current one. A show whose
// Script lists no operator console starts by itself and runs from the first to the last Cue. A Hold of the show is not a Pause of the Module: Statuses go on being received.
public sealed class CueEngine
{
    private readonly Script script;
    private readonly ActionTable? table;
    private readonly Action<string> report;
    private readonly Dictionary<StatusKind, JsonNode?> latest = new();
    private readonly Stack<Cue> history = new();
    private Cue? current;
    private bool cueSent;
    private bool warnedDescriptors;

    public CueEngine(Script script, ActionTable? table, Action<string>? report = null)
    {
        this.script = script; this.table = table; this.report = report ?? (_ => { });
    }

    public ShowState State { get; private set; } = ShowState.NotStarted;
    public string? CurrentCue => current?.Id;

    // The show, once the Script is known: with no operator it starts at once.
    public List<EngineEvent> Start()
    {
        var events = new List<EngineEvent>();
        if (script.Cues.Count == 0) { State = ShowState.Ended; return events; }
        if (!script.HasOperator) { Go(events); Evaluate(events); }
        return events;
    }

    // A Status arrives: kept, its ShowControl (if any) applied, and the Conditions looked at.
    public List<EngineEvent> Receive(StatusObject status)
    {
        var events = new List<EngineEvent>();
        latest[status.Kind] = status.Data?.DeepClone();
        if (status.ShowControl is { } command) Control(command, events);
        Evaluate(events);
        return events;
    }

    // A ShowControl command from elsewhere (the User Agent's console through the Operator Status is the usual way).
    public List<EngineEvent> Control(string command)
    {
        var events = new List<EngineEvent>();
        Control(command, events);
        Evaluate(events);
        return events;
    }

    private void Control(string command, List<EngineEvent> events)
    {
        switch (command)
        {
            case "Go":
                if (State == ShowState.NotStarted) Go(events);   // (the caller then looks at the Conditions)
                else if (State == ShowState.Held)
                {
                    State = ShowState.Running;
                    if (current is not null && !cueSent) { events.Add(new(EventKind.CuePoint, current.Id)); cueSent = true; }
                }
                break;
            case "Hold":
                if (State == ShowState.Running) State = ShowState.Held;
                break;
            case "Skip" when State is ShowState.Running or ShowState.Held && current is not null:
                var next = NextInList(current);
                history.Push(current);
                if (next is null) End(); else MoveTo(next, events);
                break;
            case "Back" when State is ShowState.Running or ShowState.Held && history.Count > 0:
                MoveTo(history.Pop(), events);
                break;
        }
    }

    private void Go(List<EngineEvent> events)
    {
        State = ShowState.Running;
        MoveTo(script.Cues[0], events);
    }

    private void MoveTo(Cue cue, List<EngineEvent> events)
    {
        current = cue;
        latest.Clear();                                         // a new Cue Point ID resets what the one before asked for
        cueSent = false;
        if (State == ShowState.Running) { events.Add(new(EventKind.CuePoint, cue.Id)); cueSent = true; }
    }

    private void End() { State = ShowState.Ended; current = null; latest.Clear(); }

    private Cue? NextInList(Cue cue)
    {
        var i = script.Cues.ToList().FindIndex(c => c.Id == cue.Id);
        return i >= 0 && i + 1 < script.Cues.Count ? script.Cues[i + 1] : null;
    }

    private bool Met(CueGroup group)
    {
        if (group.HasDescriptorConditions)
        {
            if (!warnedDescriptors) { warnedDescriptors = true; report("a Cue Condition names Descriptors; Conditions on Descriptors are not evaluated, only Conditions on Statuses"); }
            return false;
        }
        return group.Targets.All(t => latest.TryGetValue(t.Kind, out var have) && JsonMatch.Meets(t.Data, have));
    }

    private void Evaluate(List<EngineEvent> events)
    {
        // A group without Conditions is met at once, so the loop goes on until a Cue waits for something.
        for (var guard = 0; State == ShowState.Running && current is not null && guard <= script.Cues.Count * 64; guard++)
        {
            var group = current.Groups.FirstOrDefault(Met);
            if (group is null) return;
            foreach (var id in group.REActions) Action(EventKind.REAction, id, events);
            foreach (var id in group.VEActions) Action(EventKind.VEAction, id, events);
            var next = group.NextCue is { } named ? script.Find(named) : NextInList(current);
            if (group.NextCue is not null && next is null) report($"the NextCue '{group.NextCue}' of Cue '{current.Id}' is not a Cue of the Script: the show ends");
            history.Push(current);
            if (next is null) End(); else MoveTo(next, events);
        }
    }

    // An Action ID identifies an Action of the Action Table; one it does not have is not executed. (A Mixed ID names the same entry as a plain one.)
    private void Action(EventKind kind, string id, List<EngineEvent> events)
    {
        if (table is not null && !table.Has(id)) { report($"the Action ID '{id}' is not an Action of the Action Table: not executed"); return; }
        events.Add(new(kind, id));
    }
}
