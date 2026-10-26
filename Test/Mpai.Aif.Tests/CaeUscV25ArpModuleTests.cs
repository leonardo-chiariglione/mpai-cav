using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Cae.Usc;
using Mpai.Cae.Usc.Arp;

namespace Mpai.Aif.Tests;

// AUDIO RECORDING PRESERVATION AS A MODULE under a Controller, the two apart (the Controller and the Module are always separate): its five SubAIMs execute in a process of their own
// (AIF.AimHost), wired as the L3 says - Continuous execution, with the loop between the Audio Analysis and the Video Analysis. The User Agent gives it the digitised audio of a tape and
// the film of the playback head; the Module finds the irregularities, classifies them and packages the recording. What it gives settles as the analyses exchange their files, so the User
// Agent reads the Port until it holds what it looks for.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class CaeUscV25ArpModuleTests : IDisposable
{
    private const string Arp = "1CAE-ARP-V2.5-I01";
    private static readonly string[] Aims = ["1CAE-AAP-V2.5-I01", "1CAE-VAP-V2.5-I01", "1CAE-TIC-V2.5-I01", "1CAE-TAR-V2.5-I01", "1CAE-PAP-V2.5-I01"];
    private const int Rate = 16000;
    private readonly string work = Path.Combine(Path.GetTempPath(), "cae-arp-" + Guid.NewGuid().ToString("N"));
    private readonly HostProcess process;
    private readonly ControllerApi api;

    public CaeUscV25ArpModuleTests()
    {
        Directory.CreateDirectory(work);
        var amds = Path.Combine(work, "module-amds"); Directory.CreateDirectory(amds);
        foreach (var file in Directory.EnumerateFiles(Repository.Amds, "*.json")) File.Copy(file, Path.Combine(amds, Path.GetFileName(file)));
        var l3 = File.ReadAllText(Path.Combine(amds, Arp + ".json"));
        foreach (var aim in Aims)
            l3 = System.Text.RegularExpressions.Regex.Replace(l3, "(\"AIMName\"\\s*:\\s*\"" + aim + "\"\\s*,\\s*\"Relation\"\\s*:\\s*)\"Internal\"", "$1\"External\"");
        File.WriteAllText(Path.Combine(amds, Arp + ".json"), l3);
        var settings = Path.Combine(work, "aim-settings.json"); File.WriteAllText(settings, "{}");
        process = new HostProcess(amds: amds, provider: Path.Combine(AppContext.BaseDirectory, "Mpai.Cae.Usc.dll") + ":Mpai.Cae.Usc.UscProvider",
                                  extra: ["--settings", settings], workingDirectory: Repository.Root);
        api = new ControllerApi(amds, settings, _ => new CompositeProvider());
        api.Controller.AimHostKey = HostProcess.Key;
        foreach (var aim in Aims) api.Controller.AimHosts[aim] = process.Address;
        Assert.Equal(AifError.OK, api.StartFlow(Arp));
    }

    public void Dispose() { api.Dispose(); process.Dispose(); try { Directory.Delete(work, true); } catch { } }

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

    private static byte[] Frame(int brightness, int seed)
    {
        var f = new byte[32 * 24]; var state = (uint)seed * 2654435761u + 12345u;
        for (var i = 0; i < f.Length; i++) { state = state * 1664525u + 1013904223u; f[i] = (byte)Math.Clamp(brightness + (int)(state >> 24) % 11 - 5, 0, 255); }
        return f;
    }

    // Read a boundary Output Port until what it gave is what the test looks for, or the time is up: the last thing read is returned.
    private string? ReadUntil(string type, int number, Func<string, bool> found, int seconds = 60)
    {
        var clock = Stopwatch.StartNew(); string? last = null;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            var read = api.OutputRead(Arp, type, number, 3_000);
            if (read.Error != AifError.OK) continue;
            last = read.Json!;
            if (found(last)) return last;
        }
        return last;
    }

    [Fact]
    public void TheModuleFindsTheIrregularitiesOfTheTapeClassifiesThemAndPackagesTheRecording()
    {
        var tape = IrregularTape();
        var film = new VideoFrames(32, 24, 10, Enumerable.Range(0, 100).Select(t => Frame(t >= 30 && t < 36 ? 200 : t >= 70 ? 60 : 120, t)).ToArray());      // a splice at 2.9-3.7 s, a new tape at 6.9 s
        var av = AudioVisualObject.Make("av", new Audio(tape.Samples[(int)(2.5 * Rate)..], Rate), film);                                                  // the film starts 2.5 s into the recording
        Assert.Equal(AifError.OK, api.InputWrite(Arp, "OSD-BAO-V1.5", 1, AudioObject.Make("pres", tape), 20_000));
        Assert.Equal(AifError.OK, api.InputWrite(Arp, "OSD-BMO-V1.5", 1, av, 20_000));

        // The Preservation Master: it settles when it holds the irregularities the analyses agree on (the dropout, the splice, the click and the change of tape, with the step in level).
        static bool Complete(string json)
        {
            var files = AudioObject.Files(json, "BasicAudioObjectData").Where(f => f.Id is not null).ToDictionary(f => f.Id!, f => f.Bytes);
            if (!files.TryGetValue("irregularities.json", out var irr)) return false;
            var (list, _) = IrregularityFile.Read(Encoding.UTF8.GetString(irr));
            return list.Count == 4 && files.Keys.Count(k => k.StartsWith("irregularity-image-")) == 4;
        }
        var master = ReadUntil("OSD-BAO-V1.5", 2, Complete);
        if (master is null) Assert.Fail("nothing came at the Preservation Master Files");
        if (!Complete(master!))
        {
            var held = AudioObject.Files(master!, "BasicAudioObjectData").Where(f => f.Id is not null).ToDictionary(f => f.Id!, f => f.Bytes);
            var what = string.Join(", ", held.Keys) + (held.TryGetValue("irregularities.json", out var irr0) ? " | " + string.Join(" ", IrregularityFile.Read(Encoding.UTF8.GetString(irr0)).Irregularities.Select(i => $"{i.Source}:{i.Type}@{i.Start:0.0}")) : "");
            Assert.Fail("the Preservation Master Files never held the four classified irregularities and their images: " + what);
        }

        var files = AudioObject.Files(master!, "BasicAudioObjectData").ToDictionary(f => f.Id!, f => f.Bytes);
        Assert.Equal(tape.ToWav(), files["preservation-audio.wav"]);                                                  // the digitised audio as it is
        Assert.Contains("preservation-audio-visual.bin", files.Keys);
        var (list, offset) = IrregularityFile.Read(Encoding.UTF8.GetString(files["irregularities.json"]));
        Assert.InRange(offset!.Value, 2499, 2501);                                                                     // milliseconds
        Assert.Equal(["da", "sp", "da", "sp"], list.Select(i => i.Type));                                              // the dropout, the splice, the click, the change of tape
        Assert.Equal(["a", "v", "a", "b"], list.Select(i => i.Source));
        var manifest = JsonNode.Parse(Encoding.UTF8.GetString(files["manifest.json"]))!["Files"]!.AsArray();
        Assert.Equal(files.Keys.Count - 1, manifest.Count);                                                           // every file but the manifest is in it

        // The Access Copy: nothing in the irregularities says a speed or an equalisation was wrong, so nothing was restored: it is the recording.
        var access = ReadUntil("OSD-BAO-V1.5", 1, _ => true, 20);
        Assert.NotNull(access);
        Assert.Equal(tape.ToWav(), AudioObject.Files(access!, "BasicAudioObjectData").Single().Bytes);
    }
}
