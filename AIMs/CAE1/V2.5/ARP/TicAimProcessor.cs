using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Cae.Usc.Arp;

// CAE-TIC-V2.5, Tape Irregularity Classification: the Irregularity File of the Audio Analysis (with the Audio Files of its irregularities) and that of the Video Analysis (with the
// Irregularity Images) in; the Irregularity File of the SELECTED irregularities and their Irregularity Images out - to Tape Audio Restoration and to Packaging.
//
// It classifies each irregularity (its IrregularityType, the codes of the Irregularity File page) and selects the relevant ones: those it can classify. A type an irregularity already has
// is kept (so are its properties: the speed and equalisation standards Tape Audio Restoration needs). The rules, A STAND-IN for a trained classifier, go by what the definitions say
// each type is and what the AIM can see:
//   - seen by the film (Source v or b): sot, Start of tape, if it is within two seconds of the start of the Audio-Visual File (the Offset says where in the Preservation Audio File);
//     eot, End of tape, if it is within two seconds of its end (the length of the film is a parameter: without it there is no eot); otherwise sp, a splice (a change of tape, which is
//     what a change of brightness of the film is).
//   - heard only (Source a), by what its Audio File holds: a dropout of a second or more is pps, play, pause and stop (a silence between recordings); a shorter dropout or a click is da,
//     damaged tape. A step in level cannot say whether it is a variation of the speed or of the equalisation standard (ssv, esv) or a signal backward (sb): it is not classified.
// An irregularity it cannot classify is not relevant, and is not given. It gives the file again only if it is not what it gave last (the loop with the analyses ends).
public sealed class TicAimProcessor(string instanceId, string audioFilesKey, string audioIrregularityKey, string imagesKey, string videoIrregularityKey, string irregularityKey, string outImagesKey, double? filmEndSeconds = null) : IAimProcessor
{
    public string InstanceId { get; } = instanceId;

    private IReadOnlyList<Irregularity> _audio = [], _video = [];
    private int? _offset;                                                           // milliseconds
    private readonly Dictionary<string, Audio> _clips = new();
    private readonly Dictionary<string, byte[]> _images = new();
    private string _lastSignature = "";

    private static string? Get(Message m, string key) => m.Ports.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    public Task<Message> ProcessAsync(Message message)
    {
        try
        {
            if (Get(message, audioFilesKey) is string files) foreach (var item in AudioObject.Read(files)) if (item.Id is not null) _clips[item.Id] = item.Audio;
            if (Get(message, imagesKey) is string images) foreach (var (id, png) in VisualObject.Images(images)) if (id is not null) _images[id] = png;
            if (Get(message, audioIrregularityKey) is string a) { var (list, offset) = IrregularityFile.Read(a); _audio = list; _offset = offset ?? _offset; }
            if (Get(message, videoIrregularityKey) is string v) { var (list, offset) = IrregularityFile.Read(v); _video = list; _offset = offset ?? _offset; }
            if (_audio.Count == 0 && _video.Count == 0) return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = "IrregularityFile", DataType = "CAE-IRR-V2.5" });

            var selected = IrregularityFile.Union(_audio.Concat(_video)).Select(Classify).Where(i => i.Type is not null).ToList();
            var signature = IrregularityFile.Signature(selected) + "#" + string.Join(",", selected.Where(i => _images.ContainsKey(i.Id)).Select(i => i.Id));
            if (selected.Count == 0 || signature == _lastSignature) return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = "IrregularityFile", DataType = "CAE-IRR-V2.5" });
            _lastSignature = signature;

            var irr = IrregularityFile.Make(selected, _offset, null, i => _images.ContainsKey(i.Id) ? "urn:mpai:cae:vap:image:" + i.Id : null);
            var ports = new Dictionary<string, string> { [irregularityKey] = irr };
            var pictured = selected.Where(i => _images.ContainsKey(i.Id)).Select(i => (i.Id, _images[i.Id])).ToList();
            if (pictured.Count > 0) ports[outImagesKey] = VisualObject.Make("tic-" + Guid.NewGuid().ToString("N")[..12], pictured);
            return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = "IrregularityFile", DataType = "CAE-IRR-V2.5", Payload = irr, Ports = ports });
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "CAE-TIC-V2.5: " + ex.Message));
        }
    }

    private Irregularity Classify(Irregularity i)
    {
        if (i.Type is not null) return i;
        if (i.Source is "v" or "b")
        {
            var filmStart = (_offset ?? 0) / 1000.0;
            if (_offset is not null && i.Start <= filmStart + 2.0) return i with { Type = "sot" };
            if (filmEndSeconds is double end && i.End >= end - 2.0) return i with { Type = "eot" };
            return i with { Type = "sp" };
        }
        if (!_clips.TryGetValue(i.Id, out var clip)) return i;
        var kinds = AudioAnalysis.Detect(clip);
        if (kinds.Any(k => k.Kind == "dropout" && k.End - k.Start >= 1.0)) return i with { Type = "pps" };
        if (kinds.Any(k => k.Kind is "dropout" or "click")) return i with { Type = "da" };
        return i;
    }
}
