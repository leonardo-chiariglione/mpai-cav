using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Cae.Usc.Arp;

// CAE-TAR-V2.5, Tape Audio Restoration: the Irregularity File and the Preservation Audio File in; the Restored Audio Files and the Editing List out.
//
// The Irregularity File says, for an irregularity, the standards the tape was READ with (ReadingSpeedStandard, ReadingEqualisationStandard) and those it was WRITTEN with
// (WritingSpeedStandard, WritingEqualisationStandard); the section of the Preservation Audio File the irregularity covers is its TimeLabel (a Simple Time). Where the two differ the
// AIM corrects the section: the sampling frequency to apply (a tape read at twice the speed it was written at plays at half the rate) and the equalisation (the response it should
// have had over the one it had). It does not detect the errors: the analysis did. One restored file for each section corrected; the Editing List records what was applied to each.
// The Irregularity File has no field for a tape read backwards, so the AIM does not reverse anything (ReadingBackwards is false in the Editing List).
// When no irregularity needs a correction the AIM gives nothing.
public sealed class TarAimProcessor(string instanceId, string irregularityKey, string audioKey, string editingListKey, string restoredKey) : IAimProcessor
{
    public string InstanceId { get; } = instanceId;

    // The AIM runs whenever a Message arrives (the Preservation Audio File comes once, the Irregularity File - the selected irregularities, which may be given more than once as the
    // analyses settle - when it comes): it keeps the audio and the last file it restored against, and restores again only when the file is not the last.
    private string? _audioJson, _irregularities, _lastRestoredFor;

    private static Message Nothing(Message m) => new() { MessageId = m.MessageId, MessageType = "EditingList", DataType = "CAE-ELS-V2.5" };

    public Task<Message> ProcessAsync(Message message)
    {
        if (message.Ports.TryGetValue(irregularityKey, out var i) && !string.IsNullOrWhiteSpace(i)) _irregularities = i;
        if (message.Ports.TryGetValue(audioKey, out var a) && !string.IsNullOrWhiteSpace(a)) { _audioJson = a; _lastRestoredFor = null; }
        if (_irregularities is null || _audioJson is null) return Task.FromResult(Nothing(message));       // the other is still to come
        if (_irregularities == _lastRestoredFor) return Task.FromResult(Nothing(message));                   // the same file again: nothing new
        _lastRestoredFor = _irregularities;
        var irregularities = _irregularities; var audioJson = _audioJson;
        try
        {
            var items = AudioObject.Read(audioJson);
            if (items.Count != 1) throw new FormatException($"The Preservation Audio File is one audio file: it holds {items.Count}.");
            var audio = items[0].Audio;
            var root = JsonNode.Parse(irregularities) as JsonObject ?? throw new FormatException("An Irregularity File is a JSON object.");
            if (root["Header"]?.GetValue<string>() != "CAE-IRR-V2.5") throw new FormatException($"Not an Irregularity File: Header {root["Header"]}.");

            var restorations = new JsonArray(); var restored = new List<AudioObject.Item>();
            double? originalSpeed = null; string? originalEqualisation = null;
            foreach (var irregularity in (root["Irregularities"] as JsonArray ?? throw new FormatException("The Irregularity File has no Irregularities.")).OfType<JsonObject>())
            {
                var p = irregularity["IrregularityProperties"] as JsonObject; if (p is null) continue;
                double? readSpeed = Number(p, "ReadingSpeedStandard"), writeSpeed = Number(p, "WritingSpeedStandard");
                string? readEq = p["ReadingEqualisationStandard"]?.GetValue<string>(), writeEq = p["WritingEqualisationStandard"]?.GetValue<string>();
                var speedDiffers = readSpeed is not null && writeSpeed is not null && readSpeed != writeSpeed;
                var eqSpeed = writeSpeed ?? readSpeed;
                var eqDiffers = readEq is not null && writeEq is not null && eqSpeed is not null && (readEq != writeEq || speedDiffers);
                if (!speedDiffers && !eqDiffers) continue;

                var (start, end) = SectionOf(irregularity, audio);
                var from = (int)Math.Round(start * audio.Rate); var to = Math.Min((int)Math.Round(end * audio.Rate), audio.Samples.Length);
                if (to <= from) throw new FormatException("An irregularity covers no sample of the Preservation Audio File.");
                var section = new Audio(audio.Samples[from..to], audio.Rate);
                if (eqDiffers) section = Tape.CorrectEqualisation(section, readEq!, readSpeed ?? writeSpeed!.Value, writeEq!, writeSpeed ?? readSpeed!.Value);
                var applied = speedDiffers ? Tape.AppliedSamplingFrequency(audio.Rate, readSpeed!.Value, writeSpeed!.Value) : audio.Rate;
                section = new Audio(section.Samples, applied);

                var id = Guid.NewGuid().ToString();
                originalSpeed ??= readSpeed; originalEqualisation ??= readEq;
                var restoration = new JsonObject
                {
                    ["RestorationID"] = id, ["PreservationAudioFileStart"] = Clock(start), ["PreservationAudioFileEnd"] = Clock(to / (double)audio.Rate),
                    ["RestoredAudioFileURI"] = "urn:mpai:cae:tar:restored:" + id, ["ReadingBackwards"] = false, ["AppliedSamplingFrequency"] = applied
                };
                if (writeSpeed is not null) restoration["AppliedSpeedStandard"] = writeSpeed;
                if (writeEq is not null) restoration["AppliedEqualisationStandard"] = writeEq;
                restorations.Add(restoration); restored.Add(new AudioObject.Item(id, section));
            }
            if (restorations.Count == 0) return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = "EditingList", DataType = "CAE-ELS-V2.5" });

