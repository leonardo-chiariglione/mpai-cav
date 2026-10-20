using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Mpai.Cae.Usc;

// A grayscale image (8 bit) as a PNG file: what the Irregularity Images are. Written, and read back to its pixels, with nothing but the framework.
public static class Png
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] Table = Enumerable_Crc();

    private static uint[] Enumerable_Crc()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++) { var c = n; for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[n] = c; }
        return t;
    }

    private static uint Crc(byte[] type, byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in type) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var t = Encoding.ASCII.GetBytes(type);
        s.Write(BigEndian(data.Length)); s.Write(t); s.Write(data); s.Write(BigEndian((int)Crc(t, data)));
    }

    private static byte[] BigEndian(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    public static byte[] Encode(byte[] pixels, int width, int height)
    {
        if (pixels.Length != width * height) throw new ArgumentException("The pixels are not width x height.");
        using var m = new MemoryStream();
        m.Write(Signature);
        var header = new byte[13]; BigEndian(width).CopyTo(header, 0); BigEndian(height).CopyTo(header, 4); header[8] = 8; header[9] = 0;      // 8 bit, grayscale
        Chunk(m, "IHDR", header);
        using var raw = new MemoryStream();
        for (var y = 0; y < height; y++) { raw.WriteByte(0); raw.Write(pixels, y * width, width); }                                         // each scanline: filter 0
        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Optimal, true)) z.Write(raw.ToArray());
        Chunk(m, "IDAT", packed.ToArray()); Chunk(m, "IEND", []);
        return m.ToArray();
    }

    // The width, the height and the pixels of a PNG this class wrote (8 bit grayscale).
    public static (int Width, int Height, byte[] Pixels) Decode(byte[] png)
    {
        if (png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(Signature)) throw new FormatException("Not a PNG file.");
        int width = 0, height = 0; var idat = new MemoryStream();
        for (var p = 8; p + 8 <= png.Length;)
        {
            var length = (png[p] << 24) | (png[p + 1] << 16) | (png[p + 2] << 8) | png[p + 3]; var type = Encoding.ASCII.GetString(png, p + 4, 4);
            if (type == "IHDR") { width = (png[p + 8] << 24) | (png[p + 9] << 16) | (png[p + 10] << 8) | png[p + 11]; height = (png[p + 12] << 24) | (png[p + 13] << 16) | (png[p + 14] << 8) | png[p + 15]; if (png[p + 16] != 8 || png[p + 17] != 0) throw new FormatException("Only 8 bit grayscale PNG is read."); }
            else if (type == "IDAT") idat.Write(png, p + 8, length);
            p += 12 + length;
        }
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress); using var raw = new MemoryStream(); z.CopyTo(raw);
        var scanlines = raw.ToArray(); var pixels = new byte[width * height];
        for (var y = 0; y < height; y++) Array.Copy(scanlines, y * (width + 1) + 1, pixels, y * width, width);
        return (width, height, pixels);
    }
}
