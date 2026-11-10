using AIF.Controller;
using AIF.Store;
using Mpai.Cae.Aii;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Aif.Tests;

// CAE-USC V2.5, THE TWO YAMNET AIMS ON THEIR OWN: Audio Instance Identification (CAE-AII: a Basic Audio Object in, an Instance Identifier out) and Audio Scene Identification
// (CAE-ASI: Basic Audio Scene Descriptors in; a Basic Speech Object if the sound is speech, else the Basic Audio Object). Sounds: speech and music of Test\Data; silence made here.
// NOT TESTED, for want of a recording in the repository (nothing is downloaded): a dog and a horn. A synthetic horn-like tone is only LOOKED AT (its classes are printed), not asserted.
[Trait("Group", "Fast")]
public class CaeUscV25AiiAsiTests
{
    private static string Models => Path.Combine(Repository.Root, "Models");
    private static readonly AmdStore Amds = Scan();
    private static AmdStore Scan() { var s = new AmdStore(Repository.Amds); s.Scan(); return s; }

    private static Mpai.Cae.Aii.SoundClassifier AiiClassifier() => new(Path.Combine(Models, "yamnet.onnx"), Path.Combine(Models, "yamnet_class_map.csv"));
    private static Mpai.Cae.Asi.SoundClassifier AsiClassifier() => new(Path.Combine(Models, "yamnet.onnx"), Path.Combine(Models, "yamnet_class_map.csv"));

    // Mono 16 kHz 16-bit samples from a PCM WAV (first channel; linear resampling), the first `seconds` seconds from `from`.
    private static short[] Wav(string path, double from = 0, double seconds = 4)
    {
        var b = File.ReadAllBytes(path);
        int rate = 0, channels = 1, bits = 16, dataAt = 0, dataLen = 0;
        for (var p = 12; p + 8 <= b.Length;)
        {
            var id = System.Text.Encoding.ASCII.GetString(b, p, 4); var len = BitConverter.ToInt32(b, p + 4);
            if (id == "fmt ") { channels = BitConverter.ToInt16(b, p + 10); rate = BitConverter.ToInt32(b, p + 12); bits = BitConverter.ToInt16(b, p + 22); }
            if (id == "data") { dataAt = p + 8; dataLen = Math.Min(len, b.Length - dataAt); break; }
            p += 8 + len + (len & 1);
        }
        Assert.Equal(16, bits);
        var frames = dataLen / (2 * channels);
        var n = (int)(seconds * 16000);
        var o = new short[n];
        for (var i = 0; i < n; i++)
        {
            var t = from + i / 16000.0; var pos = t * rate; var f = (int)pos; if (f + 1 >= frames) break;
            double At(int fr) => BitConverter.ToInt16(b, dataAt + fr * 2 * channels);
            o[i] = (short)(At(f) + (At(f + 1) - At(f)) * (pos - f));
        }
        return o;
    }

    private static BasicAudioObject Bao(short[] pcm) => new()
    {
        BasicAudioObjectID = "bao-1",
        BasicAudioObjectData = { new InlineAudioData(Convert.ToBase64String(pcm.SelectMany(s => BitConverter.GetBytes(s)).ToArray())) }
    };

    private static async Task<InstanceIdentifier> Identify(short[] pcm)
    {
        using var classifier = AiiClassifier();
        var ports = AimPortReader.Load(Amds, "1CAE-AII-V2.5-I01");
        var aim = new CaeAiiAimProcessor("1CAE-AII-V2.5-I01", classifier, ports);
        var reply = await aim.ProcessAsync(new Message { MessageId = "m", Ports = new Dictionary<string, string> { [ports.Input("OSD-BAO-V1.5")] = MpaiJson.ToJson(Bao(pcm)) } });
        Assert.False(reply.IsError, reply.Payload);
        return MpaiJson.FromJson<InstanceIdentifier>(reply.Ports[ports.Output("OSD-IID-V1.5")])!;
    }

    private static async Task<(BasicSpeechObject? Speech, BasicAudioObject? Audio)> Split(short[] pcm)
    {
        using var classifier = AsiClassifier();
        var ports = AimPortReader.Load(Amds, "1CAE-ASI-V2.5-I01");
        var aim = new Mpai.Cae.Asi.CaeAsiAimProcessor("1CAE-ASI-V2.5-I01", classifier, ports);
        var scene = new BasicAudioSceneDescriptors
        {
            BasicAudioSceneDescriptorsID = "bas-1", AudioObjectCount = 1,
            BasicAudioSceneDescriptorsEntries = { new BasicAudioSceneEntry { AudioObjectIDOrAudioObject = Bao(pcm) } }
        };
        var reply = await aim.ProcessAsync(new Message { MessageId = "m", Ports = new Dictionary<string, string> { [ports.Input("OSD-BAS-V1.5")] = MpaiJson.ToJson(scene) } });
        Assert.False(reply.IsError, reply.Payload);
        var speech = reply.Ports.TryGetValue(ports.Output("OSD-BSO-V1.5"), out var s) ? MpaiJson.FromJson<BasicSpeechObject>(s) : null;
        var audio = reply.Ports.TryGetValue(ports.Output("OSD-BAO-V1.5"), out var a) ? MpaiJson.FromJson<BasicAudioObject>(a) : null;
        return (speech, audio);
    }

