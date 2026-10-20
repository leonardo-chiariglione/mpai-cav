using System.IO.Compression;

namespace Mpai.Cav.Recordings;

// A PNG OF RGB PIXELS, and nothing more: what a synthetic camera needs, with no
// library to depend on. Eight bits a channel, no filter, zlib compression.
public static class Png
{
    public static byte[] Encode(int width, int height, byte[] rgb)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        WriteBig(header, 0, (uint)width);
        WriteBig(header, 4, (uint)height);
        header[8] = 8;                                    // bits per channel
        header[9] = 2;                                    // colour type: RGB
        Chunk(output, "IHDR", header);

        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.SmallestSize, leaveOpen: true))
            for (var y = 0; y < height; y++)
            {
                z.WriteByte(0);                           // filter: none
                z.Write(rgb, y * width * 3, width * 3);
            }
        Chunk(output, "IDAT", raw.ToArray());
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBig(length, 0, (uint)data.Length);
        output.Write(length);
        var typed = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(typed, 0);
        data.CopyTo(typed, 4);
        output.Write(typed);
        var crc = new byte[4];
        WriteBig(crc, 0, Crc(typed));
        output.Write(crc);
    }

    private static void WriteBig(byte[] into, int at, uint value)
    {
        into[at] = (byte)(value >> 24); into[at + 1] = (byte)(value >> 16);
        into[at + 2] = (byte)(value >> 8); into[at + 3] = (byte)value;
    }

    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc(byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
