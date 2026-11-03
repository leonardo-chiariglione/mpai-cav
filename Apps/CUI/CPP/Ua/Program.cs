using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Mpai.Aif.Api;
using Mpai.Core;
using Mpai.Ua;
using Mpai.Wdl;

namespace Mpai.Cui.Ua;

// CuiUa - THE USER AGENT of Company Performance Prediction, a console program.
//
//   CuiUa --company <company.json> [--horizon <months>] [--controller http://127.0.0.1:5061] [--wait <seconds>] [--json <out.json>] [--workflow <CUI-CPP.orch>]
//
// A company file is one JSON object with the data a company gives: PredictionHorizon (months), FinancialDescriptors (CUI-FND), GovernanceDescriptors (CUI-GVS) and, optionally, ClimateRiskDescriptors
// (CUI-LRD), CyberRiskDescriptors (CUI-CRD) and SecondaryRiskStatements (CUI-SRS). Each is given to the Module by a device the workflow binds to its Port - by Data Type and Port Number - and what the
// Module gives is delivered to the Report device, which prints it: the descriptors and the explanation each carries. The User Agent starts and stops the Module through the Controller API of a Controller that
// is a program of its own (CuiController); it names no AIM and reads no storage.
internal static class Program
{
    private static string? Value(string[] args, string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

    private static async Task<int> Main(string[] args)
    {
        var companyFile = Value(args, "--company");
        if (companyFile is null || !File.Exists(companyFile))
        {
            Console.Error.WriteLine("usage: CuiUa --company <company.json> [--horizon <months>] [--controller <url>] [--wait <seconds>] [--json <out.json>] [--workflow <CUI-CPP.orch>]");
            return 2;
        }
        JsonObject company;
        try { company = JsonNode.Parse(File.ReadAllText(companyFile)) as JsonObject ?? throw new JsonException("not an object"); }
        catch (JsonException e) { Console.Error.WriteLine($"{companyFile}: not a company file: {e.Message}"); return 2; }
        var horizon = Value(args, "--horizon") ?? company["PredictionHorizon"]?.ToString();
        if (!double.TryParse(horizon, NumberStyles.Float, CultureInfo.InvariantCulture, out var months) || months <= 0) { Console.Error.WriteLine("The Prediction Horizon is a number of months above zero (--horizon or PredictionHorizon)."); return 2; }
        if (company["FinancialDescriptors"] is null && company["Header"]?.ToString() != "CUI-FND-V2.1") { }
        var wait = TimeSpan.FromSeconds(double.TryParse(Value(args, "--wait"), NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : 30);
        var workflowFile = Value(args, "--workflow") ?? Path.Combine(MpaiPaths.Root, "Apps", "CUI", "CPP", "CUI-CPP.orch");
        var controller = Value(args, "--controller") ?? "http://127.0.0.1:5061";

        // THE DEVICES: what the company gives, each bound by the workflow to one Port; what is not given is a device that gives nothing.
        var devices = new DeviceRegistry();
        var asked = new List<string>();
        IDevice Given(string device, string dataType, JsonNode? datum, bool required)
        {
            if (datum is null) { if (required) throw new InvalidOperationException($"The company file gives no {device}."); return new SilentDevice(); }
            asked.Add(device);
            return new GivenDevice(dataType, datum.ToJsonString());
        }
        try
        {
            devices.RegisterDevice("Horizon", new GivenDevice("number", months.ToString("R", CultureInfo.InvariantCulture)));
            devices.RegisterDevice("Financial", Given("FinancialDescriptors", "CUI-FND-V2.1", company["FinancialDescriptors"], true));
            devices.RegisterDevice("Governance", Given("GovernanceDescriptors", "CUI-GVS-V2.1", company["GovernanceDescriptors"], true));
            devices.RegisterDevice("Climate", Given("ClimateRiskDescriptors", "CUI-LRD-V2.1", company["ClimateRiskDescriptors"], false));
            devices.RegisterDevice("Cyber", Given("CyberRiskDescriptors", "CUI-CRD-V2.1", company["CyberRiskDescriptors"], false));
            devices.RegisterDevice("SecondaryRisks", Given("SecondaryRiskStatements", "CUI-SRS-V2.1", company["SecondaryRiskStatements"], false));
        }
        catch (InvalidOperationException e) { Console.Error.WriteLine(e.Message); return 2; }
        var report = new ReportDevice(asked.Contains("SecondaryRiskStatements"));
        devices.RegisterDevice("Report", report);

        using var api = new Mpai.Mas.Client.RemoteControllerApi(controller);
        var workflow = new WorkflowReader().Read(File.ReadAllText(workflowFile));
        using var stop = new CancellationTokenSource();
        var interpreter = new WorkflowInterpreter(api.Async(), devices, line => { if (args.Contains("--verbose")) Console.WriteLine("[workflow] " + line); });
        string? failure = null;
        var running = Task.Run(async () => { try { await interpreter.RunAsync(workflow, stop.Token); } catch (OperationCanceledException) { } catch (Exception e) { failure = e.Message; report.Abandon(); } });

        var complete = await report.WaitAsync(wait);
        stop.Cancel();
        try { await running.WaitAsync(TimeSpan.FromSeconds(15)); } catch (TimeoutException) { }
        if (failure is not null) { Console.Error.WriteLine("The workflow failed: " + failure); return 1; }
        if (!complete) { Console.Error.WriteLine($"The Module did not give everything within {wait.TotalSeconds:0} s; what it gave is below."); }

        Console.WriteLine(Describe(months, report.Data));
        if (Value(args, "--json") is { } outPath)
            File.WriteAllText(outPath, new JsonObject(report.Data.Select(p => KeyValuePair.Create<string, JsonNode?>(p.Key, JsonNode.Parse(p.Value)))).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return complete ? 0 : 1;
    }

    // WHAT THE MODULE GAVE, in words: the descriptors and the explanation they carry (the importance of each descriptor to the probability).
    private static string Describe(double months, IReadOnlyDictionary<string, string> data)
    {
        var s = new StringBuilder();
        string Pct(double? v) => v is { } x ? (x * 100).ToString("0.0", CultureInfo.InvariantCulture) + " %" : "?";
        double? Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
        s.AppendLine($"Company Performance Prediction - Prediction Horizon {months.ToString("0.##", CultureInfo.InvariantCulture)} months");
        if (data.TryGetValue("CUI-PDD-V2.1", out var pdd) && JsonNode.Parse(pdd)?["PrimaryDefaultDescriptors"]?[0] is { } d0)
        {
            s.AppendLine();
            s.AppendLine($"Primary Default probability: {Pct(Num(d0["PrimaryDefaultProbability"]))}");
            foreach (var i in (d0["PrimaryDefaultDescriptorImpacts"] as JsonArray) ?? [])
                s.AppendLine($"    {i?["PrimaryDefaultDescriptorID"],-26} {Num(i?["PrimaryDefaultDescriptorImpact"])?.ToString("0.0", CultureInfo.InvariantCulture),6} % of the score");
        }
        if (data.TryGetValue("CUI-ORD-V2.1", out var ord) && JsonNode.Parse(ord)?["OrganisationDescriptors"]?[0] is { } o0)
        {
            s.AppendLine();
            s.AppendLine($"Organisation: impact on the probability of default {Pct(Num(o0["ImpactOnDefault"]))}");
            foreach (var i in (o0["GovernanceDescriptorsImpact"] as JsonArray) ?? [])
                s.AppendLine($"    {i?["GovernaceDescriptorID"],-26} {Num(i?["GovernanceDescriptorImpact"])?.ToString("0.0", CultureInfo.InvariantCulture),6} % of the score");
        }
        if (data.TryGetValue("CUI-PSD-V2.1", out var psd) && JsonNode.Parse(psd)?["PrimaryDiscontinuityDescriptors"] is JsonArray entries)
        {
            s.AppendLine();
            foreach (var e in entries)
            {
                s.AppendLine($"Primary Discontinuity probability ({e?["PrimaryRiskID"]}): {Pct(Num(e?["PrimaryDiscontinuityProbability"]))}");
                foreach (var i in (e?["PrimaryDiscontinuityDescriptorImpacts"] as JsonArray) ?? [])
                    s.AppendLine($"    {i?["PrimaryRiskID"],-26} {Num(i?["PrimaryRiskDescriptorImpact"])?.ToString("0.##", CultureInfo.InvariantCulture)}");
            }
        }
        if (data.TryGetValue("number", out var sdp))
        {
            s.AppendLine();
            s.AppendLine($"Secondary Discontinuity probability: {Pct(Num(JsonNode.Parse(sdp)))}");
        }
        return s.ToString();
    }
}

// A DEVICE THAT GIVES NOTHING: what the company did not give (an optional Port of the Module).
internal sealed class SilentDevice : IDevice
{
    public Task DeliverAsync(string dataType, string json, CancellationToken cancel) => Task.CompletedTask;
    public async IAsyncEnumerable<(string DataType, string Json)> ReadAsync([EnumeratorCancellation] CancellationToken cancel)
    {
        await Task.Delay(Timeout.Infinite, cancel);
        yield break;
    }
    public Task SafeStopAsync() => Task.CompletedTask;
}

// THE REPORT: what the Module gives, kept by Data Type until the descriptors it was asked for have all come.
internal sealed class ReportDevice(bool secondaryAsked) : IDevice
{
    private readonly ConcurrentDictionary<string, string> data = new();
    private readonly TaskCompletionSource<bool> done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyDictionary<string, string> Data => data;
    public void Abandon() => done.TrySetResult(false);

    private long lastDelivery = Environment.TickCount64;

    public Task DeliverAsync(string dataType, string json, CancellationToken cancel)
    {
        data[dataType] = json;
        Interlocked.Exchange(ref lastDelivery, Environment.TickCount64);
        if (data.ContainsKey("CUI-ORD-V2.1") && data.ContainsKey("CUI-PDD-V2.1") && data.ContainsKey("CUI-PSD-V2.1") && (!secondaryAsked || data.ContainsKey("number"))) done.TrySetResult(true);
        return Task.CompletedTask;
    }

    // The descriptors have all come; an AIM computes again when an optional Input comes after the first computation, so the report waits until nothing has come for a while, and keeps the last.
    public async Task<bool> WaitAsync(TimeSpan within)
    {
        var until = Environment.TickCount64 + (long)within.TotalMilliseconds;
        try { if (!await done.Task.WaitAsync(within)) return false; } catch (TimeoutException) { return false; }
        while (Environment.TickCount64 - Interlocked.Read(ref lastDelivery) < 1500 && Environment.TickCount64 < until) await Task.Delay(100);
        return true;
    }

    public async IAsyncEnumerable<(string DataType, string Json)> ReadAsync([EnumeratorCancellation] CancellationToken cancel)
    {
        await Task.Delay(Timeout.Infinite, cancel);
        yield break;
    }
    public Task SafeStopAsync() => Task.CompletedTask;
}
