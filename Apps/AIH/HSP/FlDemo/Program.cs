using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;
using AIF.SharedStorage;
using AIF.Store;
using Mpai.Aih;
using Mpai.Aih.Chain;
using Mpai.Aih.Hfl;
using Mpai.Aih.Nnm;
using Mpai.Nnw.NoConstraint;

namespace Mpai.Aih.FlDemo;

// AIH-HSP, FEDERATED LEARNING, NARRATED. The Back End watermarks its model and uploads an AIM with it to the Store; three End Users download the same AIM, train the
// network and the final layer on their own images (which stay), and return the model; the Back End checks with its watermark that each trained AIM was made from its
// model, averages those (network and final layer) weighted by the number of cases, checks the result on a fixed set, registers the new version and uploads it - with the
// mark again - for the next round. It ends with an audit and with the table of known problems.
//
// WHAT IS REAL AND WHAT IS NOT is said on the screen and in the table at the end. In short: the images are SYNTHETIC, the network is small and its starting weights are
// random (the Back End trains it first), the Controllers and the Module of the Front Ends and of the Back End are NOT started here (the components are called directly),
// and the sender of a model is simulated (the Back End's code never reads it).
internal static class Program
{
    private static readonly string Root = FindRoot();
    private static readonly string Work = Path.Combine(Path.GetTempPath(), "aih-fl-demo-" + Guid.NewGuid().ToString("N"));
    private const int Bits = 16;
    // Chosen by trials on this demo (2026-11-12): 16 values a bit (256 of the 5,760 weights the mark may use; 8 left too little margin, 64 cost 13 points of accuracy);
    // the End Users train the network and the final layer together for 5 passes over their cases (no separate refit of the final layer: the final layers of different
    // End Users then drift apart and their average is poor); the Back End's first model is trained for 40 passes.
    private const int Rounds = 4, FirstModelEpochs = 40, ValuesPerBit = 16, FrontEndEpochs = 5;
    private const double MaxDistance = 2;                                        // a trained AIM moves 0.3 to 0.5 from the published model in this demo

    private static void Say(string text) => Console.WriteLine(text);
    private static void Step(string text) { Console.WriteLine(); Console.WriteLine("== " + text); }

    private static async Task<int> Main()
    {
        Say("AIH-HSP V1.1 - federated learning, narrated");
        Say("THIS IS A SIMULATION OF THE PLATFORM: synthetic chest-like images, a small network trained from random weights, components called directly (no Controller, no AIM host),");
        Say("the Store and the Blockchain are the test ones. What is real and what is not: see the table at the end.");
        try { await Run(); Say(""); Say("Done."); return 0; }
        catch (Exception ex) { Console.WriteLine(); Console.WriteLine("FAILED: " + ex.Message); return 1; }
        finally { try { Directory.Delete(Work, true); } catch { } }
    }

