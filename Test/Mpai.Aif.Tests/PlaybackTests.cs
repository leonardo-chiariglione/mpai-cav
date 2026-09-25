using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AIF.Controller;
using AIF.SharedStorage;
using Mpai.Aif.Api;
using Mpai.Rca;
using Mpai.Wdl;

namespace Mpai.Aif.Tests;

// PLAYBACK (M3219 3.5): a record is a source of the User Agent's Physical Layer;
// a workflow binds its tracks to boundary Input Ports with stream, all started
// together on one clock, paced by the Controller's stamps. Played back into the
// same Module and recorded again, the two records hold the same data.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class PlaybackTests
{
    private const string Text = StorageTests.Text;
    private const string Rec = "1TST-REC-V1.0-I01";

    private static string Location() => Path.Combine(Path.GetTempPath(), "mpai-phase6-" + Guid.NewGuid().ToString("N"));

    private static readonly byte[] Bytes = Enumerable.Range(0, 100_000).Select(i => (byte)(i % 251)).ToArray();

    // A test writes the three inputs at their own rates, and the Controller records.
    private static string MakeRecord(ControllerApi api)
    {
        api.RecordStart(Rec, out var id);
        var started = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            while (started.ElapsedMilliseconds < i * 20) Thread.Sleep(1);
            api.InputWrite(Rec, Text, 1, $"a{i}", 2000);
            if (i % 4 == 2) api.InputWrite(Rec, Text, 2, $"b{i / 4}", 2000);
            if (i % 10 == 5)
            {
                var (_, reference) = api.PayloadPut(Rec, Text, 3, Bytes);
                api.InputWrite(Rec, Text, 3, new JsonObject { ["Entries"] = new JsonArray(new JsonObject { ["DataLength"] = Bytes.Length, ["DataURI"] = reference }) }.ToJsonString(), 2000);
            }
        }
        Thread.Sleep(200);                                   // the last echoes
        api.RecordStop(Rec, out _);
        return id!;
    }

    // The record played by a workflow, recorded again.
    private static async Task<(string Id, WorkflowInterpreter.PlaybackReport Report)> Play(ControllerApi api, string first, string workflowTail)
    {
        var devices = new DeviceRegistry().RegisterRecord("first", new StoredRecord(api.ModuleStorage(Rec)!, first));
        var interpreter = new WorkflowInterpreter(api.Async(), devices);
        api.RecordStart(Rec, out var id);
        await interpreter.RunAsync(new WorkflowReader().Read($"workflow PLAY over {Rec}\non Start:\n{workflowTail}"), CancellationToken.None);
        Thread.Sleep(200);
        api.RecordStop(Rec, out _);
        return (id!, interpreter.Playbacks.Single());
    }

    // Each Message of a record: direction and Port, its Object with its payloads
    // resolved, and its time from the first Message of the record.
    private static List<(string Key, string Value, double AtMs)> Read(IRuledStorage storage, string id)
    {
        var records = RecordTests.Records(storage, id).OrderBy(r => (long)r["Sequence"]!).ToList();
        var start = records.Select(r => DateTimeOffset.Parse((string)r["Stamp"]!)).Min();
        return records.Select(r =>
        {
            var json = (string)r["Json"]!;
            var m = System.Text.RegularExpressions.Regex.Match(json, "record:payload/([0-9]+)");
            if (m.Success && storage.MPAI_AIFM_RuledStorage_Get($"{id}/payload/{m.Groups[1].Value}", out var bytes) == StorageOutcome.OK)
                json = $"payload of {bytes.Length} bytes, " + (bytes.SequenceEqual(Bytes) ? "as written" : "different");
            return ($"{r["Direction"]}#{r["PortNumber"]}", json, (DateTimeOffset.Parse((string)r["Stamp"]!) - start).TotalMilliseconds);
        }).ToList();
    }

    private static string Tracks(List<(string Key, string Value, double AtMs)> messages) =>
        string.Join(" | ", messages.GroupBy(m => m.Key).OrderBy(g => g.Key).Select(g => $"{g.Key}: {string.Join(",", g.Select(m => m.Value))}"));

    // The times of the inputs played against the times recorded, divided by the rate.
    private static (double Median, double Max) Pace(List<(string Key, string Value, double AtMs)> recorded, List<(string Key, string Value, double AtMs)> played, double rate)
    {
        var a = recorded.Where(m => m.Key.StartsWith("In")).ToList();
        var b = played.Where(m => m.Key.StartsWith("In")).ToList();
        var errors = a.Zip(b).Select(p => Math.Abs(p.Second.AtMs - p.First.AtMs / rate)).OrderBy(e => e).ToList();
        return (errors[errors.Count / 2], errors[^1]);
    }

    [Fact]
    public async Task SameDataAtItsPace()
    {
        var result = new Dictionary<string, string>();
        var report = new Dictionary<string, object>();
        using var api = new ControllerApi(StorageTests.Amds, Path.Combine(StorageTests.Amds, "no-settings.json"), new StorageAims());
        api.StartFlow(Rec);
        api.SharedStorageInit(Rec, Location());
        var storage = api.ModuleStorage(Rec)!;

        var first = MakeRecord(api);
        var recorded = Read(storage, first);

        const string streams = """
                stream A (TST-TXT-V1.0:1) from record "first"
                stream B (TST-TXT-V1.0:2) from record "first"
                stream P (TST-TXT-V1.0:3) from record "first"
            """;
        foreach (var (rate, tail) in new[] { (1.0, streams + "\n    wait 3s\n"), (2.0, streams.Replace("\"first\"", "\"first\" at x2") + "\n    wait 3s\n") })
        {
            var (id, played) = await Play(api, first, tail);
            var again = Read(storage, id);
            var label = rate == 1 ? "at its original rate" : "at twice its rate";
            result[$"played {label}: the same Objects, in the same order, on each Port"] =
                Tracks(again) == Tracks(recorded) ? "yes" : Tracks(again);
            // THE PACE IS REPORTED, NOT JUDGED (M3219 3.7): alone, each write starts
            // within a millisecond or two of its time; beside other tests that load
            // the machine, the pool of threads it waits on can be late by hundreds.
            var (median, max) = Pace(recorded, again, rate);
            report[label] = new { played.Messages, RecordedMs = played.Recorded.TotalMilliseconds, TookMs = played.Took.TotalMilliseconds,
                                  WriteErrorMs = new { Median = played.MedianErrorMs, P95 = played.P95ErrorMs, Max = played.MaxErrorMs },
                                  StampErrorMs = new { Median = median, Max = max } };
        }
        result["the record played"] = Tracks(recorded);
        api.StopFlow(Rec);

        var path = Path.Combine(Repository.Root, "Test", "Reports", "playback.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Expected.Match("playback.json", result);
    }

    // What stops a workflow before anything is played.
    [Fact]
    public async Task Refusals()
    {
        var result = new Dictionary<string, string>();
        using var api = new ControllerApi(StorageTests.Amds, Path.Combine(StorageTests.Amds, "no-settings.json"), new StorageAims());
        api.StartFlow(Rec);
        api.SharedStorageInit(Rec, Location());
        var first = MakeRecord(api);

        async Task<string> Run(DeviceRegistry devices, string line)
        {
            try
            {
                await new WorkflowInterpreter(api.Async(), devices).RunAsync(
                    new WorkflowReader().Read($"workflow PLAY over {Rec}\non Start:\n    {line}\n    wait 10ms\n"), CancellationToken.None);
                return "played";
            }
            catch (Exception refused) { return $"{refused.GetType().Name}: {refused.Message}"; }
        }

        var devices = new DeviceRegistry().RegisterRecord("first", new StoredRecord(api.ModuleStorage(Rec)!, first));
        result["a track the record does not have"] = await Run(devices, "stream Q (TST-TXT-V1.0:4) from record \"first\"");
        result["a record the User Agent does not know"] = await Run(devices, "stream A (TST-TXT-V1.0:1) from record \"nope\"");
        result["two rates for one record"] = await Run(devices,
            "stream A (TST-TXT-V1.0:1) from record \"first\"\n    stream B (TST-TXT-V1.0:2) from record \"first\" at x2");
        api.StopFlow(Rec);

        // A record of which the User Agent is not a reader: its header written by
        // an AIM of TST-MPS for no one else.
        const string mps = "1TST-MPS-V1.0-I01";
        api.StartFlow(mps);
        api.SharedStorageInit(mps, Location());
        api.Advance(mps, [new(Text, 1, "R9/header={}; category=Boundary")]);
        var closed = new DeviceRegistry().RegisterRecord("closed", new StoredRecord(api.ModuleStorage(mps)!, "R9"));
        result["a record the User Agent is not a reader of"] = await Run(closed, "stream A (TST-TXT-V1.0:1) from record \"closed\"");
        api.StopFlow(mps);

        // The reader, now: stream is a step.
        var read = new WorkflowReader().Read($"workflow PLAY over {Rec}\non Start:\n    stream A (TST-TXT-V1.0:1) from record \"R\" at x2.5\n");
        var s = read.OnStart.Single();
        result["stream, read"] = $"{s.Kind} {s.Port} from {s.Text} at x{s.Rate}";
        try { new WorkflowReader().Read($"workflow PLAY over {Rec}\non Start:\n    stream A (TST-TXT-V1.0:1) from \"R\"\n"); result["stream without 'record'"] = "read"; }
        catch (WorkflowReader.WorkflowSyntaxError e) { result["stream without 'record'"] = "refused: " + e.Message; }

        Expected.Match("playback-refusals.json", result);
    }
}
