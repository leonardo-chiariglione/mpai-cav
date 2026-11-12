using System.Text.Json;
using AIF.Controller;
using Mpai.Cae.Usc;
using Mpai.Cae.Usc.Arp;
using Mpai.Osd.Speech;

namespace Mpai.Aif.Tests;

// CAE-USC V2.5, CAE-TIC: THE RULES AGAINST THE MODEL on the same clips (N-12). On the held-out pieces of the SIMULATED dataset (Test\Tools\TapeSim; when it has been built), every
// eighth clip of test_seen and test_unseen goes through the AIM three times: with no model (the rules alone), with the model and no minimum probability (the model alone), and as it is
// deployed (the model, the rules under its MinProbability). What each gives is compared with the impairment the simulator made ('none' = the irregularity is not selected). The numbers are
// written to rules-vs-model.json next to the dataset. Simulated impairments of music, not real tapes.
[Trait("Group", "Fast")]
public class CaeUscV25TicRulesVersusModelTests
{
    private const string L3 = "1CAE-TIC-V2.5-I01", Clips = "OSD-BAO-V1.5#1", AudioIrr = "CAE-IRR-V2.5#1", ImagesIn = "OSD-BVO-V1.5#1", VideoIrr = "CAE-IRR-V2.5#2", Irr = "CAE-IRR-V2.5#9", ImagesOut = "OSD-BVO-V1.5#9";
    private static readonly string Dataset = Path.Combine(Repository.Root, "Test", "Data", "Music", "dataset");
    private const string Id = "00000000-0000-0000-0000-000000000001";

    private static async Task<string> TypeGiven(TicAimProcessor aim, short[] samples)
    {
        var irregularity = new Irregularity(Id, "a", 10.0, 12.0);                                                     // the clip is the irregularity with 1 s on each side, as the Audio Analysis gives it
        var clip = new AudioObject.Item(Id, new Audio(samples, 16000));
        var message = new Message { MessageId = "m", Ports = new Dictionary<string, string> { [AudioIrr] = IrregularityFile.Make([irregularity]), [Clips] = AudioObject.Make("clips", clip) } };
        var reply = await aim.ProcessAsync(message);
        return reply.Ports.TryGetValue(Irr, out var irr) ? IrregularityFile.Read(irr).Irregularities.Single().Type ?? "none" : "none";
    }

    [Fact]
    public async Task OnTheSameClipsTheDeployedAimIsAtLeastAsRightAsTheRules()
    {
        if (!File.Exists(Path.Combine(Dataset, "test_seen.npy"))) return;                                                  // the dataset is not in the repository
        var model = new TapeIrregularityModel(ModelFiles.SettingsOf(Repository.Root, L3, new Dictionary<string, string>())["TicModel"]);
        try
        {
            var report = new Dictionary<string, object>();
            foreach (var set in new[] { "test_seen", "test_unseen" })
            {
                var truth = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dataset, set + ".json"))).RootElement.EnumerateArray().Select(e => e.GetProperty("class").GetString()!).ToArray();
                using var stream = File.OpenRead(Path.Combine(Dataset, set + ".npy"));
                var header = new byte[10]; stream.ReadExactly(header);
                var start = 10 + (header[8] | header[9] << 8); const int length = 64000;
                var buffer = new byte[length * 2];
                var ways = new Dictionary<string, Func<TicAimProcessor>>
                {
                    ["rules"] = () => new TicAimProcessor("tic", Clips, AudioIrr, ImagesIn, VideoIrr, Irr, ImagesOut),
                    ["model"] = () => new TicAimProcessor("tic", Clips, AudioIrr, ImagesIn, VideoIrr, Irr, ImagesOut, null, model, 0.0),
                    ["aim"] = () => new TicAimProcessor("tic", Clips, AudioIrr, ImagesIn, VideoIrr, Irr, ImagesOut, null, model),
                };
                var right = ways.Keys.ToDictionary(k => k, _ => 0);
                var perClass = ways.Keys.ToDictionary(k => k, _ => new Dictionary<string, int[]>());               // class -> [right, total]
                var total = 0;
                for (var k = 0; k < truth.Length; k += 8)
                {
                    stream.Seek(start + (long)k * length * 2, SeekOrigin.Begin); stream.ReadExactly(buffer);
                    var samples = new short[length]; Buffer.BlockCopy(buffer, 0, samples, 0, buffer.Length);
                    total++;
                    foreach (var (name, make) in ways)
                    {
                        var given = await TypeGiven(make(), samples);
                        var c = perClass[name].TryGetValue(truth[k], out var v) ? v : perClass[name][truth[k]] = new int[2];
                        c[1]++; if (given == truth[k]) { c[0]++; right[name]++; }
                    }
                }
                report[set] = new
                {
                    clips = total,
                    accuracy = ways.Keys.ToDictionary(k => k, k => Math.Round(right[k] / (double)total, 3)),
                    recall = ways.Keys.ToDictionary(k => k, k => perClass[k].OrderBy(p => Array.IndexOf(TapeIrregularityModel.Classes, p.Key)).ToDictionary(p => p.Key, p => Math.Round(p.Value[0] / (double)p.Value[1], 3))),
                };
                Assert.True(right["aim"] >= right["rules"], $"{set}: the AIM is right {right["aim"]} times, the rules alone {right["rules"]} times, of {total}");
            }
            File.WriteAllText(Path.Combine(Dataset, "rules-vs-model.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { model.Dispose(); }
    }
}
