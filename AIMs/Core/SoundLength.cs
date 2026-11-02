using System;
using System.Collections.Generic;
using System.Linq;

namespace Mpai.Core;

// HOW LONG A SOUND LASTS, for the List (the author, 2026/10/26): the Duration an Object states is read from its sound, a WAV (PCM) stream, as its header gives the sampling
// frequency, the channels, the bits of a sample and the length of the data. A sound in another form has no Duration here, and none is invented.
public static class SoundLength
{
    // Seconds; null when the bytes are not a WAV stream whose length can be read.
    public static double? Seconds(byte[]? wav)
    {
        if (wav is null || wav.Length < 44) return null;
        if (!(wav[0] == 'R' && wav[1] == 'I' && wav[2] == 'F' && wav[3] == 'F' && wav[8] == 'W' && wav[9] == 'A' && wav[10] == 'V' && wav[11] == 'E')) return null;
        int channels = 0, rate = 0, bits = 0; long? data = null;
        var at = 12;
        while (at + 8 <= wav.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(wav, at, 4);
            var size = BitConverter.ToUInt32(wav, at + 4);
            if (id == "fmt " && at + 8 + 16 <= wav.Length)
            {
                channels = BitConverter.ToUInt16(wav, at + 10);
                rate = (int)BitConverter.ToUInt32(wav, at + 12);
                bits = BitConverter.ToUInt16(wav, at + 22);
            }
            else if (id == "data") { data = Math.Min(size, (uint)(wav.Length - at - 8)); break; }
            at += 8 + (int)size + (int)(size & 1);
        }
        if (channels <= 0 || rate <= 0 || bits <= 0 || data is null) return null;
        return Math.Round((double)data.Value / (channels * (bits / 8.0) * rate), 6);
    }

    // The end, in seconds, that a Space/Time states: the end of its first relative time segment, when that is later than its start (an instant states no end); null when it states none.
    public static double? EndOf(SpaceTime? placement)
    {
        var segment = placement?.Time?.SimpleTimeData?.FirstOrDefault(s => s.TimeType == false);
        if (segment is null || segment.EndTime <= segment.StartTime) return null;
        return segment.TimeUnit switch { "01" => segment.EndTime / 1e3, "10" => segment.EndTime / 1e6, "11" => segment.EndTime / 1e9, _ => segment.EndTime };
    }

    // The start, in seconds, of a Space/Time's first Time segment: where in its parent (or in its Scene) a member begins (a relative time); 0 when it states none.
    public static double StartOf(SpaceTime? placement)
    {
        var segment = placement?.Time?.SimpleTimeData?.FirstOrDefault(s => s.TimeType == false);      // an absolute time is no offset
        if (segment is null) return 0;
        return segment.TimeUnit switch { "01" => segment.StartTime / 1e3, "10" => segment.StartTime / 1e6, "11" => segment.StartTime / 1e9, _ => segment.StartTime };
    }
}
