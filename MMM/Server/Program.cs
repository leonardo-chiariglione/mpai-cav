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
    [5] = "Friend1 operates from the Universe (ULocFriend1): its human is captured there. Until identified, nothing of Friend1 is seen in the M-Instance.",
    [6] = "Identified: now Persona1 exists in the M-Instance, and is seen.",
    [7] = "Persona1 is animated from the identified stream.",
    [11] = "Only now is the Room perceptible: Friend1 can see it.",
    [16] = "Friend1 grants Friend2 the right to enter the Room.",
    [19] = "Access revoked: Friend2's next attempt to enter is refused (403, insufficient rights)."
};
// What the viewer's voice says for each step: what happens, and why.
var speech = new Dictionary<int, string>
{
    [1] = "Human1 registers with the M-Instance, and receives Friend1, the identity its Persona will act under.",
    [2] = "Friend1 buys a parcel of land from the Seller, from the Universe. The transaction also gives Friend1 the rights to add things on the parcel and to move to it.",
    [3] = "Friend1 buys a room, and with it the right to enter it.",
    [4] = "Friend1 adds its Persona at Metaverse Square. Nothing can be seen yet: the Persona has no identified data, and what is not identified does not exist in the M-Instance.",
    [5] = "Friend1 operates from the Universe. There, the data of its human is captured. Until it is identified, nothing of Friend1 is seen in the M-Instance.",
    [6] = "The captured stream is identified. Now Persona1 exists in the M-Instance, and it is seen.",
    [7] = "Friend1 animates its Persona from the identified stream, so the Persona moves as its human does.",
    [8] = "Friend1 signals its presence to the presence service, with a message.",
    [9] = "Friend1 moves its Persona to the parcel, and stops at its edge. It cannot enter the room: the room is not there yet.",
    [10] = "Friend1 places the room on the parcel. The room exists, but nobody can see it yet.",
    [11] = "Friend1 makes the room perceptible. Only now can the room be seen, and so only now can Friend1 enter it.",
    [12] = "Friend1 enters the room.",
    [13] = "Friend1 renders the room to its human, in the Universe, so that the human sees it too.",
    [14] = "Friend1 invites Friend2 with a message: come to my room. Friend2 reads it.",
    [15] = "Friend2 accepts, with a message back to Friend1.",
    [16] = "Friend2 has no right to enter the room yet. Friend1 grants it that right.",
    [17] = "With the right granted, Friend2 enters the room, and the two friends are together.",
    [18] = "Friend2 leaves the room.",
    [19] = "Friend1 revokes the right. Friend2's next attempt to enter is refused: insufficient rights."
};
// Friend1 operates from the Universe: nothing of it is seen until its captured data is identified
// (step 6). Friend2 is already at its place when the Use Case begins.
string[] HiddenAt(int n) => n < 6 ? ["Persona1ID"] : [];

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
        m.Say = "Use Case 2: two friends meet in the metaverse. Friend2 is already at its place. Friend1 will register, buy a parcel and a room, bring its Persona in, and invite Friend2.";
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
        m.Say = speech.GetValueOrDefault(n);
        m.Hidden = HiddenAt(n);
        if (manual) { Record(); if (n < Steps) await next.WaitAsync(); }
        else await Task.Delay(TimeSpan.FromSeconds(pause));
    });
    m.Caption = "Use Case 2 done: all " + Steps + " steps";
    m.Note = null;
    m.Say = "The use case is complete: all " + Steps + " steps.";
    m.Manual = manual;
    if (manual) Record();
    Console.WriteLine("Use Case 2 done.");
}
await app.WaitForShutdownAsync();
return 0;
