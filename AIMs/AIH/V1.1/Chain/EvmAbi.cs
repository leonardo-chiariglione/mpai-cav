using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace Mpai.Aih.Chain;

// THE PART OF THE ETHEREUM CONTRACT ABI that AihLicence needs: arguments that are strings (and the answer, a bool or a bytes32), and the data of the Entry event.
public static class EvmAbi
{
    // The first four bytes of the Keccak-256 of the signature: "make(string,string,string,string)".
    public static string Selector(string signature) => Keccak256.Hex(Keccak256.Hash(signature))[..8];

    public static string Topic(string signature) => "0x" + Keccak256.Hex(Keccak256.Hash(signature));

    // Call data, as 0x-hexadecimal: the selector, then each string as an offset in the head and its length and bytes in the tail.
    public static string Encode(string signature, params string[] strings)
    {
        var head = new StringBuilder();
        var tail = new StringBuilder();
        var tailStart = strings.Length * 32;
        foreach (var s in strings)
        {
            head.Append(Word(tailStart + tail.Length / 2));
            var bytes = Encoding.UTF8.GetBytes(s);
            tail.Append(Word(bytes.Length));
            tail.Append(Convert.ToHexString(bytes).ToLowerInvariant());
            tail.Append(new string('0', (32 - bytes.Length % 32) % 32 * 2));
        }
        return "0x" + Selector(signature) + head + tail;
    }

    public static string Word(BigInteger value) => value.ToString("x").TrimStart('0').PadLeft(64, '0');

    // The data of an event, or the answer of a call, as the words of the head.
    public static byte[] Bytes(string hex) => Convert.FromHexString(hex.StartsWith("0x") ? hex[2..] : hex);

    public static BigInteger Number(byte[] data, int wordIndex) => new BigInteger(data.AsSpan(wordIndex * 32, 32), isUnsigned: true, isBigEndian: true);

    public static bool Bool(byte[] data, int wordIndex) => Number(data, wordIndex) != 0;

    public static string String(byte[] data, int wordIndex)
    {
        var offset = (int)Number(data, wordIndex);
        var length = (int)Number(data, offset / 32);
        return Encoding.UTF8.GetString(data, offset + 32, length);
    }

    // The Entry event: (uint256 index, string kind, string aihDataId, string requester, string detail, bool allowed, uint256 time).
    public const string EntrySignature = "Entry(uint256,string,string,string,string,bool,uint256)";

    public static (int Index, string Kind, string AihDataId, string Requester, string Detail, bool Allowed, long Time) DecodeEntry(string hex)
    {
        var d = Bytes(hex);
        return ((int)Number(d, 0), String(d, 1), String(d, 2), String(d, 3), String(d, 4), Bool(d, 5), (long)Number(d, 6));
    }
}
