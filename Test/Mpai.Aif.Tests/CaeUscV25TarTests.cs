using System.Text.Json;
using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.Metadata;
using Json.Schema;
using Mpai.Cae.Usc;
using Mpai.Cae.Usc.Arp;

namespace Mpai.Aif.Tests;

// CAE-USC V2.5, Audio Recording Preservation: TAPE AUDIO RESTORATION. The Irregularity File says the standards a section of the tape was read with and was written with; the AIM
// corrects the section - the sampling frequency to apply, the equalisation - and records what it applied in the Editing List.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class CaeUscV25TarTests
{
    private const string Irr = "CAE-IRR-V2.5#1", Bao = "OSD-BAO-V1.5#1", Els = "CAE-ELS-V2.5#1", Out = "OSD-BAO-V1.5#2";
    private const int Rate = 48000;

    private static bool Valid(string[] schema, string json)
    {
        var root = Path.GetFullPath(Repository.Schemas);
        var compiled = PublishedSchemas.At(root)[Path.Combine([root, .. schema])];
        using var document = JsonDocument.Parse(json);
        lock (PublishedSchemas.Lock) return compiled.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;
    }

    private static Audio Tone(double seconds, double hertz, double level = 10000)
    {
        var s = new short[(int)(seconds * Rate)];
        for (var i = 0; i < s.Length; i++) s[i] = (short)(level * Math.Sin(2 * Math.PI * hertz * i / Rate));
        return new Audio(s, Rate);
    }

    private static double Rms(Audio a, double from, double to) =>
        Math.Sqrt(a.Samples.Skip((int)(from * a.Rate)).Take((int)((to - from) * a.Rate)).Select(v => (double)v * v).Average());

    public sealed record Irregularity(double Start, double End, double? ReadSpeed = null, double? WriteSpeed = null, string? ReadEq = null, string? WriteEq = null);

    private static string IrregularityFile(params Irregularity[] list) => new JsonObject
    {
        ["Header"] = "CAE-IRR-V2.5", ["IrregularityFileID"] = "irr-1",
        ["Irregularities"] = new JsonArray(list.Select(i =>
        {
            var props = new JsonObject();
            if (i.ReadSpeed is not null) props["ReadingSpeedStandard"] = i.ReadSpeed; if (i.WriteSpeed is not null) props["WritingSpeedStandard"] = i.WriteSpeed;
            if (i.ReadEq is not null) props["ReadingEqualisationStandard"] = i.ReadEq; if (i.WriteEq is not null) props["WritingEqualisationStandard"] = i.WriteEq;
            return (JsonNode)new JsonObject
            {
                ["IrregularityID"] = Guid.NewGuid().ToString(), ["Source"] = "a", ["IrregularityType"] = "s",
                ["TimeLabel"] = new JsonObject
                {
                    ["Header"] = "OSD-TIM-V1.5", ["TimeID"] = "t", ["Data"] = $"{i.Start} {i.End}",
                    ["Qualifier"] = new JsonObject
                    {
                        ["Header"] = "TFA-TIQ-V1.5", ["TimeQualifierID"] = "tq", ["SubTypes"] = new JsonObject(),
                        ["Formats"] = new JsonObject { ["MPAIFormat"] = new JsonObject
                        {
                            ["Header"] = "OSD-STM-V1.5", ["SimpleTimeID"] = "st",
                            ["SimpleTimeData"] = new JsonArray(new JsonObject { ["FlagsByte"] = 0, ["StartTime"] = i.Start, ["EndTime"] = i.End, ["AccuracyMode"] = "single", ["AccuracyPlusMinus"] = 0 })
                        } }
                    }
                },
                ["IrregularityProperties"] = props
            };
        }).ToArray())
    }.ToJsonString();

    private static TarAimProcessor Tar() => new("tar", Irr, Bao, Els, Out);
    private static Message Of(params (string Key, string Json)[] ports) => new() { MessageId = "m", Ports = ports.ToDictionary(p => p.Key, p => p.Json) };

    // ---- the tape standards --------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ATapeReadAtTwiceTheSpeedItWasWrittenAtPlaysAtHalfTheRate()
    {
        Assert.Equal(24000, Tape.AppliedSamplingFrequency(48000, 15, 7.5));
        Assert.Equal(96000, Tape.AppliedSamplingFrequency(48000, 3.75, 7.5));
        Assert.Equal(48000, Tape.AppliedSamplingFrequency(48000, 7.5, 7.5));
    }

    [Fact]
    public void TheEqualisationIsCorrectedByTheResponseItShouldHaveHadOverTheOneItHad()
    {
        // Read with IEC1 at 7.5 ips (70 us) but written with IEC2 (3180 us and 50 us): at 5 kHz the high frequencies were cut too much (by 70 us instead of 50 us).
        var high = Tape.CorrectEqualisation(Tone(1, 5000), "IEC1", 7.5, "IEC2", 7.5);
        var gainHigh = Rms(high, 0.2, 0.8) / Rms(Tone(1, 5000), 0.2, 0.8);
        var w = 2 * Math.PI * 5000;
        Assert.Equal(Math.Sqrt(1 + Math.Pow(w * 70e-6, 2)) / Math.Sqrt(1 + Math.Pow(w * 50e-6, 2)), gainHigh, 2);       // 1.30
        // and at 100 Hz the low-frequency boost of the 3180 us time constant it lacked is given back.
        var low = Tape.CorrectEqualisation(Tone(1, 100), "IEC1", 7.5, "IEC2", 7.5);
        var gainLow = Rms(low, 0.2, 0.8) / Rms(Tone(1, 100), 0.2, 0.8);
        var wl = 2 * Math.PI * 100;
        Assert.InRange(gainLow, Math.Sqrt(1 + 1 / Math.Pow(wl * 3180e-6, 2)) - 0.02, Math.Sqrt(1 + 1 / Math.Pow(wl * 3180e-6, 2)) + 0.02);        // 1.12 (a little off: the spectrum of a finite tone leaks to where the boost is large)
        // the same standard at the same speed is the identity
        var same = Tape.CorrectEqualisation(Tone(1, 1000), "IEC2", 7.5, "IEC2", 7.5);
        Assert.Equal(Tone(1, 1000).Samples.Skip(1000).Take(100), same.Samples.Skip(1000).Take(100), new ToleranceComparer(2));
    }

    private sealed class ToleranceComparer(int tolerance) : IEqualityComparer<short>
    {
        public bool Equals(short a, short b) => Math.Abs(a - b) <= tolerance;
        public int GetHashCode(short s) => 0;
    }

    [Fact]
    public void AnUnknownEqualisationAtASpeedIsRefused() => Assert.Throws<ArgumentException>(() => Tape.TimeConstants("IEC2", 5));

    // ---- the AIM -------------------------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASectionReadAtTwiceTheSpeedItWasWrittenAtIsRestoredAndTheEditingListSaysSo()
    {
        var audio = Tone(4, 440);
        var reply = await Tar().ProcessAsync(Of((Irr, IrregularityFile(new Irregularity(1.0, 2.0, 15, 7.5, "IEC1", "IEC1"))), (Bao, AudioObject.Make("pres", audio))));
        Assert.False(reply.IsError, reply.Payload);
        Assert.True(Valid(["CAE1", "V2.5", "data", "EditingList.json"], reply.Ports[Els]));
        Assert.True(Valid(["OSD", "V1.5", "data", "BasicAudioObject.json"], reply.Ports[Out]));

        var list = JsonNode.Parse(reply.Ports[Els])!;
        var restoration = list["Restorations"]![0]!;
        Assert.Equal("00:00:01.000", restoration["PreservationAudioFileStart"]!.GetValue<string>());
        Assert.Equal("00:00:02.000", restoration["PreservationAudioFileEnd"]!.GetValue<string>());
        Assert.Equal(24000, restoration["AppliedSamplingFrequency"]!.GetValue<int>());
        Assert.Equal(7.5, restoration["AppliedSpeedStandard"]!.GetValue<double>());
        Assert.False(restoration["ReadingBackwards"]!.GetValue<bool>());
        Assert.Equal(48000, list["OriginalSamplingFrequency"]!.GetValue<int>());
        Assert.Equal(15, list["OriginalSpeedStandard"]!.GetValue<double>());

        var restored = AudioObject.Read(reply.Ports[Out]).Single();
        Assert.Equal(restoration["RestorationID"]!.GetValue<string>(), restored.Id);                  // the restored file is the one the Editing List names
        Assert.Equal(24000, restored.Audio.Rate);
        Assert.Equal(48000, restored.Audio.Samples.Length);                                           // the samples of the second, at half the rate: it plays two seconds
        Assert.Equal(audio.Samples.Skip(48000).Take(10), restored.Audio.Samples.Take(10));            // the equalisation was the same: the samples are the tape's
    }

    [Fact]
    public async Task AWrongEqualisationIsCorrectedAndEachSectionHasItsRestoration()
    {
        var audio = Tone(4, 5000);
        var reply = await Tar().ProcessAsync(Of((Irr, IrregularityFile(new Irregularity(0.5, 1.5, 7.5, 7.5, "IEC1", "IEC2"), new Irregularity(2.0, 3.0, 3.75, 7.5, "IEC2", "IEC2"))),
                                                (Bao, AudioObject.Make("pres", audio))));
        Assert.False(reply.IsError, reply.Payload);
        Assert.True(Valid(["CAE1", "V2.5", "data", "EditingList.json"], reply.Ports[Els]));
        var list = JsonNode.Parse(reply.Ports[Els])!["Restorations"]!.AsArray();
        Assert.Equal(2, list.Count);
        var files = AudioObject.Read(reply.Ports[Out]);
        Assert.Equal([list[0]!["RestorationID"]!.GetValue<string>(), list[1]!["RestorationID"]!.GetValue<string>()], files.Select(f => f.Id));
        Assert.Equal(Rate, files[0].Audio.Rate); Assert.Equal(96000, files[1].Audio.Rate);            // the first: the speed was right; the second: read at half the speed
        Assert.True(Rms(files[0].Audio, 0.2, 0.8) > 1.2 * Rms(new Audio(audio.Samples[24000..72000], Rate), 0.2, 0.8));   // the equalisation of 5 kHz was corrected (x 1.3)
    }

    [Fact]
    public async Task WhereNothingNeedsCorrectingTheAimGivesNothing()
    {
        var reply = await Tar().ProcessAsync(Of((Irr, IrregularityFile(new Irregularity(1, 2, 7.5, 7.5, "IEC2", "IEC2"), new Irregularity(2, 3))), (Bao, AudioObject.Make("pres", Tone(4, 440)))));
        Assert.False(reply.IsError, reply.Payload);
        Assert.False(reply.Ports.ContainsKey(Els)); Assert.False(reply.Ports.ContainsKey(Out));
    }

    [Fact]
    public async Task TheAimKeepsThePreservationAudioFileAndRestoresEachIrregularityFileThatComesOnceOnly()
    {
        var tar = Tar();
        Assert.False((await tar.ProcessAsync(Of((Bao, AudioObject.Make("pres", Tone(4, 440)))))).Ports.ContainsKey(Els));                  // the audio first: nothing to restore yet
        var file = IrregularityFile(new Irregularity(1.0, 2.0, 15, 7.5, "IEC1", "IEC1"));
        var reply = await tar.ProcessAsync(Of((Irr, file)));                                                                                   // the file comes later, alone
        Assert.True(reply.Ports.ContainsKey(Els)); Assert.Equal(24000, JsonNode.Parse(reply.Ports[Els])!["Restorations"]![0]!["AppliedSamplingFrequency"]!.GetValue<int>());
        Assert.False((await tar.ProcessAsync(Of((Irr, file)))).Ports.ContainsKey(Els));                                                        // the same again: nothing new
        var more = await tar.ProcessAsync(Of((Irr, IrregularityFile(new Irregularity(1.0, 2.0, 15, 7.5), new Irregularity(3.0, 3.5, 3.75, 7.5)))));   // another file: restored again
        Assert.Equal(2, JsonNode.Parse(more.Ports[Els])!["Restorations"]!.AsArray().Count);
    }

    [Fact]
    public async Task TheAimSaysWhatItCannotRestore()
    {
        Assert.False((await Tar().ProcessAsync(Of((Bao, AudioObject.Make("pres", Tone(1, 440)))))).Ports.ContainsKey(Els));       // no Irregularity File yet: it waits for it
        var beyond = await Tar().ProcessAsync(Of((Irr, IrregularityFile(new Irregularity(10, 12, 15, 7.5))), (Bao, AudioObject.Make("pres", Tone(4, 440)))));
        Assert.True(beyond.IsError); Assert.Contains("covers no sample", beyond.Payload);
        var notIrr = await Tar().ProcessAsync(Of((Irr, "{\"Header\":\"X\"}"), (Bao, AudioObject.Make("pres", Tone(4, 440)))));
        Assert.True(notIrr.IsError); Assert.Contains("Not an Irregularity File", notIrr.Payload);
        var mp4 = JsonNode.Parse(AudioObject.Make("pres", Tone(1, 440)))!; mp4["AudioQualifier"]!["Formats"]!["TransportFormat"]!["FileFormats"] = "FLAC";
        var flac = await Tar().ProcessAsync(Of((Irr, IrregularityFile(new Irregularity(0, 1, 15, 7.5))), (Bao, mp4.ToJsonString())));
        Assert.Contains("this Implementation does not decode it (it decodes WAV)", flac.Payload);
    }
}
