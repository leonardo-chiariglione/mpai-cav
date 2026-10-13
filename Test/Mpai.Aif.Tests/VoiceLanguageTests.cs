using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Mpai.Core;

namespace Mpai.Aif.Tests;

// DOES EACH VOICE SPEAK ITS LANGUAGE? (the author, 2026/10/11: the voices of the settings are for en, pt, es, de, fr, it, ja and zh, and the author says six of them - not
// Chinese and Japanese - speak). Each voice is asked to say a sentence of its language; what comes out is measured (is it a sound, how long, how loud) and heard by
// speech recognition in that language, and the text heard is compared with the text said. The six are asserted; ja and zh are only reported: they stay in the settings,
// and the report, Test/Reports/voices-by-language.json, says whether they speak.
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
public class VoiceLanguageTests
{
    private static readonly (string Language, string Text)[] Said =
    [
        ("en", "Good morning. The concert starts at eight."),
        ("pt", "Bom dia. O concerto começa às oito."),
        ("es", "Buenos días. El concierto empieza a las ocho."),
        ("de", "Guten Morgen. Das Konzert beginnt um acht Uhr."),
        ("fr", "Bonjour. Le concert commence à huit heures."),
        ("it", "Buongiorno. Il concerto inizia alle otto."),
        ("ja", "おはようございます。コンサートは八時に始まります。"),
        ("zh", "早上好。音乐会八点开始。")
    ];
    private static readonly string[] Asserted = ["en", "pt", "es", "de", "fr", "it"];

    private static Dictionary<string, string> Settings(string aim)
    {
        var all = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Root, "AIMs", "aim-settings.json")))!;
        return all[aim]!.AsObject().ToDictionary(p => p.Key, p =>
        {
            var v = (string)p.Value!;
            return File.Exists(Path.Combine(Repository.Root, v)) ? Path.Combine(Repository.Root, v) : v;
        });
    }

    // The 16-bit samples of a WAV.
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

    // How alike two texts are, letters only, from 0 to 1: one minus the edit distance over the longer length.
    private static double Alike(string a, string b)
    {
        static string Plain(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        a = Plain(a); b = Plain(b);
        if (a.Length == 0 || b.Length == 0) return 0;
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return 1 - (double)d[a.Length, b.Length] / Math.Max(a.Length, b.Length);
    }

    [SkippableFact]
    [Trait("Duration", "Long")]
    public void EachVoiceIsHeardSayingItsLanguage()
    {
        var tts = Settings("1MMC-TTS-V2.5-I01");
        var asr = Settings("1MMC-ASR-V2.5-I01");
        Skip.IfNot(File.Exists(tts["PiperExecutable"]) && File.Exists(asr["ModelPath"]) && File.Exists(asr["ExecutablePath"]), "Piper or the Whisper model is absent: the model files are obtained separately.");

        var synthesizer = Mpai.Aims.Tts.TtsFactory.Create(tts);
        var report = new JsonObject();
        var failures = new List<string>();
        foreach (var (language, text) in Said)
        {
            var row = new JsonObject { ["Text"] = text };
            report[language] = row;
            try
            {
                var qualifier = new TextQualifier { TextQualifierID = "voice-" + language, Attributes = new TextAttributes { Language = new Language { LanguageCode = language } } };
                var speech = synthesizer.ProcessAsync(BasicTextObject.FromText(text, qualifier)).GetAwaiter().GetResult();
                var samples = Samples(speech.Data);
                var rate = speech.Data.Length > 28 ? BitConverter.ToInt32(speech.Data, 24) : 22050;
                var seconds = samples.Length / (double)Math.Max(rate, 1);
                var rms = samples.Length == 0 ? 0 : Math.Sqrt(samples.Select(s => (double)s * s).Average()) / 32768;
                row["Seconds"] = Math.Round(seconds, 2); row["Loudness"] = Math.Round(rms, 4);

                var settings = new Dictionary<string, string>(asr) { ["LanguageCode"] = language };
                var heard = Mpai.Aims.Asr.AsrFactory.Create(settings).ProcessAsync(speech).GetAwaiter().GetResult().GetText();
                var alike = Alike(text, heard);
                row["Heard"] = heard; row["Alike"] = Math.Round(alike, 2);
                var speaks = seconds >= 0.8 && rms >= 0.005 && alike >= 0.6;
                row["Speaks"] = speaks;
                if (!speaks && Asserted.Contains(language)) failures.Add($"{language}: {seconds:0.0} s, loudness {rms:0.000}, heard \"{heard}\" ({alike:0.00} alike)");
            }
            catch (Exception failure)
            {
                row["Speaks"] = false; row["Failure"] = failure.Message;
                if (Asserted.Contains(language)) failures.Add($"{language}: {failure.Message}");
            }
        }

        Directory.CreateDirectory(Path.Combine(Repository.Root, "Test", "Reports"));
        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "voices-by-language.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Assert.True(failures.Count == 0, "a voice does not speak its language: " + string.Join("; ", failures));
    }

    [Fact]
    public void TheTextsAreToldApartByHowAlikeTheyAre()
    {
        Assert.Equal(1.0, Alike("Good morning. The concert starts at eight.", "good morning the concert starts at eight"), 3);
        Assert.True(Alike("Good morning. The concert starts at eight.", "Buongiorno. Il concerto inizia alle otto.") < 0.5);
        Assert.Equal(0, Alike("abc", ""));
    }
}
