using System.Text.Json.Nodes;
using Mpai.Mmm;
using Mpai.Mmm.Client;
using Mpai.Mmm.Server;
using Microsoft.Extensions.Hosting;

// THE MMM SERVER, ALONE. dotnet run --project MMM/Server -- [url] [--demo [seconds|manual]]
// The M-Instance of Use Case 2 behind the MMM-API, its viewer at <url>/viewer/.
// --demo: Use Case 2 is played against it a step at a time (default 3 s apart), for
// the viewer to show; the server stays up after it.
// --demo manual: the presenter decides. In the viewer, Space, the up arrow, the right arrow or
// a click shows the next step; the down arrow (or left arrow) the previous one - the scene
// as it was shown, replayed from a record of each step, nothing is undone in the M-Instance.
// Enter in this window is also "next".
var url = args.FirstOrDefault(a => a.StartsWith("http", StringComparison.Ordinal)) ?? "http://127.0.0.1:7099";
var demo = Array.IndexOf(args, "--demo");
var manual = demo >= 0 && demo + 1 < args.Length && args[demo + 1] == "manual";
var pause = demo >= 0 && !manual && demo + 1 < args.Length && double.TryParse(args[demo + 1], out var s) ? s : 3.0;
const int Steps = 19;

var repo = AppContext.BaseDirectory;
while (repo is not null && !Directory.Exists(Path.Combine(repo, "MMM", "Viewer"))) repo = Path.GetDirectoryName(repo.TrimEnd(Path.DirectorySeparatorChar));
if (repo is null) { Console.Error.WriteLine("The repository (MMM/Viewer, schemas) is not above this program."); return 1; }

var m = new MInstance(Path.Combine(repo, "schemas"));
Uc2.Setup(m);

// What a step means, said under its caption - for the steps whose point is not seen on the stage.
var notes = new Dictionary<int, string>
{
    [4] = "Persona1 is added at Metaverse Square, but nothing can be seen yet: it has no identified data.",
    [5] = "The human is captured from the Universe (ULocFriend1). Until it is identified, nothing is seen in the M-Instance.",
    [6] = "Identified: now Persona1 exists in the M-Instance, and is seen.",
    [7] = "Persona1 is animated from the identified stream.",
    [11] = "Only now is the Room perceptible: Friend1 can see it.",
    [15] = "Friend2 acts for the first time: its Persona is seen.",
    [16] = "Friend1 grants Friend2 the right to enter the Room.",
    [19] = "Access revoked: Friend2's next attempt to enter is refused (403, insufficient rights)."
};
// Nothing is seen of a Persona before it is identified (Persona1: step 6) or acts (Persona2: step 15).
string[] HiddenAt(int n) => [.. (n < 6 ? new[] { "Persona1ID" } : []), .. (n < 15 ? new[] { "Persona2ID" } : [])];

// The record of the steps, for the previous-step key: the scene after each one, and the one shown.
var shots = new List<JsonObject>();
var cursor = 0;
var gate = new object();
void Record() { lock (gate) { shots.Add(m.Snapshot()); cursor = shots.Count - 1; } }
JsonObject Shown() { lock (gate) return shots.Count > 0 ? shots[cursor] : m.Snapshot(); }
void Back() { lock (gate) { if (cursor > 0) cursor--; } }

// One "next" waits at a time: pressing it twice quickly does not skip a step. A step already
// performed is shown again; at the last one performed, the next step is performed.
var next = new SemaphoreSlim(0, 1);
void Next()
{
    lock (gate) { if (cursor < shots.Count - 1) { cursor++; return; } }
    try { next.Release(); } catch (SemaphoreFullException) { }
}

var app = MmmServer.Build(m, url, Path.Combine(repo, "MMM", "Viewer"), manual ? Next : null, manual ? Back : null, manual ? Shown : null);
await app.StartAsync();
Console.WriteLine($"M-Instance {m.ID} on {url}; its viewer at {url.TrimEnd('/')}/viewer/");
if (demo >= 0)
{
    using var http = new HttpClient { BaseAddress = new Uri(url) };
    m.Hidden = HiddenAt(0);
    if (manual)
    {
        m.Manual = true;
        m.Caption = "Use Case 2: Friends meet in the metaverse";
        Record();
        Console.WriteLine("Manual: Space / up arrow / right arrow / click in the viewer, or Enter here, shows the next step; down arrow the previous one.");
        _ = Task.Run(() => { while (Console.ReadLine() is not null) Next(); });
        await next.WaitAsync();
    }
    else await Task.Delay(TimeSpan.FromSeconds(pause * 2));
    await Uc2.RunAsync(http, m, async (step, r) =>
    {
        Console.WriteLine($"{step,-48} {Uc2.Said(r)}");
        var n = int.TryParse(step.AsSpan(0, 2), out var k) ? k : 0;
        m.Caption = $"Step {n} of {Steps}: {step[3..]}";
        m.Note = notes.GetValueOrDefault(n);
        m.Hidden = HiddenAt(n);
        if (manual) { Record(); if (n < Steps) await next.WaitAsync(); }
        else await Task.Delay(TimeSpan.FromSeconds(pause));
    });
    m.Caption = "Use Case 2 done: all " + Steps + " steps";
    m.Note = null;
    m.Manual = manual;
    if (manual) Record();
    Console.WriteLine("Use Case 2 done.");
}
await app.WaitForShutdownAsync();
return 0;
