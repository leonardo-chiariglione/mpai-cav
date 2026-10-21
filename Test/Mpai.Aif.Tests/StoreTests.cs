using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AIF.Store;

namespace Mpai.Aif.Tests;

// The MPAI Store of this repository: only an Implementer that MPAI approved may submit, and signs what it submits; the whole L3 is validated - against the AIM Metadata schema (L1),
// then against its L2, the one its Header names - and one that is not an instance of its L2 is refused; the fingerprint of an Implementation in the packages folder is recorded
// and never given out: it is asked about (verify). The Store runs from its own build output, on a free port, with a Store folder of its own that is deleted afterwards.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class StoreTests
{
    public const string Admin = "test-admin-key";

    // A running Store Service, its folder, its packages folder; stopped and removed at Dispose.
    public sealed class RunningStore : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "mpai-store-test-" + Guid.NewGuid().ToString("N"));
        public string Packages => Path.Combine(Root, "packages");
        public string StoreFolder => Path.Combine(Root, "store");
        public string Url { get; }
        public HttpClient Http { get; }
        private readonly Process process;
        private readonly StringBuilder log = new();

        public RunningStore(string extraArguments = "")
        {
            var exe = Path.Combine(Repository.Root, "Store", "Service", "bin", "Debug", "net10.0", "StoreService.dll");        // run by dotnet: no apphost to be blocked
            Skip.IfNot(File.Exists(exe), "The Store is not built: " + exe);
            Directory.CreateDirectory(Packages);
            Url = $"http://127.0.0.1:{FreePort()}";
            process = Process.Start(new ProcessStartInfo("dotnet",
                $"\"{exe}\" --Urls {Url} --Root \"{StoreFolder}\" --Packages \"{Packages}\" --Schemas \"{Repository.Schemas}\" --AdminKey {Admin} {extraArguments}")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!;
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            Http = new HttpClient { BaseAddress = new Uri(Url), Timeout = TimeSpan.FromSeconds(30) };
            Ready().GetAwaiter().GetResult();
        }

        public string Log { get { lock (log) return log.ToString(); } }

        private async Task Ready()
        {
            var until = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < until)
            {
                try { using var r = await Http.GetAsync("/MPAI/Store/L3"); if (r.IsSuccessStatusCode) return; }
                catch (HttpRequestException) { }
                await Task.Delay(250);
            }
            throw new TimeoutException("The Store did not answer within 30 s:\n" + Log);
        }

        public void Dispose()
        {
            Http.Dispose();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); process.WaitForExit(5000); } catch { }
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }

    // AN IMPLEMENTER, as the Store App does it: registers, gives the Store the identity of its key with the registration code, and - once MPAI approves - signs what it submits.
    public sealed class Implementer
    {
        public string Id { get; private set; } = "";
        public string Code { get; private set; } = "";
        public ImplementerKey Key { get; } = ImplementerKey.Create();

        public static async Task<Implementer> Register(HttpClient http, string kind = "Human", string name = "A. Implementer")
        {
            using var response = await http.PostAsync("/MPAI/Store/Implementer",
                new StringContent(new JsonObject { ["kind"] = kind, ["name"] = name, ["organisation"] = "Test", ["email"] = "a@example.org" }.ToJsonString(), Encoding.UTF8, "application/json"));
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.Created, body);
            var json = JsonNode.Parse(body)!;
            return new Implementer { Id = (string)json["implementerId"]!, Code = (string)json["registrationCode"]! };
        }

        public async Task BindKey(HttpClient http)
        {
            using var response = await http.PostAsync($"/MPAI/Store/Implementer/{Id}/key", new StringContent(
                new JsonObject { ["code"] = Code, ["cii"] = Key.Cii(Id, DateTimeOffset.UtcNow) }.ToJsonString(), Encoding.UTF8, "application/json"));
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        }

        public static async Task Approve(HttpClient http, string id)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/MPAI/Store/Implementer/{id}/approve");
            request.Headers.Add("MPAI-Admin", Admin);
            using var response = await http.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        }

        public static async Task<Implementer> Approved(HttpClient http, string kind = "Human", string name = "A. Implementer")
        {
            var who = await Register(http, kind, name);
            await who.BindKey(http);
            await Approve(http, who.Id);
            return who;
        }
    }

    [SkippableFact]
    public async Task RefusesWhatDoesNotValidate()
    {
        using var store = new RunningStore();
        var http = store.Http;
        var me = await Implementer.Approved(http);
        var asr = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Amds, "1MMC-ASR-V2.5-I01.json")))!;
        asr["Identifier"]!["ImplementerID"] = me.Id;

        // It validates: published, and checked against the L2 its Header names.
        var ok = await Submit(http, asr, me.Key);
        Assert.True(ok.Status == HttpStatusCode.Created, $"a valid L3 was not published: {ok.Status} {ok.Body}\n{store.Log}");
        Assert.DoesNotContain("has no Header", ok.Body);
        Assert.DoesNotContain("No L2 of", ok.Body);

        // Without its Header it is not a valid instance of the AIM Metadata schema (L1): refused, and said.
        var headless = asr.DeepClone();
        headless.AsObject().Remove("Header");
        var refused = await Submit(http, headless, me.Key);
        Assert.True(refused.Status == HttpStatusCode.UnprocessableEntity, $"an invalid L3 was not refused: {refused.Status} {refused.Body}");
        Assert.Contains("violations", refused.Body);
        Assert.Contains("Header", refused.Body);

        // An input the L2 of ASR does not have: refused, and the nonconformity said.
        var extra = asr.DeepClone();
        extra["ExternalPorts"]!.AsArray().Add(new JsonObject
        {
            ["Name"] = "Extra", ["Direction"] = "Input", ["DataType"] = "OSD-BVO-V1.5",
            ["Technology"] = "Software", ["Protocol"] = "", ["IsRemote"] = false
        });
        var nonconforming = await Submit(http, extra, me.Key);
        Assert.True(nonconforming.Status == HttpStatusCode.UnprocessableEntity, $"an L3 not an instance of its L2 was not refused: {nonconforming.Status} {nonconforming.Body}");
        Assert.Contains("nonconformities", nonconforming.Body);
        Assert.Contains("OSD-BVO-V1.5", nonconforming.Body);
    }

    // ONLY AN IMPLEMENTER THAT MPAI APPROVED SUBMITS, and signs what it submits (STR-4); an approved Implementation is fingerprinted, and the fingerprint is asked about, not given.
    [SkippableFact]
    public async Task OnlyAnApprovedImplementerSubmits_AndTheFingerprintIsRecordedAndVerified()
    {
        using var store = new RunningStore();
        var http = store.Http;
        var package = Path.Combine(store.Packages, "asr");
        Directory.CreateDirectory(package);
        File.WriteAllBytes(Path.Combine(package, "Mpai.Mmc.Asr.dll"), Encoding.UTF8.GetBytes("the binary of the Implementation"));

        var asr = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Amds, "1MMC-ASR-V2.5-I01.json")))!;
        asr["Implementations"]![0]!["ImplementationURI"] = new Uri(package + Path.DirectorySeparatorChar).AbsoluteUri;

        // The pages are there, and an ID not assigned is not known.
        using (var page = await http.GetAsync("/MPAI/Store/Register")) Assert.Contains("Register as an Implementer", await page.Content.ReadAsStringAsync());
        using (var page = await http.GetAsync("/MPAI/Store/Approve")) Assert.Contains("waiting for MPAI", await page.Content.ReadAsStringAsync());
        using (var unknown = await http.GetAsync("/MPAI/Store/Implementer/2")) Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // An Implementer ID the Store has not assigned: refused, and the page to register at is named.
        asr["Identifier"]!["ImplementerID"] = "2";
        var stranger = ImplementerKey.Create();
        var refused = await Submit(http, asr, stranger);
        Assert.True(refused.Status == HttpStatusCode.Forbidden, $"{refused.Status} {refused.Body}");
        Assert.Contains("/MPAI/Store/Register", refused.Body);

        // A registration says what the entity is, a human or a machine; without it, it is refused.
        using (var noKind = await http.PostAsync("/MPAI/Store/Implementer", new StringContent(new JsonObject { ["name"] = "X" }.ToJsonString(), Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.BadRequest, noKind.StatusCode);
        using (var oddKind = await http.PostAsync("/MPAI/Store/Implementer", new StringContent(new JsonObject { ["name"] = "X", ["kind"] = "Cyborg" }.ToJsonString(), Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.BadRequest, oddKind.StatusCode);

        // Registered, and nothing more: the ID is assigned (it is 2: 1 is MPAI's own) but MPAI has not approved it.
        var me = await Implementer.Register(http, "Machine", "Health Back End");
        Assert.Equal("2", me.Id);
        var waiting = await Submit(http, asr, me.Key);
        Assert.True(waiting.Status == HttpStatusCode.Forbidden, $"{waiting.Status} {waiting.Body}");
        Assert.Contains("MPAI has not yet approved", waiting.Body);

        // The key is bound with the registration code only; another code does not do.
        using (var wrong = await http.PostAsync($"/MPAI/Store/Implementer/{me.Id}/key", new StringContent(
            new JsonObject { ["code"] = "0000", ["cii"] = me.Key.Cii(me.Id, DateTimeOffset.UtcNow) }.ToJsonString(), Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        await me.BindKey(http);
        using (var notAdmin = await http.PostAsync($"/MPAI/Store/Implementer/{me.Id}/approve", null)) Assert.Equal(HttpStatusCode.Unauthorized, notAdmin.StatusCode);
        await Implementer.Approve(http, me.Id);
        using (var known = await http.GetAsync("/MPAI/Store/Implementer/" + me.Id))
        {
            var json = JsonNode.Parse(await known.Content.ReadAsStringAsync())!;
            Assert.Equal("Approved", (string?)json["status"]);
            Assert.Equal("Machine", (string?)json["kind"]);                                     // the registration said what the entity is
            Assert.NotNull(json["credential"]);                                                 // the credential the Store issued
        }

        // Approved, it must still sign: an unsigned submission, one signed by another key, or one whose L3 changed after it was signed, is not accepted.
        asr["Identifier"]!["ImplementerID"] = me.Id;
        Assert.Equal(HttpStatusCode.Unauthorized, (await Submit(http, asr, null)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Submit(http, asr, stranger)).Status);
        using (var tampered = new HttpRequestMessage(HttpMethod.Post, "/MPAI/Store/L3") { Content = new StringContent(asr.ToJsonString(), Encoding.UTF8, "application/json") })
        {
            var changed = asr.DeepClone(); changed["Documentation"] = "changed after signing";
            tampered.Headers.Add("MPAI-Signature", me.Key.Sign(changed.AsObject()));
            using var answer = await http.SendAsync(tampered);
            Assert.Equal(HttpStatusCode.Unauthorized, answer.StatusCode);
        }
        var ok = await Submit(http, asr, me.Key);
        Assert.True(ok.Status == HttpStatusCode.Created, $"{ok.Status} {ok.Body}\n{store.Log}");

        // MPAI's own ID 1 submits with the key of the Store's operator, and not without it.
        asr["Identifier"]!["ImplementerID"] = "1";
        Assert.Equal(HttpStatusCode.Unauthorized, (await Submit(http, asr, me.Key)).Status);
        Assert.Equal(HttpStatusCode.Created, (await Submit(http, asr, null, Admin)).Status);

        // The fingerprint of the binary in the package is recorded, as the SHA-256 of what is there ...
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("the binary of the Implementation")));
        var approved = JsonNode.Parse(File.ReadAllText(Path.Combine(store.StoreFolder, "approved", "1MMC-ASR-V2.5-I01.json")))!;
        Assert.Equal(fingerprint, (string?)approved["Implementations"]!["Mpai.Mmc.Asr"]!["Fingerprint"]);

        // ... and asked about: the Store says the same, different or not recorded - the fingerprint itself is not given out.
        Assert.Equal("Same", await Verify(http, "1MMC-ASR-V2.5-I01", "Mpai.Mmc.Asr", fingerprint));
        Assert.Equal("Same", await Verify(http, "1MMC-ASR-V2.5-I01", "Mpai.Mmc.Asr", fingerprint.ToLowerInvariant()));
        Assert.Equal("Different", await Verify(http, "1MMC-ASR-V2.5-I01", "Mpai.Mmc.Asr", new string('0', 64)));
        Assert.Equal("NotRecorded", await Verify(http, "1MMC-ASR-V2.5-I01", "Another.dll", fingerprint));
        using (var none = await http.PostAsync("/MPAI/Store/L3/1XXX-NNN-V1.0-I01/verify", new StringContent("{}", Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
        using (var names = await http.GetAsync("/MPAI/Store/L3/1MMC-ASR-V2.5-I01/implementations"))
        {
            var body = await names.Content.ReadAsStringAsync();
            Assert.Contains("Mpai.Mmc.Asr", body);
            Assert.DoesNotContain(fingerprint, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task<string> Verify(HttpClient http, string aim, string binary, string fingerprint)
    {
        using var answer = await http.PostAsync($"/MPAI/Store/L3/{aim}/verify", new StringContent(
            new JsonObject { ["BinaryName"] = binary, ["Fingerprint"] = fingerprint }.ToJsonString(), Encoding.UTF8, "application/json"));
        return (string)JsonNode.Parse(await answer.Content.ReadAsStringAsync())!["Result"]!;
    }

    // A submission: signed by the Implementer's key, or with the admin key for MPAI's own ID.
    public static async Task<(HttpStatusCode Status, string Body)> Submit(HttpClient http, JsonNode l3, ImplementerKey? key, string? admin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/MPAI/Store/L3") { Content = new StringContent(l3.ToJsonString(), Encoding.UTF8, "application/json") };
        if (key is not null) request.Headers.Add("MPAI-Signature", key.Sign(l3.AsObject()));
        if (admin is not null) request.Headers.Add("MPAI-Admin", admin);
        using var response = await http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
