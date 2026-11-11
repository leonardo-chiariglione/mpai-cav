using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Mpai.Aif.Api;
using AIF.Controller;
using Mpai.Aif.PortData;
using Mpai.Mas.Client;
using Mpai.Mas.Server;
using Xunit;

namespace Mpai.Aif.Tests;

// MPAI-MAS V1.1 END TO END, IN THIS PROCESS: the MAS server over an echo Module and the MAS client. A boundary Port is two path segments; the body
// is a JSON object or a Bout Instance; Bouts in both directions, the RCA giving its own with the Instance Credential the SCI issued at creation.
public sealed class MasBoutTests
{
    private const string Text = "OSD-BTO-V1.5";

    private sealed class EchoRunner : IModuleRunner
    {
        public readonly List<string> Received = [];
        public IReadOnlyList<BoundaryPort> PortsOf(string moduleName) =>
        [
            new BoundaryPort { Direction = "Input", DataType = Text, DataTypes = [Text], PortNumber = 1, Bout = true },
            new BoundaryPort { Direction = "Output", DataType = Text, DataTypes = [Text], PortNumber = 1, Bout = true },
            new BoundaryPort { Direction = "Output", DataType = Text, DataTypes = [Text], PortNumber = 2 }
        ];
        public string? Start(string moduleName) => null;
        public RunResult Run(string moduleName, IReadOnlyDictionary<string, string> inputs)
        {
            var input = inputs.TryGetValue(Text + "#1", out var v) ? v : "";
            lock (Received) Received.Add(input);
            return new RunResult { Outputs = new Dictionary<string, string> { [Text + "#1"] = input, [Text + "#2"] = input } };
        }
        public void Stop(string moduleName) { }
    }

