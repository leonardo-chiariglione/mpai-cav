using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Cae.Usc.Arp;

// CAE-AAP-V2.5, Audio Analysis for Preservation: the Preservation Audio File, the Preservation Audio-Visual File and the Irregularity File of the Video Analysis in; the Audio Files of
// the irregularities and the Audio Irregularity File out. It works in steps, which the loop with the Video Analysis makes:
//   1. With the Preservation Audio File (and the Audio-Visual File) it calculates the offset between the audio of the two, detects the irregularities of the audio, gives each an
//      identifier that is unique on the tape, and gives the Irregularity File (Source a, with the Offset) and the Audio Files of what it found (each with a second of margin on each side)
//      - the Video Analysis needs the Offset to put its irregularities in the time of the Preservation Audio File.
//   2. Each time the Irregularity File of the Video Analysis comes (in the time of the Preservation Audio File: it has the Offset from this AIM) it merges it with its own: the same
//      identifier is the same irregularity, and one the two found is one, Source b. It gives the merged Irregularity File and the Audio Files of all of them - but only if the merged
//      file is not what it gave last, which is what makes the loop with the Video Analysis come to an end.
// The Offset is in MILLISECONDS (the Irregularity File says so), the time reference the Preservation Audio File, and zero or more: an Audio-Visual File that starts BEFORE the
// Preservation Audio File has a negative offset, which the file cannot say, and the AIM says so. It runs whenever a Message arrives (continuous execution: it has a loop).
public sealed class AapAimProcessor(string instanceId, string audioKey, string avKey, string videoIrregularityKey, string filesKey, string irregularityKey) : IAimProcessor
{
    private const double Margin = 1.0;
    public string InstanceId { get; } = instanceId;

    private Audio? _audio, _avAudio;
    private int? _offset;                                  // milliseconds
    private List<Irregularity> _own = [];
    private string _lastSignature = "";

    private static string? Get(Message m, string key) => m.Ports.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    // The offset, once the AIM has the Preservation Audio File and the audio of the Audio-Visual File - in whatever order they come.
    private void SetOffset()
    {
        if (_audio is null || _avAudio is null) return;
        var samples = AudioAnalysis.Offset(_audio, _avAudio);
        if (samples < 0) throw new FormatException($"The Audio-Visual File starts {-samples / (double)_audio.Rate:0.###} s before the Preservation Audio File: the Irregularity File can say an Offset of zero or more only.");
        _offset = (int)Math.Round(samples * 1000.0 / _audio.Rate);
    }

    public Task<Message> ProcessAsync(Message message)
    {
        try
        {
            List<Irregularity>? give = null;
            if (Get(message, audioKey) is string audioJson)
            {
                var items = AudioObject.Read(audioJson);
                if (items.Count != 1) throw new FormatException($"The Preservation Audio File is one audio file: it holds {items.Count}.");
                _audio = items[0].Audio; _offset = null; _lastSignature = "";
                _own = AudioAnalysis.Detect(_audio).Select(d => new Irregularity(Guid.NewGuid().ToString(), "a", d.Start, d.End)).ToList();
                SetOffset();
                give = _own;
            }
            if (Get(message, avKey) is string av)
            {
                _avAudio = AudioVisualObject.Read(av).Audio;
                SetOffset();
                give ??= _own;
            }
            if (Get(message, videoIrregularityKey) is string videoJson)
            {
                if (_audio is null) throw new FormatException("The Irregularity File of the Video Analysis came before the Preservation Audio File.");
                give = IrregularityFile.Union(_own.Concat(IrregularityFile.Read(videoJson).Irregularities));
            }
            if (give is null || _audio is null) return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = "IrregularityFile", DataType = "CAE-IRR-V2.5" });
            var signature = IrregularityFile.Signature(give) + "@" + _offset;
            if (signature == _lastSignature) return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = "IrregularityFile", DataType = "CAE-IRR-V2.5" });   // nothing new: the loop is done
            _lastSignature = signature;

            var irr = IrregularityFile.Make(give, _offset, i => "urn:mpai:cae:aap:audio:" + i.Id);
            var files = new AudioObject.Item[give.Count];
            for (var k = 0; k < give.Count; k++)
            {
                var from = Math.Clamp((int)((give[k].Start - Margin) * _audio.Rate), 0, _audio.Samples.Length); var to = Math.Clamp((int)((give[k].End + Margin) * _audio.Rate), from, _audio.Samples.Length);
                files[k] = new AudioObject.Item(give[k].Id, new Audio(_audio.Samples[from..to], _audio.Rate));
            }
            var ports = new Dictionary<string, string> { [irregularityKey] = irr };
            if (files.Length > 0) ports[filesKey] = AudioObject.Make("aap-" + Guid.NewGuid().ToString("N")[..12], files);
            return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = "IrregularityFile", DataType = "CAE-IRR-V2.5", Payload = irr, Ports = ports });
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "CAE-AAP-V2.5: " + ex.Message));
        }
    }
}