            var list = new JsonObject { ["Header"] = "CAE-ELS-V2.5", ["EditingListID"] = "els-" + Guid.NewGuid().ToString("N")[..12], ["OriginalSamplingFrequency"] = audio.Rate, ["Restorations"] = restorations };
            if (originalSpeed is not null) list["OriginalSpeedStandard"] = originalSpeed;
            if (originalEqualisation is not null) list["OriginalEqualisationStandard"] = originalEqualisation;
            var listJson = list.ToJsonString(); var audioOut = AudioObject.Make("tar-" + Guid.NewGuid().ToString("N")[..12], restored.ToArray());
            return Task.FromResult(new Message
            {
                MessageId = message.MessageId, MessageType = "EditingList", DataType = "CAE-ELS-V2.5", Payload = listJson,
                Ports = new Dictionary<string, string> { [editingListKey] = listJson, [restoredKey] = audioOut }
            });
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "CAE-TAR-V2.5: " + ex.Message));
        }
    }

    private static double? Number(JsonObject o, string name) => o[name]?.GetValue<double>();

    // The section an irregularity covers: its TimeLabel, a Time whose Qualifier gives the Simple Time (or a Simple Time itself).
    private static (double Start, double End) SectionOf(JsonObject irregularity, Audio audio)
    {
        var time = irregularity["TimeLabel"] as JsonObject ?? throw new FormatException("An irregularity has no TimeLabel.");
        var simple = time["SimpleTimeData"] as JsonArray ?? time["Qualifier"]?["Formats"]?["MPAIFormat"]?["SimpleTimeData"] as JsonArray
                     ?? throw new FormatException("The TimeLabel of an irregularity is not a Simple Time (no SimpleTimeData).");
        var t = simple.OfType<JsonObject>().FirstOrDefault() ?? throw new FormatException("The Simple Time of an irregularity is empty.");
        var factor = (t["TimeUnit"]?.GetValue<string>() ?? "00") switch { "00" => 1.0, "01" => 1e-3, "10" => 1e-6, "11" => 1e-9, var u => throw new FormatException($"Unknown TimeUnit '{u}'.") };
        var start = t["StartTime"]!.GetValue<double>() * factor; var end = t["EndTime"]!.GetValue<double>() * factor;
        return (Math.Max(0, start), Math.Min(end, audio.Seconds));
    }

    // The Editing List writes times as hh:mm:ss.mmm.
    public static string Clock(double seconds)
    {
        var ms = (long)Math.Round(seconds * 1000);
        return string.Create(CultureInfo.InvariantCulture, $"{ms / 3_600_000:00}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00}.{ms % 1000:000}");
    }
}
