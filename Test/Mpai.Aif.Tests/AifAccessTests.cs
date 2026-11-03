using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using AIF.Controller;
using AIF.Store;
using AIF.Trust;
using Xunit.Abstractions;

namespace Mpai.Aif.Tests;

// ACCESS (AIF V3.0, Storage 6; Basic API 3.9, 4.11.3): the data a Module reads and
// never writes. Only the Controller writes it - it obtains it from the URIs the Metadata
// of an AIM names (AccessProcessTests); an AIM only reads, through the Controller.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class AifAccessTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static string Root()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpai-access-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string Text(AccessStore store, string source, string key) =>
        store.Get(source, key, out var data) == AccessOutcome.OK ? Encoding.UTF8.GetString(data) : "-";

    // What the Controller keeps, an AIM that declares it reads through the Controller, and an AIM that declares nothing does not.
    [Fact]
    public async Task AnAimReadsWhatItDeclares()
    {
        var result = new Dictionary<string, string>();
        var access = new AccessStore(Root());
        result["the Controller creates a Source"] = access.Create("my-vocabulary/").ToString();
        result["the Controller puts an item"] = $"{access.Put("my-vocabulary/", "greeting", Encoding.UTF8.GetBytes("ciao"), "Data", ["Reader"])}; Version {access.Version("my-vocabulary/")}";
        result["it puts another"] = $"{access.Put("my-vocabulary/", "farewell", Encoding.UTF8.GetBytes("arrivederci"))}; Version {access.Version("my-vocabulary/")}";
        result["it deletes one"] = $"{access.Delete("my-vocabulary/", "farewell")}; Version {access.Version("my-vocabulary/")}";
        result["it deletes one not there"] = access.Delete("my-vocabulary/", "farewell").ToString();

        // An AIM of a Module reads, through its context: the only Access functions it has.
        using var host = new AimHost { Access = access };
        host.RegisterRuntime(new Reader());
        host.RegisterRuntime(new Reader("Stranger"));
        host.BindAccess("Reader", [new AccessEntry("vocabulary", "my-vocabulary/", "", [], false)]);     // what its L3 declares
        var read = await host.ProcessAsync("Reader", new Message { MessageId = "1" });
        result["an AIM reads"] = read.Payload;
        result["an AIM that declares nothing reads"] = (await host.ProcessAsync("Stranger", new Message { MessageId = "2" })).Payload;

        foreach (var (k, v) in result) output.WriteLine($"{k}: {v}");
        Expected.Match("access-reads.json", result);
    }

    // AN AIM ON AN AIM HOST reads the Controller's Access through the link the Controller
    // opened to the host; the Controller answers reads only.
    [Fact]
    public async Task OnAnAimHost()
    {
        var result = new Dictionary<string, string>();
        var access = new AccessStore(Root());
        access.Create("my-vocabulary/");
        access.Put("my-vocabulary/", "greeting", Encoding.UTF8.GetBytes("ciao"));

        // Any AIM the host holds an L3 of; what it runs is the Reader.
        // The L3 of this AIM declares what it reads (AccessSources): a copy of the repository's, with that added.
        var amds = Root();
        foreach (var f in Directory.EnumerateFiles(Mpai.Core.MpaiPaths.Amds, "1CAE-ASE-V1.0-I01.json"))
        {
            var l3 = JsonNode.Parse(File.ReadAllText(f))!.AsObject();
            l3["AccessSources"] = new JsonArray(new JsonObject { ["Name"] = "vocabulary", ["Uri"] = "my-vocabulary/", ["Sha256"] = new string('0', 64) });
            File.WriteAllText(Path.Combine(amds, Path.GetFileName(f)), l3.ToJsonString());
        }
        var store = new AmdStore(amds); store.Scan();
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
        result["a write asked of the Controller"] = RemoteAccess.Answer(access, new JsonObject { ["Function"] = "Put", ["Source"] = "my-vocabulary/", ["Key"] = "greeting" }).ToJsonString();

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
            var got = context.MPAI_AIFM_Access_Get("my-vocabulary/", "greeting", out var data);
            var missing = context.MPAI_AIFM_Access_Get("my-vocabulary/", "farewell", out _);
            return Task.FromResult(message with
            {
                Payload = $"Get greeting: {got} {Encoding.UTF8.GetString(data)}; Get farewell: {missing}; " +
                          $"List: {string.Join(", ", context.MPAI_AIFM_Access_List("my-vocabulary/", ""))}; " +
                          $"Version: {context.MPAI_AIFM_Access_Version("my-vocabulary/")}; " +
                          $"Version of a Source not there: {context.MPAI_AIFM_Access_Version("none")}"
            });
        }
    }

    // DATA THE USER AGENT DECLARES AND READS, that no AIM declares - a catalogue of avatars many Apps share: obtained and checked
    // by the Controller, read through the Controller API, and not readable by an AIM that is not among its readers.
}
