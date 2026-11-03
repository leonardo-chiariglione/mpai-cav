using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

using AIF.Channels;
using AIF.Controller;
using AIF.Trust;

namespace Mpai.Aif.Tests;

// THE PROCESS OF ADDING, UPDATING AND REMOVING AN ACCESS COMPONENT (AIF V3.0, Storage 6): only the Controller writes Access. A
// Provider publishes data at a URI the Metadata of an AIM names; the Controller obtains it over a link the Trust Protocol admitted
// (the Provider presents an anchor, or a credential under an anchor, that the Controller trusts), keeps it only if its SHA-256 is
// the one the Metadata gives, and records where, when and at which Version. A new SHA-256 for the Uri is a new Version of the
// Source; data the Source held before is refused; a Source leaves Access when no Module declares it any more.
[Trait("Group", "Fast")]
public class AccessProcessTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static TrustAnchorKey Key(string id, ECDsa key) => new(id, key, Now.AddHours(-1), Now.AddDays(1));
    private static TrustProtocol Party(string id, ECDsa key, params JsonObject[] trusted) => new(Key(id, key), key, trusted);

    private static string Root()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpai-access-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static AccessEntry Entry(string uri, string text) =>
        new("data", uri, Sha(text), [], false, Terms: new AccessTerms("test", "https://example.org/terms", false));

    private static HttpClient AnyCertificate() =>
        new(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });

    // A PROVIDER: an https server that holds files, and answers the TrustRequest at ProviderLink.TrustPath when it has a Trust
    // Protocol (the Provider's end of the Trust Protocol).
    private sealed class Provider : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 certificate = RemoteLink.CertificateForALink("provider");
        private readonly TrustProtocol? protocol;
        private readonly CancellationTokenSource stop = new();
        public Dictionary<string, string> Files { get; } = new();
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public Provider(TrustProtocol? protocol)
        {
            this.protocol = protocol;
            listener.Start();
            _ = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(stop.Token); } catch { return; }
                    _ = Task.Run(() => Serve(client));
                }
            });
        }

        public string Uri(string path) => $"https://localhost:{Port}{path}";

        private async Task Serve(TcpClient client)
        {
            using var _ = client;
            try
            {
                using var tls = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate, ClientCertificateRequired = true });
                while (true)
                {
                    var header = new StringBuilder();
                    var one = new byte[1];
                    while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        if (await tls.ReadAsync(one) == 0) return;
                        header.Append((char)one[0]);
                    }
                    var lines = header.ToString().Split("\r\n");
                    var request = lines[0].Split(' ');
                    var length = lines.Where(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Select(l => int.Parse(l[15..].Trim())).FirstOrDefault();
                    var body = new byte[length];
                    for (var read = 0; read < length;) read += await tls.ReadAsync(body.AsMemory(read));
                    byte[] answer; var status = "200 OK";
                    if (request[1] == ProviderLink.TrustPath)
                    {
                        if (protocol is null) { status = "404 Not Found"; answer = []; }
                        else if (request[0] == "POST") answer = Encoding.UTF8.GetBytes(ProviderLink.Answer(protocol, Encoding.UTF8.GetString(body), certificate, tls.RemoteCertificate));
                        else answer = [];
                    }
                    else if (Files.TryGetValue(request[1], out var text)) answer = Encoding.UTF8.GetBytes(text);
                    else { status = "404 Not Found"; answer = []; }
                    var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: {answer.Length}\r\nContent-Type: application/octet-stream\r\n\r\n");
                    await tls.WriteAsync(head); await tls.WriteAsync(answer); await tls.FlushAsync();
                }
            }
            catch { }
        }

        public ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); return ValueTask.CompletedTask; }
    }

    private static string Text(AccessStore store, string source, string key) =>
        store.Get(source, key, out var data) == AccessOutcome.OK ? Encoding.UTF8.GetString(data) : "-";

    // ADD: a Provider the Controller trusts is admitted and its data kept; the Trace says where, when and at which Version.
    [Fact]
    public async Task TheDataOfAnAdmittedProviderIsKept()
    {
        using var controllerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var providerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var controller = Party("controller-1", controllerKey, Key("provider-1", providerKey).Object());
        await using var provider = new Provider(Party("provider-1", providerKey, Key("controller-1", controllerKey).Object()));
        provider.Files["/maps.txt"] = "streets of Turin";
        var store = new AccessStore(Root());
        var fetcher = new AccessFetcher(store, AnyCertificate()) { Trust = controller };
        var entry = Entry(provider.Uri("/maps.txt"), "streets of Turin");

        Assert.Equal(AccessOutcome.OK, fetcher.Ensure(entry, "aim", out var why));
        Assert.Equal("", why);
        Assert.Equal("streets of Turin", Text(store, entry.Uri, "maps.txt"));
        Assert.Equal(1, store.Version(entry.Uri));
        Assert.Equal(AccessOutcome.OK, store.Trace(entry.Uri, "maps.txt", out var trace));
        Assert.Equal(AccessStore.Controller, trace!.Writer);
        Assert.Equal(entry.Uri, trace.From);
        Assert.Equal(["aim"], trace.Readers);
    }

    // REFUSED: a Provider the Controller does not trust, one that cannot be admitted at all, and data that is not the data named.
    [Fact]
    public async Task AProviderThatIsNotAdmittedOrWhoseDataIsNotTheDataNamedGivesNothing()
    {
        using var controllerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var providerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var strangerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var controller = Party("controller-1", controllerKey, Key("provider-1", providerKey).Object());
        var store = new AccessStore(Root());
        var fetcher = new AccessFetcher(store, AnyCertificate()) { Trust = controller };

        // no credential the Controller trusts: the anchor of a stranger
        await using var stranger = new Provider(Party("provider-9", strangerKey, Key("controller-1", controllerKey).Object()));
        stranger.Files["/maps.txt"] = "streets of Turin";
        var unknown = Entry(stranger.Uri("/maps.txt"), "streets of Turin");
        Assert.Equal(AccessOutcome.Refused, fetcher.Ensure(unknown, "aim", out var why));
        Assert.Contains("not admitted", why);
        Assert.Equal(-1, store.Version(unknown.Uri));

        // a host that does not speak the Trust Protocol
        await using var plain = new Provider(null);
        plain.Files["/maps.txt"] = "streets of Turin";
        var silent = Entry(plain.Uri("/maps.txt"), "streets of Turin");
        Assert.Equal(AccessOutcome.Refused, fetcher.Ensure(silent, "aim", out why));
        Assert.Equal(-1, store.Version(silent.Uri));

        // an admitted Provider whose data is not the data the Metadata names
        await using var provider = new Provider(Party("provider-1", providerKey, Key("controller-1", controllerKey).Object()));
        provider.Files["/maps.txt"] = "streets of Milan";
        var wrong = Entry(provider.Uri("/maps.txt"), "streets of Turin");
        Assert.Equal(AccessOutcome.Refused, fetcher.Ensure(wrong, "aim", out why));
        Assert.Contains("is not the data its Metadata names", why);
        Assert.Equal(-1, store.Version(wrong.Uri));

        // a Provider reached without https is not admitted either
        Assert.Equal(AccessOutcome.Refused, fetcher.Ensure(Entry("http://localhost:1/maps.txt", "x"), "aim", out why));
        Assert.Contains("https", why);
    }

    // OPENLY PUBLISHED data: a host the Controller was told is open is obtained without admission, by its SHA-256 alone.
    [Fact]
    public async Task OpenlyPublishedDataIsObtainedByItsHashAlone()
    {
        using var controllerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var store = new AccessStore(Root());
        await using var host = new Provider(null);
        host.Files["/open.txt"] = "public data";
        var fetcher = new AccessFetcher(store, AnyCertificate()) { Trust = Party("controller-1", controllerKey) };
        var entry = Entry(host.Uri("/open.txt"), "public data");
        Assert.Equal(AccessOutcome.Refused, fetcher.Ensure(entry, "aim", out _));
        fetcher.OpenHosts.Add("localhost");
        Assert.Equal(AccessOutcome.OK, fetcher.Ensure(entry, "aim", out _));
        Assert.Equal("public data", Text(store, entry.Uri, "open.txt"));
    }

    // UPDATE: a new SHA-256 for the Uri is a new Version of the Source that the AIMs see; the same data is no update; the data a
    // Source held before is refused, and a mirror that serves other data is refused.
    [Fact]
    public async Task AnUpdateIsANewVersionAndASourceIsNeverBroughtBack()
    {
        using var controllerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var providerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var controller = Party("controller-1", controllerKey, Key("provider-1", providerKey).Object());
        await using var provider = new Provider(Party("provider-1", providerKey, Key("controller-1", controllerKey).Object()));
        provider.Files["/maps.txt"] = "streets of Turin";
        var store = new AccessStore(Root());
        var fetcher = new AccessFetcher(store, AnyCertificate()) { Trust = controller };
        var uri = provider.Uri("/maps.txt");

        var first = fetcher.Refresh([Entry(uri, "streets of Turin")], "aim");
        Assert.True(first[0].Updated); Assert.Equal(1, store.Version(uri));
        Assert.False(fetcher.Refresh([Entry(uri, "streets of Turin")], "aim")[0].Updated);    // the same data: no new Version
        Assert.Equal(1, store.Version(uri));

        // the Provider publishes new data, the Metadata names its SHA-256
        provider.Files["/maps.txt"] = "streets of Turin, 2026";
        var second = fetcher.Refresh([Entry(uri, "streets of Turin, 2026")], "aim");
        Assert.True(second[0].Updated); Assert.Equal(AccessOutcome.OK, second[0].Outcome);
        Assert.Equal(2, store.Version(uri));
        Assert.Equal("streets of Turin, 2026", Text(store, uri, "maps.txt"));
        var aim = store.As(new AIF.SharedStorage.StorageHolder("M", "aim"));
        Assert.Equal(2, aim.Version(uri));                                                    // what an AIM reads: MPAI_AIFM_Access_Version

        // a Metadata that names the data the Source held before: refused, the Source stays as it is
        provider.Files["/maps.txt"] = "streets of Turin";
        var back = fetcher.Refresh([Entry(uri, "streets of Turin")], "aim");
        Assert.Equal(AccessOutcome.Refused, back[0].Outcome); Assert.False(back[0].Updated);
        Assert.Equal(2, store.Version(uri));
        Assert.Equal("streets of Turin, 2026", Text(store, uri, "maps.txt"));

        // the data served is not the data named (a changed Provider, a mirror): refused, nothing changes
        provider.Files["/maps.txt"] = "something else";
        var other = fetcher.Refresh([Entry(uri, "streets of Turin, 2027")], "aim");
        Assert.Equal(AccessOutcome.Refused, other[0].Outcome);
        Assert.Equal(2, store.Version(uri));
    }

    // REMOVE: the Sources that no Module's Metadata declares leave Access, with a record; a declared Source is not removed; an AIM
    // has no call that deletes.
    [Fact]
    public void ASourceNoModuleDeclaresLeavesAccessAndADeclaredOneDoesNot()
    {
        var work = Root();
        var kept = Path.Combine(work, "kept.txt"); File.WriteAllText(kept, "kept");
        var gone = Path.Combine(work, "gone.txt"); File.WriteAllText(gone, "gone");
        var store = new AccessStore(Root());
        var fetcher = new AccessFetcher(store);
        var keptEntry = Entry(new Uri(kept).AbsoluteUri, "kept");
        var goneEntry = Entry(new Uri(gone).AbsoluteUri, "gone");
        Assert.Equal(AccessOutcome.OK, fetcher.Ensure(keptEntry, "aim", out _));
        Assert.Equal(AccessOutcome.OK, fetcher.Ensure(goneEntry, "aim", out _));

        var (removed, still) = fetcher.Prune(AccessFetcher.Declared([keptEntry]));
        Assert.Equal([goneEntry.Uri], removed);
        Assert.Equal([keptEntry.Uri], still);
        Assert.Equal(-1, store.Version(goneEntry.Uri));
        Assert.True(store.Version(keptEntry.Uri) >= 0);
        Assert.Equal("kept", Text(store, keptEntry.Uri, "kept.txt"));
        Assert.Equal(AccessOutcome.NotFound, store.Remove(goneEntry.Uri));
        Assert.Contains(goneEntry.Uri, File.ReadAllText(Path.Combine(StoreRoot(store), "access-removed.log")), StringComparison.Ordinal);

        // what was removed is obtained again only when a Module declares it again
        Assert.Equal(AccessOutcome.OK, fetcher.Ensure(goneEntry, "aim", out _));
        Assert.Equal(2, store.Version(goneEntry.Uri));                          // a Source made again goes on from the Version it had (it only rises)

        // an AIM cannot write Access: the Controller answers reads only
        var refused = RemoteAccess.Answer(store, new JsonObject { ["Function"] = "Delete", ["Source"] = goneEntry.Uri, ["Key"] = "gone.txt" });
        Assert.False(refused["Ok"]!.GetValue<bool>());
        Assert.True(store.Version(goneEntry.Uri) >= 0);
    }

    private static string StoreRoot(AccessStore store) =>
        (string)typeof(AccessStore).GetField("root", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(store)!;
}
