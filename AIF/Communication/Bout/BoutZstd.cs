using ZstdSharp;
using ZstdSharp.Unsafe;

namespace AIF.Bout;

// THE ZSTANDARD OF A BOUT (IETF RFC 8878). The Dictionary of a Bout is a Zstandard dictionary in the format of RFC 8878, section 5 - magic number,
// Dictionary_ID, entropy tables, content - whose Dictionary_ID is the BoutID and whose content is the canonical form of an instance of the flow. A raw
// content dictionary would have Dictionary_ID 0, so it is never used: every frame names the Bout it was compressed with by its Dictionary_ID.
// Each instance is one frame that states its Frame_Content_Size, so that a receiver refuses an instance larger than MaxInstanceLength before
// decompressing it.
public static class BoutZstd
{
    public const int Level = 19;
    public const int MinBoutId = 32768;                 // RFC 8878 leaves 32768 to 2^31-1 to private use
    public const int MaxBoutId = int.MaxValue;

    // A formatted dictionary with Dictionary_ID boutId, built from the canonical form of one instance.
    public static unsafe byte[] MakeDictionary(byte[] content, int boutId)
    {
        if (boutId < MinBoutId) throw new ArgumentOutOfRangeException(nameof(boutId), "A BoutID is from 32768 to 2147483647.");
        if (content.Length == 0) throw new ArgumentException("A Dictionary has content.", nameof(content));

        // The entropy tables are taken from the content repeated: the instances of a flow are of that kind.
        const int copies = 8;
        var samples = new byte[content.Length * copies];
        var sizes = new nuint[copies];
        for (var i = 0; i < copies; i++) { Buffer.BlockCopy(content, 0, samples, i * content.Length, content.Length); sizes[i] = (nuint)content.Length; }

        var max = content.Length + 2048;
        var dictionary = new byte[max];
        var parameters = new ZDICT_params_t { compressionLevel = Level, notificationLevel = 0, dictID = (uint)boutId };
        nuint written;
        fixed (byte* dst = dictionary)
        fixed (byte* src = content)
        fixed (byte* smp = samples)
        fixed (nuint* sz = sizes)
            written = Methods.ZDICT_finalizeDictionary(dst, (nuint)max, src, (nuint)content.Length, smp, sz, copies, parameters);
        if (Methods.ZDICT_isError(written)) throw new InvalidOperationException("Zstandard: " + Methods.ZDICT_getErrorName(written));
        var result = dictionary.AsSpan(0, (int)written).ToArray();
        if (DictionaryId(result) != (uint)boutId) throw new InvalidOperationException("The Dictionary_ID is not the BoutID.");
        return result;
    }

    // The Dictionary_ID of a dictionary in the format of RFC 8878, section 5; 0 for raw content.
    public static unsafe uint DictionaryId(byte[] dictionary)
    {
        fixed (byte* d = dictionary) return Methods.ZSTD_getDictID_fromDict(d, (nuint)dictionary.Length);
    }

    // One frame, with the Dictionary_ID and the Frame_Content_Size in its header.
    // level: how hard the giver compresses (1 to 22); it changes the size of the frame and the time to make it, never what the receiver decompresses.
    public static byte[] Compress(byte[] dictionary, byte[] content, int level = Level)
    {
        using var compressor = new Compressor(level);
        compressor.LoadDictionary(dictionary);
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_contentSizeFlag, 1);
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_dictIDFlag, 1);
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 0);
        return compressor.Wrap(content).ToArray();
    }

    // The Dictionary_ID a frame names; 0 where it names none.
    public static unsafe uint FrameDictionaryId(byte[] frame)
    {
        fixed (byte* f = frame) return Methods.ZSTD_getDictID_fromFrame(f, (nuint)frame.Length);
    }

    // The Frame_Content_Size a frame states, or null where it states none or is not a frame.
    public static unsafe long? FrameContentSize(byte[] frame)
    {
        ulong size;
        fixed (byte* f = frame) size = Methods.ZSTD_getFrameContentSize(f, (nuint)frame.Length);
        return size >= ulong.MaxValue - 1 || size > long.MaxValue ? null : (long)size;
    }

    // The content of a frame, never more than maxLength bytes.
    public static byte[] Decompress(byte[] dictionary, byte[] frame, int maxLength)
    {
        using var decompressor = new Decompressor();
        decompressor.LoadDictionary(dictionary);
        return decompressor.Unwrap(frame, maxLength).ToArray();
    }
}
