using System.Text.Json.Nodes;

using AIF.Controller;

namespace Mpai.Aif.Tests;

// The AIM host and the control path on their own (M3217 3.1, 3.3): a host in a
// process of its own; a Controller's client placing AIMs of a Module instance on
// it, firing them, pausing, resuming and stopping them, asking their status.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
[Collection(Timing.Name)]
public class RemoteHostTests
{
    private const string Text = "TST-TXT-V1.0";
    private const string Module = "TST#1";

    private static Message In(string text) => new() { MessageId = "m", Ports = new() { ["Text"] = text } };

    [Fact]
    public async Task ControlPath()
    {
        var result = new Dictionary<string, string>();
        using var hostProcess = new HostProcess();

        try { await using var wrong = await AimHostClient.ConnectAsync(hostProcess.Address, "not the key"); result["a Controller with the wrong key"] = "admitted"; }
        catch (UnauthorizedAccessException) { result["a Controller with the wrong key"] = "not admitted"; }

        await using var host = await AimHostClient.ConnectAsync(hostProcess.Address, HostProcess.Key);
        foreach (var aim in new[] { "1TST-UPP-V1.0-I01", "1TST-SLP-V1.0-I01", "1TST-RPT-V1.0-I01" })
            await host.PlaceAsync(Module, aim);
        try { await host.PlaceAsync(Module, "1TST-XXX-V1.0-I01"); result["placing an AIM the host has no L3 of"] = "placed"; }
        catch (InvalidOperationException refused) { result["placing an AIM the host has no L3 of"] = "refused: " + refused.Message.Replace(hostProcess.Address, "<host>"); }

        var reports = new List<string>();
        var upp = new RemoteProcessor("1TST-UPP-V1.0-I01", host, Module);
        var slp = new RemoteProcessor("1TST-SLP-V1.0-I01", host, Module);
        var rpt = new RemoteProcessor("1TST-RPT-V1.0-I01", host, Module, (aim, text) => reports.Add($"{aim}: {text}"));

        result["TST-UPP fired on the host"] = (await upp.ProcessAsync(In("x"))).Ports["Upper"];
        result["TST-RPT fired on the host"] = (await rpt.ProcessAsync(In("r"))).Ports["Reported"] + $"; its report came back: {string.Join("; ", reports)}";

        // Paused: a run on the host waits; resumed: it completes.
        await host.AskAsync("Pause", Module);
        var held = slp.ProcessAsync(In("p"));
        var waited = await Task.WhenAny(held, Task.Delay(800)) != held;
        await host.AskAsync("Resume", Module);
        result["Pause, then TST-SLP fired"] = (waited ? "held" : "not held") + $"; after Resume '{(await held.WaitAsync(TimeSpan.FromSeconds(5))).Ports["Slept"]}'";

        // Stopped during a run: the AIM on the host is signalled.
        var stopped = slp.ProcessAsync(In("s"));
        await Task.Delay(100);
        await host.AskAsync("Stop", Module);
        try { await stopped.WaitAsync(TimeSpan.FromSeconds(5)); result["Stop during a run on the host"] = "the run completed"; }
        catch (OperationCanceledException) { result["Stop during a run on the host"] = "the AIM was stopped"; }

        var status = await host.AskAsync("Status", Module);
        result["Status from the host"] = string.Join("; ", status["Aims"]!.AsArray().Select(a => $"{a!["Aim"]} {a["Status"]}"));

        await host.AskAsync("Release", Module);
        result["Status after Release"] = (await host.AskAsync("Status", Module))["Error"]?.GetValue<string>() ?? "still held";

        // The host gone: an AIM fired there is an error of the AIM.
        await host.PlaceAsync("TST#2", "1TST-UPP-V1.0-I01");
        var gone = new RemoteProcessor("1TST-UPP-V1.0-I01", host, "TST#2");
        hostProcess.Kill();
        try { await gone.ProcessAsync(In("x")); result["TST-UPP fired after its host went"] = "answered"; }
        catch (InvalidOperationException lost) { result["TST-UPP fired after its host went"] = lost.Message.StartsWith("the link to its host") ? "an error: the link to its host was lost" : lost.Message; }

        Expected.Match("remote-host.json", result);
    }
}
