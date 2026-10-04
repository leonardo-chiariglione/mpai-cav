using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using AIF.Controller;
using AIF.Store;
using AIF.Trust;
using Xunit.Abstractions;

namespace Mpai.Aif.Tests;

// ACCESS (AIF V3.0, Storage 6; Basic API 3.9, 4.11.3, 6): the data a Module reads and
// never writes. Each Source has one Writer. A Provider writes its Sources over a link
// the Controller admitted with the Trust Protocol, each request signed by it and
// stating a Version higher than the current one; the User writes its own through the
// User Agent; an AIM only reads, through the Controller.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class AifAccessTests(ITestOutputHelper output)
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

    private static string Text(AccessStore store, string source, string key) =>
        store.Get(source, key, out var data) == AccessOutcome.OK ? Encoding.UTF8.GetString(data) : "-";

    [Fact]
    public async Task Provider()
    {
        var result = new Dictionary<string, string>();
        using var controllerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var providerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var strangerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var controllerAnchor = Key("controller-1", controllerKey).Object();

        // The Controller trusts two Providers; the stranger it does not know.
        var controller = Party("controller-1", controllerKey, Key("provider-1", providerKey).Object(), Key("provider-2", otherKey).Object());
        var store = new AccessStore(Root());
        var refusals = new List<string>();
        await using var server = new AccessProviderServer(store, controller);
        server.Refused += refusals.Add;

        await using var provider = await AccessProvider.ConnectAsync("localhost", server.Port,
            Party("provider-1", providerKey, controllerAnchor), Key("provider-1", providerKey), providerKey);
        await using var other = await AccessProvider.ConnectAsync("localhost", server.Port,
            Party("provider-2", otherKey, controllerAnchor), Key("provider-2", otherKey), otherKey);

        result["a Provider creates its Source"] = $"{await provider.MPAI_AIFP_Access_Create("maps")}; the Writer is {store.WriterOf("maps")}, Version {store.Version("maps")}";
        result["it puts an item at Version 1"] = $"{await provider.MPAI_AIFP_Access_Put("maps", 1, "turin", Encoding.UTF8.GetBytes("streets of Turin"))}; read: {Text(store, "maps", "turin")}, Version {store.Version("maps")}";
        result["it updates the item at Version 2"] = $"{await provider.MPAI_AIFP_Access_Put("maps", 2, "turin", Encoding.UTF8.GetBytes("streets of Turin, 2026"))}; read: {Text(store, "maps", "turin")}";
        result["an update at an older Version"] = $"{await provider.MPAI_AIFP_Access_Put("maps", 1, "turin", Encoding.UTF8.GetBytes("old streets"))}; read: {Text(store, "maps", "turin")}";

        // A request recorded and sent again, as it was.
        var request = provider.Signed(new JsonObject { ["Function"] = "Put", ["Source"] = "maps", ["Version"] = 3, ["Key"] = "milan", ["Data"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("streets of Milan")) });
        result["a signed request"] = $"{await provider.SendAsIs(request)}; read: {Text(store, "maps", "milan")}";
        result["the same request sent again"] = $"{await provider.SendAsIs(request)}; Version {store.Version("maps")}";

        // Altered on the way: the Data changed after it was signed.
        var altered = JsonNode.Parse(provider.Signed(new JsonObject { ["Function"] = "Put", ["Source"] = "maps", ["Version"] = 4, ["Key"] = "turin", ["Data"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("the Provider's")) }))!;
        altered["Data"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("someone else's"));
        result["a request altered after it was signed"] = $"{await provider.SendAsIs(altered.ToJsonString())}; read: {Text(store, "maps", "turin")}";

        // Not signed at all.
        result["a request not signed"] = $"{await provider.SendAsIs(new JsonObject { ["Function"] = "Delete", ["Source"] = "maps", ["Version"] = 5, ["Key"] = "turin" }.ToJsonString())}; read: {Text(store, "maps", "turin")}";

        // Signed by one trusted Provider, sent on the link of another.
        result["a request signed by the Provider of another link"] = $"{await other.SendAsIs(provider.Signed(new JsonObject { ["Function"] = "Delete", ["Source"] = "maps", ["Version"] = 6, ["Key"] = "turin" }))}; read: {Text(store, "maps", "turin")}";

        // Another trusted Provider, on its own link, writing a Source it did not create.
        result["another Provider writes the Source"] = $"{await other.MPAI_AIFP_Access_Put("maps", 10, "turin", Encoding.UTF8.GetBytes("streets"))}; read: {Text(store, "maps", "turin")}";
        result["another Provider creates the same Source"] = (await other.MPAI_AIFP_Access_Create("maps")).ToString();

        // The User, through the User Agent, writing a Provider's Source.
        result["the User writes the Provider's Source"] = $"{store.Put("maps", AccessStore.User, "turin", Encoding.UTF8.GetBytes("my streets"))}; read: {Text(store, "maps", "turin")}";

        result["the Provider deletes an item"] = $"{await provider.MPAI_AIFP_Access_Delete("maps", 7, "milan")}; listed: {string.Join(", ", store.List("maps"))}";
        result["who wrote an item, at which Version"] = store.Trace("maps", "turin", out var trace) == AccessOutcome.OK ? $"{trace!.Writer} at Version {trace.Version}" : "-";

        // A Provider the Controller does not trust: its link is not admitted.
        try
        {
            await using var stranger = await AccessProvider.ConnectAsync("localhost", server.Port,
                Party("provider-3", strangerKey, controllerAnchor), Key("provider-3", strangerKey), strangerKey);
            result["a Provider the Controller does not trust"] = $"admitted: {await stranger.MPAI_AIFP_Access_Create("stolen")}";
        }
        catch (UnauthorizedAccessException) { result["a Provider the Controller does not trust"] = $"its link refused; the Source exists: {store.Version("stolen") >= 0}"; }

        result["refused before the store"] = string.Join(" | ", refusals);
        foreach (var (k, v) in result) output.WriteLine($"{k}: {v}");
        Expected.Match("access-provider.json", result);
    }

    // The User's Sources, through the User Agent; an AIM reading, through the Controller.
    [Fact]
    public async Task UserAndAim()
    {
        var result = new Dictionary<string, string>();
        var amds = Root();
        var ua = new UserAgent(new AmdStore(amds)) { Access = new AccessStore(Root()) };

        result["the User creates a Source"] = ua.MPAI_AIFU_Access_Create("my-vocabulary").ToString();
        result["the User puts an item"] = $"{ua.MPAI_AIFU_Access_Put("my-vocabulary", "greeting", Encoding.UTF8.GetBytes("ciao"))}; Version {ua.Access!.Version("my-vocabulary")}";
        result["the User puts another"] = $"{ua.MPAI_AIFU_Access_Put("my-vocabulary", "farewell", Encoding.UTF8.GetBytes("arrivederci"))}; Version {ua.Access.Version("my-vocabulary")}";
        result["the User deletes one"] = $"{ua.MPAI_AIFU_Access_Delete("my-vocabulary", "farewell")}; Version {ua.Access.Version("my-vocabulary")}";
        result["the User deletes one not there"] = ua.MPAI_AIFU_Access_Delete("my-vocabulary", "farewell").ToString();
        result["the User writes a Source it did not create"] = $"{ua.Access.Create("maps", "provider-1")} by provider-1; the User: {ua.MPAI_AIFU_Access_Put("maps", "turin", Encoding.UTF8.GetBytes("x"))}";
        result["the User writes a Source no one created"] = ua.MPAI_AIFU_Access_Put("nothing", "k", [1]).ToString();
        result["without an Access"] = new UserAgent(new AmdStore(amds)).MPAI_AIFU_Access_Create("x").ToString();

        // An AIM of a Module reads, through its context: the only Access functions it has.
        using var host = new AimHost { Access = ua.Access };
        host.RegisterRuntime(new Reader());
        var read = await host.ProcessAsync("Reader", new Message { MessageId = "1" });
        result["an AIM reads"] = read.Payload;

        foreach (var (k, v) in result) output.WriteLine($"{k}: {v}");
        Expected.Match("access-user.json", result);
    }

    // AN AIM ON AN AIM HOST reads the Controller's Access through the link the Controller
    // opened to the host; the Controller answers reads only.
    [Fact]
    public async Task OnAnAimHost()
    {
        var result = new Dictionary<string, string>();
        var access = new AccessStore(Root());
        access.Create("my-vocabulary", AccessStore.User);
        access.Put("my-vocabulary", AccessStore.User, "greeting", Encoding.UTF8.GetBytes("ciao"));

        // Any AIM the host holds an L3 of; what it runs is the Reader.
        var store = new AmdStore(Mpai.Core.MpaiPaths.Amds); store.Scan();
        var aim = "1CAE-ASE-V1.0-I01";
        var server = new AIF.RemoteHost.AimHostServer(store, AIF.Store.AimSettings.Empty, new ReaderProvider(aim), Root());
        await using var listener = new AIF.Channels.RemoteLink.Listener(0, new AIF.Channels.KeyAdmission("k"), server.Serve);
        await using var link = await AIF.Channels.RemoteLink.ConnectAsync("localhost", listener.Port, "k");
        var asked = new List<string>();
        link.OnRequest = frame =>
        {
            asked.Add($"{frame["Function"]} {frame["Source"]}");
            return Task.FromResult<JsonObject?>((string?)frame["Kind"] == "Access"
                ? RemoteAccess.Answer(access, frame)
                : new JsonObject { ["Ok"] = false });
        };

        var placed = await link.RequestAsync(new JsonObject { ["Kind"] = "Place", ["Module"] = "M#1", ["Aim"] = aim });
        result["placed"] = placed["Ok"]?.GetValue<bool>() == true ? "yes" : placed.ToJsonString();
        var run = await link.RequestAsync(new JsonObject { ["Kind"] = "Process", ["Module"] = "M#1", ["Aim"] = aim, ["MessageId"] = "1", ["Ports"] = new JsonObject() });
        result["the AIM on the host reads"] = (string?)run["Payload"] ?? run.ToJsonString();
        result["asked of the Controller"] = string.Join(", ", asked);
        result["a write asked of the Controller"] = RemoteAccess.Answer(access, new JsonObject { ["Function"] = "Put", ["Source"] = "my-vocabulary", ["Key"] = "greeting" }).ToJsonString();

        foreach (var (k, v) in result) output.WriteLine($"{k}: {v}");
        Expected.Match("access-host.json", result);
    }

    private sealed class ReaderProvider(string aim) : IAimProvider
    {
        public bool CanCreate(string aimName) => aimName == aim;
        public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage) =>
            new Reader(aimName);
    }

    private sealed class Reader(string instanceId = "Reader") : IAimProcessor
    {
        public string InstanceId => instanceId;

        public Task<Message> ProcessAsync(Message message)
        {
            var context = message.Context;
            var got = context.MPAI_AIFM_Access_Get("my-vocabulary", "greeting", out var data);
            var missing = context.MPAI_AIFM_Access_Get("my-vocabulary", "farewell", out _);
            return Task.FromResult(message with
            {
                Payload = $"Get greeting: {got} {Encoding.UTF8.GetString(data)}; Get farewell: {missing}; " +
                          $"List: {string.Join(", ", context.MPAI_AIFM_Access_List("my-vocabulary", ""))}; " +
                          $"Version: {context.MPAI_AIFM_Access_Version("my-vocabulary")}; " +
                          $"Version of a Source not there: {context.MPAI_AIFM_Access_Version("none")}"
            });
        }
    }
}
