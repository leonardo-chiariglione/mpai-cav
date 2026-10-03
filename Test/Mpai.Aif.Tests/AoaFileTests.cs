using Mpai.Aims.Audio;
using Mpai.Core;

namespace Mpai.Aif.Tests;

// AUDIO OBJECT ACQUISITION FROM A FILE STATES THE FILE'S OWN FORMAT (2026/10/03). A WAV
// says its sampling rate, bit depth and channels, and the Qualifier states those - a
// 22.05 kHz stereo file is not labelled with a default. Data with no WAV header gets
// the defaults, which are 48 kHz since AOA captures audio at 48 kHz (speech, at 16 kHz,
// is Speech Object Acquisition's: the GA's remark, 2026/10/02).
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class AoaFileTests
{
    private static byte[] Wav(int rate, short bits, short channels, int samples)
    {
        var data = samples * channels * bits / 8;
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8.ToArray()); w.Write(36 + data); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write(channels);
        w.Write(rate); w.Write(rate * channels * bits / 8); w.Write((short)(channels * bits / 8)); w.Write(bits);
        w.Write("data"u8.ToArray()); w.Write(data); w.Write(new byte[data]);
        return stream.ToArray();
    }

    private static string Format(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aoa-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, content);
        try
        {
            var q = new FileAudioAcquisition(path).AcquireAsync(new AcquisitionRequest()).GetAwaiter().GetResult().AudioQualifier;
            var pcm = q?.Formats?.ContentFormat?.RawData?.SampleSpace;
            var channels = q?.Attributes?.Device?.CaptureConfiguration?.ChannelCount;
            return $"{pcm?.SamplingFrequency} Hz, {pcm?.Precision} bit, {channels} channel(s)";
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TheFileSaysWhatItIs() =>
        Expected.Match("aoa-file.json", new Dictionary<string, string>
        {
            ["a 22.05 kHz 16-bit stereo WAV"] = Format(Wav(22050, 16, 2, 2205)),
            ["a 48 kHz 16-bit mono WAV"] = Format(Wav(48000, 16, 1, 4800)),
            ["data with no WAV header: the defaults"] = Format(new byte[1000])
        });
}
