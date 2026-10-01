using Mpai.Mmm;
using Mpai.Mmm.Client;
using Mpai.Mmm.Server;
using Microsoft.Extensions.Hosting;

// THE MMM SERVER, ALONE. dotnet run --project MMM/Server -- [url] [--demo [seconds]]
// The M-Instance of Use Case 2 behind the MMM-API, its viewer at <url>/viewer/.
// --demo: Use Case 2 is played against it a step at a time (default 3 s apart), for
// the viewer to show; the server stays up after it.
var url = args.FirstOrDefault(a => a.StartsWith("http", StringComparison.Ordinal)) ?? "http://127.0.0.1:7099";
var demo = Array.IndexOf(args, "--demo");
var pause = demo >= 0 && demo + 1 < args.Length && double.TryParse(args[demo + 1], out var s) ? s : 3.0;

var repo = AppContext.BaseDirectory;
while (repo is not null && !Directory.Exists(Path.Combine(repo, "MMM", "Viewer"))) repo = Path.GetDirectoryName(repo.TrimEnd(Path.DirectorySeparatorChar));
if (repo is null) { Console.Error.WriteLine("The repository (MMM/Viewer, schemas) is not above this program."); return 1; }

var m = new MInstance(Path.Combine(repo, "schemas"));
Uc2.Setup(m);
var app = MmmServer.Build(m, url, Path.Combine(repo, "MMM", "Viewer"));
await app.StartAsync();
Console.WriteLine($"M-Instance {m.ID} on {url}; its viewer at {url.TrimEnd('/')}/viewer/");
if (demo >= 0)
{
    await Task.Delay(TimeSpan.FromSeconds(pause * 2));
    using var http = new HttpClient { BaseAddress = new Uri(url) };
    await Uc2.RunAsync(http, m, async (step, r) =>
    {
        Console.WriteLine($"{step,-48} {Uc2.Said(r)}");
        await Task.Delay(TimeSpan.FromSeconds(pause));
    });
    Console.WriteLine("Use Case 2 done.");
}
await app.WaitForShutdownAsync();
return 0;
