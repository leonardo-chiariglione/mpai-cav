using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Cae.Usc.Sra;

// CAE-SRA-V2.5, Speech Restoration Assembly. Inputs: the Damaged Segment (a Basic Speech Object, speech at 16 kHz), the Damaged List (CAE-DLS-V2.5: for each damaged section, its
// Time as a Simple Time, from left to right) and the Synthesised Speech (Basic Speech Objects, ONE FOR EACH DAMAGED SECTION, in the order of the Damaged
// List, each as soon as it is made). Output: the Restored Segment, a Basic Speech Object, in parts. Everything in the Speech Restoration System is speech.
//
// The Restored Segment is available before the last piece of speech is: when the piece for a damaged section arrives, the Assembly gives the speech from the end of the
// previous damaged section to the start of this one followed by the piece (brought to the rate of the Damaged Segment, should it differ); with the last piece it gives what follows the last damaged
// section too. The parts, in the order they are given, make the Restored Segment. A piece that comes before the Damaged Segment and the Damaged List waits for them.
// The AIM runs whenever a Message arrives (continuous execution).
public sealed class SraAimProcessor : IAimProcessor
{
    private readonly string _damaged, _list, _synthesised, _out;
    private Audio? _audio;
    private List<Section>? _sections;
    private readonly Queue<Audio> _pieces = new();
    private int _done, _cursor;

    public string InstanceId { get; }

    public SraAimProcessor(string instanceId, string damagedSegmentKey, string damagedListKey, string synthesisedSpeechKey, string outputKey)
    {
        InstanceId = instanceId; _damaged = damagedSegmentKey; _list = damagedListKey; _synthesised = synthesisedSpeechKey; _out = outputKey;
    }

    public Task<Message> ProcessAsync(Message message)
    {
        try
        {
            if (message.Ports.TryGetValue(_damaged, out var d) && !string.IsNullOrWhiteSpace(d)) { _audio = Single(SpeechObject.Read(d), "Damaged Segment"); _done = 0; _cursor = 0; }
            if (message.Ports.TryGetValue(_list, out var l) && !string.IsNullOrWhiteSpace(l)) { _sections = Sections(l); _done = 0; _cursor = 0; }
            if (message.Ports.TryGetValue(_synthesised, out var s) && !string.IsNullOrWhiteSpace(s))
                foreach (var piece in SpeechObject.Read(s)) _pieces.Enqueue(piece);

            var part = new List<short>(); int rate = 0; var gave = false;
            while (_audio is not null && _sections is not null && _pieces.Count > 0)
            {
                if (_done >= _sections.Count) throw new FormatException($"There is more Synthesised Speech than the Damaged List has sections ({_sections.Count}).");
                var section = _sections[_done];
                var from = Math.Clamp((int)Math.Round(section.Start * _audio.Rate), _cursor, _audio.Samples.Length);
                var to = Math.Clamp((int)Math.Round(section.End * _audio.Rate), from, _audio.Samples.Length);
                part.AddRange(_audio.Samples.AsSpan(_cursor, from - _cursor).ToArray());
                part.AddRange(_pieces.Dequeue().At(_audio.Rate).Samples);
                _cursor = to; _done++; rate = _audio.Rate; gave = true;
                if (_done == _sections.Count) { part.AddRange(_audio.Samples.AsSpan(_cursor).ToArray()); _cursor = _audio.Samples.Length; }
            }
            if (!gave) return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = "BasicSpeechObject", DataType = SpeechObject.Header });
            var finished = _done == _sections!.Count;
            var text = SpeechObject.Make($"sra-{Guid.NewGuid().ToString("N")[..12]}", new Audio(part.ToArray(), rate));
            if (finished) { _audio = null; _sections = null; _done = 0; _cursor = 0; }          // the next Damaged Segment starts again
            return Task.FromResult(new Message
            {
                MessageId = message.MessageId, MessageType = "BasicSpeechObject", DataType = SpeechObject.Header, Payload = text,
                Ports = new Dictionary<string, string> { [_out] = text }
            });
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "CAE-SRA-V2.5: " + ex.Message));
        }
    }

    // A damaged section: from Start to End, in seconds.
    public sealed record Section(double Start, double End);

    // The sections of a Damaged List, from left to right. The Time of a damaged section is a Simple Time (OSD-STM-V1.5): its SimpleTimeData gives StartTime, EndTime and
    // TimeUnit (00 seconds, 01 milliseconds, 10 microseconds, 11 nanoseconds). A Time that carries its Simple Time in its Qualifier (Formats, MPAIFormat) is read as well.
    public static List<Section> Sections(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("A Damaged List is a JSON object.");
        if (root["Header"]?.GetValue<string>() != "CAE-DLS-V2.5") throw new FormatException($"Not a Damaged List: Header {root["Header"]}.");
        var sections = new List<Section>();
        foreach (var item in (root["DamagedListData"] as JsonArray ?? throw new FormatException("The Damaged List has no DamagedListData.")).OfType<JsonObject>())
        {
            var time = item["DamagedSectionTime"] as JsonObject ?? throw new FormatException("A damaged section has no DamagedSectionTime.");
            var simple = time["SimpleTimeData"] as JsonArray ?? time["Qualifier"]?["Formats"]?["MPAIFormat"]?["SimpleTimeData"] as JsonArray
                         ?? throw new FormatException("The Time of a damaged section is not a Simple Time (no SimpleTimeData).");
            var t = simple.OfType<JsonObject>().FirstOrDefault() ?? throw new FormatException("The Simple Time of a damaged section is empty.");
            var unit = t["TimeUnit"]?.GetValue<string>() ?? "00";
            var factor = unit switch { "00" => 1.0, "01" => 1e-3, "10" => 1e-6, "11" => 1e-9, _ => throw new FormatException($"Unknown TimeUnit '{unit}'.") };
            var section = new Section(t["StartTime"]!.GetValue<double>() * factor, t["EndTime"]!.GetValue<double>() * factor);
            if (section.End < section.Start) throw new FormatException("A damaged section ends before it starts.");
            if (sections.Count > 0 && section.Start < sections[^1].End) throw new FormatException("The damaged sections are not from left to right, or they overlap.");
            sections.Add(section);
        }
        return sections;
    }

    private static Audio Single(IReadOnlyList<Audio> files, string what) =>
        files.Count == 1 ? files[0] : throw new FormatException($"The {what} is one Audio File: it holds {files.Count}.");
}
