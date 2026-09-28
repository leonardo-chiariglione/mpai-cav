using System.Text.Json;
using System.Text.Json.Nodes;

using Mpai.Cav.Ams;
using Mpai.Cav.Map;

namespace Mpai.Aif.Tests;

// STEP 2 (M3243 3.2): EDP IN DIALOGUE WITH THE PASSENGER AND THE AMS, in text. EDP
// in process, its language model Ollama's; the Offline Map of Phase 8, five of its
// way points named - two stations. What the passenger says, and what the AMS
// answers, written to it as HCI would; what it tells the passenger and what it sends
// the AMS read back. Judged: a place understood as its way point; one the map does
// not have, and one named ambiguously, not sent - the passenger told the places,
// asked which; the Routes told with their times; yes, a choice of the second, no;
// suspend, resume, stop; the Route's state and the arrival told; every AMS-HCI
// Message valid. Reported: each turn - what was said, what was answered, what was
// sent - and the longest.
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
[Collection(Timing.Name)]
public class CavDialogueTests
{
    public static readonly IReadOnlyDictionary<string, string> Places = new Dictionary<string, string>
    {
        ["W21"] = "Central Station", ["W02"] = "North Station", ["W20"] = "Hospital", ["W12"] = "School", ["W22"] = "Museum"
    };

    private sealed class Edp
    {
        private readonly Mpai.Mmc.Edp.EdpAimProcessor edp;
        private readonly AIF.Controller.AimPortReader ports;
        private int turn;
        public readonly List<string> Turns = [];
        public readonly List<JsonNode> Sent = [];
        public double LongestMs;

        public Edp(string storage)
        {
            var store = new AIF.Store.AmdStore(Repository.Amds);
            store.Scan();
            ports = AIF.Controller.AimPortReader.Load(store, "1MMC-EDP-V2.5-I01");
            var privateStorage = new AIF.SharedStorage.FileSharedStorage(storage, "1MMC-EDP-V2.5-I01", "test");
            edp = new Mpai.Mmc.Edp.EdpAimProcessor("1MMC-EDP-V2.5-I01", new Mpai.Mmc.Edp.OllamaClient("llama3.2:3b"), ports, privateStorage);
        }

        private (string? Reply, JsonNode? Sent) Process(Dictionary<string, string> input, string said)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var output = edp.ProcessAsync(new AIF.Controller.Message { MessageId = $"T{++turn}", Ports = input }).GetAwaiter().GetResult();
            LongestMs = Math.Max(LongestMs, clock.Elapsed.TotalMilliseconds);
            string? reply = output.Ports.TryGetValue(ports.Output("OSD-BTO-V1.5"), out var t) ? Mpai.Core.MpaiJson.FromJson<Mpai.Core.BasicTextObject>(t)?.GetText() : null;
            JsonNode? sent = output.Ports.TryGetValue(ports.Output("CAV-AHM-V2.0"), out var a) ? JsonNode.Parse(a) : null;
            if (sent is not null) Sent.Add(sent);
            Turns.Add($"{said} -> {reply ?? "(nothing said)"}{(sent is null ? "" : " [" + sent["HCIMessage"]!.ToJsonString() + "]")}");
            return (reply, sent);
        }

        public void Know(string map) => Process(new() { [ports.Input("OSD-BOO-V1.5")] = map }, "(the Offline Map)");

        public (string? Reply, JsonNode? Sent) Say(string text) =>
            Process(new() { [ports.Input("OSD-BTO-V1.5")] = Mpai.Core.MpaiJson.ToJson(Mpai.Core.BasicTextObject.FromText(text)) }, $"\"{text}\"");

