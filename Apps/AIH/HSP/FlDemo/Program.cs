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

namespace Mpai.Aih.FlDemo;

// AIH-HSP, FEDERATED LEARNING, NARRATED. The Back End uploads an AIM with a WATERMARKED model to the Store; three End Users download it, train its final layer on their own
// images (which stay), and return the model; the Back End requests the models, accepts them, averages them weighted by the number of cases, checks the result on a fixed set,
// registers the new version with its licence and uploads it - with a new watermark - for a second round. It ends with an audit and with the table of known problems.
//
// WHAT IS REAL AND WHAT IS NOT is said on the screen and in the table at the end. In short: the images are SYNTHETIC, the network under the final layer has fixed random
// weights, the Controllers and the Module of the Front Ends and of the Back End are NOT started here (the components are called directly), and the sender of a model is
// simulated (the Back End's code never reads it).
internal static class Program
{
    private static readonly string Root = FindRoot();
    private static readonly string Work = Path.Combine(Path.GetTempPath(), "aih-fl-demo-" + Guid.NewGuid().ToString("N"));
    private const int Bits = 16;

    private static void Say(string text) => Console.WriteLine(text);
    private static void Step(string text) { Console.WriteLine(); Console.WriteLine("== " + text); }

    private static async Task<int> Main()
    {
        Say("AIH-HSP V1.1 - federated learning, narrated");
        Say("THIS IS A SIMULATION OF THE PLATFORM: synthetic chest-like images, a frozen network of random weights, components called directly (no Controller, no AIM host),");
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
        var frozenPath = Path.Combine(Root, "Test", "Data", "Fl", "frozen-backbone.onnx");
        if (!File.Exists(frozenPath)) throw new FileNotFoundException("the frozen network is missing; make it with Test\\Tools\\FlLab\\make_frozen_backbone.py: " + frozenPath);
        var original = File.ReadAllBytes(frozenPath);
        var checkSet = new[] { Neutral }.Concat(EndUsers).SelectMany((d, i) => Cases(d, 50, 9000 + i)).ToList();

        var amds = new AmdStore(Path.Combine(Root, "AIMs", "AMDs")); amds.Scan();
        var hfl = new HflAimProcessor("hfl", AimPortReader.Load(amds, AihProvider.Hfl));
        var ports = AimPortReader.Load(amds, AihProvider.Hfl);
        var conformance = new AIF.Metadata.L2Conformance(Path.Combine(Root, "schemas"), _ => null);
        var store = new ModelAimPublisher(Path.Combine(Work, "store"), Path.Combine(Work, "back-end"), l3 => conformance.Check(l3));
        var chain = new HashChainBlockchain();
        var register = new ModelVersionRegister(chain, "hbe-1");

        // The Back End's first model: a final layer trained centrally on a few neutral cases.
        Step("The Back End makes its first model: a final layer trained on 200 neutral cases, over a frozen network");
        float[] headWeights;
        using (var seed = new FrontEndTrainer(frozenPath, new FileSharedStorage(Path.Combine(Work, "seed"), "be", "be")))
        {
            var k = 0; foreach (var (img, f) in Cases(Neutral, 200, 1)) seed.KeepCase("n" + k++, img, f);
            var m = seed.Train(null, epochs: 80);
            headWeights = FrontEndTrainer.Weights(m);
            Say($"   accuracy on the fixed check set of 200 cases: {seed.Score(m, checkSet).Accuracy:P0}");
        }
        var startModel = FrontEndTrainer.ModelObject("model-0", headWeights, "the Back End's first model");

        var refusalsSeen = 0;
        for (var round = 1; round <= 2; round++)
        {
            var key = "back-end-secret-round-" + round; var payload = ModelWatermark.Bits(round == 1 ? 0xA5C3UL : 0x1234UL, Bits);
            Step($"ROUND {round}: the Back End watermarks the frozen network and uploads an AIM with the model to the Store");
            var (marked, locator) = ModelWatermark.Insert(original, key, "key:backend-round-" + round, payload);
            Say($"   watermark: {ModelWatermark.Method}; {Bits} bits, in {locator["Targets"]!.AsArray().Count} tensors of the frozen network; the key is never carried (Locator: KeyReference only)");
            var markedPath = Path.Combine(Work, $"marked-{round}.onnx"); File.WriteAllBytes(markedPath, marked);
            // The Back End fits its current final layer to the network it just marked (the mark moves the features a little), on neutral cases, before it publishes.
            using (var fit = new FrontEndTrainer(markedPath, new FileSharedStorage(Path.Combine(Work, "fit" + round), "be", "be")))
            {
                var k = 0; foreach (var (img, f) in Cases(Neutral, 200, 1)) fit.KeepCase("n" + k++, img, f);
                startModel = fit.Train(startModel, epochs: 80);
                startModel["MLModelObjectID"] = "model-" + (round - 1);
                Say($"   the model on the marked network: accuracy on the fixed check set {fit.Score(startModel, checkSet).Accuracy:P0}");
            }
            var model = startModel.DeepClone().AsObject();
            ((JsonArray)model["MLModelData"]!).Add(new JsonObject { ["Data"] = "1" });
            ((JsonArray)model["MLModelData"]!).Add(new JsonObject { ["Data"] = Convert.ToBase64String(marked) });
            var aim = store.Upload(model);
            Say($"   the Store validated and published the AIM {aim} (BUILT; the Store is the test one)");

            Step("Three End Users download the AIM and train the final layer on their own images (which stay)");
            var returned = new List<(string Sender, JsonObject Model)>();
            for (var u = 0; u < EndUsers.Length; u++)
            {
                var repository = new AimRepository(Path.Combine(Work, $"controller-{EndUsers[u].Name}-{round}"));
                repository.Obtain(aim, new StoreAimSource(Path.Combine(Work, "store"), "hfe-" + EndUsers[u].Name));
                var got = JsonNode.Parse(File.ReadAllText(repository.FileOf(aim, aim + ".model.json")))!.AsObject();
                var start = FrontEndTrainer.ModelObject("model-" + (round - 1), FrontEndTrainer.Weights(got), "downloaded");
                using var fe = new FrontEndTrainer(markedPath, new FileSharedStorage(Path.Combine(Work, $"vault-{EndUsers[u].Name}"), EndUsers[u].Name, EndUsers[u].Name));
                if (fe.Cases == 0) { var k = 0; foreach (var (img, f) in Cases(EndUsers[u], 100, 100 + u)) fe.KeepCase($"case-{k++:D3}", img, f); }
                var own = Cases(EndUsers[u], 200, 500 + u + 10 * round);
                var before = fe.Score(start, own);
                var trained = fe.Train(start);
                var after = fe.Score(trained, own);
                Say($"   {EndUsers[u].Name}: {fe.Cases} own images kept in the End User's storage; accuracy on later own cases {before.Accuracy:P0} -> {after.Accuracy:P0}; model version v{fe.Version} kept");
                returned.Add((EndUsers[u].Name, fe.ExportWithNetwork()));
            }

            Step("The Back End requests the models and checks what came back");
            Say("   SIMULATED: the models arrive without a sender shown. The Controllers still sign their answers (not simulated here); the Back End's code never reads who sent a model.");
            foreach (var (sender, m) in returned)
            {
                var found = ModelWatermark.Extract(FrontEndTrainer.NetworkOf(m)!, key, payload);
                Say($"   a returned model: watermark {(found.Found ? "FOUND" : "not found")} ({found.Matching} of {found.Bits} bits)");
            }
            var judge = new FrontEndTrainer(markedPath, new FileSharedStorage(Path.Combine(Work, "judge"), "j", "j"));
            hfl.Policy = new RoundPolicy
            {
                WeightCount = headWeights.Length, MaxDistance = 5,
                Evaluate = w => judge.Score(FrontEndTrainer.ModelObject("e", w, "evaluation"), checkSet)
            };
            hfl.StartFrom(startModel);
            Message Msg(JsonObject o) => new() { MessageId = Guid.NewGuid().ToString("N"), Ports = new Dictionary<string, string> { [ports.Input(AihJson.FederatedLearn)] = o.ToJsonString() } };
            JsonObject Fdl(JsonObject item) => new() { ["Header"] = "AIH-FDL-V1.1", ["FederatedLearnID"] = AihJson.NewId("fdl"), ["FederatedLearnTime"] = AihJson.Time(), ["FederatedLearnData"] = new JsonArray(item) };
            foreach (var _ in returned) await hfl.ProcessAsync(Msg(Fdl(new JsonObject { ["Request"] = new JsonObject { ["HFEID"] = "hfe", ["PreLearnNNModelID"] = "model-" + (round - 1) } })));
            Message last = new();
            foreach (var (_, m) in returned)
            {
                var head = FrontEndTrainer.ModelObject(m["MLModelObjectID"]!.GetValue<string>(), FrontEndTrainer.Weights(m), "returned", HflAimProcessor.Cases(m));   // the final layer only: the network need not travel on
                var response = Fdl(new JsonObject { ["Response"] = new JsonObject { ["AIHDataID"] = "aih-data-of-a-front-end-" + Guid.NewGuid().ToString("N")[..6], ["PostLearnNNModel"] = head } });
                register.NoteResponse(response);
                last = await hfl.ProcessAsync(Msg(response));
            }
            foreach (var r in hfl.Refusals.Skip(refusalsSeen)) Say($"   REFUSED ({r.What}, round {r.Round}): {r.Reason}");
            refusalsSeen = hfl.Refusals.Count;
            var outcome = hfl.Outcomes[^1];
            if (!outcome.Accepted) { Say("   the result was refused: the old model stays"); continue; }
            Say($"   accepted: {outcome.Updates} models averaged, weighted by their number of cases ({outcome.Cases}); checked on the fixed set: {judge.Score(FrontEndTrainer.ModelObject("e", hfl.Current!, "x"), checkSet).Accuracy:P0} (was {judge.Score(startModel, checkSet).Accuracy:P0})");
            startModel = FrontEndTrainer.ModelObject(outcome.ModelId!, hfl.Current!, "version made by round " + round);
            headWeights = hfl.Current!;
            var version = register.Register(outcome.ModelId!, outcome.Round, outcome.StartedFrom, outcome.Updates, outcome.Cases, EndUsers.Select(e => "user-" + e.Name));
            Say($"   registered the version {outcome.ModelId} with its Model Licence (Smart Contract {version!.ContractId}) on the Blockchain (the test one)");
            judge.Dispose();
        }

        Step("Audit of the last version, asked by the Back End; and a stranger asking");
        var last2 = register.Versions.Last();
        Say("   a stranger may use it: " + register.MayUse(last2.ModelId, "user-Mallory") + "; an End User named by the licence may: " + register.MayUse(last2.ModelId, "user-Alice"));
        Say("   a stranger's audit: " + (register.Audit(last2.ModelId, "user-Mallory") is null ? "refused" : "given"));
        Say(register.Audit(last2.ModelId, "hbe-1")!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        Step("Known problems of federated learning, and what this demo does about each");
        Say("   Problem                                      Status      How");
        Say("   Images leave the End User                    BUILT       only the final layer's weights and the number of cases leave; images stay in the End User's storage");
        Say("   A poisoned or extreme update                 BUILT       size, finite values and a distance limit before averaging");
        Say("   A bad result of averaging                    BUILT       evaluation on a fixed set; the old version is kept and the refusal recorded");
        Say("   Unequal numbers of cases                     BUILT       average weighted by the number of cases (count in the second Data item)");
        Say("   Who sent a model is used against the sender  SIMULATED   the Back End's code never reads the sender (the Controllers still sign)");
        Say("   Theft or leaking of the model                BUILT       watermark in the frozen network, found in returned models (the method is the demo's own)");
        Say("   Who may use a version, which data fed it     BUILT       Model Licence on the Blockchain, audit of each version (test ledger)");
        Say("   Real chest images and a real network         SIMULATED   synthetic images; a frozen network of fixed random weights");
        Say("   Controllers, Modules and AIM hosts           SIMULATED   the components are called directly here; the first-slice demo runs them as Controllers");
        Say("   Leakage through the model's weights          PLANNED     noise (differential privacy) is not added");
        Say("   Server sees each End User's update           PLANNED     secure aggregation is not built");
        Say("   Watermark robustness (pruning, fine-tuning)  PLANNED     not tested");
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "AIMs", "AMDs")) && Directory.Exists(Path.Combine(dir.FullName, "Apps", "AIH"))) return dir.FullName;
        throw new DirectoryNotFoundException("The software tree is not above " + AppContext.BaseDirectory);
    }
}
