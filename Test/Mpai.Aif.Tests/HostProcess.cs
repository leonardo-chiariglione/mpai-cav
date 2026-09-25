using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Mpai.Aif.Tests;

// AN AIM HOST IN A PROCESS OF ITS OWN (M3217 3.6), standing in for another
// machine: the AIF.AimHost program, given the L3s of Test/Data/Phase5 and the test
// AIMs of this assembly as its provider. The port it listens on is read from what
// it prints.
public sealed class HostProcess : IDisposable
{
    public const string Key = "phase 5 test key";

    private readonly Process process;
    public int Port { get; }

    // The CPU the host has used: what an AIM on it costs, for the comparison of
    // the transports.
    public TimeSpan ProcessorTime { get { process.Refresh(); return process.TotalProcessorTime; } }
    public string Address => $"localhost:{Port}";

    public HostProcess(string? amds = null)
    {
        var bin = AppContext.BaseDirectory;
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[]
        {
            Path.Combine(bin, "AIF.AimHost.dll"), "--port", "0", "--key", Key,
            "--amds", amds ?? RemoteTests.Amds,
            "--storage", Path.Combine(Path.GetTempPath(), "mpai-phase5-host-" + Guid.NewGuid().ToString("N")),
            "--provider", Path.Combine(bin, "Mpai.Aif.Tests.dll") + ":Mpai.Aif.Tests.RemoteAims"
        }) info.ArgumentList.Add(arg);

        process = Process.Start(info)!;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            var line = process.StandardOutput.ReadLine();
            if (line is null) break;
            var m = Regex.Match(line, @"listening on (\d+)");
            if (m.Success)
            {
                Port = int.Parse(m.Groups[1].Value);
                _ = Task.Run(() => { while (process.StandardOutput.ReadLine() is not null) { } });
                return;
            }
        }
        throw new InvalidOperationException("The AIM host did not start: " + process.StandardError.ReadToEnd());
    }

    public void Kill()
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        process.WaitForExit(5000);
    }

    public void Dispose() => Kill();
}
