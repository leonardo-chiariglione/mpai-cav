using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Mpai.Cae.Usc;
using Mpai.Cae.Usc.Arp;

namespace Mpai.Aif.Tests;

// CAE-USC V2.5, THE FOUR APPS, run as a person runs them: the program with its options, files in, files out. Each starts the Module of its use case (a Controller and, in a process of its own, the
// AIM host) and runs the User Agent on the workflow, so each takes some seconds; the one with the real voice needs the voice engine and is skipped where it is not installed.
[Trait("Group", "Slow")]
public class CaeUscV25AppTests : IDisposable
{
    private readonly string work = Path.Combine(Path.GetTempPath(), "cae-apps-" + Guid.NewGuid().ToString("N"));
    public CaeUscV25AppTests() => Directory.CreateDirectory(work);
    public void Dispose() { try { Directory.Delete(work, true); } catch { } }

    private static string Exe(string use, string name) => Path.Combine(Repository.Root, "Apps", "CAE", use, "App", "bin", "Debug", "net10.0", name + ".exe");

    private static (int Code, string Out, string Error) Run(string exe, params string[] args)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = Repository.Root };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(6))) { process.Kill(true); Assert.Fail("The App did not finish: " + output.Result); }
        return (process.ExitCode, output.Result, error.Result);
    }

    private string Wav(string name, double seconds, double hertz, double level)
    {
        var s = new short[(int)(seconds * 16000)];
        for (var i = 0; i < s.Length; i++) s[i] = (short)(level * Math.Sin(2 * Math.PI * hertz * i / 16000));
        var path = Path.Combine(work, name); File.WriteAllBytes(path, new Audio(s, 16000).ToWav()); return path;
    }

    private static double Seconds(string path) => Audio.FromWav(File.ReadAllBytes(path)).Seconds;

    // ---- Speech Restoration System ----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void SpeechRestorationSystemReplacesTheDamagedSectionsAndTheFileHasTheLengthItShould()
    {
        var voice = Wav("voice.wav", 2, 220, 6000); var damaged = Wav("damaged.wav", 3, 220, 9000); var output = Path.Combine(work, "restored.wav");
        var (code, text, error) = Run(Exe("SRS", "SrsApp"), "--voice", voice, "--damaged", damaged, "--sections", "0.5-1.0,2.0-2.5", "--say", "hello", "--say", "world wide", "--out", output, "--stand-in");
        Assert.True(code == 0, text + error);
        Assert.Equal(3 - 1.0 + 0.3 + 0.6, Seconds(output), 2);                                   // 3 s, less the two damaged sections, plus the speech for the two texts
        Assert.Equal(Audio.FromWav(File.ReadAllBytes(damaged)).Samples[100], Audio.FromWav(File.ReadAllBytes(output)).Samples[100]);          // outside the damaged sections it is the recording
    }

    [Fact]
    public void SpeechRestorationSystemSaysWhatIsWrongWithWhatItWasGiven()
    {
        var voice = Wav("voice.wav", 1, 220, 6000);
        var (code, _, error) = Run(Exe("SRS", "SrsApp"), "--voice", voice, "--damaged", voice, "--sections", "0.1-0.2,0.5-0.6", "--say", "only one", "--out", Path.Combine(work, "x.wav"));
        Assert.Equal(1, code); Assert.Contains("2 damaged section(s) and 1 --say", error);
        Assert.Equal(1, Run(Exe("SRS", "SrsApp"), "--voice", voice, "--damaged", voice, "--sections", "0.5-0.1", "--say", "x", "--out", Path.Combine(work, "x.wav")).Code);      // the end before the start
        Assert.Equal(1, Run(Exe("SRS", "SrsApp"), "--damaged", voice).Code);                                                                                                     // nothing else
    }

    [SkippableFact]
    public void SpeechRestorationSystemInTheVoiceOfTheSpeakerGivesTheWholeRestoredSegment()
    {
        Skip.IfNot(Directory.Exists(@"D:\AI\openvoice\converter") && File.Exists(@"D:\AI\pyannote-venv\Scripts\python.exe") && Directory.Exists(@"D:\AI\Datasets\libri_wav\1272"), "the voice engine or the clips are not installed");
        var output = Path.Combine(work, "voice.wav"); var damaged = @"D:\AI\Datasets\libri_wav\1462\clip.wav";
        var (code, text, error) = Run(Exe("SRS", "SrsApp"), "--voice", @"D:\AI\Datasets\libri_wav\1272\clip.wav", "--damaged", damaged, "--sections", "1.0-1.8,3.0-3.6", "--say", "hello there", "--say", "good morning", "--out", output);
        Assert.True(code == 0, text + error);
        var restored = Seconds(output); var original = Seconds(damaged);
        Assert.InRange(restored, original - 1.4 + 1.0, original - 1.4 + 4.0);                     // the recording less 1.4 s plus the two spoken texts
        Assert.True(File.ReadAllBytes(output).Length > 44);
    }

    // ---- Emotion Enhanced Speech -----------------------------------------------------------------------------------------------------------------------------------

    private string Syllables(string name, double hertz, double perSecond, double level)
    {
        var s = new short[3 * 16000];
        for (var i = 0; i < s.Length; i++) s[i] = (short)(level * Math.Pow(Math.Sin(Math.PI * perSecond * i / 16000), 2) * Math.Sin(2 * Math.PI * hertz * i / 16000));
        var path = Path.Combine(work, name); File.WriteAllBytes(path, new Audio(s, 16000).ToWav()); return path;
    }

    [Fact]
    public void EmotionEnhancedSpeechByTheEmotionAndByTheModelUtterance()
    {
        var neutral = Syllables("neutral.wav", 200, 3, 8000); var model = Syllables("model.wav", 300, 5, 16000);
        var sad = Path.Combine(work, "sad.wav");
        var (code, text, error) = Run(Exe("EES", "EesApp"), "--speech", neutral, "--emotion", "SADNESS", "--degree", "0.5", "--out", sad);
        Assert.True(code == 0, text + error);
        var heard = Audio.FromWav(File.ReadAllBytes(sad)); Assert.InRange(heard.Seconds, 2.5, 4.5);
        var level = (string wav) => Math.Sqrt(Audio.FromWav(File.ReadAllBytes(wav)).Samples.Average(v => (double)v * v));
        Assert.True(level(sad) < level(neutral), "sadness is quieter");                                  // the features of sadness: lower level (the test of the Module says by how much)

        var prosodic = Path.Combine(work, "prosodic.wav");
        (code, text, error) = Run(Exe("EES", "EesApp"), "--speech", neutral, "--model", model, "--out", prosodic);
        Assert.True(code == 0, text + error);
        Assert.True(level(prosodic) > level(neutral), "the model utterance is louder, and the speech takes its level");

        Assert.Equal(1, Run(Exe("EES", "EesApp"), "--speech", neutral, "--out", Path.Combine(work, "z.wav")).Code);                   // no emotion and no model
    }

    // ---- Audio Recording Preservation -----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AudioRecordingPreservationKeepsTheTapeAsItIsFindsItsIrregularitiesAndGivesTheAccessCopy()
    {
        var rate = 16000; var rng = new Random(1); var s = new short[10 * rate];
        for (var i = 0; i < s.Length; i++) s[i] = (short)(3000 * Math.Sin(2 * Math.PI * 220 * i / rate) + rng.Next(-1500, 1500));
        for (var i = (int)(3.0 * rate); i < (int)(3.8 * rate); i++) s[i] = (short)(s[i] / 100);          // a dropout
        s[6 * rate] = 25000;                                                                                // a click
        var tape = Path.Combine(work, "tape.wav"); File.WriteAllBytes(tape, new Audio(s, rate).ToWav());
        var output = Path.Combine(work, "out");

        var (code, text, error) = Run(Exe("ARP", "ArpApp"), "--tape", tape, "--out", output);
        Assert.True(code == 0, text + error);
        Assert.Equal(File.ReadAllBytes(tape), File.ReadAllBytes(Path.Combine(output, "master", "preservation-audio.wav")));       // the Master holds the tape as it is
        var (list, _) = IrregularityFile.Read(File.ReadAllText(Path.Combine(output, "master", "irregularities.json")));
        Assert.True(list.Count >= 2, "the dropout and the click");
        var found = string.Join(" ", list.Select(i => $"{i.Type}@{i.Start:0.00}-{i.End:0.00}"));
        Assert.True(list.Any(i => i.Start is > 2.5 and < 3.1), "the dropout (3.0-3.8 s): " + found); Assert.True(list.Any(i => i.Start is > 5.9 and < 6.1), "the click (6 s): " + found);
        Assert.All(list, i => Assert.Equal("da", i.Type));                                                  // the audio alone can say damaged tape
        Assert.True(File.Exists(Path.Combine(output, "master", "manifest.json")));
        Assert.Equal(File.ReadAllBytes(tape), File.ReadAllBytes(Path.Combine(output, "access-copy.wav")));         // nothing said a speed or an equalisation was wrong: nothing was restored
    }

    [Fact]
    public void AudioRecordingPreservationRefusesWhatItCannotUse()
    {
        Assert.Equal(1, Run(Exe("ARP", "ArpApp"), "--tape", Path.Combine(work, "none.wav"), "--out", work).Code);                       // no such file
        var tape = Wav("tape.wav", 1, 220, 3000);
        var (code, _, error) = Run(Exe("ARP", "ArpApp"), "--tape", tape, "--film-frames", work, "--out", work);
        Assert.Equal(1, code); Assert.Contains("--film-frames needs --film-audio", error);
    }

    // ---- Enhanced Audioconference Experience ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void EnhancedAudioconferenceExperienceFindsTheTwoSpeakersOfTheDemoRoomWhereAndWhenTheySpeak()
    {
        var room = Path.Combine(work, "room"); var output = Path.Combine(work, "out");
        Assert.Equal(0, Run(Exe("EAE", "EaeApp"), "--demo", room).Code);
        var (code, text, error) = Run(Exe("EAE", "EaeApp"), "--capture", Path.Combine(room, "room.wav"), "--array", Path.Combine(room, "array.json"), "--out", output);
        Assert.True(code == 0, text + error);
        var sources = SceneDescriptors.Read(File.ReadAllText(Path.Combine(output, "scene.json")));
        Assert.Equal(2, sources.Count);
        var one = sources.Single(x => Math.Abs(x.Azimuth - 60) < 8 && Math.Abs(x.Elevation - 20) < 8);                      // 60 degrees, a little above, 1 kHz, 0 to 3.5 s
        var two = sources.Single(x => Math.Abs(x.Azimuth + 90) < 8 && Math.Abs(x.Elevation) < 8);                           // 90 degrees to the right, 2 kHz, 2.5 to 6 s
        Assert.InRange(one.Active[0].Start, 0, 0.3); Assert.InRange(one.Active[^1].End, 3.3, 3.8);
        Assert.InRange(two.Active[0].Start, 2.3, 2.8); Assert.InRange(two.Active[^1].End, 5.8, 6.05);
        foreach (var name in new[] { "source-1.wav", "source-2.wav" }) Assert.InRange(Seconds(Path.Combine(output, name)), 5.99, 6.01);      // as long as the capture
    }

    [Fact]
    public void EnhancedAudioconferenceExperienceSaysWhatIsWrongWithWhatItWasGiven()
    {
        var room = Path.Combine(work, "room");
        Run(Exe("EAE", "EaeApp"), "--demo", room);
        Assert.Equal(1, Run(Exe("EAE", "EaeApp"), "--capture", Path.Combine(room, "none.wav"), "--array", Path.Combine(room, "array.json"), "--out", work).Code);
        // a mono recording for an array of six microphones
        var mono = Wav("mono.wav", 1, 220, 3000);
        var (code, _, error) = Run(Exe("EAE", "EaeApp"), "--capture", mono, "--array", Path.Combine(room, "array.json"), "--out", Path.Combine(work, "o"));
        Assert.Equal(1, code); Assert.Contains("6 microphones and the capture 1 channels", error);
    }
}