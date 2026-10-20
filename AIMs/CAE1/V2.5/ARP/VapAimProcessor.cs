using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Cae.Usc.Arp;

// CAE-VAP-V2.5, Video Analysis for Preservation: the Preservation Audio-Visual File and the Irregularity File of the Audio Analysis in; the Video Irregularity File and the Irregularity
// Images out.
//   - With the Audio-Visual File it detects the irregularities of the film and gives each an identifier that is unique on the tape (Source v).
//   - The Audio Analysis gives it its Irregularity File and, once it has the audio of the film, the Offset (in milliseconds) between the Preservation Audio File and that audio - until it has it the AIM waits: with it the AIM puts its
//     irregularities in the time of the Preservation Audio File (the time reference of every Time Label of the Irregularity File), merges them with those of the audio - the same
//     identifier is the same irregularity, one that the two found is one, Source b - and gives the merged Irregularity File, and the Irregularity Images of all of them: the frame of
//     the film at the irregularity (the time of the film is the time of the recording less the offset), a PNG. None where the film does not cover the irregularity.
//   - It gives them again only if they are not what it gave last, which is what makes the loop with the Audio Analysis come to an end.
// The film is the stand-in of AudioVisualObject: its frames as grayscale. The AIM runs whenever a Message arrives (continuous execution).
public sealed class VapAimProcessor(string instanceId, string avKey, string audioIrregularityKey, string videoIrregularityKey, string imagesKey) : IAimProcessor
{
    public string InstanceId { get; } = instanceId;

    private VideoFrames? _video;
    private List<Irregularity> _film = [];                 // the irregularities of the film, in the time of the film
    private List<Irregularity>? _audio;                    // what the Audio Analysis gave, in the time of the Preservation Audio File
    private int? _offset;                                  // milliseconds
    private string _lastSignature = "";
    private readonly HashSet<string> _imaged = [];

    private static string? Get(Message m, string key) => m.Ports.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    private static Message Nothing(Message m) => new() { MessageId = m.MessageId, MessageType = "IrregularityFile", DataType = "CAE-IRR-V2.5" };

    public Task<Message> ProcessAsync(Message message)
    {
        try
        {
            if (Get(message, avKey) is string av)
            {
                var (_, frames) = AudioVisualObject.Read(av);
                _video = frames ?? throw new FormatException("The Audio-Visual File has no video (no data item \"video-frames\").");
                _film = VideoAnalysis.Detect(_video).Select(d => new Irregularity(Guid.NewGuid().ToString(), "v", d.Start, d.End)).ToList();
                _imaged.Clear(); _lastSignature = "";
            }
            if (Get(message, audioIrregularityKey) is string audioJson)
            {
                var (list, offset) = IrregularityFile.Read(audioJson);
                _audio = list.ToList(); _offset = offset ?? _offset;
            }
            if (_video is null || _audio is null || _offset is null) return Task.FromResult(Nothing(message));      // the film, or the audio's file with the Offset, is still to come

            var shift = _offset!.Value / 1000.0;
            var merged = IrregularityFile.Union(_audio.Concat(_film.Select(f => f with { Start = f.Start + shift, End = f.End + shift })));
            var signature = IrregularityFile.Signature(merged);
            if (signature == _lastSignature) return Task.FromResult(Nothing(message));                  // nothing new: the loop is done
            _lastSignature = signature;

            var images = new List<(string Id, byte[] Png)>();
            foreach (var i in merged)
            {
                var at = (i.Start + i.End) / 2 - shift;
                if (at < 0 || at > _video.Seconds) continue;                                           // the film does not cover it
                images.Add((i.Id, VideoAnalysis.ImageAt(_video, at)));
            }
            var covered = images.Select(i => i.Id).ToHashSet();
            var irr = IrregularityFile.Make(merged, _offset, null, i => covered.Contains(i.Id) ? "urn:mpai:cae:vap:image:" + i.Id : null);
            var ports = new Dictionary<string, string> { [videoIrregularityKey] = irr };
            if (images.Count > 0) ports[imagesKey] = VisualObject.Make("vap-" + Guid.NewGuid().ToString("N")[..12], images);
            return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = "IrregularityFile", DataType = "CAE-IRR-V2.5", Payload = irr, Ports = ports });
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "CAE-VAP-V2.5: " + ex.Message));
        }
    }
}
