using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.Metadata;
using Json.Schema;
using Mpai.Cae.Usc;
using Mpai.Cae.Usc.Arp;

namespace Mpai.Aif.Tests;

// CAE-USC V2.5, Audio Recording Preservation: PACKAGING FOR AUDIO PRESERVATION. The Preservation Master keeps the digitised audio as it is, with what documents it and a manifest of
// SHA-256 hashes; the Access Copy is the audio with each restored section in the place of the section it restores.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class CaeUscV25PapTests
{
    private const string Audio1 = "OSD-BAO-V1.5#1", Restored = "OSD-BAO-V1.5#2", Els = "CAE-ELS-V2.5#1", Irr = "CAE-IRR-V2.5#1", Images = "OSD-BVO-V1.5#1", Av = "OSD-BMO-V1.5#1";
    private const string Access = "OSD-BAO-V1.5#8", Master = "OSD-BAO-V1.5#9";
    private const int Rate = 48000;

    private static bool Valid(string[] schema, string json)
    {
        var root = Path.GetFullPath(Repository.Schemas);
        var compiled = PublishedSchemas.At(root)[Path.Combine([root, .. schema])];
        using var document = JsonDocument.Parse(json);
        lock (PublishedSchemas.Lock) return compiled.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;
    }

    private static Audio Constant(double seconds, short value) => new(Enumerable.Repeat(value, (int)(seconds * Rate)).ToArray(), Rate);
    private static Message Of(params (string Key, string Json)[] ports) => new() { MessageId = "m", Ports = ports.ToDictionary(p => p.Key, p => p.Json) };
    private static PapAimProcessor Pap() => new("pap", Audio1, Restored, Els, Irr, Images, Av, Access, Master);
    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static Dictionary<string, byte[]> Files(string json) => AudioObject.Files(json, "BasicAudioObjectData").ToDictionary(f => f.Id!, f => f.Bytes);

    // A 24-bit, 96 kHz, stereo WAV file: what a digitisation of a tape often is, and what these AIMs do not decode.
    private static byte[] Wav24()
    {
        var data = Enumerable.Range(0, 96000 * 2 * 3).Select(i => (byte)(i * 7)).ToArray();
        using var m = new MemoryStream(); using var w = new BinaryWriter(m);
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + data.Length); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(96000); w.Write(96000 * 6); w.Write((short)6); w.Write((short)24);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(data.Length); w.Write(data);
        return m.ToArray();
    }

    private static string Object(string header, string idName, string dataName, params byte[][] files) => new JsonObject
    {
        ["Header"] = header, [idName] = "o", [dataName] = new JsonArray(files.Select(f => (JsonNode)new JsonObject { ["Data"] = Convert.ToBase64String(f) }).ToArray())
    }.ToJsonString();

    [Fact]
    public async Task TheMasterKeepsTheDigitisedAudioAsItIsEvenWhenItIsNotWhatTheAimsDecode()
    {
        var original = Wav24();
        var audio = new JsonObject
        {
            ["Header"] = "OSD-BAO-V1.5", ["BasicAudioObjectID"] = "pres", ["BasicAudioObjectData"] = new JsonArray(new JsonObject { ["Data"] = Convert.ToBase64String(original) }),
            ["AudioQualifier"] = new JsonObject { ["Header"] = "TFA-AUQ-V1.5", ["AudioQualifierID"] = "q", ["SubTypes"] = "Music", ["Formats"] = new JsonObject
                { ["TransportFormat"] = new JsonObject { ["FileFormats"] = "WAV" } } }
        }.ToJsonString();
        var reply = await Pap().ProcessAsync(Of((Audio1, audio)));
        Assert.False(reply.IsError, reply.Payload);
        Assert.True(Valid(["OSD", "V1.5", "data", "BasicAudioObject.json"], reply.Ports[Master]));
        Assert.True(Valid(["OSD", "V1.5", "data", "BasicAudioObject.json"], reply.Ports[Access]));

        var master = Files(reply.Ports[Master]);
        Assert.Equal(original, master["preservation-audio.wav"]);                              // byte for byte
        var manifest = JsonNode.Parse(Encoding.UTF8.GetString(master["manifest.json"]))!["Files"]!.AsArray();
        Assert.Equal(Sha(original), manifest.Single(f => f!["Name"]!.GetValue<string>() == "preservation-audio.wav")!["SHA256"]!.GetValue<string>());
        Assert.Equal(original, Files(reply.Ports[Access])["access-copy.wav"]);                 // nothing restored: the access copy is a copy
    }

    [Fact]
    public async Task TheMasterHoldsTheFilesThatDocumentTheRecordingAndAManifestOfTheirHashesAndNothingRestored()
    {
        var original = new Audio(Constant(2, 100).Samples, Rate).ToWav();
        var avFile = Encoding.ASCII.GetBytes("film of the playback head"); var imageA = new byte[] { 1, 2, 3 }; var imageB = new byte[] { 4, 5, 6, 7 };
        var irregularities = "{\"Header\":\"CAE-IRR-V2.5\",\"Irregularities\":[]}"; var list = "{\"Header\":\"CAE-ELS-V2.5\"}";
        var reply = await Pap().ProcessAsync(Of((Audio1, SpeechOrAudio(original)),
            (Av, Object("OSD-BMO-V1.5", "BasicAudioVisualObjectID", "BasicAudioVisualObjectData", avFile)),
            (Irr, irregularities),
            (Images, Object("OSD-BVO-V1.5", "BasicVisualObjectID", "BasicVisualObjectData", imageA, imageB)),
            (Els, list)));
        Assert.False(reply.IsError, reply.Payload);
        var master = Files(reply.Ports[Master]);
        Assert.Equal(["preservation-audio.wav", "preservation-audio-visual.bin", "irregularities.json", "irregularity-image-1.img", "irregularity-image-2.img", "editing-list.json", "manifest.json"], master.Keys);
        Assert.Equal(avFile, master["preservation-audio-visual.bin"]);
        Assert.Equal(imageB, master["irregularity-image-2.img"]);
        // the manifest names every file but itself, with its size and its hash: a later reader can check that nothing changed
        var manifest = JsonNode.Parse(Encoding.UTF8.GetString(master["manifest.json"]))!["Files"]!.AsArray();
        Assert.Equal(master.Keys.Where(k => k != "manifest.json"), manifest.Select(f => f!["Name"]!.GetValue<string>()));
        foreach (var f in manifest)
        {
            var name = f!["Name"]!.GetValue<string>();
            Assert.Equal(master[name].Length, f["Bytes"]!.GetValue<int>());
            Assert.Equal(Sha(master[name]), f["SHA256"]!.GetValue<string>());
        }
    }

    private static string SpeechOrAudio(byte[] wav) => AudioObjectOfBytes(wav);
    private static string AudioObjectOfBytes(byte[] wav) => new JsonObject
    {
        ["Header"] = "OSD-BAO-V1.5", ["BasicAudioObjectID"] = "pres", ["BasicAudioObjectData"] = new JsonArray(new JsonObject { ["Data"] = Convert.ToBase64String(wav) })
    }.ToJsonString();

    [Fact]
    public async Task TheAccessCopyHasTheRestoredSectionInThePlaceOfTheSectionItRestores()
    {
        // From 1 s to 2 s the tape was read at twice the speed it was written at: the restored section is the second, at half the rate (it lasts two seconds when played right).
        var original = new Audio(Enumerable.Range(0, 4 * Rate).Select(i => (short)(i < Rate ? 100 : i < 2 * Rate ? 200 : 300)).ToArray(), Rate);
        var id = Guid.NewGuid().ToString();
        var restoredSection = new Audio(Enumerable.Repeat((short)-5, Rate).ToArray(), Rate / 2);
        var list = new JsonObject { ["Header"] = "CAE-ELS-V2.5", ["OriginalSamplingFrequency"] = Rate, ["Restorations"] = new JsonArray(new JsonObject
        {
            ["RestorationID"] = id, ["PreservationAudioFileStart"] = "00:00:01.000", ["PreservationAudioFileEnd"] = "00:00:02.000", ["RestoredAudioFileURI"] = "urn:mpai:cae:tar:restored:" + id,
            ["ReadingBackwards"] = false, ["AppliedSamplingFrequency"] = Rate / 2 }) }.ToJsonString();
        var reply = await Pap().ProcessAsync(Of((Audio1, AudioObject.Make("pres", original)), (Restored, AudioObject.Make("rest", new AudioObject.Item(id, restoredSection))), (Els, list)));
        Assert.False(reply.IsError, reply.Payload);
        var access = Audio.FromWav(Files(reply.Ports[Access])["access-copy.wav"]);
        Assert.Equal(Rate, access.Rate);
        Assert.Equal(5.0, access.Seconds, 3);                                                   // 1 s, the restored section (2 s), 2 s
        Assert.Equal(100, access.Samples[Rate / 2]);                                            // before the section: the recording
        Assert.Equal(-5, access.Samples[Rate + Rate / 2]);                                      // in the section: the restored audio
        Assert.Equal(300, access.Samples[3 * Rate + Rate / 2]);                                 // after it: the recording again
        // the master has the recording without any restoration
        Assert.Equal(original.ToWav(), Files(reply.Ports[Master])["preservation-audio.wav"]);
        Assert.DoesNotContain(Files(reply.Ports[Master]).Keys, k => k.Contains("restored"));
    }

    [Fact]
    public async Task ThePackagerKeepsWhatComesAndPackagesAgainOnlyWhenWhatItHoldsHasChanged()
    {
        var pap = Pap(); var original = new Audio(Constant(2, 100).Samples, Rate).ToWav();
        var audio = AudioObject.Make("pres", new Audio(Constant(2, 100).Samples, Rate));
        Assert.False((await pap.ProcessAsync(Of((Irr, "{\"Header\":\"CAE-IRR-V2.5\",\"Irregularities\":[]}")))).Ports.ContainsKey(Master));       // the audio is still to come
        var first = await pap.ProcessAsync(Of((Audio1, audio)));                                                                                    // the audio: the master has it and the file that came before
        Assert.Contains("irregularities.json", Files(first.Ports[Master]).Keys);
        Assert.False((await pap.ProcessAsync(Of((Audio1, audio)))).Ports.ContainsKey(Master));                                                     // the same again: nothing new
        var more = await pap.ProcessAsync(Of((Els, "{\"Header\":\"CAE-ELS-V2.5\"}")));                                                             // the editing list: the master has one more file
        Assert.Contains("editing-list.json", Files(more.Ports[Master]).Keys);
        Assert.Equal(original, Files(more.Ports[Master])["preservation-audio.wav"]);
    }

    [Fact]
    public async Task ThePackagerSaysWhatItCannotPackage()
    {
        Assert.False((await Pap().ProcessAsync(Of())).Ports.ContainsKey(Master));                                         // no Preservation Audio File yet: nothing
        var id = Guid.NewGuid().ToString();
        string List(string start, string end) => new JsonObject { ["Header"] = "CAE-ELS-V2.5", ["Restorations"] = new JsonArray(new JsonObject
            { ["RestorationID"] = id, ["PreservationAudioFileStart"] = start, ["PreservationAudioFileEnd"] = end }) }.ToJsonString();
        var audio = AudioObject.Make("pres", Constant(3, 1));
        var missing = await Pap().ProcessAsync(Of((Audio1, audio), (Restored, AudioObject.Make("rest", Constant(1, 2))), (Els, List("00:00:01.000", "00:00:02.000"))));   // the restored file has no ID
        Assert.True(missing.IsError); Assert.Contains("whose restored audio file is not among", missing.Payload);
        var notClock = await Pap().ProcessAsync(Of((Audio1, audio), (Restored, AudioObject.Make("rest", new AudioObject.Item(id, Constant(1, 2)))), (Els, List("1s", "2s"))));
        Assert.True(notClock.IsError); Assert.Contains("hh:mm:ss.mmm", notClock.Payload);
    }
}