    private static string Speech => Directory.GetFiles(Path.Combine(Repository.Root, "Test", "Data", "Speech"), "*.wav").Order().First();
    private static string Music => Path.Combine(Repository.Root, "Test", "Data", "Music", "guitar-el-noi-de-la-mare.wav");
    private static short[] Silence => new short[64000];

    [Fact]
    public void TheClassesTheModelGives()
    {
        using var c = AiiClassifier();
        var speech = c.Classify(Array.ConvertAll(Wav(Speech), s => s / 32768f), 5);
        var music = c.Classify(Array.ConvertAll(Wav(Music, 5), s => s / 32768f), 5);
        var silence = c.Classify(new float[64000], 5);
        Console.WriteLine("speech:  " + string.Join(", ", speech.Select(r => $"{r.Label} {r.Score:F2}")));
        Console.WriteLine("music:   " + string.Join(", ", music.Select(r => $"{r.Label} {r.Score:F2}")));
        Console.WriteLine("silence: " + string.Join(", ", silence.Select(r => $"{r.Label} {r.Score:F2}")));
        Assert.True(speech[0].IsSpeech, "speech: " + speech[0].Label);
        Assert.Contains(music.Take(3), r => r.Label.Contains("music", StringComparison.OrdinalIgnoreCase) || r.Label.Contains("guitar", StringComparison.OrdinalIgnoreCase) || r.Label.Contains("string", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Silence", silence[0].Label);
        Assert.All(speech, r => Assert.InRange(r.Score, 0f, 1f));
        // a horn-like tone (synthetic, 440 Hz with harmonics): looked at, not asserted
        var tone = Enumerable.Range(0, 64000).Select(i => (float)(0.3 * Math.Sin(2 * Math.PI * 440 * i / 16000) + 0.15 * Math.Sin(2 * Math.PI * 880 * i / 16000) + 0.1 * Math.Sin(2 * Math.PI * 1320 * i / 16000))).ToArray();
        Console.WriteLine("tone:    " + string.Join(", ", c.Classify(tone, 5).Select(r => $"{r.Label} {r.Score:F2}")));
    }

    [Fact]
    public async Task AudioInstanceIdentificationGivesTheTopClassOfTheSound()
    {
        var speech = await Identify(Wav(Speech));
        Assert.Equal("OSD-IID-V1.5", speech.Header);
        Assert.Contains("Speech", speech.InstanceIdentifier_, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(speech.InstanceIdentifier_, speech.InstanceIdentifierData[0].InstanceLabel);
        Assert.InRange(speech.InstanceIdentifierData[0].LabelConfidenceLevel, 0.1f, 1f);
        Assert.Equal("Silence", (await Identify(Silence)).InstanceIdentifier_);
        var music = await Identify(Wav(Music, 5));
        Assert.NotEqual("Speech", music.InstanceIdentifier_);
        // no samples: the coarse answer
        Assert.Equal("sound", (await Identify([])).InstanceIdentifier_);
    }

    [Fact]
    public async Task AudioSceneIdentificationSplitsSpeechFromOtherSounds()
    {
        var (speech, audioOfSpeech) = await Split(Wav(Speech));
        Assert.NotNull(speech); Assert.Null(audioOfSpeech);                       // speech -> Basic Speech Object
        Assert.Equal("OSD-BSO-V1.5", speech!.Header);
        Assert.Equal("bao-1", speech.BasicSpeechObjectID);
        Assert.Equal(128000, speech.Data.Length);                                // the audio's bytes are carried on

        var (noSpeech, audioOfMusic) = await Split(Wav(Music, 5));
        Assert.Null(noSpeech); Assert.NotNull(audioOfMusic);                      // music -> the Basic Audio Object, unchanged
        Assert.Equal("bao-1", audioOfMusic!.BasicAudioObjectID);

        var (s3, a3) = await Split(Silence);
        Assert.Null(s3); Assert.NotNull(a3);                                      // silence is not speech
    }
}