    // ---- synthetic images: a body, two darker lungs, noise, and (finding) a bright blob; each End User's device has its own look ----
    private sealed record Device(string Name, float Offset, float Noise, float Contrast, float Cx, float Cy, float Spread);
    private static readonly Device Neutral = new("neutral", 0f, 0.05f, 1f, 32f, 32f, 14f);
    private static readonly Device[] EndUsers =
    {
        new("Alice", +0.03f, 0.05f, 1.0f, 20f, 30f, 4f), new("Bruno", -0.03f, 0.08f, 1.0f, 44f, 30f, 4f), new("Chiara", 0f, 0.05f, 0.8f, 32f, 46f, 5f)
    };
    private static float Gauss(Random r) => (float)(Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble()));
    private static float[] Image(Device d, bool finding, Random rng)
    {
        const int n = FrontEndTrainer.ImageSide;
        var img = new float[n * n]; float bx = 0, by = 0;
        if (finding) { bx = Math.Clamp(d.Cx + Gauss(rng) * d.Spread, 12, 52); by = Math.Clamp(d.Cy + Gauss(rng) * d.Spread, 14, 52); }
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
            {
                var v = 0.6f - 0.05f * (y / (float)n);
                foreach (var lx in new[] { 20f, 44f }) if (Math.Pow((x - lx) / 11.0, 2) + Math.Pow((y - 32) / 20.0, 2) < 1) v -= 0.25f;
                if (finding) v += 0.35f * (float)Math.Exp(-((x - bx) * (x - bx) + (y - by) * (y - by)) / (2 * 3.5 * 3.5));
                img[y * n + x] = Math.Clamp(0.5f + (v - 0.5f) * d.Contrast + d.Offset + Gauss(rng) * d.Noise, 0f, 1f);
            }
        return img;
    }
    private static List<(float[] Image, bool Finding)> Cases(Device d, int count, int seed)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, count).Select(i => { var f = i % 2 == 0; return (Image(d, f, rng), f); }).ToList();
    }

    private static async Task Run()
    {
        Directory.CreateDirectory(Work);
        var networkPath = Path.Combine(Root, "Test", "Data", "Fl", "frozen-backbone.onnx");
        if (!File.Exists(networkPath)) throw new FileNotFoundException("the starting network is missing; make it with Test\\Tools\\FlLab\\make_frozen_backbone.py: " + networkPath);
        var checkSet = new[] { Neutral }.Concat(EndUsers).SelectMany((d, i) => Cases(d, 50, 9000 + i)).ToList();

        var amds = new AmdStore(Path.Combine(Root, "AIMs", "AMDs")); amds.Scan();
        var nnw = new NnwProvider(amds);
        var pwi = nnw.Create(NnwProvider.Pwi, new Dictionary<string, string>(), null); var pwiPorts = AimPortReader.Load(amds, NnwProvider.Pwi);
        var wex = nnw.Create(NnwProvider.Wex, new Dictionary<string, string>(), null); var wexPorts = AimPortReader.Load(amds, NnwProvider.Wex);
        var hfl = new HflAimProcessor("hfl", AimPortReader.Load(amds, AihProvider.Hfl));
        var ports = AimPortReader.Load(amds, AihProvider.Hfl);
        var conformance = new AIF.Metadata.L2Conformance(Path.Combine(Root, "schemas"), _ => null);
        var store = new ModelAimPublisher(Path.Combine(Work, "store"), Path.Combine(Work, "back-end"), l3 => conformance.Check(l3));
        var chain = new HashChainBlockchain();
        var register = new ModelVersionRegister(chain, "hbe-1");
        using var judge = new FrontEndTrainer(networkPath, new FileSharedStorage(Path.Combine(Work, "judge"), "j", "j"));
        string Acc(JsonObject m) => $"{judge.Score(m, checkSet).Accuracy:P0}";

        // The Back End's first model: the network (its starting weights are random) and the final layer, trained together on neutral cases.
        Step("The Back End makes its first model: the network and the final layer trained together on 200 neutral cases");
        JsonObject current;
        using (var seed = new FrontEndTrainer(networkPath, new FileSharedStorage(Path.Combine(Work, "seed"), "be", "be")))
        {
            var k = 0; foreach (var (img, f) in Cases(Neutral, 200, 1)) seed.KeepCase("n" + k++, img, f);
            current = seed.Train(null, epochs: 80, networkEpochs: FirstModelEpochs);
            current["MLModelObjectID"] = "model-0";
        }
        Say($"   accuracy on the fixed check set of 200 cases: {Acc(current)}");

        // THE WATERMARK (NoConstraint, IMT; M3304), with the Back End's own Post-Training Watermark Insertion and Watermark Extraction AIMs (NNW V2.0). It has ONE use: the
        // Back End is sure that a trained AIM it receives was made from ITS model. So it is the same in every round and for every End User: one key, kept by the Back End
        // (it goes only to its own AIMs; the Locator that names the method carries a KeyReference, never the key), and one payload.
        var key = NnwJson.NewKey("key-backend"); const string payload = "1010010111000011";
        var locator = NnwJson.LocatorObject("wlc-backend", Bits, "key:backend", valuesPerBit: ValuesPerBit);

        var refusalsSeen = 0;
        for (var round = 1; round <= Rounds; round++)
        {
            Step($"ROUND {round}: the Back End watermarks its model and uploads an AIM with it to the Store");
            var pwiOut = await pwi.ProcessAsync(new Message { MessageId = "pwi-" + round, Ports = new Dictionary<string, string> {
                [pwiPorts.Input(NnwJson.Model)] = current.ToJsonString(),
                [pwiPorts.Input(NnwJson.Payload)] = NnwJson.PayloadObject("pld-backend", payload).ToJsonString(),
                [pwiPorts.Input(NnwJson.Key)] = key.ToJsonString(), [pwiPorts.Input(NnwJson.Locator)] = locator.ToJsonString() } });
            if (pwiOut.IsError) throw new InvalidOperationException(pwiOut.Payload);
            var published = JsonNode.Parse(pwiOut.Payload)!.AsObject();
            published["MLModelObjectID"] = "model-" + (round - 1);
            Say($"   watermark: {NoConstraint.MethodName(Bits, ValuesPerBit)}; {Bits * ValuesPerBit} weights of the network replaced (Post-Training Watermark Insertion); the key stays with the Back End");
            Say($"   accuracy on the fixed check set: {Acc(current)} before the mark, {Acc(published)} with it (the training of the End Users goes on over the marked weights)");
            var aim = store.Upload(published);
            Say($"   the Store validated and published the AIM {aim}: every End User downloads this same AIM (BUILT; the Store is the test one)");

            Step("Three End Users download the AIM and train the network and the final layer on their own images (which stay)");
            var returned = new List<JsonObject>();
            for (var u = 0; u < EndUsers.Length; u++)
            {
                var repository = new AimRepository(Path.Combine(Work, $"controller-{EndUsers[u].Name}-{round}"));
                repository.Obtain(aim, new StoreAimSource(Path.Combine(Work, "store"), "hfe-" + EndUsers[u].Name));
                var got = JsonNode.Parse(File.ReadAllText(repository.FileOf(aim, aim + ".model.json")))!.AsObject();
                using var fe = new FrontEndTrainer(networkPath, new FileSharedStorage(Path.Combine(Work, $"vault-{EndUsers[u].Name}"), EndUsers[u].Name, EndUsers[u].Name));
                // the End User's own past cases: 100 at the start, and 50 more seen since the last round
                if (fe.Cases == 0) { var k = 0; foreach (var (img, f) in Cases(EndUsers[u], 100, 100 + u)) fe.KeepCase($"case-0-{k++:D3}", img, f); }
                else { var k = 0; foreach (var (img, f) in Cases(EndUsers[u], 50, 1000 * round + u)) fe.KeepCase($"case-{round}-{k++:D3}", img, f); }
                var own = Cases(EndUsers[u], 200, 500 + u + 10 * round);
                var before = fe.Score(got, own);
                var trained = fe.Train(got, epochs: 0, networkEpochs: FrontEndEpochs);
                var after = fe.Score(trained, own);
                Say($"   {EndUsers[u].Name}: {fe.Cases} own images kept in the End User's storage; accuracy on later own cases {before.Accuracy:P0} -> {after.Accuracy:P0}; model version v{fe.Version} kept");
                returned.Add(fe.Export());
            }

            Step("The Back End receives the trained AIMs and checks that each was made from its model");
            Say("   SIMULATED: the models arrive without a sender shown. The Controllers still sign their answers (not simulated here); the Back End's code never reads who sent a model.");
            var accepted = new List<JsonObject>(); var publishedVector = HflAimProcessor.Vector(published);
            foreach (var m in returned)
            {
                var wexOut = await wex.ProcessAsync(new Message { MessageId = "wex-" + round, Ports = new Dictionary<string, string> {
                    [wexPorts.Input(NnwJson.Model)] = m.ToJsonString(), [wexPorts.Input(NnwJson.Key)] = key.ToJsonString(), [wexPorts.Input(NnwJson.Locator)] = locator.ToJsonString() } });
                var read = wexOut.IsError ? null : JsonNode.Parse(wexOut.Payload)!.AsObject();
                var ours = read is not null && (string?)read["PayloadData"] == payload && ((string?)read["DescrMetadata"])!.StartsWith("mark present", StringComparison.Ordinal);
                var moved = Math.Sqrt(HflAimProcessor.Vector(m).Zip(publishedVector, (x, y) => (double)(x - y) * (x - y)).Sum());
                Say($"   a trained AIM (moved {moved:F2} from the published model): watermark {(ours ? "FOUND - made from the Back End's model, taken" : "NOT found - not made from the Back End's model, refused")} ({read?["DescrMetadata"] ?? wexOut.Payload})");
                if (ours) accepted.Add(m);
            }

            hfl.Policy = new RoundPolicy
            {
                WeightCount = HflAimProcessor.Vector(published).Length, MaxDistance = MaxDistance,
                Evaluate = m => judge.Score(m, checkSet)
            };
            hfl.StartFrom(published);
            Message Msg(JsonObject o) => new() { MessageId = Guid.NewGuid().ToString("N"), Ports = new Dictionary<string, string> { [ports.Input(AihJson.FederatedLearn)] = o.ToJsonString() } };
            JsonObject Fdl(JsonObject item) => new() { ["Header"] = "AIH-FDL-V1.1", ["FederatedLearnID"] = AihJson.NewId("fdl"), ["FederatedLearnTime"] = AihJson.Time(), ["FederatedLearnData"] = new JsonArray(item) };
            foreach (var _ in accepted) await hfl.ProcessAsync(Msg(Fdl(new JsonObject { ["Request"] = new JsonObject { ["HFEID"] = "hfe", ["PreLearnNNModelID"] = "model-" + (round - 1) } })));
            foreach (var m in accepted)
            {
                var response = Fdl(new JsonObject { ["Response"] = new JsonObject { ["AIHDataID"] = "aih-data-of-a-front-end-" + Guid.NewGuid().ToString("N")[..6], ["PostLearnNNModel"] = m } });
                register.NoteResponse(response);
                await hfl.ProcessAsync(Msg(response));
            }
            foreach (var r in hfl.Refusals.Skip(refusalsSeen)) Say($"   REFUSED ({r.What}, round {r.Round}): {r.Reason}");
            refusalsSeen = hfl.Refusals.Count;
            var outcome = hfl.Outcomes.Count > 0 ? hfl.Outcomes[^1] : null;
            if (outcome is null || outcome.Round != round || !outcome.Accepted) { Say("   the result was refused: the Back End's model stays as it was"); continue; }
            Say($"   accepted: {outcome.Updates} models averaged (the network and the final layer), weighted by their number of cases ({outcome.Cases}); on the fixed set: {Acc(hfl.CurrentModel!)} (the published model: {Acc(published)})");
            var averaged = await wex.ProcessAsync(new Message { MessageId = "wex-avg-" + round, Ports = new Dictionary<string, string> {
                [wexPorts.Input(NnwJson.Model)] = hfl.CurrentModel!.ToJsonString(), [wexPorts.Input(NnwJson.Key)] = key.ToJsonString(), [wexPorts.Input(NnwJson.Locator)] = locator.ToJsonString() } });
            Say($"   the averaged model: {JsonNode.Parse(averaged.Payload)?["DescrMetadata"]}");
            current = hfl.CurrentModel!.DeepClone().AsObject();
            var version = register.Register(outcome.ModelId!, outcome.Round, outcome.StartedFrom, outcome.Updates, outcome.Cases, EndUsers.Select(e => "user-" + e.Name));
            Say($"   registered the version {outcome.ModelId} on the Blockchain (the test one), Smart Contract {version!.ContractId}");
        }

        Step("Audit of the last version, asked by the Back End; and a stranger asking");
        var last2 = register.Versions.Last();
        Say("   a stranger's audit: " + (register.Audit(last2.ModelId, "user-Mallory") is null ? "refused" : "given"));
        Say(register.Audit(last2.ModelId, "hbe-1")!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        Step("Known problems of federated learning, and what this demo does about each");
        Say("   Problem                                      Status      How");
        Say("   Images leave the End User                    BUILT       only the model (network and final layer) and the number of cases leave; images stay in the End User's storage");
        Say("   A poisoned or extreme update                 BUILT       size, finite values and a distance limit before averaging");
        Say("   A bad result of averaging                    BUILT       evaluation on a fixed set; the old version is kept and the refusal recorded");
        Say("   Unequal numbers of cases                     BUILT       average weighted by the number of cases (count in the second Data item)");
        Say("   Who sent a model is used against the sender  SIMULATED   the Back End's code never reads the sender (the Controllers still sign)");
        Say("   A trained AIM not made from the Back End's   BUILT       the Back End's watermark (NoConstraint, IMT) in the network; WEX on every trained AIM; without the mark, refused");
        Say("   Real chest images and a real network         SIMULATED   synthetic images; a small network whose starting weights are random, trained by the Back End and the End Users");
        Say("   Controllers, Modules and AIM hosts           SIMULATED   the components are called directly here; the first-slice demo runs them as Controllers");
        Say("   Leakage through the model's weights          PLANNED     noise (differential privacy) is not added");
        Say("   Server sees each End User's update           PLANNED     secure aggregation is not built");
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "AIMs", "AMDs")) && Directory.Exists(Path.Combine(dir.FullName, "Apps", "AIH"))) return dir.FullName;
        throw new DirectoryNotFoundException("The software tree is not above " + AppContext.BaseDirectory);
    }
}
