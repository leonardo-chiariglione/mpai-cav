using System.Text.Json;
using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.Metadata;
using Json.Schema;
using Mpai.Cae.Usc;
using Mpai.Cae.Usc.Smc;
using Mpai.Cae.Usc.Sra;

namespace Mpai.Aif.Tests;

// CAE-USC V2.5 - the Speech Restoration System, SubAIM by SubAIM (the AIMs alone, as AihAimsTests does for AIH): Speech Model Creation makes an ML Model Object from the Speech
// Segments for Modelling, Speech Restoration Assembly puts the synthesised speech in the places of the damaged sections of the Damaged Segment. All of it is speech: 16 kHz, 16 bit.
// The Text To Speech between them is MPAI-MMC's.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class CaeUscV25AimTests
{
    private const string Bso = "OSD-BSO-V1.5#1", Mlo = "AIP-MLO-V1.0#1", Bao = "OSD-BSO-V1.5#1", Synth = "OSD-BSO-V1.5#2", Dls = "CAE-DLS-V2.5#1", Out = "OSD-BSO-V1.5#9";
    private const int Speech = 16000, AudioRate = 16000;

    private static bool Valid(string[] schema, string json)
    {
        var root = Path.GetFullPath(Repository.Schemas);
        var compiled = PublishedSchemas.At(root)[Path.Combine([root, .. schema])];
        using var document = JsonDocument.Parse(json);
        lock (PublishedSchemas.Lock) return compiled.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;
    }

    private static Audio Tone(double seconds, int rate = Speech, double hertz = 220, double level = 8000)
    {
        var s = new short[(int)(seconds * rate)];
        for (var i = 0; i < s.Length; i++) s[i] = (short)(level * Math.Sin(2 * Math.PI * hertz * i / rate));
        return new Audio(s, rate);
    }
    private static Audio Constant(double seconds, short value, int rate) => new(Enumerable.Repeat(value, (int)(seconds * rate)).ToArray(), rate);

    private static JsonObject SimpleTime((double Start, double End) s) => new()
    {
        ["Header"] = "OSD-STM-V1.5", ["SimpleTimeID"] = "st",
        ["SimpleTimeData"] = new JsonArray(new JsonObject { ["FlagsByte"] = 0, ["StartTime"] = s.Start, ["EndTime"] = s.End, ["AccuracyMode"] = "single", ["AccuracyPlusMinus"] = 0 })
    };

    // A Damaged List as the schema of V2.5 has it: each damaged section a Time (OSD-TIM) whose Qualifier gives the Simple Time.
    private static string DamagedList(params (double Start, double End)[] sections) =>
        new JsonObject
        {
            ["Header"] = "CAE-DLS-V2.5", ["DamagedListID"] = "dl-1",
            ["DamagedListData"] = new JsonArray(sections.Select(s => (JsonNode)new JsonObject
            {
                ["DamagedSectionTime"] = new JsonObject
                {
                    ["Header"] = "OSD-TIM-V1.5", ["TimeID"] = "t", ["Data"] = $"{s.Start} {s.End}",
                    ["Qualifier"] = new JsonObject
                    {
                        ["Header"] = "TFA-TIQ-V1.5", ["TimeQualifierID"] = "tq", ["SubTypes"] = new JsonObject(),
                        ["Formats"] = new JsonObject { ["MPAIFormat"] = SimpleTime(s) }
                    }
                }
            }).ToArray())
        }.ToJsonString();

    // A Damaged List whose damaged sections ARE Simple Times.
    private static string DamagedListOfSimpleTimes(params (double Start, double End)[] sections) =>
        new JsonObject
        {
            ["Header"] = "CAE-DLS-V2.5", ["DamagedListID"] = "dl-1",
            ["DamagedListData"] = new JsonArray(sections.Select(s => (JsonNode)new JsonObject { ["DamagedSectionTime"] = SimpleTime(s) }).ToArray())
        }.ToJsonString();

    private static Message Of(params (string Key, string Json)[] ports) => new() { MessageId = "m", Ports = ports.ToDictionary(p => p.Key, p => p.Json) };

    private static SmcAimProcessor Smc(Action<string>? report = null) => new("smc", Bso, Mlo, new StandInSpeechTrainer(), report);
    private static SraAimProcessor Sra() => new("sra", Bao, Dls, Synth, Out);

    private static Audio Part(Message reply)
    {
        Assert.False(reply.IsError, reply.Payload);
        Assert.True(reply.Ports.ContainsKey(Out), "the Assembly gave nothing");
        return SpeechObject.Read(reply.Ports[Out]).Single();
    }

    // ---- Speech Model Creation ------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SpeechModelCreationGivesAnMLModelObjectMadeFromTheSegments()
    {
        var reply = await Smc().ProcessAsync(Of((Bso, SpeechObject.Make("segments", Tone(1), Tone(2)))));
        Assert.False(reply.IsError, reply.Payload);
        Assert.Equal("AIP-MLO-V1.0", reply.DataType);
        Assert.True(Valid(["AIP", "V1.0", "data", "MLModelObject.json"], reply.Ports[Mlo]));
        var model = JsonNode.Parse(reply.Ports[Mlo])!;
        var bytes = Convert.FromBase64String(model["MLModelData"]![0]!["Data"]!.GetValue<string>());
        var weights = new float[bytes.Length / 4]; Buffer.BlockCopy(bytes, 0, weights, 0, bytes.Length);
        Assert.Equal(3, weights.Length);
        Assert.Equal(1.5f, weights[2], 3);                                                   // the mean duration of the two segments
        Assert.Contains("2 Speech Segment(s)", model["DescrMetadata"]!.GetValue<string>());
    }

    [Fact]
    public async Task SpeechModelCreationSaysSoWhenThereIsLittleSpeechAndStillGivesTheModel()
    {
        var reports = new List<string>();
        var reply = await Smc(reports.Add).ProcessAsync(Of((Bso, SpeechObject.Make("segments", Tone(0.4)))));
        Assert.False(reply.IsError, reply.Payload);                                          // a report is not an error
        Assert.Single(reports);
        Assert.Contains("reduced quality", reports[0]);
        var enough = new List<string>();
        await Smc(enough.Add).ProcessAsync(Of((Bso, SpeechObject.Make("s", Tone(2)))));
        Assert.Empty(enough);
    }

    [Fact]
    public async Task SpeechModelCreationRefusesWhatIsNotAudioItCanRead()
    {
        var notWav = new JsonObject { ["Header"] = "OSD-BSO-V1.5", ["BasicSpeechObjectData"] = new JsonArray(new JsonObject { ["Data"] = Convert.ToBase64String("not audio"u8.ToArray()) }) }.ToJsonString();
        var reply = await Smc().ProcessAsync(Of((Bso, notWav)));
        Assert.True(reply.IsError);
        Assert.Contains("not a WAV file", reply.Payload);
        Assert.True((await Smc().ProcessAsync(Of())).IsError);                               // nothing at the input Port
    }

    // ---- The Speech Qualifier says what the data of a Speech Object is -----------------------------------------------------------------------------------------------------

    [Fact]
    public void ASpeechObjectOfSpeechFilesAndOneOfRawSamplesAreReadAlikeAndTheirQualifiersAreValid()
    {
        var tone = Tone(1);
        foreach (var json in new[] { SpeechObject.Make("file", tone), SpeechObject.MakeRaw("raw", tone), SpeechObject.MakeSynthetic("syn", tone) })
        {
            Assert.True(Valid(["OSD", "V1.5", "data", "BasicSpeechObject.json"], json));
            var read = SpeechObject.Read(json).Single();
            Assert.Equal(Speech, read.Rate); Assert.Equal(tone.Samples, read.Samples);
        }
        Assert.Equal("Synthetic", JsonNode.Parse(SpeechObject.MakeSynthetic("syn", tone))!["BasicSpeechDataQualifier"]!["Attributes"]!["Source"]!.GetValue<string>());
        Assert.Equal("WAV", JsonNode.Parse(SpeechObject.Make("file", tone))!["BasicSpeechDataQualifier"]!["Formats"]!["TransportFormats"]!["FileFormat"]!.GetValue<string>());
        Assert.Null(JsonNode.Parse(SpeechObject.MakeRaw("raw", tone))!["BasicSpeechDataQualifier"]!["Formats"]!["TransportFormats"]);
    }

    [Fact]
    public async Task TheAimsReadWhatTheQualifierSaysTheDataIs()
    {
        // Speech Segments for Modelling as raw samples (no file): the model is the same as from the files.
        var segments = new[] { Tone(1), Tone(2) };
        var fromFiles = await Smc().ProcessAsync(Of((Bso, SpeechObject.Make("a", segments))));
        var fromRaw = await Smc().ProcessAsync(Of((Bso, SpeechObject.MakeRaw("b", segments))));
        Assert.False(fromRaw.IsError, fromRaw.Payload);
        Assert.Equal(JsonNode.Parse(fromFiles.Ports[Mlo])!["MLModelData"]![0]!["Data"]!.ToString(), JsonNode.Parse(fromRaw.Ports[Mlo])!["MLModelData"]![0]!["Data"]!.ToString());

        // A speech file of another format, or samples of another precision, are speech too: this Implementation does not decode them, and says so.
        var mp4 = JsonNode.Parse(SpeechObject.Make("c", Tone(1)))!; mp4["BasicSpeechDataQualifier"]!["Formats"]!["TransportFormats"]!["FileFormat"] = "MP4";
        Assert.Contains("this Implementation does not decode it (it decodes WAV)", (await Smc().ProcessAsync(Of((Bso, mp4.ToJsonString())))).Payload);
        var raw24 = JsonNode.Parse(SpeechObject.MakeRaw("d", Tone(1)))!; raw24["BasicSpeechDataQualifier"]!["Formats"]!["ContentFormats"]!["RawData"]!["PCM"]!["SamplePrecision"] = 24;
        Assert.Contains("this Implementation does not decode it (it decodes 16 bit)", (await Smc().ProcessAsync(Of((Bso, raw24.ToJsonString())))).Payload);
    }

    // ---- Speech Restoration Assembly -----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheDamagedListInTheFormOfTheSchemaIsValid() =>
        Assert.True(Valid(["CAE1", "V2.5", "data", "DamagedList.json"], DamagedList((1.0, 2.0), (2.5, 2.75))));

    [Fact]
    public void TheTimeOfADamagedSectionIsASimpleTime()
    {
        foreach (var json in new[] { DamagedListOfSimpleTimes((1.0, 2.0), (2.5, 2.75)), DamagedList((1.0, 2.0), (2.5, 2.75)) })
        {
            var s = SraAimProcessor.Sections(json);
            Assert.Equal([1.0, 2.5], s.Select(x => x.Start)); Assert.Equal([2.0, 2.75], s.Select(x => x.End));
        }
        var ms = new JsonObject { ["Header"] = "CAE-DLS-V2.5", ["DamagedListData"] = new JsonArray(new JsonObject { ["DamagedSectionTime"] = new JsonObject
            { ["SimpleTimeData"] = new JsonArray(new JsonObject { ["StartTime"] = 1500, ["EndTime"] = 2500, ["TimeUnit"] = "01" }) } }) }.ToJsonString();
        Assert.Equal(1.5, SraAimProcessor.Sections(ms)[0].Start, 6);                          // TimeUnit 01: milliseconds
    }

    [Fact]
    public async Task AssemblyPutsTheSynthesisedSpeechInTheDamagedSectionAndLeavesTheRestAlone()
    {
        var damaged = Constant(3, 1000, AudioRate); var piece = Constant(0.5, -2000, 8000);        // a piece at another rate is brought to the rate of the Damaged Segment
        var reply = await Sra().ProcessAsync(Of((Bao, SpeechObject.Make("d", damaged)), (Dls, DamagedList((1.0, 2.0))), (Synth, SpeechObject.Make("s", piece))));
        var restored = Part(reply);
        Assert.True(Valid(["OSD", "V1.5", "data", "BasicSpeechObject.json"], reply.Ports[Out]));
        Assert.Equal(AudioRate, restored.Rate);
        Assert.Equal(2.5, restored.Seconds, 3);                                              // 1 s, 0.5 s inserted, 1 s
        Assert.All(restored.Samples.Take(AudioRate), v => Assert.Equal(1000, v));
        Assert.All(restored.Samples.Skip(AudioRate).Take(AudioRate / 2), v => Assert.Equal(-2000, v));
        Assert.All(restored.Samples.Skip(AudioRate + AudioRate / 2), v => Assert.Equal(1000, v));
    }

    [Fact]
    public async Task TheRestoredSegmentIsGivenInPartsAsTheSynthesisedSpeechComes()
    {
        var damaged = Constant(4, 1000, AudioRate);
        var sra = Sra();
        // The Damaged Segment and the Damaged List first: nothing to give yet.
        var first = await sra.ProcessAsync(Of((Bao, SpeechObject.Make("d", damaged)), (Dls, DamagedList((0.5, 1.0), (2.5, 3.0)))));
        Assert.False(first.IsError, first.Payload); Assert.False(first.Ports.ContainsKey(Out));

        // The speech for the first section: the audio up to the section, and the speech.
        var part1 = Part(await sra.ProcessAsync(Of((Synth, SpeechObject.Make("s1", Constant(0.25, 100, Speech))))));
        Assert.Equal(0.5 + 0.25, part1.Seconds, 3);
        Assert.Equal(1000, part1.Samples[AudioRate / 4]); Assert.Equal(100, part1.Samples[AudioRate / 2 + 100]);

        // The speech for the second: the audio between the sections, the speech, and the end of the Damaged Segment.
        var part2 = Part(await sra.ProcessAsync(Of((Synth, SpeechObject.Make("s2", Constant(0.25, 200, Speech))))));
        Assert.Equal(1.5 + 0.25 + 1.0, part2.Seconds, 3);
        Assert.Equal(1000, part2.Samples[100]); Assert.Equal(200, part2.Samples[(int)(1.5 * AudioRate) + 100]); Assert.Equal(1000, part2.Samples[^1]);
    }

    [Fact]
    public async Task SpeechThatComesBeforeTheDamagedSegmentWaitsForIt()
    {
        var sra = Sra();
        Assert.False((await sra.ProcessAsync(Of((Synth, SpeechObject.Make("s1", Constant(0.25, 100, Speech)))))).Ports.ContainsKey(Out));
        var restored = Part(await sra.ProcessAsync(Of((Bao, SpeechObject.Make("d", Constant(2, 1000, AudioRate))), (Dls, DamagedList((0.5, 1.0))))));
        Assert.Equal(2 - 0.5 + 0.25, restored.Seconds, 3);
        Assert.Equal(100, restored.Samples[AudioRate / 2 + 100]);
    }

    [Fact]
    public async Task AWhollyDamagedSegmentIsReplacedWholly()
    {
        var restored = Part(await Sra().ProcessAsync(Of((Bao, SpeechObject.Make("d", Constant(2, 1000, AudioRate))), (Dls, DamagedList((0, 2))), (Synth, SpeechObject.Make("s", Constant(1.5, -5, Speech))))));
        Assert.Equal(1.5, restored.Seconds, 3);
        Assert.All(restored.Samples, v => Assert.Equal(-5, v));
    }

    [Fact]
    public async Task AssemblyRefusesSectionsNotFromLeftToRightAndMoreSpeechThanSections()
    {
        var unordered = await Sra().ProcessAsync(Of((Bao, SpeechObject.Make("d", Constant(4, 1000, AudioRate))), (Dls, DamagedList((2, 3), (0.5, 1)))));
        Assert.True(unordered.IsError);
        Assert.Contains("not from left to right", unordered.Payload);

        var surplus = await Sra().ProcessAsync(Of((Bao, SpeechObject.Make("d", Constant(2, 1000, AudioRate))), (Dls, DamagedList((0, 1))), (Synth, SpeechObject.Make("s", Constant(1, 7, Speech), Constant(1, 8, Speech)))));
        Assert.True(surplus.IsError);
        Assert.Contains("more Synthesised Speech than the Damaged List has sections", surplus.Payload);
    }

    [Fact]
    public async Task TheSubAimsTogetherRestoreTheSegmentPartByPart()
    {
        // The Speech Restoration System, SubAIM by SubAIM: the model from the speech segments; a stand-in for Text To Speech makes, from the model, the speech of each text (its
        // level the model's, 0.5 s long); the Assembly puts each where the Damaged List says, as it comes.
        var model = await Smc().ProcessAsync(Of((Bso, SpeechObject.Make("segments", Tone(1.5), Tone(2.5)))));
        var weights = new float[3]; Buffer.BlockCopy(Convert.FromBase64String(JsonNode.Parse(model.Ports[Mlo])!["MLModelData"]![0]!["Data"]!.GetValue<string>()), 0, weights, 0, 12);
        Audio Say() => Tone(0.5, Speech, 220, weights[0] * 32768 * Math.Sqrt(2));

        var sra = Sra();
        await sra.ProcessAsync(Of((Bao, SpeechObject.Make("d", Tone(6, AudioRate))), (Dls, DamagedList((1.0, 1.8), (4.0, 5.0)))));
        var parts = new[] { Part(await sra.ProcessAsync(Of((Synth, SpeechObject.Make("a", Say()))))), Part(await sra.ProcessAsync(Of((Synth, SpeechObject.Make("b", Say()))))) };
        Assert.All(parts, p => Assert.Equal(AudioRate, p.Rate));
        Assert.Equal(6 - 0.8 - 1.0 + 1.0, parts.Sum(p => p.Seconds), 3);
    }
}
