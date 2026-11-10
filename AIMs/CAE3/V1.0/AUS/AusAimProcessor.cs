// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;
using Mpai.Aif.PortData;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Mmc.Sir;

namespace Mpai.Cae.Aus;

// CAE-AUS-V1.0 - Audio Segmentation. Self-contained IAimProcessor; its Ports are read by
// Data Type from its metadata (1CAE-AUS-V1.0-I01.json).
//
// Takes the speech of one or more speakers - a Speech Object (OSD-SPO-V1.5), or a Basic
// Speech Object, which is a Speech Object of one - and gives
//   OSD-SPO-V1.5  a Speech Object whose Basic Speech Objects are the speakers' TURNS, in
//                 order, each stamped with the time it begins;
//   OSD-STM-V1.5  the times at which a speaker begins to speak;
//   MMC-SOL-V2.5  for each turn, how likely it is that others speak at the same time
//                 (an estimate: see AudioSegmenter).
// Whose turn each is, is not said: the speakers are not identified (that is Speaker
// Identity Recognition's); a turn's Basic Speech Object says "Speaker n" in its
// DescrMetadata, n counted from the first speaker.
public sealed class AusAimProcessor : IAimProcessor
{
    private readonly string _inputPort, _speechPort, _timePort, _overlapPort;
    private readonly AudioSegmenter _segmenter;

    public string InstanceId { get; }

    public AusAimProcessor(string instanceId, SpeakerEmbedder embedder, AimPortReader ports)
    {
        InstanceId   = instanceId;
        _segmenter   = new AudioSegmenter(embedder.Embed);
        _inputPort   = ports.Input("OSD-SPO-V1.5");
        _speechPort  = ports.Output("OSD-SPO-V1.5");
        _timePort    = ports.Output("OSD-STM-V1.5");
        _overlapPort = ports.Output("MMC-SOL-V2.5");
    }

    public Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_inputPort, out var json) || string.IsNullOrWhiteSpace(json))
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, $"CAE-AUS-V1.0: no speech on input port '{_inputPort}'."));

        BasicSpeechObject? input;
        try { input = Basic(json); }
        catch (Exception ex) { return Task.FromResult(Message.Error(message.MessageId, InstanceId, "CAE-AUS-V1.0: " + ex.Message)); }
        if (input is null || input.Data.Length == 0)
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "CAE-AUS-V1.0: the speech carried no audio data."));

        float[] samples = WavReader.ReadMono16k(input);
        var turns = _segmenter.Segment(samples);

        // Times are absolute when the input says when it began, as the time it was acquired.
        double begin = input.BasicSpeechObjectTime?.SimpleTimeData.FirstOrDefault()?.StartTime
                       ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        var entries = new List<BasicSpeechObjectEntry>();
        var overlaps = new JsonArray();
        var times = new List<TimeSegment>();
        foreach (var turn in turns)
        {
            var pcm = Pcm16(samples, turn.Start, turn.End);
            var bso = new BasicSpeechObject
            {
                BasicSpeechObjectID = Guid.NewGuid().ToString(),
                BasicSpeechObjectTime = Instant(begin + turn.Start, begin + turn.End),
                Data = pcm,
                SpeechQualifier = Qualifier(),
                DescrMetadata = $"Speaker {turn.Speaker}"
            };
            entries.Add(new BasicSpeechObjectEntry { BSObjectIDOrBSObject = bso });
            overlaps.Add(new JsonObject { ["Confidence"] = turn.Overlap, ["SpeechObjectID"] = bso.BasicSpeechObjectID });
            times.Add(new TimeSegment
            {
                FlagsByte = 1, StartTime = begin + turn.Start, EndTime = begin + turn.Start,
                AccuracyMode = "single", AccuracyPlusMinus = 0.1, TimeType = true, TimeUnit = "00"
            });
        }

        var speech = new SpeechObject
        {
            SpeechObjectID = Guid.NewGuid().ToString(),
            BasicSpeechObjectCount = entries.Count,
            BasicSpeechObjects = entries,
            DescrMetadata = $"{turns.Select(t => t.Speaker).Distinct().Count()} speaker(s), {turns.Count} turn(s)"
        };
        var time = new SimpleTime { SimpleTimeID = Guid.NewGuid().ToString(), SimpleTimeData = times };
        var overlap = new JsonObject
        {
            ["Header"] = "MMC-SOL-V2.5", ["SpeechOverlapID"] = Guid.NewGuid().ToString(),
            ["SpeechQualifier"] = JsonNode.Parse(MpaiJson.ToJson(Qualifier())),
            ["SpeechOverlapData"] = overlaps
        };

        var speechJson = PortObjects.WriteSpeechObject(speech);
        var timeJson = MpaiJson.ToJson(time);
        return Task.FromResult(new Message
        {
            MessageId   = message.MessageId,
            MessageType = "SpeechObject",
            DataType    = "OSD-SPO-V1.5",
            Payload     = speechJson,
            Ports       = new Dictionary<string, string>
            {
                [_speechPort]  = speechJson,
                [_timePort]    = timeJson,
                [_overlapPort] = overlap.ToJsonString()
            }
        });
    }

    // A Speech Object is read as its first Basic Speech Object; a Basic Speech Object, as itself.
    private static BasicSpeechObject? Basic(string json)
    {
        var header = (string?)JsonNode.Parse(json)?["Header"];
        if (header is not null && header.StartsWith("OSD-BSO", StringComparison.Ordinal)) return PortObjects.ReadSpeech(json);
        var speech = PortObjects.ReadSpeechObject(json);
        return speech?.BasicSpeechObjects?.FirstOrDefault()?.BSObjectIDOrBSObject
               ?? throw new InvalidOperationException("the Speech Object holds no Basic Speech Object.");
    }

    private static SimpleTime Instant(double start, double end) => new()
    {
        SimpleTimeID = Guid.NewGuid().ToString(),
        SimpleTimeData =
        {
            new TimeSegment { FlagsByte = 1, StartTime = start, EndTime = end, AccuracyMode = "single", AccuracyPlusMinus = 0.1, TimeType = true, TimeUnit = "00" }
        }
    };

    // 16-bit little-endian PCM of the samples between two times.
    private static byte[] Pcm16(float[] samples, double start, double end)
    {
        int a = Math.Clamp((int)(start * AudioSegmenter.Rate), 0, samples.Length);
        int b = Math.Clamp((int)(end * AudioSegmenter.Rate), a, samples.Length);
        var bytes = new byte[(b - a) * 2];
        for (int i = a; i < b; i++)
        {
            short v = (short)Math.Clamp((int)Math.Round(samples[i] * 32767f), short.MinValue, short.MaxValue);
            bytes[(i - a) * 2] = (byte)(v & 0xFF); bytes[(i - a) * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return bytes;
    }

    // What the turns' bytes are: raw 16 kHz, 16-bit mono PCM - stated, because the bytes carry no header.
    private static SpeechQualifier Qualifier() => new()
    {
        SpeechQualifierID = Guid.NewGuid().ToString(),
        Format = new SpeechFormat { ContentFormats = new SpeechContentFormats { RawData = new Pcm { SamplingFrequency = AudioSegmenter.Rate, Precision = 16 } } }
    };
}
