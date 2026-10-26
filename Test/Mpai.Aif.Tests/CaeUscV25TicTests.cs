using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.Metadata;
using Json.Schema;
using Mpai.Cae.Usc;
using Mpai.Cae.Usc.Arp;

namespace Mpai.Aif.Tests;

// CAE-USC V2.5, Audio Recording Preservation: TAPE IRREGULARITY CLASSIFICATION. It gives each irregularity the type the definitions of the Irregularity File page describe (sp, sot, eot,
// pps, da...), selects the ones it can classify, and keeps a type and the properties an irregularity already has. Then the whole of ARP's analysis side, without a Controller:
// the Audio Analysis, the Video Analysis and the classification with their loop.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class CaeUscV25TicTests
{
    private const string Clips = "OSD-BAO-V1.5#1", AudioIrr = "CAE-IRR-V2.5#1", ImagesIn = "OSD-BVO-V1.5#1", VideoIrr = "CAE-IRR-V2.5#2", Irr = "CAE-IRR-V2.5#9", ImagesOut = "OSD-BVO-V1.5#9";
    private const int Rate = 48000;

    private static bool Valid(string[] schema, string json)
    {
        var root = Path.GetFullPath(Repository.Schemas);
        var compiled = PublishedSchemas.At(root)[Path.Combine([root, .. schema])];
        using var document = JsonDocument.Parse(json);
        lock (PublishedSchemas.Lock) return compiled.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;
    }

    private static short[] Noise(double seconds, int seed, int level = 3000)
    {
        var s = new short[(int)(seconds * Rate)]; var state = (uint)seed;
        for (var i = 0; i < s.Length; i++) { state = state * 1664525u + 1013904223u; s[i] = (short)((int)(state >> 8) % (2 * level + 1) - level); }
        return s;
    }

    private static Audio WithDropout(double seconds, double from, double to) { var s = Noise(seconds, 3); for (var i = (int)(from * Rate); i < (int)(to * Rate); i++) s[i] = 0; return new Audio(s, Rate); }
    private static Audio WithClick(double seconds, double at) { var s = Noise(seconds, 4); s[(int)(at * Rate)] = 30000; return new Audio(s, Rate); }
    private static Audio WithStep(double seconds, double at) { var s = Noise(seconds, 5); for (var i = (int)(at * Rate); i < s.Length; i++) s[i] = (short)(s[i] * 3); return new Audio(s, Rate); }

    private static Irregularity I(string id, string source, double start, double end, string? type = null, JsonObject? properties = null) => new(id, source, start, end, type, properties);
    private static Message Of(params (string Key, string Json)[] ports) => new() { MessageId = "m", Ports = ports.ToDictionary(p => p.Key, p => p.Json) };
    private static TicAimProcessor Tic(double? filmEnd = null) => new("tic", Clips, AudioIrr, ImagesIn, VideoIrr, Irr, ImagesOut, filmEnd);
    private static string Id(int n) => $"00000000-0000-0000-0000-{n:000000000000}";

    private static readonly JsonObject Standards = new() { ["ReadingSpeedStandard"] = 15, ["WritingSpeedStandard"] = 7.5, ["ReadingEqualisationStandard"] = "IEC1", ["WritingEqualisationStandard"] = "IEC1" };

    // What the two analyses give: audio irregularities with their audio files, film irregularities with their images.
    private static Message Evidence() => Of(
        (AudioIrr, IrregularityFile.Make([I(Id(1), "a", 4.0, 5.2), I(Id(2), "a", 6.0, 6.1), I(Id(3), "a", 7.0, 7.01), I(Id(4), "a", 9.0, 9.2), I(Id(8), "a", 1.0, 2.0, "ssv", Standards)], 2500)),
        (Clips, AudioObject.Make("clips", new AudioObject.Item(Id(1), WithDropout(3.2, 1.0, 2.2)), new AudioObject.Item(Id(2), WithDropout(2.1, 1.0, 1.1)),
                                         new AudioObject.Item(Id(3), WithClick(2.0, 1.0)), new AudioObject.Item(Id(4), WithStep(3.0, 1.5)))),
        (VideoIrr, IrregularityFile.Make([I(Id(5), "v", 20.0, 20.5), I(Id(6), "v", 2.6, 2.8)], 2500)),
        (ImagesIn, VisualObject.Make("images", [(Id(5), Png.Encode(new byte[12], 4, 3)), (Id(6), Png.Encode(new byte[12], 4, 3))])));

    // ---- the classification ----------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EachIrregularityGetsTheTypeItsDefinitionDescribesAndTheOnesNotClassifiedAreNotSelected()
    {
        var reply = await Tic().ProcessAsync(Evidence());
        Assert.False(reply.IsError, reply.Payload);
        Assert.True(Valid(["CAE1", "V2.5", "data", "IrregularityFile.json"], reply.Ports[Irr]));
        var (list, offset) = IrregularityFile.Read(reply.Ports[Irr]);
        Assert.Equal(2500, offset);
        var types = list.ToDictionary(i => i.Id, i => i.Type);
        Assert.Equal("ssv", types[Id(8)]);                  // a type it has, it keeps
        Assert.Equal("sot", types[Id(6)]);                  // the film: within two seconds of its start (it starts 2.5 s into the recording)
        Assert.Equal("pps", types[Id(1)]);                  // a silence of 1.2 s
        Assert.Equal("da", types[Id(2)]);                   // a silence of 0.1 s
        Assert.Equal("da", types[Id(3)]);                   // a click
        Assert.Equal("sp", types[Id(5)]);                   // the film, mid-tape
        Assert.False(types.ContainsKey(Id(4)));             // a step in level: it cannot say what it is: not relevant
        Assert.Equal(6, list.Count);
        Assert.Equal(list.OrderBy(i => i.Start).Select(i => i.Id), list.Select(i => i.Id));       // in time order
    }

    [Fact]
    public async Task ThePropertiesOfAnIrregularityAreKeptSoThatTapeAudioRestorationCanActOnThem()
    {
        var reply = await Tic().ProcessAsync(Evidence());
        var kept = IrregularityFile.Read(reply.Ports[Irr]).Irregularities.Single(i => i.Id == Id(8));
        Assert.Equal(15, kept.Properties!["ReadingSpeedStandard"]!.GetValue<double>());
        Assert.Equal(7.5, kept.Properties["WritingSpeedStandard"]!.GetValue<double>());

        // and Tape Audio Restoration, given the selected irregularities and the Preservation Audio File, restores the section the properties are about (1 s to 2 s, read at twice the speed)
        var audio = new short[4 * Rate]; Array.Fill(audio, (short)1000);
        var tar = await new TarAimProcessor("tar", "irr", "audio", "els", "restored").ProcessAsync(Of(("irr", reply.Ports[Irr]), ("audio", AudioObject.Make("pres", new Audio(audio, Rate)))));
        Assert.False(tar.IsError, tar.Payload);
        var restoration = JsonNode.Parse(tar.Ports["els"])!["Restorations"]![0]!;
        Assert.Equal("00:00:01.000", restoration["PreservationAudioFileStart"]!.GetValue<string>());
        Assert.Equal(Rate / 2, restoration["AppliedSamplingFrequency"]!.GetValue<int>());
        Assert.Single(JsonNode.Parse(tar.Ports["els"])!["Restorations"]!.AsArray());
    }

    [Fact]
    public async Task TheEndOfTheTapeIsRecognisedWhenTheLengthOfTheFilmIsKnown()
    {
        var evidence = Of((VideoIrr, IrregularityFile.Make([I(Id(7), "v", 30.2, 30.6)], 2500)));
        Assert.Equal("sp", IrregularityFile.Read((await Tic().ProcessAsync(evidence)).Ports[Irr]).Irregularities[0].Type);                  // the length of the film is not known: a splice
        Assert.Equal("eot", IrregularityFile.Read((await Tic(filmEnd: 31.0).ProcessAsync(evidence)).Ports[Irr]).Irregularities[0].Type);      // two seconds from the end of the film
    }

    [Fact]
    public async Task OnlyTheImagesOfTheSelectedIrregularitiesAreGiven()
    {
        var reply = await Tic().ProcessAsync(Evidence());
        Assert.True(Valid(["OSD", "V1.5", "data", "BasicVisualObject.json"], reply.Ports[ImagesOut]));
        var images = VisualObject.Images(reply.Ports[ImagesOut]).Select(i => i.Id);
        Assert.Equal([Id(6), Id(5)], images);                                                          // the film's two, in time order
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(reply.Ports[Irr], "ImageURI").Count);
    }

    [Fact]
    public async Task TheFileIsGivenAgainOnlyIfItIsNotWhatWasGivenLastAndNothingSelectedIsNothing()
    {
        var tic = Tic(); var evidence = Evidence();
        Assert.True((await tic.ProcessAsync(evidence)).Ports.ContainsKey(Irr));
        Assert.False((await tic.ProcessAsync(evidence)).Ports.ContainsKey(Irr));
        Assert.False((await Tic().ProcessAsync(Of((AudioIrr, IrregularityFile.Make([I(Id(4), "a", 9.0, 9.2)]))))).Ports.ContainsKey(Irr));   // a step only: nothing relevant
        Assert.False((await Tic().ProcessAsync(Of())).Ports.ContainsKey(Irr));
        Assert.True((await Tic().ProcessAsync(Of((AudioIrr, "{\"Header\":\"X\"}")))).IsError);
    }

    // ---- the analysis side of ARP, the three together --------------------------------------------------------------------------------------------------------------------

    private static Audio IrregularTape()
    {
        var s = Noise(12, 1);
        for (var i = (int)(4.0 * Rate); i < (int)(4.1 * Rate); i++) s[i] = 0;                              // a dropout
        s[(int)(7.0 * Rate)] = 30000;                                                                       // a click
        for (var i = (int)(9.0 * Rate); i < s.Length; i++) s[i] = (short)(s[i] * 3);                       // a step in level
        return new Audio(s, Rate);
    }

    private static byte[] Frame(int brightness, int seed)
    {
        var f = new byte[32 * 24]; var state = (uint)seed * 2654435761u + 12345u;
        for (var i = 0; i < f.Length; i++) { state = state * 1664525u + 1013904223u; f[i] = (byte)Math.Clamp(brightness + (int)(state >> 24) % 11 - 5, 0, 255); }
        return f;
    }

    [Fact]
    public async Task TheAudioAnalysisTheVideoAnalysisAndTheClassificationTogetherFindTheTapesIrregularitiesAndTheLoopEnds()
    {
        var tape = IrregularTape();
        var film = new VideoFrames(32, 24, 10, Enumerable.Range(0, 100).Select(t => Frame(t >= 30 && t < 36 ? 200 : t >= 70 ? 60 : 120, t)).ToArray());    // the splice at 2.9-3.7 s of the film, a new tape at 6.9 s
        var av = AudioVisualObject.Make("av", new Audio(tape.Samples[(int)(2.5 * Rate)..], Rate), film);       // the film starts 2.5 s into the recording
        var aap = new AapAimProcessor("aap", "paf", "av", "video", "files", "irr"); var vap = new VapAimProcessor("vap", "av", "irr", "irr", "images"); var tic = Tic();

        var a1 = await aap.ProcessAsync(Of(("paf", AudioObject.Make("pres", tape)), ("av", av)));            // the audio's own irregularities and the offset
        Assert.False(a1.IsError, a1.Payload);
        var v1 = await vap.ProcessAsync(Of(("av", av), ("irr", a1.Ports["irr"])));                              // the film's, in the time of the recording, merged
        Assert.False(v1.IsError, v1.Payload);
        var a2 = await aap.ProcessAsync(Of(("video", v1.Ports["irr"])));                                         // the audio's side merges them
        Assert.True(a2.Ports.ContainsKey("irr"));
        Assert.False((await vap.ProcessAsync(Of(("irr", a2.Ports["irr"])))).Ports.ContainsKey("irr"));          // the Video Analysis has nothing new: the loop ends
        Assert.False((await aap.ProcessAsync(Of(("video", v1.Ports["irr"])))).Ports.ContainsKey("irr"));

        var reply = await tic.ProcessAsync(Of((AudioIrr, a2.Ports["irr"]), (Clips, a2.Ports["files"]), (VideoIrr, v1.Ports["irr"]), (ImagesIn, v1.Ports["images"])));
        Assert.False(reply.IsError, reply.Payload);
        Assert.True(Valid(["CAE1", "V2.5", "data", "IrregularityFile.json"], reply.Ports[Irr]));
        var (list, offset) = IrregularityFile.Read(reply.Ports[Irr]);
        Assert.InRange(offset!.Value, 2499, 2501);
        // the dropout (da), the splice the film saw (sp), the click (da) and, where the step in level and the film's change of tape are one, a change of tape (sp: seen by both)
        Assert.Equal(["da", "sp", "da", "sp"], list.Select(i => i.Type));
        Assert.Equal(["a", "v", "a", "b"], list.Select(i => i.Source));
        Assert.InRange(list[1].Start, 5.35, 5.45);

        // Packaging puts it in the master with the images that document it
        var pap = await new PapAimProcessor("pap", "audio", "restored", "els", "irr", "images", "av", "access", "master").ProcessAsync(
            Of(("audio", AudioObject.Make("pres", tape)), ("irr", reply.Ports[Irr]), ("images", reply.Ports[ImagesOut]), ("av", av)));
        Assert.False(pap.IsError, pap.Payload);
        var master = AudioObject.Files(pap.Ports["master"], "BasicAudioObjectData").Select(f => f.Id).ToList();
        Assert.Contains("irregularities.json", master);
        Assert.Equal(4, master.Count(n => n!.StartsWith("irregularity-image-")));
        Assert.Contains("preservation-audio-visual.bin", master);
    }
}
