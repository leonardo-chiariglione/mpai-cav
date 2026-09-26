using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using AIF.Controller;
using AIF.RootOfTrust;
using AIF.Trust;
using Mpai.Aif.Api;
using Mpai.Core;
using Mpai.Mas.Client;
using Xunit.Abstractions;

namespace Mpai.Aif.Tests;

// PHASE 13, STEP 8 (M3223 5): the MAS Service under Zero Trust, as deployed. Its
// Controller a Trust Anchor whose key a TPM holds, its code measured; the Apps'
// binaries approved in the Store; MAD with its EDP on an AIM host that is attested
// too, reached by the Trust Protocol. A question asked of MAD is answered through
// the host, and every decision is in the Controller's Trace, intact.
[Collection(Timing.Name)]
[Trait("Group", "Service")]
[Trait("Blocks", "No")]
public class TrustServiceTests(ITestOutputHelper output)
{
    private const int Port = 5305;
    private const string Edp = "1MMC-EDP-V2.5-I01", Mad = "1MMC-MAD-V2.5-I01";

    [SkippableFact]
    public void MadWithEdpOnAnAttestedHost()
    {
        var root = Repository.Root;
        var serviceBin = Path.Combine(root, "MAS", "Service", "src", "bin", "Debug", "net10.0");
        var hostBin = Path.Combine(root, "AIF", "AimHost", "bin", "Debug", "net10.0");
        Skip.If(!Directory.Exists(Path.Combine(root, "Models")), "Models is absent: the model files are obtained separately.");
        Skip.If(!File.Exists(Path.Combine(serviceBin, "MasService.exe")), "The Service is not built.");
        Skip.If(!OllamaHas("llama3.2:3b"), "Ollama is not running with llama3.2:3b.");
        Skip.If(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == Port), $"Port {Port} is in use.");

        var result = new Dictionary<string, string>();
        var work = Path.Combine(Path.GetTempPath(), $"mpai-phase13-service-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(work, "AMDs"));
        foreach (var l3 in Directory.EnumerateFiles(Repository.Amds, "*.json")) File.Copy(l3, Path.Combine(work, "AMDs", Path.GetFileName(l3)));

        // EDP ON ANOTHER MACHINE: the deployment says so in MAD's L3 - its Relation
        // External - and names the host in the Service's configuration.
        var madL3 = Path.Combine(work, "AMDs", Mad + ".json");
        var mad = JsonNode.Parse(File.ReadAllText(madL3))!;
        foreach (var sub in mad["SubAIMs"]!.AsArray())
            if ((string?)sub!["Identifier"]!["AIMName"] == Edp) sub["Identifier"]!["Relation"] = "External";
        File.WriteAllText(madL3, mad.ToJsonString());

        // THE HOST'S FOLDER: the Service's build - the providers and the AIMs - with the AIM host.
        var hostDir = Path.Combine(work, "host");
        CopyFolder(serviceBin, hostDir);
        foreach (var f in Directory.EnumerateFiles(hostBin, "AIF.AimHost.*")) File.Copy(f, Path.Combine(hostDir, Path.GetFileName(f)), true);

        // THE PARTIES: a manufacturer; the Controller and the host, each in its TPM; the
        // code each approves of the other.
        var manufacturer = SimulatedManufacturer.Make();
        var certificate = Path.Combine(work, "manufacturer.cer");
        File.WriteAllBytes(certificate, manufacturer.Root.RawData);
        string Provision(string id, int port)
        {
            var (h, p) = TpmSimulators.At(port);
            var path = Path.Combine(work, id + ".json");
            TpmParty.Provision(path, id, h, p, manufacturer, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1));
            return path;
        }
        var controllerFile = Provision("controller-1", TpmSimulators.Controller);
        var hostFile = Provision("aimhost-1", TpmSimulators.Host);
        string Reference(string name, string dir)
        {
            var path = Path.Combine(work, name);
            File.WriteAllText(path, new JsonObject(PartyCode.Of(dir).Select(c => KeyValuePair.Create(c.File, (JsonNode?)c.Sha256))).ToJsonString());
            return path;
        }
        var controllerCode = Reference("controller-code.json", serviceBin);
        var hostCode = Reference("host-code.json", hostDir);

        using var host = new HostProcess(Path.Combine(work, "AMDs"), hostFile, TrustedParties.TrustFile(TrustedParties.AnchorOf(controllerFile)), hostDir,
            ["--manufacturer", certificate, "--reference", controllerCode, "--settings", Path.Combine(root, "AIMs", "aim-settings.json")],
            Path.Combine(hostDir, "Mpai.Providers.dll") + ":Mpai.Providers.MadProvider");

