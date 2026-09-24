using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Mpai.Aif.Tests;

// The MPAI Store of this repository (M3211 3.5): an L3 that does not validate
// against the AIM Metadata schema, or is not an instance of its L2, is refused;
// one that is both is published, its L2 being the one its Header names. The Store runs
// from its own build output, on a free port, with a Store folder of its own that
// is deleted afterwards.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class StoreTests
{
    [SkippableFact]
    public async Task RefusesWhatDoesNotValidate()
    {
        var exe = Path.Combine(Repository.Root, "MAS", "Store", "Service", "bin", "Debug", "net10.0", "StoreService.exe");
        Skip.IfNot(File.Exists(exe), "The Store is not built: " + exe);

        var port = FreePort();
        var root = Path.Combine(Path.GetTempPath(), "mpai-store-test-" + Guid.NewGuid().ToString("N"));
        var url  = $"http://127.0.0.1:{port}";
        var log  = new StringBuilder();
        using var store = Process.Start(new ProcessStartInfo(exe,
            $"--Urls {url} --Root \"{root}\" --Schemas \"{Repository.Schemas}\"")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        })!;
        store.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
        store.ErrorDataReceived  += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
        store.BeginOutputReadLine(); store.BeginErrorReadLine();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(30) };
            await Ready(http, log);

            var asr = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Amds, "1MMC-ASR-V2.5-I01.json")))!;

            // It validates: published, and checked against the L2 its Header names.
            var ok = await Submit(http, asr);
            Assert.True(ok.Status == HttpStatusCode.Created, $"a valid L3 was not published: {ok.Status} {ok.Body}\n{log}");
            Assert.DoesNotContain("has no Header", ok.Body);
            Assert.DoesNotContain("No L2 of", ok.Body);

            // Without its Header it does not validate: refused, and the violation said.
            var headless = asr.DeepClone();
            headless.AsObject().Remove("Header");
            var refused = await Submit(http, headless);
            Assert.True(refused.Status == HttpStatusCode.UnprocessableEntity, $"an invalid L3 was not refused: {refused.Status} {refused.Body}");
            Assert.Contains("violations", refused.Body);
            Assert.Contains("Header", refused.Body);

            // It validates against the schema but is not an instance of its L2 - an
            // input the L2 of ASR does not have: refused, and the nonconformity said.
            var extra = asr.DeepClone();
            extra["ExternalPorts"]!.AsArray().Add(new JsonObject
            {
                ["Name"] = "Extra", ["Direction"] = "Input", ["DataType"] = "OSD-BVO-V1.5",
                ["Technology"] = "Software", ["Protocol"] = "", ["IsRemote"] = false
            });
            var nonconforming = await Submit(http, extra);
            Assert.True(nonconforming.Status == HttpStatusCode.UnprocessableEntity, $"an L3 not an instance of its L2 was not refused: {nonconforming.Status} {nonconforming.Body}");
            Assert.Contains("nonconformities", nonconforming.Body);
            Assert.Contains("OSD-BVO-V1.5", nonconforming.Body);
        }
        finally
        {
            try { if (!store.HasExited) store.Kill(entireProcessTree: true); store.WaitForExit(5000); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task<(HttpStatusCode Status, string Body)> Submit(HttpClient http, JsonNode l3)
    {
        using var response = await http.PostAsync("/MPAI/Store/L3",
            new StringContent(l3.ToJsonString(), Encoding.UTF8, "application/json"));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task Ready(HttpClient http, StringBuilder log)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            try { using var r = await http.GetAsync("/MPAI/Store/L3"); if (r.IsSuccessStatusCode) return; }
            catch (HttpRequestException) { }
            await Task.Delay(250);
        }
        throw new TimeoutException("The Store did not answer within 30 s:\n" + log);
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
