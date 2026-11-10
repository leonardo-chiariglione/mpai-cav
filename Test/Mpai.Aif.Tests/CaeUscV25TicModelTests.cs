using System.Security.Cryptography;
using System.Text.Json;
using AIF.Controller;
using Mpai.Cae.Usc;
using Mpai.Cae.Usc.Arp;
using Mpai.Osd.Speech;

namespace Mpai.Aif.Tests;

// CAE-USC V2.5, CAE-TIC with its TRAINED CLASSIFIER (Test\Tools\TapeSim). The model was trained on SIMULATED impairments of public-domain / CC0 music: these tests show that the AIM runs it as
// it was trained (the model comes from Access, by the AccessSources of the L3, checked against its SHA-256), not that it classifies real tapes.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class CaeUscV25TicModelTests
{
    private const string L3 = "1CAE-TIC-V2.5-I01", Clips = "OSD-BAO-V1.5#1", AudioIrr = "CAE-IRR-V2.5#1", ImagesIn = "OSD-BVO-V1.5#1", VideoIrr = "CAE-IRR-V2.5#2", Irr = "CAE-IRR-V2.5#9", ImagesOut = "OSD-BVO-V1.5#9";
    private static readonly string Golden = Path.Combine(Repository.Root, "Test", "Data", "Music", "tic-golden");
    private static readonly string Dataset = Path.Combine(Repository.Root, "Test", "Data", "Music", "dataset");

    // What the Controller does for the AIM: obtains the model from Access (SHA-256 checked) and gives its place under the Setting.
    private static string ModelPath() => ModelFiles.SettingsOf(Repository.Root, L3, new Dictionary<string, string>())["TicModel"];

    private sealed record Case(string File, string Class, double[] Probabilities);
    private static List<Case> Cases() =>
        JsonSerializer.Deserialize<List<Case>>(File.ReadAllText(Path.Combine(Golden, "expected.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public void TheWindowIsCentredOnTheIrregularityAndTheMissingSamplesAreReflected()
    {
        Assert.Equal([0, 1, 2, 3, 2, 1, 0, 1], Enumerable.Range(0, 8).Select(i => TapeIrregularityModel.Reflect(i, 4)));
        Assert.Equal([2, 1, 0, 1, 2, 3, 2, 1], Enumerable.Range(-2, 8).Select(i => TapeIrregularityModel.Reflect(i, 4)));
        Assert.Equal(0, TapeIrregularityModel.Reflect(5, 1));
        var clip = Enumerable.Range(0, 2 * 16000).Select(i => (short)(i % 30000)).ToArray();                    // 2 s
        var w = TapeIrregularityModel.Window(clip, 16000);                                                      // centred in the middle of the clip: 1 s of reflection on each side
        Assert.Equal(64000, w.Length);
        Assert.Equal(clip[16000] / 32768f, w[32000]);                                                           // the centre is the sample asked for
        Assert.Equal(clip[1] / 32768f, w[32000 - 16000 - 1]);                                                   // one before the first sample is the second
        Assert.Equal(clip[^2] / 32768f, w[32000 + 16000]);                                                      // one past the last is the last but one
    }

    [Fact]
    public void TheL3DeclaresTheModelInItsAccessSourcesAndTheControllerGivesItUnderItsSetting()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository.Amds, L3 + ".json")));
        var entry = document.RootElement.GetProperty("AccessSources").EnumerateArray().Single();
        Assert.Equal("TicModel", entry.GetProperty("Setting").GetString());
        var source = new Uri(entry.GetProperty("Uri").GetString()!).LocalPath;
        Assert.Equal(entry.GetProperty("Sha256").GetString(), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))));
        var given = ModelPath();
        Assert.True(File.Exists(given));
        Assert.Equal(File.ReadAllBytes(source).Length, new FileInfo(given).Length);
    }

    // The model in the AIM gives what it gave in PyTorch (Test\Tools\TapeSim\make_golden.py): the same probabilities to 1e-3, on eight clips of pieces that were not in the training.
    [Fact]
    public void TheModelInTheAimGivesWhatItGaveWhereItWasTrained()
    {
        using var model = new TapeIrregularityModel(ModelPath());
        var cases = Cases();
        Assert.Equal(TapeIrregularityModel.Classes.Length, cases.Count);
        foreach (var c in cases)
        {
            var clip = Audio.FromWav(File.ReadAllBytes(Path.Combine(Golden, c.File)));
            var result = model.Classify(TapeIrregularityModel.Window(clip.Samples, clip.Samples.Length / 2));
            Assert.Equal(c.Class, result.Class);
            for (var k = 0; k < c.Probabilities.Length; k++) Assert.True(Math.Abs(c.Probabilities[k] - result.Probabilities[k]) < 1e-3, $"{c.File}: class {k}: {c.Probabilities[k]} in PyTorch, {result.Probabilities[k]} here");
        }
    }

    private static Message Of(params (string Key, string Json)[] ports) => new() { MessageId = "m", Ports = ports.ToDictionary(p => p.Key, p => p.Json) };
    private static string Id(int n) => $"00000000-0000-0000-0000-{n:000000000000}";

    // The Audio Analysis gives each irregularity its Audio File (the irregularity with a margin of 1 s on each side): here the clips are four seconds, the irregularity from 10 s to 12 s of the tape.
    // The AIM gives the irregularities of a type the model hears and does not select those it hears none in.
    [Fact]
    public async Task TheAimGivesEachIrregularityTheTypeTheModelHearsAndDoesNotSelectTheOnesItHearsNone()
    {
        using var model = new TapeIrregularityModel(ModelPath());
        var cases = Cases();
        var irregularities = cases.Select((c, k) => new Irregularity(Id(k + 1), "a", 10.0, 12.0)).ToList();
        var items = cases.Select((c, k) => new AudioObject.Item(Id(k + 1), Audio.FromWav(File.ReadAllBytes(Path.Combine(Golden, c.File))))).ToArray();
        var tic = new TicAimProcessor("tic", Clips, AudioIrr, ImagesIn, VideoIrr, Irr, ImagesOut, null, model, 0.0);
        var reply = await tic.ProcessAsync(Of((AudioIrr, IrregularityFile.Make(irregularities)), (Clips, AudioObject.Make("clips", items))));
        Assert.False(reply.IsError, reply.Payload);
        var types = IrregularityFile.Read(reply.Ports[Irr]).Irregularities.ToDictionary(i => i.Id, i => i.Type);
        for (var k = 0; k < cases.Count; k++)
        {
            if (cases[k].Class == "none") Assert.False(types.ContainsKey(Id(k + 1)), "an irregularity the model hears none in is selected");
            else Assert.Equal(cases[k].Class, types[Id(k + 1)]);
        }
    }

    [Fact]
    public async Task AnAnswerUnderTheMinimumProbabilityIsNotAnAnswerAndTheRulesDecide()
    {
        using var model = new TapeIrregularityModel(ModelPath());
        var cases = Cases();
        var c = cases.First(x => x.Class == "sb");                                                                                 // a type the rules cannot give
        var irregularity = new Irregularity(Id(1), "a", 10.0, 12.0);
        var clip = new AudioObject.Item(Id(1), Audio.FromWav(File.ReadAllBytes(Path.Combine(Golden, c.File))));
        var strict = new TicAimProcessor("tic", Clips, AudioIrr, ImagesIn, VideoIrr, Irr, ImagesOut, null, model, 1.1);          // no probability reaches it
        var reply = await strict.ProcessAsync(Of((AudioIrr, IrregularityFile.Make([irregularity])), (Clips, AudioObject.Make("clips", clip))));
        var types = reply.Ports.ContainsKey(Irr) ? IrregularityFile.Read(reply.Ports[Irr]).Irregularities.Select(i => i.Type).ToList() : [];
        Assert.DoesNotContain("sb", types);                                                                                       // not the model's answer
    }

    // The provider of the AIMs builds Tape Irregularity Classification with the model the Controller gave under the Setting (here by ModelFiles, which does what the Controller does), and the AIM
    // then hears what the model hears; without the Setting it has no model and classifies by the rules.
    [Fact]
    public async Task TheProviderBuildsTheAimWithTheModelTheControllerGaveUnderItsSetting()
    {
        var store = new AIF.Store.AmdStore(Repository.Amds); store.Scan();
        var provider = new UscProvider(store);
        var ports = AimPortReader.Load(store, L3);
        var sb = Cases().First(c => c.Class == "sb");                                                                                // a type only the model can give
        var clip = new AudioObject.Item(Id(1), Audio.FromWav(File.ReadAllBytes(Path.Combine(Golden, sb.File))));
        var message = Of((ports.Input("CAE-IRR-V2.5", 1), IrregularityFile.Make([new Irregularity(Id(1), "a", 10.0, 12.0)])), (ports.Input("OSD-BAO-V1.5"), AudioObject.Make("clips", clip)));

        var given = ModelFiles.SettingsOf(Repository.Root, L3, new Dictionary<string, string>());
        var heard = await provider.Create(L3, given, null).ProcessAsync(message);
        Assert.Equal("sb", IrregularityFile.Read(heard.Ports[ports.Output("CAE-IRR-V2.5")]).Irregularities.Single().Type);

        var ruled = await provider.Create(L3, new Dictionary<string, string>(), null).ProcessAsync(message);                              // no Setting: the rules, which cannot say a signal backward
        Assert.False(ruled.Ports.ContainsKey(ports.Output("CAE-IRR-V2.5")));
    }

    // On the held-out pieces of the dataset (when it has been built: Test\Tools\TapeSim\make_dataset.py), the AIM's path (window, ONNX) gives the accuracy the training reported.
    [Fact]
    public void OnTheTestPiecesTheAimsPathIsAsAccurateAsTheTrainingReported()
    {
        var clipsFile = Path.Combine(Dataset, "test_seen.npy");
        if (!File.Exists(clipsFile) || !File.Exists(Path.Combine(Dataset, "tic-report.json"))) return;                           // the dataset is not in the repository
        var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dataset, "test_seen.json"))).RootElement.EnumerateArray().Select(e => e.GetProperty("class").GetString()!).ToArray();
        var reported = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dataset, "tic-report.json"))).RootElement.GetProperty("test_seen").GetProperty("accuracy").GetDouble();
        using var stream = File.OpenRead(clipsFile);
        var header = new byte[10]; stream.ReadExactly(header);
        var headerLength = header[8] | header[9] << 8; stream.Seek(10 + headerLength, SeekOrigin.Begin);
        var start = stream.Position; const int length = 64000;
        using var model = new TapeIrregularityModel(ModelPath());
        int right = 0, total = 0;
        var buffer = new byte[length * 2];
        for (var k = 0; k < meta.Length; k += 8)
        {
            stream.Seek(start + (long)k * length * 2, SeekOrigin.Begin); stream.ReadExactly(buffer);
            var samples = new short[length]; Buffer.BlockCopy(buffer, 0, samples, 0, buffer.Length);
            total++; if (model.Classify(TapeIrregularityModel.Window(samples, length / 2)).Class == meta[k]) right++;
        }
        var accuracy = right / (double)total;
        Assert.True(accuracy >= reported - 0.06, $"accuracy {accuracy:0.000} on {total} clips; the training reported {reported:0.000}");
    }
}
