using System.Reflection;

using AIF.RemoteHost;
using AIF.Channels;
using AIF.Controller;
using AIF.Store;

// THE AIM HOST (M3217 3.1). Started on a machine that is to hold AIMs:
//
//   AIF.AimHost --port 5207 --key <key> --amds <folder of L3s> [--settings <aim-settings.json>]
//               [--storage <folder>] --provider <assembly.dll>:<Type> [--provider ...]
//
// It builds AIMs from the providers it is given - as a Service does - and serves
// one Controller at a time: the one that opens with its key.

string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
IEnumerable<string> Args(string name) => args.Select((a, i) => (a, i)).Where(x => x.a == name && x.i + 1 < args.Length).Select(x => args[x.i + 1]);

var port = int.Parse(Arg("--port") ?? "5207");
var key = Arg("--key") ?? throw new ArgumentException("--key is required: a host serves only the Controller that holds its key.");
var amds = Arg("--amds") ?? throw new ArgumentException("--amds is required: the L3s of the AIMs this host may build.");
var settings = Arg("--settings") is { } s ? AimSettings.Load(s) : AimSettings.Empty;
var storage = Arg("--storage") ?? Path.Combine(Path.GetTempPath(), "mpai-aimhost-" + port);

var store = new AmdStore(amds);
store.Scan();

var providers = Args("--provider").Select(spec =>
{
    var colon = spec.LastIndexOf(':');
    var assembly = Assembly.LoadFrom(Path.GetFullPath(spec[..colon]));
    var type = assembly.GetType(spec[(colon + 1)..], throwOnError: true)!;
    return (IAimProvider)(type.GetConstructor([typeof(AmdStore)]) is { } withStore
        ? withStore.Invoke([store])
        : Activator.CreateInstance(type)!);
}).ToList();

var server = new AimHostServer(store, settings, new CompositeProvider(providers.ToArray()), storage);
await using var listener = new RemoteLink.Listener(port, key, server.Serve);
Console.WriteLine($"[AIM host] listening on {listener.Port}; {store.Count} L3s; providers: {string.Join(", ", providers.Select(p => p.GetType().Name))}");
Console.Out.Flush();

var done = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => done.TrySetResult();
await done.Task;
