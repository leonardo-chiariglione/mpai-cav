using System.Text.Json;
using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.Metadata;
using Json.Schema;
using Mpai.Cae.Usc;
using Mpai.Cae.Usc.Arp;

namespace Mpai.Aif.Tests;

// CAE-USC V2.5, Audio Recording Preservation: AUDIO ANALYSIS FOR PRESERVATION. The offset between the Preservation Audio File and the audio of the Audio-Visual File; the irregularities
// of the audio (a dropout, a click, a step in level); the merge with those the Video Analysis found, in the time of the Preservation Audio File.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class CaeUscV25AapTests
{
    private const string Paf = "OSD-BAO-V1.5#1", Av = "OSD-BMO-V1.5#1", VideoIrr = "CAE-IRR-V2.5#1", Files = "OSD-BAO-V1.5#8", Irr = "CAE-IRR-V2.5#9";
    private const int Rate = 48000;

    private static bool Valid(string[] schema, string json)
    {
        var root = Path.GetFullPath(Repository.Schemas);
        var compiled = PublishedSchemas.At(root)[Path.Combine([root, .. schema])];
        using var document = JsonDocument.Parse(json);
        lock (PublishedSchemas.Lock) return compiled.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;
    }

    // Noise of a level, the same noise for the same seed: a stand-in for the music of a tape.
    private static short[] Noise(double seconds, int seed, int level = 3000)
    {
        var s = new short[(int)(seconds * Rate)]; var state = (uint)seed;
        for (var i = 0; i < s.Length; i++) { state = state * 1664525u + 1013904223u; s[i] = (short)((int)(state >> 8) % (2 * level + 1) - level); }
        return s;
    }

    // 12 s of tape with a dropout at 4.0-4.1 s, a click at 7.0 s and the level three times higher from 9.0 s.
    private static Audio IrregularTape()
    {
        var s = Noise(12, 1);
        for (var i = (int)(4.0 * Rate); i < (int)(4.1 * Rate); i++) s[i] = 0;
        s[(int)(7.0 * Rate)] = 30000;
        for (var i = (int)(9.0 * Rate); i < s.Length; i++) s[i] = (short)(s[i] * 3);
        return new Audio(s, Rate);
    }

    private static Message Of(params (string Key, string Json)[] ports) => new() { MessageId = "m", Ports = ports.ToDictionary(p => p.Key, p => p.Json) };
    private static AapAimProcessor Aap() => new("aap", Paf, Av, VideoIrr, Files, Irr);

    // ---- the offset ---------------------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheOffsetIsWhereTheAudioOfTheFilmStartsInThePreservationAudioFile()
    {
        var paf = new Audio(Noise(12, 7), Rate);
        var av = new Audio(paf.Samples[(int)(2.5 * Rate)..(int)(10.5 * Rate)], Rate);        // the film's audio starts 2.5 s into the recording
        Assert.InRange(AudioAnalysis.Offset(paf, av), 120000 - 48, 120000 + 48);
    }

    [Fact]
    public void TheOffsetIsNegativeWhenTheFilmStartsBeforeTheRecording()
    {
        var paf = new Audio(Noise(8, 7), Rate);
        var av = new Audio(Noise(2, 9).Concat(paf.Samples).ToArray(), Rate);                 // 2 s of something else, then the recording
        Assert.InRange(AudioAnalysis.Offset(paf, av), -2 * Rate - 48, -2 * Rate + 48);
    }

    // ---- the irregularities of the audio -------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ADropoutAClickAndAStepInLevelAreFoundWhereTheyAre()
    {
        var found = AudioAnalysis.Detect(IrregularTape());
        Assert.Equal(["dropout", "click", "step"], found.Select(f => f.Kind));
        Assert.InRange(found[0].Start, 3.97, 4.03); Assert.InRange(found[0].End, 4.07, 4.13);
        Assert.InRange((found[1].Start + found[1].End) / 2, 6.99, 7.01);
        Assert.InRange((found[2].Start + found[2].End) / 2, 8.9, 9.1);
        Assert.Empty(AudioAnalysis.Detect(new Audio(Noise(12, 1), Rate)));                   // a tape without irregularities has none
    }

    // ---- the AIM -------------------------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheAimGivesTheIrregularityFileWithTheOffsetAndTheAudioFilesOfWhatItFound()
    {
        var tape = IrregularTape();
        var av = AudioVisualObject.Make("av", new Audio(tape.Samples[(int)(2.5 * Rate)..], Rate));
        var reply = await Aap().ProcessAsync(Of((Paf, AudioObject.Make("pres", tape)), (Av, av)));
        Assert.False(reply.IsError, reply.Payload);
        Assert.True(Valid(["CAE1", "V2.5", "data", "IrregularityFile.json"], reply.Ports[Irr]));
        Assert.True(Valid(["OSD", "V1.5", "data", "BasicAudioObject.json"], reply.Ports[Files]));
        var file = JsonNode.Parse(reply.Ports[Irr])!;
        Assert.InRange(file["Offset"]!.GetValue<int>(), 2499, 2501);                            // in milliseconds
        var (list, _) = IrregularityFile.Read(reply.Ports[Irr]);
        Assert.Equal(3, list.Count); Assert.All(list, i => Assert.Equal("a", i.Source));
        Assert.Equal(3, list.Select(i => i.Id).Distinct().Count());                           // unique on the tape
        var clips = AudioObject.Read(reply.Ports[Files]);
        Assert.Equal(list.Select(i => i.Id), clips.Select(c => c.Id));                        // each irregularity has its audio file
        Assert.InRange(clips[0].Audio.Seconds, 2.05, 2.15);                                   // the dropout (0.1 s) and a second of margin on each side
        Assert.Equal("urn:mpai:cae:aap:audio:" + list[0].Id, file["Irregularities"]![0]!["AudioFileURI"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheIrregularitiesOfTheFilmComeInTheTimeOfTheRecordingAndThoseFoundByBothAreOne()
    {
        var tape = IrregularTape(); var aap = Aap();
        var first = await aap.ProcessAsync(Of((Paf, AudioObject.Make("pres", tape)), (Av, AudioVisualObject.Make("av", new Audio(tape.Samples[(int)(2.5 * Rate)..], Rate)))));
        var own = IrregularityFile.Read(first.Ports[Irr]).Irregularities;

        // The Video Analysis found two, in the time of the recording (it had the offset from the Audio Analysis): at 4.0 s (the dropout) and at 5.5 s (only the film saw it).
        var video = IrregularityFile.Make([new Irregularity(Guid.NewGuid().ToString(), "v", 4.0, 4.1), new Irregularity(Guid.NewGuid().ToString(), "v", 5.5, 5.6)]);
        var merged = await aap.ProcessAsync(Of((VideoIrr, video)));
        Assert.False(merged.IsError, merged.Payload);
        Assert.True(Valid(["CAE1", "V2.5", "data", "IrregularityFile.json"], merged.Ports[Irr]));
        var (list, offset) = IrregularityFile.Read(merged.Ports[Irr]);
        Assert.Equal(["b", "v", "a", "a"], list.Select(i => i.Source));                       // the dropout: both; 5.5 s: the film; the click and the step: the audio
        Assert.Equal(own[0].Id, list[0].Id);                                                  // the one found by both keeps the identifier of the audio
        Assert.InRange(list[1].Start, 5.49, 5.51);
        Assert.InRange(offset!.Value, 2499, 2501);
        Assert.Equal(4, AudioObject.Read(merged.Ports[Files]).Count);                         // the audio files of all four
    }

    [Fact]
    public async Task TheAudioAndTheFilmMayComeSeparatelyInEitherOrderAndTheOffsetFollowsWhenBothAreHeld()
    {
        var tape = IrregularTape(); var film = AudioVisualObject.Make("av", new Audio(tape.Samples[(int)(2.5 * Rate)..], Rate));
        var aap = Aap();
        var first = await aap.ProcessAsync(Of((Paf, AudioObject.Make("pres", tape))));                          // the recording first: its irregularities, no offset yet
        Assert.False(JsonNode.Parse(first.Ports[Irr])!.AsObject().ContainsKey("Offset"));
        var second = await aap.ProcessAsync(Of((Av, film)));                                                       // then the film: the same irregularities, now with the offset
        Assert.InRange(JsonNode.Parse(second.Ports[Irr])!["Offset"]!.GetValue<int>(), 2499, 2501);
        Assert.Equal(IrregularityFile.Read(first.Ports[Irr]).Irregularities.Select(i => i.Id), IrregularityFile.Read(second.Ports[Irr]).Irregularities.Select(i => i.Id));
        Assert.False((await aap.ProcessAsync(Of((Av, film)))).Ports.ContainsKey(Irr));                           // the same film again: nothing new

        var other = Aap();
        Assert.False((await other.ProcessAsync(Of((Av, film)))).Ports.ContainsKey(Irr));                          // the film first: there is no recording to analyse yet
        Assert.InRange(JsonNode.Parse((await other.ProcessAsync(Of((Paf, AudioObject.Make("pres", tape))))).Ports[Irr])!["Offset"]!.GetValue<int>(), 2499, 2501);
    }

    [Fact]
    public async Task TheLoopWithTheVideoAnalysisComesToAnEnd()
    {
        var tape = IrregularTape(); var aap = Aap();
        var first = await aap.ProcessAsync(Of((Paf, AudioObject.Make("pres", tape)), (Av, AudioVisualObject.Make("av", new Audio(tape.Samples[(int)(2.5 * Rate)..], Rate)))));
        var video = IrregularityFile.Make([new Irregularity(Guid.NewGuid().ToString(), "v", 5.5, 5.6)]);
        var merged = await aap.ProcessAsync(Of((VideoIrr, video)));
        Assert.True(merged.Ports.ContainsKey(Irr));
        // The same file again, and the merged file that came back from the Video Analysis (it holds the same irregularities): nothing new, nothing given.
        Assert.False((await aap.ProcessAsync(Of((VideoIrr, video)))).Ports.ContainsKey(Irr));
        Assert.False((await aap.ProcessAsync(Of((VideoIrr, merged.Ports[Irr])))).Ports.ContainsKey(Irr));
        Assert.True(first.Ports.ContainsKey(Irr));
    }

    [Fact]
    public async Task TheAimSaysWhatItCannotAnalyse()
    {
        var tape = IrregularTape();
        var video = IrregularityFile.Make([new Irregularity(Guid.NewGuid().ToString(), "v", 1, 2)]);
        var early = await Aap().ProcessAsync(Of((VideoIrr, video)));
        Assert.True(early.IsError); Assert.Contains("came before the Preservation Audio File", early.Payload);
        var before = await Aap().ProcessAsync(Of((Paf, AudioObject.Make("pres", new Audio(Noise(8, 7), Rate))), (Av, AudioVisualObject.Make("av", new Audio(Noise(2, 9).Concat(Noise(8, 7)).ToArray(), Rate)))));
        Assert.True(before.IsError); Assert.Contains("starts 2 s before the Preservation Audio File", before.Payload);
        Assert.False((await Aap().ProcessAsync(Of())).Ports.ContainsKey(Irr));                // nothing given, nothing to say
    }

    // A dropout changes the level of the second before and after it, and the detector of steps used to fire at its edges: the dropout was then absorbed as a "step" and lost.
    [Fact]
    public void ADropoutInATapeWithATonalSignalIsADropoutAndNotAStep()
    {
        var rate = 16000; var rng = new Random(1); var s = new short[10 * rate];
        for (var i = 0; i < s.Length; i++) s[i] = (short)(3000 * Math.Sin(2 * Math.PI * 220 * i / rate) + rng.Next(-1500, 1500));
        for (var i = (int)(3.0 * rate); i < (int)(3.8 * rate); i++) s[i] /= 100;
        s[6 * rate] = 25000;
        var found = AudioAnalysis.Detect(new Audio(s, rate));
        Assert.Equal(["dropout", "click"], found.Select(f => f.Kind));
        Assert.InRange(found[0].Start, 2.99, 3.01); Assert.InRange(found[0].End, 3.79, 3.81);
    }}
