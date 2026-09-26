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

    // The host's Trust Anchor object, where it admits by the Trust Protocol.
    public System.Text.Json.Nodes.JsonObject? Anchor { get; init; }

    // anchor, trust: the host's anchor and the anchors of the Controllers it serves
    // (M3223 3.4); without them, it admits by the key.
    // bin: where the host runs from; extra: more of its arguments.
    public HostProcess(string? amds = null, string? anchor = null, string? trust = null, string? bin = null, string[]? extra = null)
    {
        bin ??= AppContext.BaseDirectory;
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[]
        {
            Path.Combine(bin, "AIF.AimHost.dll"), "--port", "0",
            "--amds", amds ?? RemoteTests.Amds,
            "--storage", Path.Combine(Path.GetTempPath(), "mpai-phase5-host-" + Guid.NewGuid().ToString("N")),
            "--provider", Path.Combine(bin, "Mpai.Aif.Tests.dll") + ":Mpai.Aif.Tests.RemoteAims"
        }) info.ArgumentList.Add(arg);
        foreach (var arg in anchor is null ? ["--key", Key] : new[] { "--anchor", anchor, "--trust", trust! }) info.ArgumentList.Add(arg);
        foreach (var arg in extra ?? []) info.ArgumentList.Add(arg);

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
                _ = Task.Run(() =>
                {
                    while (process.StandardOutput.ReadLine() is { } more)
                        lock (output) { output.Add(more); if (output.Count > 200) output.RemoveAt(0); }
                });
                return;
            }
        }
        throw new InvalidOperationException("The AIM host did not start: " + process.StandardError.ReadToEnd());
    }

    // What the host has written, its last lines: for a test that fails to say why.
    private readonly List<string> output = new();
    public string Output { get { lock (output) return string.Join(Environment.NewLine, output); } }

    public void Kill()
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        process.WaitForExit(5000);
    }

    public void Dispose() => Kill();
}
