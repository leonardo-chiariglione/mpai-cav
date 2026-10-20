using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Mpai.Cae.Usc.Arp;

// An irregularity of the tape: where it is (Start and End, in seconds of the Preservation Audio File: that is the time reference of every Time Label of the Irregularity File), who found it
// (a: the audio analysis, v: the video analysis, b: both), and, once classified, its type (sp, b, sot, eot, da, di, m, s, wf; pps, ssv, esv, sb) and its properties (the speed and
// equalisation standards of the reading and of the writing).
public sealed record Irregularity(string Id, string Source, double Start, double End, string? Type = null, JsonObject? Properties = null);

// The Irregularity File (CAE-IRR-V2.5): the Irregularities, their Time labels as the schema has them (an OSD Time whose Qualifier gives the Simple Time), the Offset in MILLISECONDS
// between the Preservation Audio File and the audio of the Preservation Audio-Visual File.
public static class IrregularityFile
{
    public const string Header = "CAE-IRR-V2.5";

    public static string Make(IEnumerable<Irregularity> irregularities, int? offsetMilliseconds = null, Func<Irregularity, string?>? audioFileUri = null, Func<Irregularity, string?>? imageUri = null)
    {
        var root = new JsonObject { ["Header"] = Header, ["IrregularityFileID"] = "irr-" + Guid.NewGuid().ToString("N")[..12] };
        if (offsetMilliseconds is not null) root["Offset"] = offsetMilliseconds;
        var array = new JsonArray();
        foreach (var i in irregularities)
        {
            var o = new JsonObject { ["IrregularityID"] = i.Id, ["Source"] = i.Source, ["TimeLabel"] = TimeLabel(i.Start, i.End) };
            if (i.Type is not null) o["IrregularityType"] = i.Type;
            if (i.Properties is not null) o["IrregularityProperties"] = JsonNode.Parse(i.Properties.ToJsonString());
            if (imageUri?.Invoke(i) is string image) o["ImageURI"] = image;
            if (audioFileUri?.Invoke(i) is string uri) o["AudioFileURI"] = uri;
            array.Add(o);
        }
        root["Irregularities"] = array;
        return root.ToJsonString();
    }

    public static JsonObject TimeLabel(double start, double end) => new()
    {
        ["Header"] = "OSD-TIM-V1.5", ["TimeID"] = "t-" + Guid.NewGuid().ToString("N")[..8], ["Data"] = $"{start:0.###} {end:0.###}",
        ["Qualifier"] = new JsonObject
        {
            ["Header"] = "TFA-TIQ-V1.5", ["TimeQualifierID"] = "tq", ["SubTypes"] = new JsonObject(),
            ["Formats"] = new JsonObject { ["MPAIFormat"] = new JsonObject
            {
                ["Header"] = "OSD-STM-V1.5", ["SimpleTimeID"] = "st",
                ["SimpleTimeData"] = new JsonArray(new JsonObject { ["FlagsByte"] = 0, ["StartTime"] = start, ["EndTime"] = end, ["AccuracyMode"] = "single", ["AccuracyPlusMinus"] = 0 })
            } }
        }
    };

    public static (IReadOnlyList<Irregularity> Irregularities, int? OffsetMilliseconds) Read(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("An Irregularity File is a JSON object.");
        if (root["Header"]?.GetValue<string>() != Header) throw new FormatException($"Not an Irregularity File: Header {root["Header"]}.");
        var list = new List<Irregularity>();
        foreach (var o in (root["Irregularities"] as JsonArray ?? throw new FormatException("The Irregularity File has no Irregularities.")).OfType<JsonObject>())
        {
            var time = o["TimeLabel"] as JsonObject ?? throw new FormatException("An irregularity has no TimeLabel.");
            var simple = time["SimpleTimeData"] as JsonArray ?? time["Qualifier"]?["Formats"]?["MPAIFormat"]?["SimpleTimeData"] as JsonArray
                         ?? throw new FormatException("The TimeLabel of an irregularity is not a Simple Time (no SimpleTimeData).");
            var t = simple.OfType<JsonObject>().FirstOrDefault() ?? throw new FormatException("The Simple Time of an irregularity is empty.");
            var factor = (t["TimeUnit"]?.GetValue<string>() ?? "00") switch { "00" => 1.0, "01" => 1e-3, "10" => 1e-6, "11" => 1e-9, var u => throw new FormatException($"Unknown TimeUnit '{u}'.") };
            list.Add(new Irregularity(o["IrregularityID"]!.GetValue<string>(), o["Source"]?.GetValue<string>() ?? "a", t["StartTime"]!.GetValue<double>() * factor, t["EndTime"]!.GetValue<double>() * factor,
                                      o["IrregularityType"]?.GetValue<string>(), o["IrregularityProperties"] is JsonObject p ? (JsonObject)JsonNode.Parse(p.ToJsonString())! : null));
        }
        return (list, root["Offset"]?.GetValue<int>());
    }

    // The irregularities of several files as one list, in the time of the Preservation Audio File. The same identifier is the same irregularity (its time the union of the two, its source b
    // if the two sources differ, its type and properties those that are given); different identifiers that overlap (within half a second) and whose sources differ (a, v, b) are
    // one, found by both, and keep the identifier of the audio.
    public static List<Irregularity> Union(IEnumerable<Irregularity> irregularities)
    {
        var byId = new List<Irregularity>();
        foreach (var i in irregularities)
        {
            var at = byId.FindIndex(x => x.Id == i.Id);
            if (at < 0) { byId.Add(i); continue; }
            var x = byId[at];
            byId[at] = x with { Source = x.Source == i.Source ? x.Source : "b", Start = Math.Min(x.Start, i.Start), End = Math.Max(x.End, i.End), Type = x.Type ?? i.Type, Properties = x.Properties ?? i.Properties };
        }
        var merged = new List<Irregularity>();
        foreach (var i in byId.OrderBy(i => i.Source == "v" ? 1 : 0).ThenBy(i => i.Start))          // the audio's first: the identifier kept is theirs
        {
            var at = merged.FindIndex(x => x.Source != i.Source && i.Start <= x.End + 0.5 && x.Start <= i.End + 0.5);
            if (at >= 0)
                merged[at] = merged[at] with { Source = "b", Start = Math.Min(merged[at].Start, i.Start), End = Math.Max(merged[at].End, i.End), Type = merged[at].Type ?? i.Type, Properties = merged[at].Properties ?? i.Properties };
            else merged.Add(i);
        }
        return merged.OrderBy(i => i.Start).ToList();
    }

    // What makes two lists the same, for a loop that must stop: the identifiers, sources, times and types.
    public static string Signature(IEnumerable<Irregularity> irregularities) =>
        string.Join(";", irregularities.OrderBy(i => i.Id, StringComparer.Ordinal).Select(i => $"{i.Id}|{i.Source}|{i.Start:0.###}|{i.End:0.###}|{i.Type}|{i.Properties?.ToJsonString()}"));
}
