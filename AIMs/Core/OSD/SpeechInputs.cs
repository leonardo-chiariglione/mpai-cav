using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Mpai.Core.OSD;

// WHAT AN AIM THAT TAKES "A BASIC SPEECH OBJECT OR A SPEECH OBJECT" DOES WITH EITHER. Such a
// Port (Speaker Identity Recognition, Speech Personal Status Extraction, ...) declares both
// Data Types, and a Speech Object - what Audio Segmentation gives - holds a Basic Speech
// Object for each TURN of a speaker, each saying whose it is ("Speaker n" in its
// DescrMetadata). The AIM works on one speech:
//  - a Basic Speech Object is that speech;
//  - of a Speech Object, the speech of the speaker who speaks longest - in a cabin, the
//    person the others are talking to or about - all that speaker's turns, in order;
//  - turns that say whose they are not are taken as one speaker's.
public static class SpeechInputs
{
    public static BasicSpeechObject? Basic(string json)
    {
        var header = (string?)JsonNode.Parse(json)?["Header"];
        if (header is null || header.StartsWith("OSD-BSO", StringComparison.Ordinal))
            return MpaiJson.FromJson<BasicSpeechObject>(json);

        var turns = MpaiJson.FromJson<SpeechObject>(json)?.BasicSpeechObjects?
            .Select(e => e.BSObjectIDOrBSObject).Where(b => b is not null).Select(b => b!).ToList();
        if (turns is null || turns.Count == 0) return null;
        if (turns.Count == 1) return turns[0];

        // Whose turns are the longest in all: by the speaker each turn names.
        var dominant = turns.GroupBy(t => t.DescrMetadata ?? "")
                            .OrderByDescending(g => g.Sum(t => (long)t.Data.Length)).First().ToList();
        if (dominant.Count == 1) return dominant[0];

        // The turns of one speaker, joined: Audio Segmentation states them alike (raw PCM, one format).
        var data = new List<byte>();
        foreach (var t in dominant) data.AddRange(t.Data);
        return new BasicSpeechObject
        {
            BasicSpeechObjectID = Guid.NewGuid().ToString(),
            BasicSpeechObjectTime = dominant[0].BasicSpeechObjectTime,
            Data = data.ToArray(),
            SpeechQualifier = dominant[0].SpeechQualifier,
            DescrMetadata = dominant[0].DescrMetadata
        };
    }
}
