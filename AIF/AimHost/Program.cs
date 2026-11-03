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
//
// With a root of trust (M3223 3.5): --anchor names a party provisioned in a TPM
// (TpmParty) - the host's code is measured into it at start, each Implementation
// before it is built, and the Controller is given its quote. --manufacturer (the
// certificate of the manufacturer of roots of trust it accepts) and --reference (the
// Controller code it approves: a JSON object, file to SHA-256) require the same of
// the Controller; the Implementations it measured are judged by this host's Store.

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

// THE NATIVE LIBRARIES OF THE AIMs IT HOSTS (ONNX Runtime and the like): the host's own dependency file lists none, so a library the AIMs ask for
// is looked for beside the provider that holds them, in runtimes/<platform>/native, as a Service finds it beside its own program.
var providerFolders = Args("--provider").Select(spec => Path.GetDirectoryName(Path.GetFullPath(spec[..spec.LastIndexOf(':')]))!).Distinct().ToList();
System.Runtime.Loader.AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, name) =>
{
    var platform = OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsMacOS() ? "osx-x64" : "linux-x64";
    var file = Path.HasExtension(name) ? name : name + (OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so");
    foreach (var folder in providerFolders)
    {
        var path = Path.Combine(folder, "runtimes", platform, "native", file);
        if (File.Exists(path)) return System.Runtime.InteropServices.NativeLibrary.Load(path);
    }
    return IntPtr.Zero;
};

// ONNX Runtime FIRST, FROM ITS OWN FOLDER: Windows holds an onnxruntime.dll of its own in System32, which a library asked for by name finds before the
// one of the AIMs and which lacks the entry points they call; a library already loaded is the one the system gives for its name.
foreach (var folder in providerFolders)
{
    var ort = Path.Combine(folder, "runtimes", OperatingSystem.IsWindows() ? "win-x64" : "linux-x64", "native", OperatingSystem.IsWindows() ? "onnxruntime.dll" : "libonnxruntime.so");
    if (File.Exists(ort)) { System.Runtime.InteropServices.NativeLibrary.TryLoad(ort, out _); break; }
}

// THE HOST'S REPOSITORY OF AIMs (--identity, --store-key): the host's identity - its Instance Credential, issued under a Trust Anchor, and the key of that anchor - and the keys of the
// MPAI Store, with which it checks the AIM Licence of an AIM it is handed before it builds it. The repository's folder is the one of the L3s (--amds).
AIF.Store.AimRepository? repository = null;
if (Arg("--identity") is { } identityFile)
{
    var identity = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(identityFile))!.AsObject();
    var anchors = identity["Anchors"]!.AsObject().ToDictionary(a => a.Key, a =>
    {
        var key = System.Security.Cryptography.ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromHexString((string)a.Value!), out _);
        return key;
    });
    var storeKeys = Args("--store-key").Select(k => k.Split('=', 2)).ToDictionary(k => k[0], k =>
    {
        var key = System.Security.Cryptography.ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromHexString(k[1]), out _);
        return key;
    });
    repository = new AIF.Store.AimRepository(amds)
    {
        ExecutesApart = true,
        Identity = new AIF.Store.AimLicences.ControllerIdentity((string)identity["ControllerId"]!, (System.Text.Json.Nodes.JsonObject)identity["Credential"]!, (System.Text.Json.Nodes.JsonObject)identity["Cii"]!, [],
                                                                id => anchors.GetValueOrDefault(id)),
        StoreKeys = id => storeKeys.GetValueOrDefault(id)
    };
}

var providers = Args("--provider").Select(spec =>
{
    var colon = spec.LastIndexOf(':');
    var assembly = Assembly.LoadFrom(Path.GetFullPath(spec[..colon]));
    var type = assembly.GetType(spec[(colon + 1)..], throwOnError: true)!;
    return (IAimProvider)(repository is not null && type.GetConstructor([typeof(AmdStore), typeof(AIF.Store.AimRepository)]) is { } withRepository
        ? withRepository.Invoke([store, repository])
        : type.GetConstructor([typeof(AmdStore)]) is { } withStore
        ? withStore.Invoke([store])
        : Activator.CreateInstance(type)!);
}).ToList();

AIF.RootOfTrust.SimulatedTpm? tpm = null;
AIF.Trust.TrustAnchorKey? tpmAnchor = null;
if (anchorFile is not null && AIF.RootOfTrust.TpmParty.Is(anchorFile))
{
    (tpmAnchor, tpm) = AIF.RootOfTrust.TpmParty.Load(anchorFile);
    AIF.Trust.PartyCode.MeasureInto(tpm, AppContext.BaseDirectory);
    Console.WriteLine($"[AIM host] its root of trust: the TPM of {tpm.Name}; its code measured ({tpm.Log.Count} files)");
}
var server = new AimHostServer(store, settings, new CompositeProvider(providers.ToArray()), storage) { RootOfTrust = tpm, Repository = repository };
ILinkAdmission admission = new KeyAdmission(key ?? "");
if (anchorFile is not null)
{
    var (anchor, anchorKey) = tpmAnchor is not null ? (tpmAnchor, tpm!.IdentityKey) : AIF.Trust.TrustAnchorKey.Load(anchorFile);
    AIF.Trust.Attestation.Policy? requires = null;
    if (Arg("--manufacturer") is { } manufacturer)
    {
        var reference = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Arg("--reference")
                            ?? throw new ArgumentException("--manufacturer requires --reference: the Controller code this host approves.")))!
                        .AsObject().ToDictionary(p => p.Key, p => (string)p.Value!, StringComparer.Ordinal);
        var approvals = new ImplementationFingerprints(amds);
        requires = new AIF.Trust.Attestation.Policy(AIF.RootOfTrust.TpmParty.Manufacturer(manufacturer), m => m.Register == AIF.Trust.Attestation.CodeRegister
            ? AIF.Trust.PartyCode.Approve(reference, m)
            : ImplementationEvidence.Approve(approvals, m));
    }
    var trusted = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(trustFile!)) switch
    {
        System.Text.Json.Nodes.JsonArray many => many.Select(a => a!.AsObject()).ToList(),
        System.Text.Json.Nodes.JsonObject one => [one],
        _ => throw new ArgumentException($"{trustFile}: not a Trust Anchor object, nor an array of them.")
    };
    admission = new TrustedLink(new AIF.Trust.TrustProtocol(anchor, anchorKey, trusted, null, tpm, requires))
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
