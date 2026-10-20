using System.Text.Json;
using AIF.Controller;
using AIF.Metadata;
using Json.Schema;
using Mpai.Cae.Usc;
using Mpai.Cae.Usc.Arp;

namespace Mpai.Aif.Tests;

// CAE-USC V2.5, Audio Recording Preservation: VIDEO ANALYSIS FOR PRESERVATION, on the stand-in for the film (a series of grayscale frames). The irregularities of the film (a change of
// brightness, a splice passing the head), put in the time of the Preservation Audio File by the offset the Audio Analysis gives, merged with the audio's, with an image of each.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class CaeUscV25VapTests
{
    private const string Av = "OSD-BMO-V1.5#1", AudioIrr = "CAE-IRR-V2.5#1", VideoIrr = "CAE-IRR-V2.5#9", Images = "OSD-BVO-V1.5#9";
    private const int W = 32, H = 24, Rate = 48000, OffsetMs = 2500;

    private static bool Valid(string[] schema, string json)
    {
        var root = Path.GetFullPath(Repository.Schemas);
        var compiled = PublishedSchemas.At(root)[Path.Combine([root, .. schema])];
        using var document = JsonDocument.Parse(json);
        lock (PublishedSchemas.Lock) return compiled.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;
    }

    private static byte[] Frame(int brightness, int seed)
    {
        var f = new byte[W * H]; var state = (uint)seed * 2654435761u + 12345u;
        for (var i = 0; i < f.Length; i++) { state = state * 1664525u + 1013904223u; f[i] = (byte)Math.Clamp(brightness + (int)(state >> 24) % 11 - 5, 0, 255); }
        return f;
    }

    // 10 s at 10 frames a second: the head at brightness 120; a splice (200) from frame 30 to 35; from frame 70 a tape of another colour (60) to the end.
    private static VideoFrames Film() =>
        new(W, H, 10, Enumerable.Range(0, 100).Select(t => Frame(t >= 30 && t < 36 ? 200 : t >= 70 ? 60 : 120, t)).ToArray());

    private static Message Of(params (string Key, string Json)[] ports) => new() { MessageId = "m", Ports = ports.ToDictionary(p => p.Key, p => p.Json) };
    private static VapAimProcessor Vap() => new("vap", Av, AudioIrr, VideoIrr, Images);
    private static string FilmObject() => AudioVisualObject.Make("av", new Audio(new short[Rate], Rate), Film());
    private static Irregularity A(double start, double end) => new(Guid.NewGuid().ToString(), "a", start, end);

    // ---- the film --------------------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ASplicePassingTheHeadAndAChangeOfTapeAreFoundAndACleanFilmHasNone()
    {
        var found = VideoAnalysis.Detect(Film());
        Assert.Equal(2, found.Count);
        Assert.InRange(found[0].Start, 2.85, 3.05); Assert.InRange(found[0].End, 3.55, 3.75);       // the splice: its two edges are one irregularity
        Assert.InRange(found[1].Start, 6.85, 7.05); Assert.InRange(found[1].End, 7.05, 7.25);       // the other tape
        Assert.Empty(VideoAnalysis.Detect(new VideoFrames(W, H, 10, Enumerable.Range(0, 100).Select(t => Frame(120, t)).ToArray())));
    }

    [Fact]
    public void AnImageIsAPngOfTheFrame()
    {
        var film = Film();
        var png = VideoAnalysis.ImageAt(film, 3.2);                                                    // frame 32: the splice
        var (w, h, pixels) = Png.Decode(png);
        Assert.Equal((W, H), (w, h));
        Assert.Equal(film.Frames[32], pixels);
        Assert.Equal([137, 80, 78, 71], png.Take(4));
    }

    // ---- the AIM --------------------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithTheFilmAndTheAudiosFileTheAimGivesTheMergedIrregularitiesInTheTimeOfTheRecordingAndTheirImages()
    {
        var vap = Vap();
        var waiting = await vap.ProcessAsync(Of((Av, FilmObject())));                                    // the film alone: it does not know where it lies in the recording yet
        Assert.False(waiting.Ports.ContainsKey(VideoIrr));

        // The Audio Analysis found two irregularities of the recording, at 4.2 s and at 8.0 s, and says the film starts 2.5 s into the recording.
        var a = A(4.2, 4.3); var b = A(8.0, 8.1);
        var reply = await vap.ProcessAsync(Of((AudioIrr, IrregularityFile.Make([a, b], OffsetMs))));
        Assert.False(reply.IsError, reply.Payload);
        Assert.True(Valid(["CAE1", "V2.5", "data", "IrregularityFile.json"], reply.Ports[VideoIrr]));
        Assert.True(Valid(["OSD", "V1.5", "data", "BasicVisualObject.json"], reply.Ports[Images]));
        var (list, offset) = IrregularityFile.Read(reply.Ports[VideoIrr]);
        Assert.Equal(OffsetMs, offset);
        Assert.Equal(["a", "v", "a", "v"], list.Select(i => i.Source));                                  // the audio's two and the film's two, in time order
        Assert.InRange(list[1].Start, 5.35, 5.45); Assert.InRange(list[1].End, 6.05, 6.25);              // the splice (2.9-3.7 s of the film), 2.5 s later: in the time of the recording
        Assert.InRange(list[3].Start, 9.35, 9.45);                                                         // the other tape (6.9 s of the film)
        Assert.Equal([a.Id, b.Id], new[] { list[0].Id, list[2].Id });

        var images = VisualObject.Images(reply.Ports[Images]);
        Assert.Equal(list.Select(i => i.Id), images.Select(i => i.Id));                                  // an image of each
        Assert.Equal("png", AudioObject.FileFormatOf(reply.Ports[Images], "VisualQualifier"));
        var film = Film();
        Assert.Equal(film.Frames[(int)Math.Round((4.25 - 2.5) * 10)], Png.Decode(images[0].Png).Pixels);   // at 4.25 s of the recording: 1.75 s of the film
        Assert.Contains("ImageURI", reply.Ports[VideoIrr]);
    }

    [Fact]
    public async Task AnIrregularityTheAudioAndTheFilmBothFoundIsOne()
    {
        var vap = Vap(); await vap.ProcessAsync(Of((Av, FilmObject())));
        var audio = A(5.5, 5.6);                                                                          // in the splice the film saw (5.4-6.2 s of the recording)
        var reply = await vap.ProcessAsync(Of((AudioIrr, IrregularityFile.Make([audio], OffsetMs))));
        var (list, _) = IrregularityFile.Read(reply.Ports[VideoIrr]);
        Assert.Equal(["b", "v"], list.Select(i => i.Source));                                            // the splice: both; the other tape: the film
        Assert.Equal(audio.Id, list[0].Id);                                                               // it keeps the identifier of the audio
        Assert.InRange(list[0].Start, 5.35, 5.6); Assert.InRange(list[0].End, 6.05, 6.25);               // and covers what both saw
    }

    [Fact]
    public async Task TheAimGivesAFileAgainOnlyIfItIsNotWhatItGaveLastSoTheLoopWithTheAudioAnalysisEnds()
    {
        var vap = Vap(); await vap.ProcessAsync(Of((Av, FilmObject())));
        var file = IrregularityFile.Make([A(4.2, 4.3)], OffsetMs);
        var first = await vap.ProcessAsync(Of((AudioIrr, file)));
        Assert.True(first.Ports.ContainsKey(VideoIrr));
        Assert.False((await vap.ProcessAsync(Of((AudioIrr, file)))).Ports.ContainsKey(VideoIrr));       // the same again: nothing
        Assert.False((await vap.ProcessAsync(Of((AudioIrr, first.Ports[VideoIrr])))).Ports.ContainsKey(VideoIrr));   // its own merged file back from the Audio Analysis: nothing
    }

    [Fact]
    public async Task WhatTheFilmDoesNotCoverHasNoImageAndTheAimSaysWhatItCannotAnalyse()
    {
        var vap = Vap(); await vap.ProcessAsync(Of((Av, FilmObject())));
        var reply = await vap.ProcessAsync(Of((AudioIrr, IrregularityFile.Make([A(1.0, 1.1), A(50, 51)], OffsetMs))));     // before the film (it starts at 2.5 s) and after it
        var (list, _) = IrregularityFile.Read(reply.Ports[VideoIrr]);
        var images = VisualObject.Images(reply.Ports[Images]).Select(i => i.Id).ToHashSet();
        Assert.Equal(2, images.Count);                                                                     // only the film's two have an image
        Assert.All(list.Where(i => i.Source == "a"), i => Assert.DoesNotContain(i.Id, images));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(reply.Ports[VideoIrr], "ImageURI").Count);

        var noVideo = await Vap().ProcessAsync(Of((Av, AudioVisualObject.Make("av", new Audio(new short[Rate], Rate)))));
        Assert.True(noVideo.IsError); Assert.Contains("has no video", noVideo.Payload);
        Assert.False((await Vap().ProcessAsync(Of((AudioIrr, IrregularityFile.Make([A(1, 2)], OffsetMs))))).Ports.ContainsKey(VideoIrr));      // audio's file before the film: it waits
    }
}