        var config = Path.Combine(work, "mas-server.json");
        var trace = Path.Combine(work, "trust-trace.jsonl");
        File.WriteAllText(config, new JsonObject
        {
            ["ListenUrl"] = $"https://localhost:{Port}/",
            ["AppDirectory"] = Path.Combine(root, "Apps"),
            ["Apps"] = new JsonArray("MAD"),
            ["AmdDirectory"] = Path.Combine(work, "AMDs"),
            ["SettingsPath"] = Path.Combine(root, "AIMs", "aim-settings.json"),
            ["SchemaDirectory"] = Path.Combine(root, "schemas"),
            ["AimHosts"] = new JsonObject { [Edp] = host.Address },
            ["Trust"] = new JsonObject
            {
                ["Anchor"] = controllerFile, ["HostAnchors"] = new JsonArray(hostFile), ["Manufacturer"] = certificate,
                ["HostCode"] = hostCode, ["Trace"] = trace
            }
        }.ToJsonString());

        // THE STORE'S APPROVAL of what the Service deploys.
        var approved = Run(Path.Combine(serviceBin, "MasService.exe"), $"approve \"{config}\"", root, TimeSpan.FromMinutes(2));
        result["the Store's approvals"] = $"{Regex.Matches(approved, @"^\s+approved ", RegexOptions.Multiline).Count} AIMs approved; " +
                                          $"{Regex.Matches(approved, @"NOT approved", RegexOptions.Multiline).Count} not";

        // THE SERVICE, UNDER TRUST.
        var log = new StringBuilder();
        var ready = new ManualResetEventSlim();
        using var service = new Process
        {
            StartInfo = new ProcessStartInfo(Path.Combine(serviceBin, "MasService.exe"), $"\"{config}\"")
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
            Assert.True(ready.Wait(TimeSpan.FromMinutes(5)), "The Service did not listen within 5 minutes:\n" + log);
            string Line(string starts) => log.ToString().Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(starts)) ?? "(none)";
            result["the Service: trust"] = Regex.Replace(Line("Trust:"), @"traced in .*$", "traced");
            result["the Service: MAD"] = Line(Mad + ":");

            // A QUESTION TO MAD: its EDP on the host, asking Ollama.
            using var api = new RemoteControllerApi($"https://localhost:{Port}/", timeout: TimeSpan.FromMinutes(5));
            var clock = Stopwatch.StartNew();
            var run = api.Advance(Mad, [new ControllerApi.Datum("OSD-BTO-V1.5", 2, MpaiJson.ToJson(BasicTextObject.FromText("What is the capital of Italy?")))]);
            var summary = run.Outputs.FirstOrDefault(o => o.DataType == "MMC-SUM-V2.5").Json ?? "";
            output.WriteLine($"MAD answered in {clock.ElapsedMilliseconds} ms: {summary}");
            result["MAD, asked the capital of Italy"] = $"{run.Error}; " + (summary.Contains("Rome") ? "it names Rome" : "it does not name Rome") +
                                                        $"; speech {(run.Outputs.Any(o => o.DataType == "OSD-BSO-V1.5") ? "given" : "none")}";
        }
        finally
        {
            try { if (!service.HasExited) service.Kill(entireProcessTree: true); } catch { }
            service.WaitForExit(10000);
            output.WriteLine(log.ToString());
        }

        // THE HOST, AND THE TRACE.
        result["the host"] = string.Join("; ", host.Output.Split(Environment.NewLine)
            .Where(l => l.Contains("admitted") || l.Contains("refused") || l.Contains("placed") || l.Contains("root of trust"))
            .Select(l => Regex.Replace(l.Replace("[AIM host] ", ""), @"#[0-9a-f]{32}", "#<instance>").Trim())
            .Distinct());
        var records = TrustTrace.Load(trace);
        result["the Trace"] = TrustTrace.Verify(records, TrustedParties.AnchorOf(controllerFile)) ?? "intact";
        var operations = records.Select(r => r["TrustOperation"]!).ToList();
        result["its link decisions"] = string.Join(", ", operations.Where(o => (string?)o["TargetType"] is "TrustMessage" || ((string?)o["TargetType"] == "AttestationEvidence" && ((string?)o["TargetID"] ?? "").StartsWith("aimhost")))
            .GroupBy(o => $"{o["OperationType"]} {o["TargetType"]} {o["Status"]}").Select(g => g.Key).Distinct().OrderBy(k => k, StringComparer.Ordinal));
        result["its failures"] = string.Join("; ", operations.Where(o => (string?)o["Status"] == "Failure").Select(o => $"{o["OperationType"]} {o["TargetType"]}: {o["FailureReason"]}"));
        result["EDP, placed on the host"] = host.Output.Contains($"{Edp} placed for {Mad}") ? "yes" : "no";
        Expected.Match("trust-service.json", result);
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), true);
    }

    private static string Run(string exe, string arguments, string cwd, TimeSpan limit)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, arguments)
        {
            WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        })!;
        var text = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(limit)) { p.Kill(true); throw new TimeoutException($"{exe} {arguments}"); }
        return text.Result + p.StandardError.ReadToEnd();
    }

    private static bool OllamaHas(string model)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            return http.GetStringAsync("http://localhost:11434/api/tags").GetAwaiter().GetResult().Contains($"\"{model}\"");
        }
        catch { return false; }
    }
}