        public (string? Reply, JsonNode? Sent) Told(JsonObject ams, string what) =>
            Process(new() { [ports.Input("CAV-AHM-V2.0")] = new JsonObject { ["Header"] = AmsTypes.Hci, ["AMSHCIMessageID"] = "AHM-A", ["AMSMessage"] = ams }.ToJsonString() },
                    $"(the AMS: {what})");
    }

    // The Routes the AMS proposes, of the times given.
    private static JsonObject Routes(params double[] seconds) => new()
    {
        ["RouteList"] = new JsonArray(seconds.Select((t, i) => (JsonNode)new JsonObject
        {
            ["Header"] = "CAV-RTE-V2.0", ["RouteID"] = $"RTE{i + 1:D4}", ["OfflineMapID"] = "MAP", ["RouteTime"] = Mpai.Cav.Ess.EssJson.SimpleTime($"R{i}-T", 0),
            ["RouteSegments"] = new JsonArray(new JsonObject
            {
                ["WayPoint1ID"] = "W00", ["WayPoint2ID"] = "W10", ["EstimatedArrDepSpaceTime"] = Mpai.Cav.Ess.EssJson.SimpleTime($"R{i}-E", (long)(t * 1000))
            })
        }).ToArray())
    };

    private static JsonObject State(string status, string routeId = "RTE0001") => new() { ["RouteStatus"] = new JsonObject { ["RouteID"] = routeId, ["Status"] = status } };

    private static string? To(JsonNode? sent) => sent?["HCIMessage"]?["RequestedRoutes"]?[0]?["Route"]?["RouteSegments"]?[0]?["WayPoint2ID"]?.GetValue<string>();
    private static string? Command(JsonNode? sent) => sent is null ? null : $"{sent["HCIMessage"]?["RouteCommand"]} {sent["HCIMessage"]?["SelectedRouteID"]}".Trim();
    private static string Else(string? reply, JsonNode? sent) => $"sent {(sent is null ? "nothing" : sent["HCIMessage"]!.ToJsonString())}: {reply ?? "nothing said"}";

    [SkippableFact]
    public void Step2EdpInDialogue()
    {
        Skip.IfNot(OllamaHas("llama3.2:3b"), "Ollama is not running with llama3.2:3b.");
        var result = new Dictionary<string, string>();
        var storage = Path.Combine(Path.GetTempPath(), "mpai-p11-edp-" + Guid.NewGuid().ToString("N"));
        var map = RoadMap.Grid(3).Named(Places).ToOfflineMapObject(0);
        var turns = new List<string>();
        var sent = new List<JsonNode>();
        double longest = 0;
        try
        {
            var edp = new Edp(Path.Combine(storage, "1"));
            edp.Know(map);
            var (r, s) = edp.Say("Please take me to the hospital.");
            result["a place"] = To(s) == "W20" ? "understood as its way point, requested" : Else(r, s);
            (r, s) = edp.Say("Actually, I would like to go to the airport.");
            result["a place the map does not have"] = s is null && r is { } a && a.Contains("do not know") && a.Contains("Museum") ? "not sent; the places told" : Else(r, s);
            (r, s) = edp.Say("Take me to the station.");
            result["a place named ambiguously"] = s is null && r is { } b && b.Contains("Central Station") && b.Contains("North Station") ? "not sent; asked which" : Else(r, s);
            (r, s) = edp.Say("The central one.");
            result["the answer to which"] = To(s) == "W21" ? "understood, requested" : Else(r, s);
            (r, _) = edp.Told(Routes(44, 95), "two Routes, 44 s and 95 s");
            result["the Routes told"] = r is { } c && c.Contains("40 seconds") && c.Contains("2 minutes") ? "with their times" : r ?? "nothing said";
            (r, s) = edp.Say("Yes, let's go.");
            result["yes"] = Command(s) == "Execute RTE0001" ? "the first Route executed" : Else(r, s);
            (r, _) = edp.Told(State("Executing"), "Executing");
            result["on the way"] = r?.Contains("on our way to Central Station") == true ? "told" : r ?? "nothing said";
            (r, s) = edp.Say("Can we stop for a moment, please?");
            result["suspend"] = Command(s) == "Suspend" ? "sent" : Else(r, s);
            edp.Told(State("Suspended"), "Suspended");
            (r, s) = edp.Say("OK, we can continue now.");
            result["resume"] = Command(s) == "Resume" ? "sent" : Else(r, s);
            edp.Told(State("Executing"), "Executing");
            (r, _) = edp.Told(State("Arrived"), "Arrived");
            result["the arrival"] = r?.Contains("arrived at Central Station") == true ? "told" : r ?? "nothing said";
            turns.AddRange(edp.Turns); sent.AddRange(edp.Sent); longest = Math.Max(longest, edp.LongestMs);

            edp = new Edp(Path.Combine(storage, "2"));
            edp.Know(map);
            (_, s) = edp.Say("Bring me to the museum, please.");
            var museum = To(s);
            edp.Told(Routes(60, 80), "two Routes, 60 s and 80 s");
            (r, s) = edp.Say("I prefer the second route.");
            result["a choice of the second Route"] = museum == "W22" && Command(s) == "Execute RTE0002" ? "the second executed" : $"{museum}; " + Else(r, s);
            edp.Told(State("Executing", "RTE0002"), "Executing");
            (r, s) = edp.Say("Stop the car, I want to get out.");
            result["stop"] = Command(s) == "Stop" ? "sent" : Else(r, s);
            edp.Told(State("Stopped", "RTE0002"), "Stopped");
            (_, s) = edp.Say("Go to the school.");
            var school = To(s);
            edp.Told(Routes(70), "one Route, 70 s");
            (r, s) = edp.Say("No, thanks.");
            result["no"] = school == "W12" && s is null ? "nothing sent; the CAV stays" : $"{school}; " + Else(r, s);
            turns.AddRange(edp.Turns); sent.AddRange(edp.Sent); longest = Math.Max(longest, edp.LongestMs);
        }
        finally { try { Directory.Delete(storage, recursive: true); } catch { } }

        var schemas = AIF.Metadata.PublishedSchemas.At(Repository.Schemas);
        var schema = schemas[Path.GetFullPath(Path.Combine(Repository.Schemas, "CAV2", "V2.0", "data", "AMSHCIMessage.json"))];
        var valid = sent.Count(t => { using var doc = JsonDocument.Parse(t.ToJsonString()); lock (AIF.Metadata.PublishedSchemas.Lock) return schema.Evaluate(doc.RootElement).IsValid; });
        result["every AMS-HCI Message against its schema"] = valid == sent.Count ? "valid" : $"{valid} of {sent.Count} valid";
        var report = new Dictionary<string, object> { ["turns"] = turns, ["the longest turn"] = $"{longest / 1000:0.0} s" };
        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "cav-stage1-edp-dialogue.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + Environment.NewLine);
        Expected.Match("cav-stage1-edp-dialogue.json", result);
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
