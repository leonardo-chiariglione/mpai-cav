using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

using Mpai.Cae.Usc;

namespace Mpai.Cae.Apps;

// What the Apps of CAE-USC take from the person and give back, as the Data Types of the Module's boundary: files and a few words on the command line become Speech Objects, a Text List,
// a Damaged List; the Speech Object that comes back is written as a file.
public static class Inputs
{
    // Speech is 16 kHz (the speech standard of the Modules): a file at another rate is brought to it; stereo or other than 16 bit is refused.
    public static Audio Speech(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"There is no file {path}.");
        var audio = Audio.FromWav(File.ReadAllBytes(path));
        return audio.Rate == 16000 ? audio : audio.At(16000);
    }

    // Audio as it is in the file (16-bit PCM WAV mono), at its own rate.
    public static Audio Speech16(string path) => File.Exists(path) ? Audio.FromWav(File.ReadAllBytes(path)) : throw new FileNotFoundException($"There is no file {path}.");

    public static string SpeechObjectOf(string id, params string[] paths) => SpeechObject.Make(id, paths.Select(Speech).ToArray());

    public static string TextList(IEnumerable<string> texts) => new JsonObject
    {
        ["Header"] = "OSD-BTO-V1.5", ["BasicTextObjectID"] = "texts",
        ["BasicTextData"] = new JsonArray(texts.Select(t => (JsonNode)new JsonObject { ["Data"] = t }).ToArray())
    }.ToJsonString();

    // "0.5-1.0,2.0-2.5": the damaged sections of the Damaged Segment, in seconds from its start.
    public static IReadOnlyList<(double Start, double End)> Sections(string text)
    {
        var result = new List<(double, double)>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ends = part.Split('-', StringSplitOptions.TrimEntries);
            if (ends.Length != 2 || !double.TryParse(ends[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) || !double.TryParse(ends[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var e) || s < 0 || e <= s)
                throw new FormatException($"'{part}' is not a damaged section: write it start-end, in seconds (0.5-1.0), sections apart by commas.");
            result.Add((s, e));
        }
        return result.Count > 0 ? result : throw new FormatException("There is no damaged section.");
    }

    public static string DamagedList(IEnumerable<(double Start, double End)> sections) =>
        new JsonObject
        {
            ["Header"] = "CAE-DLS-V2.5", ["DamagedListID"] = "damaged",
            ["DamagedListData"] = new JsonArray(sections.Select(s => (JsonNode)Section(s.Start, s.End)).ToArray())
        }.ToJsonString();

    private static JsonObject Section(double start, double end) => new()
    {
        ["DamagedSectionTime"] = new JsonObject
        {
            ["Header"] = "OSD-TIM-V1.5", ["TimeID"] = "t", ["Data"] = $"{start.ToString(CultureInfo.InvariantCulture)} {end.ToString(CultureInfo.InvariantCulture)}",
            ["Qualifier"] = new JsonObject
            {
                ["Header"] = "TFA-TIQ-V1.5", ["TimeQualifierID"] = "tq", ["SubTypes"] = new JsonObject(),
                ["Formats"] = new JsonObject { ["MPAIFormat"] = new JsonObject
                {
                    ["Header"] = "OSD-STM-V1.5", ["SimpleTimeID"] = "st",
                    ["SimpleTimeData"] = new JsonArray(new JsonObject { ["FlagsByte"] = 0, ["StartTime"] = start, ["EndTime"] = end, ["AccuracyMode"] = "single", ["AccuracyPlusMinus"] = 0 })
                } }
            }
        }
    };

    // The speech of a Speech Object, one file for each piece it holds: path, or path-1, path-2 ... when it holds several.
    public static IReadOnlyList<string> WriteSpeech(string json, string path)
    {
        var pieces = SpeechObject.Read(json);
        var written = new List<string>();
        for (var i = 0; i < pieces.Count; i++)
        {
            var name = pieces.Count == 1 ? path : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path) + "-" + (i + 1) + Path.GetExtension(path));
            File.WriteAllBytes(name, pieces[i].ToWav()); written.Add(name);
        }
        return written;
    }
}