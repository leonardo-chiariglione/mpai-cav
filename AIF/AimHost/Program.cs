using System.Reflection;

using AIF.RemoteHost;
using AIF.Channels;
using AIF.Controller;
using AIF.Store;

// THE AIM HOST (M3217 3.1). Started on a machine that is to hold AIMs:
//
//   AIF.AimHost --port 5207 (--anchor <its anchor> --trust <Controllers' anchors> | --key <key>)
//               --amds <folder of L3s> [--settings <aim-settings.json>]
//               [--storage <folder>] --provider <assembly.dll>:<Type> [--provider ...]
//
// It builds AIMs from the providers it is given - as a Service does - and serves
// the Controllers it trusts. Under Zero Trust (M3223 3.4) the host is a Trust
// Anchor - --anchor, its anchor and key as TrustAnchorKey.Save writes them - and
// serves a Controller whose Trust Anchor is among those --trust names (a file of
// one PTF Trust Anchor object, or of an array of them) and which proves it with
// the Trust Protocol. Without them, a Controller that opens with the key.

string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
IEnumerable<string> Args(string name) => args.Select((a, i) => (a, i)).Where(x => x.a == name && x.i + 1 < args.Length).Select(x => args[x.i + 1]);

var port = int.Parse(Arg("--port") ?? "5207");
var key = Arg("--key");
var anchorFile = Arg("--anchor");
var trustFile = Arg("--trust");
if ((anchorFile is null) != (trustFile is null))
    throw new ArgumentException("--anchor and --trust go together: the host's own anchor, and the anchors of the Controllers it serves.");
if (anchorFile is not null && key is not null)
    throw new ArgumentException("--key and --anchor: a host admits a Controller by the Trust Protocol or by a key, not both.");
if (anchorFile is null && key is null)
    throw new ArgumentException("--anchor and --trust, or --key, are required: a host serves only the Controllers it trusts.");
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
ILinkAdmission admission = new KeyAdmission(key ?? "");
if (anchorFile is not null)
{
    var (anchor, anchorKey) = AIF.Trust.TrustAnchorKey.Load(anchorFile);
    var trusted = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(trustFile!)) switch
    {
        System.Text.Json.Nodes.JsonArray many => many.Select(a => a!.AsObject()).ToList(),
        System.Text.Json.Nodes.JsonObject one => [one],
        _ => throw new ArgumentException($"{trustFile}: not a Trust Anchor object, nor an array of them.")
    };
    admission = new TrustedLink(new AIF.Trust.TrustProtocol(anchor, anchorKey, trusted))
    {
        Answered = (controller, refused) => Console.WriteLine(refused is null
            ? $"[AIM host] {controller} admitted by the Trust Protocol"
            : $"[AIM host] {controller} refused: {refused}")
    };
}
await using var listener = new RemoteLink.Listener(port, admission, server.Serve);
listener.Failed += why => Console.WriteLine($"[AIM host] a connection failed: {why}");
Console.WriteLine($"[AIM host] listening on {listener.Port}; {store.Count} L3s; providers: {string.Join(", ", providers.Select(p => p.GetType().Name))}; " +
                  (anchorFile is null ? "admits by key" : "admits by the Trust Protocol"));
Console.Out.Flush();

var done = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => done.TrySetResult();
await done.Task;
