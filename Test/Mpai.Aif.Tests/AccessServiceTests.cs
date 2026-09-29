using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Core;
using Mpai.Mas.Client;

namespace Mpai.Aif.Tests;

// PHASE 16, STEP 3 (M3245 3.3, 3.6): MAC AND ACR OFFERED BY THE SERVICE, as the
// server runs them. The MAS Service of this repository, from its build output, on
// port 5109, offering MAC and ACR over a gallery of its own, what a session
// registers forgotten when it closes (ForgetOnClose). A client: ACR registers the
// author, MAC grants him; the client leaves the Service. Judged: both Apps offered;
// registered and granted; when the client left, his descriptors deleted from the
// gallery, and MAC refuses him to a new client. The Service is started from its
// build (MPAI_SERVICE_EXE where another build is to be tried).
[Trait("Group", "Service")]
[Trait("Blocks", "No")]
public class AccessServiceTests
{
    private const string Url = "https://localhost:5109/";

    [SkippableFact]
    public async Task Step3OfferedByTheService()
    {
        var root = Repository.Root;
        var exe = Environment.GetEnvironmentVariable("MPAI_SERVICE_EXE") ?? Path.Combine(root, "MAS", "Service", "src", "bin", "Debug", "net10.0", "MasService.exe");
        Skip.IfNot(File.Exists(exe), "The Service is not built: " + exe);
        Skip.IfNot(File.Exists(Path.Combine(AccessTests.Images, "R.Reagan1.jpg")), $"The photographs of the tests are not in {AccessTests.Images}.");
        Skip.If(System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == 5109), "Port 5109 is in use.");

        var result = new Dictionary<string, string>();
        var gallery = Path.Combine(Path.GetTempPath(), "mpai-p16-service-gallery-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(Path.GetTempPath(), "mpai-p16-service-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(config, new JsonObject
        {
            ["ListenUrl"] = Url, ["AppDirectory"] = Path.Combine(root, "Apps"), ["Apps"] = new JsonArray("MAC", "ACR"),
            ["AmdDirectory"] = Repository.Amds, ["SettingsPath"] = Path.Combine(root, "AIMs", "aim-settings.json"),
            ["SchemaDirectory"] = Repository.Schemas, ["Gallery"] = gallery, ["ForgetOnClose"] = true
        }.ToJsonString());
        var log = new StringBuilder();
        var ready = new ManualResetEventSlim();
        using var service = new Process
        {
            StartInfo = new ProcessStartInfo(exe, $"\"{config}\"")
            {
                WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            }
        };
        service.OutputDataReceived += (_, e) => { if (e.Data is null) return; lock (log) log.AppendLine(e.Data); if (e.Data.Contains("[MAS] Listening")) ready.Set(); };
        service.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
        service.Start();
        service.BeginOutputReadLine();
        service.BeginErrorReadLine();
        try
        {
            if (!ready.Wait(TimeSpan.FromMinutes(5))) throw new InvalidOperationException("The Service did not start:\n" + log);
            using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator });
            var apps = JsonNode.Parse(await http.GetStringAsync(Url + "MPAI/AIFU/Apps"))!.ToJsonString();
            result["offered"] = apps.Contains("\"MAC\"") && apps.Contains("\"ACR\"") ? "MAC and ACR" : apps;

            const string author = "Leonardo", voice = "en_GB-alan-medium";
            var client = new RemoteControllerApi(Url);
            foreach (var module in new[] { "1MMC-ACR-V2.5-I01", "1MMC-MAC-V2.5-I01" })
                if (client.StartFlow(module) is var started && started != AifError.OK) throw new InvalidOperationException($"{module}: {started}\n{log}");
            var registered = client.Advance("1MMC-ACR-V2.5-I01",
                [new("OSD-BVO-V1.5", 1, AccessTests.Face("Leonardo Speaking.jpg")), new("OSD-BSO-V1.5", 1, AccessTests.Voice("My name is Leonardo, and I want to register.", voice)),
                 new("OSD-BTO-V1.5", 2, AccessTests.Text(author)), new("OSD-BTO-V1.5", 1, AccessTests.Text("You are registered."))]);
            bool? Check(RemoteControllerApi api)
            {
                var r = api.Advance("1MMC-MAC-V2.5-I01",
                    [new("OSD-BVO-V1.5", 1, AccessTests.Face("Leonardo Speaking.jpg")), new("OSD-BSO-V1.5", 1, AccessTests.Voice("Good morning, I would like to come in, please.", voice))]);
                return r.ByType("boolean") is { } b ? bool.Parse(b) : null;
            }
            var granted = Check(client);
            var key = Convert.ToBase64String(Encoding.UTF8.GetBytes(SubjectGallery.SubjectKeyPrefix + author)).Replace('/', '_').Replace('+', '-');
            bool Kept() => File.Exists(Path.Combine(gallery, key + ".data"));
            result["registered, then checked"] = registered.Error == AifError.OK && Kept() && granted == true ? "in the gallery; access granted" : $"{registered.Error}; kept {Kept()}; granted {granted}";

            // The client leaves: its session closes.
            using (var leave = new HttpRequestMessage(HttpMethod.Post, Url + "MPAI/AIFU/Leave"))
            {
                leave.Headers.Add("MPAI-Client", MasClientIdentity.Id);
                await http.SendAsync(leave);
            }
            result["the client left"] = !Kept() ? "his descriptors deleted" : "his descriptors still in the gallery";
            var another = new RemoteControllerApi(Url);
            another.StartFlow("1MMC-MAC-V2.5-I01");
            result["then"] = Check(another) == false ? "access refused" : "access not refused";
        }
        finally
        {
            try { if (!service.HasExited) service.Kill(entireProcessTree: true); } catch { }
            try { File.Delete(config); Directory.Delete(gallery, recursive: true); } catch { }
        }
        Expected.Match("access-service.json", result);
    }
}
