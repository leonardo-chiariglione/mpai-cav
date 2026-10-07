using Mpai.Mmm;
using Mpai.Mmm.Client;
using Mpai.Mmm.Server;
using Microsoft.Extensions.Hosting;

// THE MMM SERVER, ALONE. dotnet run --project MMM/Server -- [url] [--demo [seconds|manual]]
// The M-Instance of Use Case 2 behind the MMM-API, its viewer at <url>/viewer/.
// --demo: Use Case 2 is played against it a step at a time (default 3 s apart), for
// the viewer to show; the server stays up after it.
// --demo manual: each step waits for the presenter - Space, the right arrow or a click in
// the viewer, or Enter in this window - and the viewer says which step it has just shown.
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

// One "next" waits at a time: pressing it twice quickly does not skip a step.
var next = new SemaphoreSlim(0, 1);
void Next() { try { next.Release(); } catch (SemaphoreFullException) { } }

var app = MmmServer.Build(m, url, Path.Combine(repo, "MMM", "Viewer"), manual ? Next : null);
await app.StartAsync();
Console.WriteLine($"M-Instance {m.ID} on {url}; its viewer at {url.TrimEnd('/')}/viewer/");
if (demo >= 0)
{
    using var http = new HttpClient { BaseAddress = new Uri(url) };
    if (manual)
    {
        m.Manual = true;
        m.Caption = "Use Case 2: Friends meet in the metaverse";
        Console.WriteLine("Manual: Space / right arrow / click in the viewer, or Enter here, shows the next step.");
        _ = Task.Run(() => { while (Console.ReadLine() is not null) Next(); });
        await next.WaitAsync();
    }
    else await Task.Delay(TimeSpan.FromSeconds(pause * 2));
    await Uc2.RunAsync(http, m, async (step, r) =>
    {
        Console.WriteLine($"{step,-48} {Uc2.Said(r)}");
        var n = int.TryParse(step.AsSpan(0, 2), out var k) ? k : 0;
        m.Caption = $"Step {n} of {Steps}: {step[3..]}";
        if (manual) { if (n < Steps) await next.WaitAsync(); }
        else await Task.Delay(TimeSpan.FromSeconds(pause));
    });
    m.Caption = "Use Case 2 done: all " + Steps + " steps";
    m.Manual = false;
    Console.WriteLine("Use Case 2 done.");
}
await app.WaitForShutdownAsync();
return 0;
