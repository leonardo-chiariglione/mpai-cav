using System;
using System.IO;
using System.Text;

namespace Mpai.Cae.Usc;

// Audio as the AIMs of CAE-USC V2.5 handle it: a mono stream of 16-bit samples at a rate. Read from, and written to, the bytes of a 16-bit PCM WAV file (of several
// channels, the first is taken). Anything else is not audio these AIMs can use.
public sealed record Audio(short[] Samples, int Rate)
{
    public double Seconds => Rate > 0 ? Samples.Length / (double)Rate : 0;

    public static Audio FromWav(byte[] b)
    {
        if (b.Length < 44 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' || b[8] != 'W' || b[9] != 'A' || b[10] != 'V' || b[11] != 'E')
            throw new FormatException("The audio is not a WAV file.");
        int channels = 1, rate = 0, bits = 0, dataAt = -1, dataLength = 0;
        for (var p = 12; p + 8 <= b.Length;)
        {
            var id = Encoding.ASCII.GetString(b, p, 4); var size = BitConverter.ToInt32(b, p + 4);
            if (id == "fmt ") { channels = BitConverter.ToInt16(b, p + 10); rate = BitConverter.ToInt32(b, p + 12); bits = BitConverter.ToInt16(b, p + 22); }
            else if (id == "data") { dataAt = p + 8; dataLength = Math.Min(size, b.Length - dataAt); break; }
            p += 8 + size + (size & 1);
        }
        if (dataAt < 0 || bits != 16 || rate <= 0 || channels < 1) throw new FormatException("The audio is not 16-bit PCM.");
        var frames = dataLength / (2 * channels);
        var samples = new short[frames];
        for (var i = 0; i < frames; i++) samples[i] = BitConverter.ToInt16(b, dataAt + i * 2 * channels);
        return new Audio(samples, rate);
    }

    public byte[] ToWav()
    {
        using var m = new MemoryStream();
        using var w = new BinaryWriter(m);
        var data = Samples.Length * 2;
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + data); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(data);
        foreach (var s in Samples) w.Write(s);
        w.Flush();
        return m.ToArray();
    }

    // The same audio at another rate (linear interpolation).
    public Audio At(int rate)
    {
        if (rate == Rate || Samples.Length == 0) return this;
        var length = (int)Math.Round(Samples.Length * (double)rate / Rate);
        var result = new short[length];
        for (var i = 0; i < length; i++)
        {
            var position = i * (double)Rate / rate; var at = (int)position; var fraction = position - at;
            var a = Samples[Math.Min(at, Samples.Length - 1)]; var c = Samples[Math.Min(at + 1, Samples.Length - 1)];
            result[i] = (short)Math.Round(a + (c - a) * fraction);
        }
        return new Audio(result, rate);
    }
}