    private static readonly Lazy<(string Url, EchoRunner Runner)> Server = new(() =>
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var url = $"http://127.0.0.1:{port}";
        var runner = new EchoRunner();
        _ = Task.Run(() => new MasServer(runner, PortDataCodecs.Default(), url).RunAsync());
        using var probe = new HttpClient();
        for (var i = 0; i < 100; i++)
        {
            try { probe.GetAsync(url + "/MPAI/AIFU/Status").GetAwaiter().GetResult(); break; }
            catch (HttpRequestException) { Thread.Sleep(100); }
        }
        return (url, runner);
    });

    private static string TextJson(string s) =>
        PortDataCodecs.Default().ToInternal(Text, Encoding.UTF8.GetBytes(new JsonObject
        {
            ["Header"] = Text, ["BasicTextObjectID"] = "T-" + s, ["BasicTextData"] = new JsonArray(new JsonObject { ["Data"] = s })
        }.ToJsonString()));

    private static string? TextOf(string? internalJson) =>
        internalJson is null ? null : (string?)JsonNode.Parse(PortDataCodecs.Default().ToWire(Text, internalJson))?["BasicTextData"]?[0]?["Data"];

    [Fact]
    public void Paths_APortIsTwoSegments_AndOtherFormsAre404()
    {
        var (url, _) = Server.Value;
        using var http = new HttpClient();
        var created = JsonNode.Parse(http.PostAsync(url + "/MPAI/AIFU/Controller", null).Result.Content.ReadAsStringAsync().Result)!;
        var cid = (string)created["id"]!;
        Assert.NotNull(created["anchor"]);
        var started = JsonNode.Parse(http.PostAsync($"{url}/MPAI/AIFU/{cid}/MODULE/Start",
            new StringContent("{\"module\":\"echo\"}", Encoding.UTF8, "application/json")).Result.Content.ReadAsStringAsync().Result)!;
        var mid = (string)started["id"]!;
        var body = Encoding.UTF8.GetString(PortDataCodecs.Default().ToWire(Text, TextJson("x")));
        HttpStatusCode Post(string path) => http.PostAsync($"{url}/MPAI/AIFU/{cid}/MODULE/{mid}/Input/{path}", new StringContent(body, Encoding.UTF8, "application/json")).Result.StatusCode;

        Assert.Equal(HttpStatusCode.OK, Post("OSD-BTO-V1.5/1"));
        Assert.Equal(HttpStatusCode.NotFound, Post("OSD-BTO-V1.5"));          // the V1.0 one-segment form
        Assert.Equal(HttpStatusCode.NotFound, Post("OSD-BTO-V1.5:1"));
        Assert.Equal(HttpStatusCode.NotFound, Post("OSD-BTO-V1.5%231"));      // <DataType>#<PortNumber>, encoded
        Assert.Equal(HttpStatusCode.NotFound, Post("OSD-BTO-V1.5/01"));       // a leading zero
        Assert.Equal(HttpStatusCode.NotFound, Post("osd-bto-v1.5/1"));        // not the canonical form
        Assert.Equal(HttpStatusCode.UnsupportedMediaType,
            http.PostAsync($"{url}/MPAI/AIFU/{cid}/MODULE/{mid}/Input/OSD-BTO-V1.5/1", new StringContent(body, Encoding.UTF8, "text/plain")).Result.StatusCode);
    }

    [Fact]
    public void OutputBout_TheSciGivesSignedFramesThatTheClientVerifiesAndDecompresses()
    {
        var (url, _) = Server.Value;
        using var client = new RemoteControllerApi(url);
        Assert.Equal(AifError.OK, client.StartFlow("echo"));

        // A Port whose AIM Description states no Bout is not given as one.
        Assert.Equal(AifError.NotAuthorised, client.OutputBoutOpen("echo", Text, 2));

        Assert.Equal(AifError.OK, client.OutputBoutOpen("echo", Text, 1));
        for (var k = 1; k <= 4; k++)
        {
            Assert.Equal(AifError.OK, client.InputWrite("echo", Text, 1, TextJson($"turn {k}")));
            var read = client.OutputRead("echo", Text, 1);
            Assert.Equal(AifError.OK, read.Error);
            Assert.Equal($"turn {k}", TextOf(read.Json));
        }
        Assert.Equal(3, client.BoutInstancesReceived);                       // the first output made the Bout; the next three were frames
        Assert.Equal(AifError.OK, client.OutputBoutClose("echo", Text, 1));
    }

    [Fact]
    public void InputBout_TheClientGivesABoutWithTheCredentialTheSciIssued()
    {
        var (url, runner) = Server.Value;
        using var client = new RemoteControllerApi(url) { GivesBouts = true };
        Assert.Equal(AifError.OK, client.StartFlow("echo"));
        Assert.NotNull(client.Credential);
        Assert.Equal("ProcessInstance", (string?)client.Credential!["Subject"]!["InstanceType"]);

        Assert.Equal(AifError.OK, client.InputBoutGive("echo", Text, 1, TextJson("first")));
        for (var k = 1; k <= 3; k++)
        {
            Assert.Equal(AifError.OK, client.InputWrite("echo", Text, 1, TextJson($"compressed {k}")));
            Assert.Equal($"compressed {k}", TextOf(client.OutputRead("echo", Text, 1).Json));
        }
        Assert.Equal(3, client.BoutInstancesSent);
        Assert.Equal(AifError.OK, client.InputBoutEnd("echo", Text, 1));
    }

    [Fact]
    public void InputBout_WithoutACredential_IsRefused()
    {
        var (url, _) = Server.Value;
        using var client = new RemoteControllerApi(url);
        Assert.Equal(AifError.OK, client.StartFlow("echo"));
        Assert.Null(client.Credential);
        Assert.Equal(AifError.NotTrusted, client.InputBoutGive("echo", Text, 1, TextJson("first")));
    }

    [Fact]
    public async Task BrowserClient_GivesAndReceivesBouts()
    {
        var (url, _) = Server.Value;
        using var http = new HttpClient { BaseAddress = new Uri(url + "/") };
        // Keys that answer only asynchronously, as the browser's Web Crypto does.
        var client = new RemoteControllerApiAsync(http)
        {
            GivesBouts = true,
            NewKey = () => Task.FromResult<AIF.Bout.BoutKey>(new BoutTests.AsyncOnlyKey(System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256))),
            KeyFromSpki = spki =>
            {
                var k = System.Security.Cryptography.ECDsa.Create();
                k.ImportSubjectPublicKeyInfo(spki, out _);
                return Task.FromResult<AIF.Bout.BoutKey?>(new BoutTests.AsyncOnlyKey(k));
            }
        };
        Assert.Equal(AifError.OK, await client.StartFlowAsync("echo"));
        Assert.NotNull(client.Credential);

        Assert.Equal(AifError.OK, await client.OutputBoutOpenAsync("echo", Text, 1, AIF.Bout.BoutSecurity.Chain, 1));
        Assert.Equal(AifError.OK, await client.InputBoutGiveAsync("echo", Text, 1, TextJson("first")));
        for (var k = 1; k <= 3; k++)
        {
            Assert.Equal(AifError.OK, await client.InputWriteAsync("echo", Text, 1, TextJson($"both ways {k}")));
            Assert.Equal($"both ways {k}", TextOf((await client.OutputReadAsync("echo", Text, 1)).Json));
        }
        Assert.Equal(3, client.BoutInstancesSent);
        Assert.Equal(2, client.BoutInstancesReceived);                       // the first output made the Bout
        Assert.Equal(AifError.OK, await client.InputBoutEndAsync("echo", Text, 1));
        Assert.Equal(AifError.OK, await client.OutputBoutCloseAsync("echo", Text, 1));
    }
}
