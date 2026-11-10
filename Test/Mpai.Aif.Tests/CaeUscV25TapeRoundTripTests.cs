using System.Text.Json.Nodes;
using AIF.Controller;
using Mpai.Cae.Usc;
using Mpai.Cae.Usc.Arp;
using Mpai.Osd.Speech;

namespace Mpai.Aif.Tests;

// CAE-USC V2.5, TAPE AUDIO RESTORATION ON MUSIC, ROUND TRIP. Real music (Test\Data\Music, public domain or CC0) is given the impairments a wrong reading of a tape makes - an
// equalisation of another standard, a speed of another value - the Irregularity File says the standards the tape was written with and read with, and Tape Audio Restoration gives
// it back. Judged against the music the tape was made from. HONEST LIMIT: the impairments are made with the same model of the tape standards that restores them (the inverse of the
// correction, the sampling of the speed), so these tests prove that restoration undoes what it models, on music rather than on a tone; they do not prove the model is the tape's. Real
// tapes would.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class CaeUscV25TapeRoundTripTests
{
    private const string Irr = "CAE-IRR-V2.5#1", Bao = "OSD-BAO-V1.5#1", Els = "CAE-ELS-V2.5#1", Out = "OSD-BAO-V1.5#2";

    public static IEnumerable<object[]> Clips() =>
        new[] { "piano-beethoven-op101", "piano-orchestra-mozart-k595", "guitar-el-noi-de-la-mare", "trnecek-op77", "voice-zappatore" }.Select(n => new object[] { n });

    private static Audio Clip(string name, double seconds = 6.0)
    {
        var all = Audio.FromWav(File.ReadAllBytes(Path.Combine(Repository.Root, "Test", "Data", "Music", name + ".wav")));
        var six = all.Samples[..(int)(seconds * all.Rate)];
        // A tape is recorded with headroom, and the correction of the equalisation can raise a level (the low-frequency boost of IEC2): the music is set to a peak of 0.4 of full scale so
        // that the 16-bit file of the wrong reading does not saturate (a saturated file cannot be given back, whatever the restoration).
        var peak = six.Max(v => Math.Abs((int)v)); var gain = 0.4 * 32767 / peak;
        return new Audio(six.Select(v => (short)Math.Round(v * gain)).ToArray(), all.Rate);       // six seconds: enough music, quick
    }

    // The distance of a signal from the music, relative to the energy of the music.
    private static double RelativeError(short[] a, short[] reference)
    {
        var n = Math.Min(a.Length, reference.Length); double d = 0, e = 0;
        for (var i = 0; i < n; i++) { var x = a[i] - (double)reference[i]; d += x * x; e += (double)reference[i] * reference[i]; }
        return Math.Sqrt(d / e);
    }

    private static string IrregularityFile(double start, double end, double readSpeed, double writeSpeed, string readEq, string writeEq) => new JsonObject
    {
        ["Header"] = "CAE-IRR-V2.5", ["IrregularityFileID"] = "irr-rt",
        ["Irregularities"] = new JsonArray(new JsonObject
        {
            ["IrregularityID"] = Guid.NewGuid().ToString(), ["Source"] = "a", ["IrregularityType"] = "s",
            ["TimeLabel"] = new JsonObject
            {
                ["Header"] = "OSD-TIM-V1.5", ["TimeID"] = "t", ["Data"] = $"{start} {end}",
                ["Qualifier"] = new JsonObject
                {
                    ["Header"] = "TFA-TIQ-V1.5", ["TimeQualifierID"] = "tq", ["SubTypes"] = new JsonObject(),
                    ["Formats"] = new JsonObject { ["MPAIFormat"] = new JsonObject
                    {
                        ["Header"] = "OSD-STM-V1.5", ["SimpleTimeID"] = "st",
                        ["SimpleTimeData"] = new JsonArray(new JsonObject { ["FlagsByte"] = 0, ["StartTime"] = start, ["EndTime"] = end, ["AccuracyMode"] = "single", ["AccuracyPlusMinus"] = 0 })
                    } }
                }
            },
            ["IrregularityProperties"] = new JsonObject
            {
                ["ReadingSpeedStandard"] = readSpeed, ["WritingSpeedStandard"] = writeSpeed, ["ReadingEqualisationStandard"] = readEq, ["WritingEqualisationStandard"] = writeEq
            }
        })
    }.ToJsonString();

    private static async Task<(AudioObject.Item Restored, JsonNode List)> Restore(Audio tape, string irregularities, double? margin = null)
    {
        var tar = margin is null ? new TarAimProcessor("tar", Irr, Bao, Els, Out) : new TarAimProcessor("tar", Irr, Bao, Els, Out, margin.Value);
        var reply = await tar.ProcessAsync(new Message { MessageId = "m", Ports = new Dictionary<string, string> { [Irr] = irregularities, [Bao] = AudioObject.Make("pres", tape) } });
        Assert.False(reply.IsError, reply.Payload);
        return (AudioObject.Read(reply.Ports[Out]).Single(), JsonNode.Parse(reply.Ports[Els])!);
    }

    [Theory]
    [MemberData(nameof(Clips))]
    public async Task MusicWrittenWithOneEqualisationAndReadWithAnotherIsGivenBackItsSound(string name)
    {
        var music = Clip(name); var seconds = music.Samples.Length / (double)music.Rate;
        // The tape was written with IEC2 and read with IEC1, both at 7.5 ips: what the digitised file holds is the music with the response it should have had taken away and the one it had put in.
        var heard = Tape.CorrectEqualisation(music, "IEC2", 7.5, "IEC1", 7.5);
        Assert.DoesNotContain(heard.Samples, v => v is short.MaxValue or short.MinValue);            // the wrong reading did not saturate the file
        var impaired = RelativeError(heard.Samples, music.Samples);
        Assert.True(impaired > 0.05, $"the wrong reading changed the sound by {impaired:P1} only: the test would prove nothing");

        var (restored, list) = await Restore(heard, IrregularityFile(0, seconds, 7.5, 7.5, "IEC1", "IEC2"));
        Assert.Equal(music.Rate, restored.Audio.Rate);                                               // the speed was right: the rate is not changed
        Assert.Equal("IEC2", list["Restorations"]![0]!["AppliedEqualisationStandard"]!.GetValue<string>());
        var remaining = RelativeError(restored.Audio.Samples, music.Samples);
        var n = music.Samples.Length; var edge = music.Rate;                                           // the first and the last second: where a section cut from a longer tape has its ends
        var middle = RelativeError(restored.Audio.Samples[edge..(n - edge)], music.Samples[edge..(n - edge)]);
        var ends = RelativeError([.. restored.Audio.Samples[..edge], .. restored.Audio.Samples[(n - edge)..]], [.. music.Samples[..edge], .. music.Samples[(n - edge)..]]);
        // Away from the ends of the section the music is given back to within 2%; the ends of a section cut from the tape are where the correction (one block, zero-padded) leaves most
        // (8% of the first and last second of one clip): a finding, reported, not hidden - the correction could take a margin of the tape around the section.
        Assert.True(middle < 0.02, $"{name}: the middle of the music is left {middle:P2} wrong after restoration (it was {impaired:P1} before; the first and last second {ends:P2})");
        Assert.True(remaining < impaired / 3, $"{name}: restoration took away only {1 - remaining / impaired:P0} of the error ({remaining:P2} left of {impaired:P1}; the ends {ends:P2})");
    }

    [Theory]
    [MemberData(nameof(Clips))]
    public async Task MusicWrittenAtOneSpeedAndReadAtTwiceThatSpeedPlaysAtTheRightSpeedAgain(string name)
    {
        var music = Clip(name); var seconds = music.Samples.Length / (double)music.Rate;
        // Read at 15 ips what was written at 7.5 ips: the file holds the music twice as fast, one sample for two of the music (the head averages them: no frequency above half the rate is lost
        // that the file could hold).
        var fast = new short[music.Samples.Length / 2];
        for (var i = 0; i < fast.Length; i++) fast[i] = (short)((music.Samples[2 * i] + music.Samples[2 * i + 1]) / 2);
        var tape = new Audio(fast, music.Rate);

        var (restored, list) = await Restore(tape, IrregularityFile(0, seconds / 2, 15, 7.5, "IEC1", "IEC1"));
        Assert.Equal(music.Rate / 2, restored.Audio.Rate);                                           // it is to be played at half the rate
        Assert.Equal(music.Rate / 2, list["Restorations"]![0]!["AppliedSamplingFrequency"]!.GetValue<int>());
        Assert.Equal(seconds, restored.Audio.Samples.Length / (double)restored.Audio.Rate, 2);        // and lasts as long as the music did
        // the music at half the rate, as it is: the same samples the head gave, so what the listener hears is the music again, lower by an octave than the file as digitised
        var reference = new short[fast.Length];
        for (var i = 0; i < fast.Length; i++) reference[i] = (short)((music.Samples[2 * i] + music.Samples[2 * i + 1]) / 2);
        Assert.True(RelativeError(restored.Audio.Samples, reference) < 0.001, $"{name}: the restored samples are not the tape's");
    }

    [Theory]
    [InlineData("trnecek-op77")]
    [InlineData("piano-beethoven-op101")]
    [InlineData("voice-zappatore")]
    public async Task ASectionCutFromTheMiddleOfTheTapeIsGivenBackAtItsEndsWithAMarginOfTape(string name)
    {
        // The tape is ten seconds; the section of the irregularity is seconds 3 to 7. The section is corrected with a margin of tape around it (one second by default) and cut back: its first and
        // last second are as good as its middle. With no margin (as the AIM did before) they are several times worse.
        var music = Clip(name, 10.0); var r = music.Rate;
        var heard = Tape.CorrectEqualisation(music, "IEC2", 7.5, "IEC1", 7.5);
        var file = IrregularityFile(3, 7, 7.5, 7.5, "IEC1", "IEC2");
        var want = music.Samples[(3 * r)..(7 * r)];
        double Ends(short[] got) => RelativeError([.. got[..r], .. got[(3 * r)..]], [.. want[..r], .. want[(3 * r)..]]);

        var withMargin = (await Restore(heard, file)).Restored.Audio.Samples;
        Assert.Equal(4 * r, withMargin.Length);
        Assert.True(Ends(withMargin) < 0.015, $"{name}: the first and last second of the section are {Ends(withMargin):P2} wrong with a margin of tape");
        Assert.True(RelativeError(withMargin[r..(3 * r)], want[r..(3 * r)]) < 0.015, $"{name}: the middle is wrong");
        var without = (await Restore(heard, file, 0)).Restored.Audio.Samples;
        Assert.True(Ends(without) > 2 * Ends(withMargin), $"{name}: with no margin the ends are {Ends(without):P2}, with one {Ends(withMargin):P2}: the margin did not help");
    }

    [Fact]
    public async Task ASectionAtTheStartOfTheTapeHasNoMarginBeforeItAndIsStillRestoredWhereTheTapeHasOne()
    {
        var music = Clip("piano-beethoven-op101", 10.0); var r = music.Rate;
        var heard = Tape.CorrectEqualisation(music, "IEC2", 7.5, "IEC1", 7.5);
        var (restored, _) = await Restore(heard, IrregularityFile(0, 4, 7.5, 7.5, "IEC1", "IEC2"));
        var got = restored.Audio.Samples; var want = music.Samples[..(4 * r)];
        Assert.Equal(4 * r, got.Length);
        Assert.True(RelativeError(got[(2 * r)..], want[(2 * r)..]) < 0.015, "the end of the section, where the tape has a margin, is not restored");        // the start has none: it is as it was
    }
}
