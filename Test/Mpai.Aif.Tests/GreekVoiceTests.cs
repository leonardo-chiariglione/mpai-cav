using System.Text;

using Mpai.Core;

namespace Mpai.Aif.Tests;

// GREEK SPEAKS (MMC-TTS-V2.5, Piper voice el_GR-rapunzelina-medium, declared in the L3 of TTS as "Voice:el"). A Greek sentence is given to the speech
// synthesis with the language 'el' and what comes out is measured: a sound, not silence, of a plausible length for the sentence. The sound is also
// written, when the folder exists, to the Downloads of the user so that a Greek speaker can listen to it. Whether it is good Greek is for that speaker to say.
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
public class GreekVoiceTests
{
    private const string Sentence = "Καλημέρα, αυτή είναι η περίληψη της συνάντησης.";

    [SkippableFact]
    public void AGreekSentenceIsSpokenByTheGreekVoice()
    {
        var settings = TestModels.Settings("1MMC-TTS-V2.5-I01");
        Skip.IfNot(settings.TryGetValue("PiperExecutable", out var piper) && File.Exists(piper) && settings.TryGetValue("Voice:el", out var voice) && File.Exists(voice),
                   "Piper or the Greek voice is absent: the model files are obtained separately.");

        var synthesizer = Mpai.Aims.Tts.TtsFactory.Create(settings);
        var qualifier = new TextQualifier { TextQualifierID = "voice-el", Attributes = new TextAttributes { Language = new Language { LanguageCode = "el" } } };
        var speech = synthesizer.ProcessAsync(BasicTextObject.FromText(Sentence, qualifier)).GetAwaiter().GetResult();

        var samples = Samples(speech.Data);
        var rate = BitConverter.ToInt32(speech.Data, 24);
        var seconds = samples.Length / (double)rate;
        var rms = Math.Sqrt(samples.Select(s => (double)s * s).Average()) / 32768;

        Assert.Equal(22050, rate);
        Assert.InRange(seconds, 1.5, 12);      // 47 characters: some three to four seconds
        Assert.True(rms >= 0.005, $"the sound is too quiet to be speech: loudness {rms:0.0000}");

        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads)) File.WriteAllBytes(Path.Combine(downloads, "greek-sample.wav"), speech.Data);
    }

    [Fact]
    public void GreekTextIsPlacedAsGreek() =>
        Assert.Equal("el", Mpai.Mmc.Cts.LanguageDetector.Detect(Sentence));

    private static short[] Samples(byte[] wav)
    {
        for (var pos = 12; pos + 8 <= wav.Length;)
        {
            var id = Encoding.ASCII.GetString(wav, pos, 4);
            var length = BitConverter.ToInt32(wav, pos + 4);
            if (id == "data")
            {
                var bytes = Math.Min(length, wav.Length - pos - 8);
                var s = new short[bytes / 2];
                Buffer.BlockCopy(wav, pos + 8, s, 0, s.Length * 2);
                return s;
            }
            pos += 8 + length + (length & 1);
        }
        return [];
    }
}
