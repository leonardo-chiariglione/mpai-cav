using System;
using System.Text;

namespace Mpai.Aih.Chain;

// KECCAK-256, the hash of Ethereum (the original Keccak padding, not the SHA3-256 of NIST, which .NET has). It gives the function selectors and the event topic
// the adapter to an Ethereum node needs; written here so that the adapter needs no library.
public static class Keccak256
{
    private static readonly ulong[] RoundConstants =
    [
        0x0000000000000001, 0x0000000000008082, 0x800000000000808A, 0x8000000080008000, 0x000000000000808B, 0x0000000080000001, 0x8000000080008081, 0x8000000000008009,
        0x000000000000008A, 0x0000000000000088, 0x0000000080008009, 0x000000008000000A, 0x000000008000808B, 0x800000000000008B, 0x8000000000008089, 0x8000000000008003,
        0x8000000000008002, 0x8000000000000080, 0x000000000000800A, 0x800000008000000A, 0x8000000080008081, 0x8000000000008080, 0x0000000080000001, 0x8000000080008008
    ];
    private static readonly int[] Rotations = [1, 3, 6, 10, 15, 21, 28, 36, 45, 55, 2, 14, 27, 41, 56, 8, 25, 43, 62, 18, 39, 61, 20, 44];
    private static readonly int[] Lanes = [10, 7, 11, 17, 18, 3, 5, 16, 8, 21, 24, 4, 15, 23, 19, 13, 12, 2, 20, 14, 22, 9, 6, 1];

    public static byte[] Hash(byte[] data)
    {
        const int rate = 136;
        var state = new ulong[25];
        var padded = new byte[(data.Length / rate + 1) * rate];
        Array.Copy(data, padded, data.Length);
        padded[data.Length] ^= 0x01;
        padded[^1] ^= 0x80;
        for (var block = 0; block < padded.Length; block += rate)
        {
            for (var i = 0; i < rate / 8; i++) state[i] ^= BitConverter.ToUInt64(padded, block + i * 8);
            Permute(state);
        }
        var result = new byte[32];
        for (var i = 0; i < 4; i++) BitConverter.GetBytes(state[i]).CopyTo(result, i * 8);
        return result;
    }

    public static byte[] Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));

    public static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static void Permute(ulong[] a)
    {
        var c = new ulong[5];
        for (var round = 0; round < 24; round++)
        {
            for (var x = 0; x < 5; x++) c[x] = a[x] ^ a[x + 5] ^ a[x + 10] ^ a[x + 15] ^ a[x + 20];
            for (var x = 0; x < 5; x++)
            {
                var d = c[(x + 4) % 5] ^ Rotl(c[(x + 1) % 5], 1);
                for (var y = 0; y < 25; y += 5) a[y + x] ^= d;
            }
            var t = a[1];
            for (var i = 0; i < 24; i++)
            {
                var j = Lanes[i];
                var next = a[j];
                a[j] = Rotl(t, Rotations[i]);
                t = next;
            }
            for (var y = 0; y < 25; y += 5)
            {
                for (var x = 0; x < 5; x++) c[x] = a[y + x];
                for (var x = 0; x < 5; x++) a[y + x] = c[x] ^ (~c[(x + 1) % 5] & c[(x + 2) % 5]);
            }
            a[0] ^= RoundConstants[round];
        }
    }

    private static ulong Rotl(ulong v, int n) => (v << n) | (v >> (64 - n));
}
