using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

using Mpai.Rca;

namespace Mpai.Aif.Tests;

// PHASE 17, STEP 1 (M3248 3.1, 3.4): WHICH CERTIFICATE OF THE SERVICE A CLIENT
// ACCEPTS. The browser client's host and the desktop client accept a Service on the
// loopback as it is; any other presents a certificate the machine trusts, checked
// by the system.
[Trait("Blocks", "No")]
public class HostCertificateTests
{
    [Fact]
    public void ServiceCertificate()
    {
        var result = new Dictionary<string, string>();
        foreach (var url in new[] { "http://127.0.0.1:5005/", "https://localhost:5105/", "https://[::1]:5105/", "https://mas.example.org/", "https://192.168.1.10:5105/" })
            result[url] = ServiceCertificates.Validator(new Uri(url)) is null ? "checked by the system" : "accepted as it is";
        Expected.Match("host-certificate.json", result);
    }
}

// PHASE 17, STEP 1 (M3248 3.1, 3.4): THE HOST AS A PUBLIC SERVER RUNS IT. The
// browser client's host published Release, laid out as the Linux package lays it
// out (client/ beside UserAgent/), and started from another folder with no
// environment set. Judged: its page, its icon and its client served; Production,
// and the headers a public site sends; no symbols among the client's files; a
// Service on the loopback - a stand-in answering the Apps route - reached through
// it. It publishes, so it runs with -Full.
[Trait("Group", "Service")]
[Trait("Blocks", "No")]
public class HostPublishedTests
{
    [Fact]
    public async Task Published()
    {
        var root = Repository.Root;
        var package = Path.Combine(Path.GetTempPath(), "mpai-p17-host-" + Guid.NewGuid().ToString("N"));
        var elsewhere = Path.Combine(package, "elsewhere");
        var client = Path.Combine(package, "client");
        Directory.CreateDirectory(elsewhere);
        var result = new Dictionary<string, string>();
        var log = new StringBuilder();

        var publish = Run("dotnet", $"publish \"{Path.Combine(root, "UserAgent", "Clients", "Browser", "Host", "RcaWeb.Host.csproj")}\" -c Release -o \"{client}\" -nologo -v q", root, log);
        Assert.True(publish == 0, "The host did not publish:\n" + log);
        foreach (var dir in new[] { "Assets", "Orchestration" })
            Copy(Path.Combine(root, "UserAgent", dir), Path.Combine(package, "UserAgent", dir));
        var framework = Path.Combine(client, "wwwroot", "_framework");
        result["the client's files"] = Directory.EnumerateFiles(framework).Any(f => f.EndsWith(".pdb")) ? "with symbols" : "no symbols";

        // The Service, stood in for on the loopback: the Apps route answered.
        var stand = new TcpListener(IPAddress.Loopback, 0);
        stand.Start();
        var servicePort = ((IPEndPoint)stand.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var serving = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var c = await stand.AcceptTcpClientAsync(stop.Token);
                var s = c.GetStream();
                var buffer = new byte[8192];
                await s.ReadAsync(buffer, stop.Token);
                var body = "[{\"id\":\"STANDIN\"}]";
                var reply = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                await s.WriteAsync(Encoding.ASCII.GetBytes(reply), stop.Token);
            }
        });

        var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        var hostPort = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();
        var ready = new ManualResetEventSlim();
        var info = new ProcessStartInfo(Path.Combine(client, OperatingSystem.IsWindows() ? "RcaWeb.Host.exe" : "RcaWeb.Host"),
                                        $"--Service http://127.0.0.1:{servicePort}/ --Urls http://127.0.0.1:{hostPort}")
        {
            WorkingDirectory = elsewhere, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        info.Environment.Remove("ASPNETCORE_ENVIRONMENT");
        info.Environment.Remove("DOTNET_ENVIRONMENT");
        using var host = new Process { StartInfo = info };
        host.OutputDataReceived += (_, e) => { if (e.Data is null) return; lock (log) log.AppendLine(e.Data); if (e.Data.Contains("Content root path")) ready.Set(); };
        host.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
        host.Start();
        host.BeginOutputReadLine();
        host.BeginErrorReadLine();
        try
        {
            Assert.True(ready.Wait(TimeSpan.FromMinutes(1)), "The host did not start:\n" + log);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{hostPort}/") };
            lock (log)
            {
                var said = log.ToString();
                result["the environment"] = said.Contains("Hosting environment: Production") ? "Production" : "not Production";
                var rootLine = said.Split('\n').FirstOrDefault(l => l.Contains("Content root path:"))?.Split("Content root path:")[1].Trim().TrimEnd(Path.DirectorySeparatorChar) ?? "";
                result["its content root"] = rootLine == client ? "its own folder" : rootLine == elsewhere ? "the folder it was started from" : rootLine;
            }

            var page = await http.GetAsync("");
            var html = await page.Content.ReadAsStringAsync();
            result["its page"] = page.IsSuccessStatusCode && html.Contains("MPAI as a Service") && html.Contains("favicon.svg") ? "served, naming its icon" : $"{(int)page.StatusCode}";
            string Header(string name) => page.Headers.TryGetValues(name, out var v) ? string.Join(", ", v) : "(none)";
            foreach (var name in new[] { "X-Content-Type-Options", "Referrer-Policy", "X-Frame-Options", "Permissions-Policy" })
                result["header " + name] = Header(name);
            result["its icon"] = (await http.GetAsync("favicon.svg")).IsSuccessStatusCode ? "served" : "not served";
            result["its client"] = (await http.GetAsync("_framework/dotnet.js")).IsSuccessStatusCode ? "served" : "not served";
            var apps = await http.GetAsync("MPAI/AIFU/Apps");
            result["the Service, through it"] = apps.IsSuccessStatusCode && (await apps.Content.ReadAsStringAsync()).Contains("STANDIN") ? "reached" : $"{(int)apps.StatusCode}";
        }
        finally
        {
            try { if (!host.HasExited) host.Kill(entireProcessTree: true); } catch { }
            stop.Cancel();
            stand.Stop();
            try { await serving; } catch { }
            try { Directory.Delete(package, recursive: true); } catch { }
        }
        Expected.Match("host-published.json", result);
    }

    private static int Run(string exe, string args, string dir, StringBuilder log)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { WorkingDirectory = dir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = p.StandardOutput.ReadToEndAsync();
        var errors = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        log.Append(output.Result).Append(errors.Result);
        return p.ExitCode;
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target);
        }
    }
}
