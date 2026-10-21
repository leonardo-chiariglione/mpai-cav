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
// --world: the world of the MMM - the Metaverse Square, buildings with doors, the MPAI building with its meeting room - instead of Use Case 2 (MMM/World/world.json).
// --code 123456 (or MMM_CODE): nobody gets anything from the server without the meeting code. --invite A,B,C: the Users that are invited from the start, the first the convener.
var worldMode = args.Contains("--world");
string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
var code = Arg("--code") ?? Environment.GetEnvironmentVariable("MMM_CODE");
var invite = (Arg("--invite") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
// --identity: no meeting code; whoever asks is named (MMM/Server/Identity.cs). --chair Name (default: the first of --invite) is whoever browses on this machine. --invites Anna=token,Bruno=token: the
// personal links of the guests the chair invited (?invite=token). --guests login=Name: the same for people who come through `tailscale serve`. --visitors: anybody else may come in as a visitor,
// to the atrium, which is public, and no further; without it nobody else is answered.
var chairName = Arg("--chair") ?? invite.FirstOrDefault() ?? "Chair";
Dictionary<string, string> Pairs(string? list) => (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
    .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
var guests = Pairs(Arg("--guests"));                                                                           // login -> name
var tokens = Pairs(Arg("--invites")).ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);            // token -> name
Identity? identity = args.Contains("--identity") || args.Contains("--tailscale") ? new Identity(chairName, guests, tokens, args.Contains("--visitors")) : null;
if (identity is not null) invite = [.. new[] { chairName }.Concat(invite).Concat(tokens.Values).Concat(guests.Values).Distinct()];
if (worldMode)
{
    World.Setup(m, repo);
    // The Personae a registering human chooses from (MMM/Viewer/personae/catalogue): the Rocketbox avatars.
    var catalogue = Path.Combine(repo, "MMM", "Viewer", "personae", "catalogue", "catalogue.json");
    if (File.Exists(catalogue))
    {
        var entries = JsonNode.Parse(File.ReadAllText(catalogue))!.AsArray();
        m.Catalogue = entries.Select(e => (string)e!["Name"]!).ToHashSet(StringComparer.Ordinal);
        m.CatalogueEntries = new JsonArray(entries.Select(e => (JsonNode)new JsonObject { ["Name"] = (string)e!["Name"]!, ["Group"] = (string)e["Group"]!, ["Gender"] = (string)e["Gender"]! }).ToArray());
    }
    // Rocketbox avatars (MIT) of the ABV demo, in turn to the Users that register: five humans, and the Secretary has Persona2.
    m.DefaultModels = ["Persona1.glb", "Male03.glb", "Thalia.glb", "Male05.glb", "Male08.glb", "Persona2.glb"];
    // --models Anna=Thalia.glb,Bruno=Male03.glb: the avatar of each User's Persona (the files of Apps/ABV/AVS/Server/avatars); the others take the list above in turn.
    foreach (var pair in (Arg("--models") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => p.Split('=', 2)).Where(p => p.Length == 2))
        m.SetModel($"{pair[0]}-Persona", pair[1]);
}
else
{
    Uc2.Setup(m);
    m.DefaultModels = ["Persona1.glb", "Persona2.glb"];   // the Personae of the Users that join from a client are drawn with these in turn
    Meeting.Setup(m);                                      // the meeting room, its seats, the AVS Service and the Secretary
}

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

var internalKey = Guid.NewGuid().ToString("N");        // how the services of the M-Instance (the AVS Service, the Secretary, the ASR Service) get past the meeting code: they are not guests
var avatarFolder = Path.Combine(repo, "Apps", "ABV", "AVS", "Server", "avatars");
var app = MmmServer.Build(m, url, Path.Combine(repo, "MMM", "Viewer"), manual ? Next : null, manual ? Back : null, manual ? Shown : null, code, Directory.Exists(avatarFolder) ? avatarFolder : null, internalKey, identity);
await app.StartAsync();
Console.WriteLine($"M-Instance {m.ID} on {url}; its viewer at {url.TrimEnd('/')}/viewer/");
// The meeting's Processes: the AVS Service and the Secretary, acting through the MMM-API like any Process.
// The ASR Service recognises with Whisper (the repository's Models\Whisper), if it is there.
var whisper = new WhisperAsr(repo);
if (whisper.Available) { whisper.Start(); Console.WriteLine("Whisper starting for the ASR Service (the model loads in a few seconds)."); }
else Console.WriteLine("Models\\Whisper is not there: the ASR Service finds no words.");
// --meeting-demo [seconds]: the bots of the meeting use case play it against this server, a step at a time (default 5 s apart), for the viewer - open /viewer/?look=12,0 -
// to show. The bots have no voices, so the ASR Service recognises the words of a script instead of Whisper.
var meetingDemo = Array.IndexOf(args, "--meeting-demo");
var meetingPause = meetingDemo >= 0 && meetingDemo + 1 < args.Length && double.TryParse(args[meetingDemo + 1], out var mp) ? mp : 5.0;
var inside = new HttpClient { BaseAddress = new Uri(url) }; inside.DefaultRequestHeaders.Add("X-Mmm-Internal", internalKey);
var meeting = new MeetingServices(inside, m, meetingDemo >= 0 ? MeetingUseCase.ScriptedRecognizer() : whisper.Available ? whisper.RecognizeAsync : null);
await meeting.StartAsync();
meeting.Preinvite(invite);
// --until 19:00 (today, or tomorrow if it has passed) or --hours 2: when the meeting ends; its rights end with it. Without either, the convener ends it.
if (Arg("--until") is { } until && TimeSpan.TryParse(until, out var clock)) { var at = DateTime.Today + clock; if (at < DateTime.Now) at = at.AddDays(1); meeting.EndsAt = new DateTimeOffset(at); }
else if (Arg("--hours") is { } hours && double.TryParse(hours, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h)) meeting.EndsAt = DateTimeOffset.UtcNow.AddHours(h);
if (meeting.EndsAt is { } ends) Console.WriteLine($"The meeting ends at {ends.ToLocalTime():HH:mm}: its rights end with it.");                                      // the first of --invite is the convener, the others are invited
_ = Task.Run(async () => { while (true) { try { await meeting.PumpAsync(); } catch (Exception e) { Console.Error.WriteLine("meeting: " + e.Message); } await Task.Delay(200); } });
var minutesShown = 0;
_ = Task.Run(async () => { while (true) { try { await meeting.PumpAsrAsync(); } catch (Exception e) { Console.Error.WriteLine("asr: " + e.Message); } await Task.Delay(200); } });
_ = Task.Run(async () => { while (true) { var minutes = meeting.TheMinutes; while (minutesShown < minutes.Count) Console.WriteLine("minutes: " + minutes[minutesShown++]); await Task.Delay(200); } });
if (meetingDemo >= 0)
{
    using var bots = new HttpClient { BaseAddress = new Uri(url) };
    m.Caption = "A meeting in the MMM"; m.Say = "A meeting in the M M M: three participants and the Secretary meet in a room, through the M M M A P I only.";
    await Task.Delay(TimeSpan.FromSeconds(meetingPause));
    var record = await MeetingUseCase.RunAsync(bots, m, meeting, async (step, note) =>
    {
        Console.WriteLine(step);
        m.Caption = step; m.Note = note; m.Say = note;
        await Task.Delay(TimeSpan.FromSeconds(meetingPause));
    });
    m.Caption = "The meeting is over"; m.Note = null; m.Say = "The meeting is over.";
    Console.WriteLine("Meeting use case done.");
}
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
