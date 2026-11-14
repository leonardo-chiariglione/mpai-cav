using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using AIF.Controller;
using AIF.SharedStorage;
using AIF.Store;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Mpai.Aih;
using Mpai.Aih.Hfl;
using Mpai.Nnw.NoConstraint;
using Mpai.Onnx;
using Xunit.Abstractions;

namespace Mpai.Aif.Tests;

// AIH-HSP, NIGHT REQUEST N-22: THE FIRST FEDERATED ROUND ON REAL NIH CHEST X-RAYS, and how good the NN Model is after it. NOT A CLINICAL TEST: the labels are mined by
// NLP from the radiology reports (noisy); the task is a demonstration of the round.
//
// The data: images_001 of NIH ChestX-ray14 (4,999 images, 1,335 patients). The frozen network: TorchXRayVision DenseNet-121, weights trained on CheXpert only (so NIH is
// not seen in its pretraining); its 1024 features of every image are computed ONCE (Test\Tools\FlLab\cxr_features.py) and cached in Test\Data\ChestXray\features
// (git-ignored); ONNX Runtime on the exported network gives the cached features (checked here). The network that is trained, watermarked and travels is a HEAD on the
// features (Test\Data\Fl\cxr-head.onnx: a fixed gain, Gemm 1024 -> 64, Relu) with the final layer (64 weights and a bias).
//
// The split, BY PATIENT, never by image: the 967 images of the official test list (patients disjoint from the rest) are the Back End's fixed check set; of the other
// patients, 10% (drawn with a fixed seed) are the Back End's own (its start model is trained on them, nothing of the Front Ends); the rest are three End Users, made
// different on purpose: patients of 60 years or more -> End User 3 ("older"); younger patients most of whose images are AP views -> End User 1 ("mostly AP"); the
// others -> End User 2 ("mostly PA"). Each End User keeps 20% of its patients apart (held-out): the threshold of every model is fixed on the pooled held-out cases of
// the three (Youden's J), never on the check set.
//
// The round: the Back End trains the start model (head + final layer, the watermark inserted during the training, DWI: NoConstraint, 16 bits, 16 values a bit); each
// Front End trains it on its End User's cases (the head and the final layer together, as in FlDemo); Health Federated Learning averages the three (weighted by cases,
// quality gate on the check set); the round-1 NN Model is what goes back. The upper bound: one model trained in the same way on all the End Users' training cases.
// Measured on the check set: AUC, accuracy, sensitivity, specificity at the threshold, with 95% intervals (bootstrap over PATIENTS, 1000 draws), and the watermark.
public class AihCxrRoundTests(ITestOutputHelper output)
{
    private static string Features => Path.Combine(Repository.Root, "Test", "Data", "ChestXray", "features");
    private static string Head => Path.Combine(Repository.Root, "Test", "Data", "Fl", "cxr-head.onnx");
    private static string Chex => Path.Combine(Repository.Root, "Test", "Data", "Fl", "chex-densenet121-features.onnx");

    [SkippableFact]
    [Trait("Group", "Fast")]
    public void TheCachedFeaturesAreThoseOfTheNetworkRunByOnnxRuntime()
    {
        Skip.IfNot(File.Exists(Chex) && File.Exists(Path.Combine(Features, "cxr-check-x.bin")), "the chest network or the cached features are not there");
        var x = Floats(Path.Combine(Features, "cxr-check-x.bin"));
        var expected = Floats(Path.Combine(Features, "cxr-check-features.bin"));
        using var session = OnnxSessions.Create(Chex);
        using var results = session.Run([NamedOnnxValue.CreateFromTensor("image", new DenseTensor<float>(x, [2, 1, 224, 224]))]);
        var got = results.First().AsTensor<float>().ToArray();
        var worst = got.Zip(expected, (a, b) => Math.Abs(a - b) / (1e-3 + Math.Abs(b))).Max();
        Assert.True(worst < 1e-3, $"largest relative difference {worst}");
    }

    // A small fixed subset (about 300 images: whole patients drawn with a fixed seed, a third from the check set), fast: the whole round runs, the mark is found in every model, the measures are numbers.
    [SkippableFact]
    [Trait("Group", "Fast")]
    public void TheRoundRunsOnASmallSubset()
    {
        Skip.IfNot(File.Exists(Head) && File.Exists(Path.Combine(Features, "cxr-features.bin")), "the head or the cached features are not there (Test\\Tools\\FlLab\\cxr_features.py)");
        var report = CxrRound.Run(CxrRound.Load(Features, limit: 300), Head, CxrTask.Any, draws: 200, say: output.WriteLine);
        foreach (var m in report["Models"]!.AsArray().Where(m => m!["Refused"] is null))
        {
            Assert.True((bool)m!["Watermark"]!["Found"]!, $"the mark in {m["Model"]}");
            Assert.InRange((double)m["AUC"]![0]!, 0.0, 1.0);
        }
    }

    // THE LONG RUN: all 4,999 images, the task Finding against No Finding and then Effusion against the rest; writes Test\Reports\aih-cxr-round1.json.
    [SkippableFact]
    [Trait("Group", "Long")]
    public void TheFirstRoundOnAllTheImages()
    {
        Skip.IfNot(File.Exists(Head) && File.Exists(Path.Combine(Features, "cxr-features.bin")), "the head or the cached features are not there (Test\\Tools\\FlLab\\cxr_features.py)");
        var data = CxrRound.Load(Features);
        var any = CxrRound.Run(data, Head, CxrTask.Any, draws: 1000, say: output.WriteLine);
        var effusion = CxrRound.Run(data, Head, CxrTask.Effusion, draws: 1000, say: output.WriteLine);
        var file = Path.Combine(Repository.Root, "Test", "Reports", "aih-cxr-round1.json");
        File.WriteAllText(file, new JsonObject { ["Request"] = "N-22", ["Made"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), ["Tasks"] = new JsonArray(any, effusion) }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        foreach (var m in any["Models"]!.AsArray().Where(m => m!["Refused"] is null)) Assert.True((bool)m!["Watermark"]!["Found"]!, $"the mark in {m["Model"]}");
    }

    internal static float[] Floats(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var f = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, f, 0, bytes.Length);
        return f;
    }

    public enum CxrTask { Any, Effusion }
}

internal static class CxrRound
{
    public sealed record Image(string Name, string Patient, int Age, string View, bool Any, bool Effusion, bool InTest, float[] X)
    {
        public bool Finding(AihCxrRoundTests.CxrTask t) => t == AihCxrRoundTests.CxrTask.Any ? Any : Effusion;
    }

    public const int FeatureCount = 1024, Bits = 16, ValuesPerBit = 16;
    public const int BackEndNetworkEpochs = 10, BackEndHeadEpochs = 300, FrontEndNetworkEpochs = 5;
    public const float NetworkRate = 0.0002f;
    private const string Payload = "1010010111000011";

    public static List<Image> Load(string folder, int? limit = null)
    {
        var x = AihCxrRoundTests.Floats(Path.Combine(folder, "cxr-features.bin"));
        var lines = File.ReadAllLines(Path.Combine(folder, "cxr-index.csv")).Skip(1).ToList();
        if (x.Length != lines.Count * FeatureCount) throw new InvalidDataException($"{lines.Count} images in the index, {x.Length / FeatureCount} feature vectors.");
        var images = lines.Select((l, i) =>
        {
            var f = l.Split(',');
            return new Image(f[0], f[1], int.Parse(f[2], CultureInfo.InvariantCulture), f[4], f[5] == "1", f[6] == "1", f[7] == "1", x.AsSpan(i * FeatureCount, FeatureCount).ToArray());
        }).ToList();
        if (limit is not int n) return images;
        // a subset: whole patients drawn with a fixed seed, a third of n images from the check set (the official test list) and two thirds from the others
        var subset = new List<Image>();
        foreach (var (inTest, want) in new[] { (true, n / 3), (false, n - n / 3) })
        {
            var taken = 0;
            foreach (var g in Shuffled(images.Where(i => i.InTest == inTest).GroupBy(i => i.Patient).OrderBy(g => g.Key, StringComparer.Ordinal), 5))
            {
                if (taken >= want) break;
                subset.AddRange(g); taken += g.Count();
            }
        }
        return subset;
    }

    private static List<T> Shuffled<T>(IEnumerable<T> items, int seed)
    {
        var list = items.ToList(); var r = new Random(seed);
        for (var i = list.Count - 1; i > 0; i--) { var j = r.Next(i + 1); (list[i], list[j]) = (list[j], list[i]); }
        return list;
    }

    public sealed record Split(List<Image> Check, List<Image> BackEnd, (string Name, List<Image> Train, List<Image> Held)[] EndUsers);

    public static Split SplitByPatient(List<Image> images)
    {
        var check = images.Where(i => i.InTest).ToList();
        var patients = Shuffled(images.Where(i => !i.InTest).GroupBy(i => i.Patient).OrderBy(g => g.Key, StringComparer.Ordinal), 22);
        var backEndCount = Math.Max(1, patients.Count / 10);
        var backEnd = patients.Take(backEndCount).SelectMany(g => g).ToList();
        var rest = patients.Skip(backEndCount).ToList();
        int Age(IGrouping<string, Image> g) => g.Select(i => i.Age).OrderBy(a => a).ElementAt(g.Count() / 2);
        bool MostlyAp(IGrouping<string, Image> g) => g.Count(i => i.View == "AP") * 2 > g.Count();
        var groups = new[]
        {
            ("End User 1 (younger, mostly AP views)", rest.Where(g => Age(g) < 60 && MostlyAp(g)).ToList()),
            ("End User 2 (younger, mostly PA views)", rest.Where(g => Age(g) < 60 && !MostlyAp(g)).ToList()),
            ("End User 3 (60 years or more)", rest.Where(g => Age(g) >= 60).ToList())
        };
        var endUsers = groups.Select((g, k) =>
        {
            var ps = Shuffled(g.Item2, 30 + k); var held = Math.Max(1, ps.Count / 5);
            return (g.Item1, ps.Skip(held).SelectMany(p => p).ToList(), ps.Take(held).SelectMany(p => p).ToList());
        }).ToArray();
        return new Split(check, backEnd, endUsers);
    }

    private static FrontEndTrainer Trainer(string head, string folder, IEnumerable<Image> cases, AihCxrRoundTests.CxrTask task)
    {
        var t = new FrontEndTrainer(head, new FileSharedStorage(folder, "eu", "eu"), FeatureCount);
        foreach (var c in cases) t.KeepCase(c.Name, c.X, c.Finding(task));
        return t;
    }

    public static JsonObject Run(List<Image> images, string head, AihCxrRoundTests.CxrTask task, int draws, Action<string> say)
    {
        var work = Path.Combine(Path.GetTempPath(), "aih-cxr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try { return RunIn(work, images, head, task, draws, say); }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    private static JsonObject RunIn(string work, List<Image> images, string head, AihCxrRoundTests.CxrTask task, int draws, Action<string> say)
    {
        var split = SplitByPatient(images);
        // THE FROZEN PART ends with a fixed standardisation of each of the 1024 features: its mean and spread on the Back End's OWN images (nothing of the End
        // Users' or of the check set). Known to every party with the network; not trained.
        var mean = new double[FeatureCount]; var spread = new double[FeatureCount];
        foreach (var i in split.BackEnd) for (var k = 0; k < FeatureCount; k++) mean[k] += i.X[k] / split.BackEnd.Count;
        foreach (var i in split.BackEnd) for (var k = 0; k < FeatureCount; k++) spread[k] += (i.X[k] - mean[k]) * (i.X[k] - mean[k]) / split.BackEnd.Count;
        Image Std(Image i) => i with { X = i.X.Select((v, k) => (float)((v - mean[k]) / (Math.Sqrt(spread[k]) + 1e-3))).ToArray() };
        split = new Split(split.Check.Select(Std).ToList(), split.BackEnd.Select(Std).ToList(),
                          split.EndUsers.Select(e => (e.Name, e.Train.Select(Std).ToList(), e.Held.Select(Std).ToList())).ToArray());
        if (split.Check.Count == 0 || split.BackEnd.Count == 0 || split.EndUsers.Any(e => e.Train.Count == 0 || e.Held.Count == 0))
            throw new InvalidOperationException("a group of the split is empty: " + string.Join(", ", split.EndUsers.Select(e => $"{e.Name} {e.Train.Count}/{e.Held.Count}")));
        string Count(List<Image> l) => $"{l.Count} images of {l.Select(i => i.Patient).Distinct().Count()} patients, {l.Count(i => i.Finding(task))} with the finding";
        say($"== Task: {(task == AihCxrRoundTests.CxrTask.Any ? "Finding (any of the 14) against No Finding" : "Effusion against the rest")}");
        say($"   check set (official test list): {Count(split.Check)}");
        say($"   Back End's own: {Count(split.BackEnd)}");
        foreach (var e in split.EndUsers) say($"   {e.Name}: training {Count(e.Train)}; held-out {Count(e.Held)}");

        // the watermark of the Back End: a fixed key here (the demo's), one payload
        var seed = NoConstraint.Seed([0.4142, 0.7320, 0.2360]); var payload = NoConstraint.Bits(Payload);
        JsonObject start;
        using (var be = Trainer(head, Path.Combine(work, "back-end"), split.BackEnd, task))
        {
            start = be.Train(null, epochs: BackEndHeadEpochs, networkEpochs: BackEndNetworkEpochs, networkRate: NetworkRate, mark: onnx => NoConstraint.InsertOnnx(onnx, seed, payload, null, ValuesPerBit));
            start["MLModelObjectID"] = "model-0";
        }

        var held = split.EndUsers.SelectMany(e => e.Held).ToList();
        using var judge = new FrontEndTrainer(head, new FileSharedStorage(Path.Combine(work, "judge"), "j", "j"), FeatureCount);
        var checkCases = split.Check.Select(i => (i.X, i.Finding(task))).ToList();

        var locals = new List<(string Name, JsonObject Model)>();
        for (var k = 0; k < split.EndUsers.Length; k++)
        {
            using var fe = Trainer(head, Path.Combine(work, "fe" + k), split.EndUsers[k].Train, task);
            locals.Add((split.EndUsers[k].Name, fe.Train(start, epochs: 0, networkEpochs: FrontEndNetworkEpochs, networkRate: NetworkRate)));
        }

        // Health Federated Learning: one round, the three updates averaged (weighted by cases), the quality gate on the Back End's check set
        var amds = new AmdStore(Path.Combine(Repository.Root, "AIMs", "AMDs")); amds.Scan();
        var ports = AimPortReader.Load(amds, AihProvider.Hfl);
        var hfl = new HflAimProcessor("hfl", ports)
        {
            Policy = new RoundPolicy { WeightCount = HflAimProcessor.Vector(start).Length, MaxDistance = 1000, Evaluate = m => judge.Score(m, checkCases) }
        };
        hfl.StartFrom(start);
        Message Msg(JsonObject o) => new() { MessageId = Guid.NewGuid().ToString("N"), Ports = new Dictionary<string, string> { [ports.Input(AihJson.FederatedLearn)] = o.ToJsonString() } };
        JsonObject Fdl(JsonObject item) => new() { ["Header"] = "AIH-FDL-V1.1", ["FederatedLearnID"] = AihJson.NewId("fdl"), ["FederatedLearnTime"] = AihJson.Time(), ["FederatedLearnData"] = new JsonArray(item) };
        foreach (var _ in locals) hfl.ProcessAsync(Msg(Fdl(new JsonObject { ["Request"] = new JsonObject { ["HFEID"] = "hfe", ["PreLearnNNModelID"] = "model-0" } }))).GetAwaiter().GetResult();
        foreach (var (_, m) in locals)
            hfl.ProcessAsync(Msg(Fdl(new JsonObject { ["Response"] = new JsonObject { ["AIHDataID"] = "aih-data-" + Guid.NewGuid().ToString("N")[..6], ["PostLearnNNModel"] = m } }))).GetAwaiter().GetResult();
        var refusals = hfl.Refusals.Select(r => $"{r.What}: {r.Reason}").ToList();
        foreach (var r in refusals) say("   HFL REFUSED " + r);
        var outcome = hfl.Outcomes.LastOrDefault();
        var round1 = outcome is { Accepted: true } ? hfl.CurrentModel! : null;
        // where the quality gate refused the round, the average it refused is measured too, for information (it is not sent back)
        var refusedAverage = round1 is null ? Average(locals.Select(l => l.Model).ToList()) : null;
        var startVector = HflAimProcessor.Vector(start);
        var moved = locals.Select(l => Math.Sqrt(HflAimProcessor.Vector(l.Model).Zip(startVector, (a, b) => (double)(a - b) * (a - b)).Sum())).ToList();
        say($"   the Front Ends' models moved {string.Join(", ", moved.Select(d => d.ToString("F2", CultureInfo.InvariantCulture)))} from the start model (weights {startVector.Length})");

        JsonObject central;
        using (var all = Trainer(head, Path.Combine(work, "central"), split.EndUsers.SelectMany(e => e.Train), task))
            central = all.Train(start, epochs: 0, networkEpochs: FrontEndNetworkEpochs, networkRate: NetworkRate);

        var models = new List<(string Name, JsonObject? Model)> { ("start model (Back End)", start) };
        models.AddRange(locals.Select(l => ("local model of " + l.Name, (JsonObject?)l.Model)));
        models.Add(("round-1 NN Model (federated average)", round1));
        if (refusedAverage is not null) models.Add(("(the average the quality gate refused, for information)", refusedAverage));
        models.Add(("central model (upper bound: all End Users' training cases)", central));

        var rows = new JsonArray();
        say($"   {"Model",-62} {"AUC [95%]",-22} {"thr",6} {"Accuracy [95%]",-22} {"Sensitivity [95%]",-22} {"Specificity [95%]",-22} {"held-out AUC [95%]",-22} mark");
        foreach (var (name, model) in models)
        {
            if (model is null) { say($"   {name,-62} (refused by the quality gate: none)"); rows.Add(new JsonObject { ["Model"] = name, ["Refused"] = true }); continue; }
            var heldP = judge.Probabilities(model, held.Select(i => i.X));
            var heldY = held.Select(i => i.Finding(task)).ToArray();
            var threshold = Youden(heldP, heldY);
            var heldCi = Bootstrap(held, heldP, heldY, threshold, draws)[0];
            var heldAuc = Auc(heldP, heldY);
            var p = judge.Probabilities(model, split.Check.Select(i => i.X));
            var y = split.Check.Select(i => i.Finding(task)).ToArray();
            var m = Measures(p, y, threshold);
            var ci = Bootstrap(split.Check, p, y, threshold, draws);
            var mark = NoConstraint.ExtractOnnx(FrontEndTrainer.NetworkOf(model)!, seed, Bits, null, ValuesPerBit);
            string F((double V, double Lo, double Hi) t) => $"{t.V:F3} [{t.Lo:F3}-{t.Hi:F3}]";
            say($"   {name,-62} {F((m.Auc, ci[0].Lo, ci[0].Hi)),-22} {threshold,6:F3} {F((m.Acc, ci[1].Lo, ci[1].Hi)),-22} {F((m.Sens, ci[2].Lo, ci[2].Hi)),-22} {F((m.Spec, ci[3].Lo, ci[3].Hi)),-22} {F((heldAuc, heldCi.Lo, heldCi.Hi)),-22} {(mark.Carries(payload) ? "found" : "NOT found")} ({mark.Strength:F2}/{mark.Threshold:F2})");
            JsonArray A(double v, (double Lo, double Hi) c) => new(Math.Round(v, 4), Math.Round(c.Lo, 4), Math.Round(c.Hi, 4));
            rows.Add(new JsonObject
            {
                ["Model"] = name, ["Cases"] = HflAimProcessor.Cases(model), ["Threshold"] = Math.Round(threshold, 4),
                ["AUC"] = A(m.Auc, ci[0]), ["Accuracy"] = A(m.Acc, ci[1]), ["Sensitivity"] = A(m.Sens, ci[2]), ["Specificity"] = A(m.Spec, ci[3]),
                ["HeldOutAUC"] = A(heldAuc, heldCi),
                ["Watermark"] = new JsonObject { ["Found"] = mark.Carries(payload), ["Strength"] = Math.Round(mark.Strength, 3), ["Threshold"] = Math.Round(mark.Threshold, 3), ["Correlation"] = Math.Round(mark.Correlation, 4) }
            });
        }
        JsonObject Group(string name, List<Image> l) => new() { ["Group"] = name, ["Images"] = l.Count, ["Patients"] = l.Select(i => i.Patient).Distinct().Count(), ["WithFinding"] = l.Count(i => i.Finding(task)) };
        var groupsJson = new JsonArray(Group("check set (official test list)", split.Check), Group("Back End's own", split.BackEnd));
        foreach (var e in split.EndUsers) { groupsJson.Add(Group(e.Name + " training", e.Train)); groupsJson.Add(Group(e.Name + " held-out", e.Held)); }
        return new JsonObject
        {
            ["Task"] = task == AihCxrRoundTests.CxrTask.Any ? "Finding (any of the 14) against No Finding" : "Effusion against the rest",
            ["Groups"] = groupsJson, ["Models"] = rows, ["HflRefusals"] = new JsonArray(refusals.Select(r => (JsonNode)r).ToArray()),
            ["Moved"] = new JsonArray(moved.Select(d => (JsonNode)Math.Round(d, 3)).ToArray()),
            ["Settings"] = new JsonObject
            {
                ["Network"] = "TorchXRayVision DenseNet-121, densenet121-res224-chex (CheXpert only), frozen, 1024 features", ["Head"] = "gain, Gemm 1024->64, Relu; final layer 64+1",
                ["Standardisation"] = "each feature: mean and spread on the Back End's own images", ["NetworkRate"] = NetworkRate, ["BackEndNetworkEpochs"] = BackEndNetworkEpochs, ["BackEndHeadEpochs"] = BackEndHeadEpochs, ["FrontEndNetworkEpochs"] = FrontEndNetworkEpochs,
                ["Watermark"] = $"NoConstraint DWI, {Bits} bits, {ValuesPerBit} values a bit", ["Threshold"] = "Youden's J on the pooled held-out cases of the three End Users",
                ["Intervals"] = $"95% percentile bootstrap over the patients (of the check set; of the held-out cases for HeldOutAUC), {draws} draws, seed 1",
                ["HeldOutAUC"] = "AUC on the pooled held-out cases of the three End Users (their patients are in no training set; the threshold is fixed on these cases)"
            }
        };
    }

    // The weighted average (by cases) of the final layers and of the networks of the models: what Health Federated Learning makes.
    private static JsonObject Average(List<JsonObject> models)
    {
        var cases = models.Select(m => (double)HflAimProcessor.Cases(m)).ToList(); var total = cases.Sum();
        var heads = models.Select(FrontEndTrainer.Weights).ToList();
        var w = new float[heads[0].Length];
        for (var k = 0; k < models.Count; k++) for (var i = 0; i < w.Length; i++) w[i] += (float)(heads[k][i] * cases[k] / total);
        var first = FrontEndTrainer.NetworkOf(models[0])!; var tensors = OnnxFloats.Weights(first);
        var nets = models.Select(m => OnnxFloats.Read(FrontEndTrainer.NetworkOf(m)!, tensors)).ToList();
        var n = new float[nets[0].Length];
        for (var k = 0; k < models.Count; k++) for (var i = 0; i < n.Length; i++) n[i] += (float)(nets[k][i] * cases[k] / total);
        return FrontEndTrainer.ModelObject("refused-average", w, "the average refused by the quality gate", (int)total, OnnxFloats.Write(first, tensors, n));
    }

    public static double Youden(double[] p, bool[] y)
    {
        var best = 0.5; var bestJ = double.MinValue;
        foreach (var t in p.Distinct().OrderBy(v => v))
        {
            var m = Measures(p, y, t);
            if (m.Sens + m.Spec - 1 > bestJ) { bestJ = m.Sens + m.Spec - 1; best = t; }
        }
        return best;
    }

    public static (double Auc, double Acc, double Sens, double Spec) Measures(double[] p, bool[] y, double t)
    {
        int tp = 0, tn = 0, fp = 0, fn = 0;
        for (var i = 0; i < p.Length; i++) { var pos = p[i] >= t; if (y[i]) { if (pos) tp++; else fn++; } else { if (pos) fp++; else tn++; } }
        return (Auc(p, y), (tp + tn) / (double)p.Length, tp + fn == 0 ? double.NaN : tp / (double)(tp + fn), tn + fp == 0 ? double.NaN : tn / (double)(tn + fp));
    }

    // The area under the ROC curve: the Mann-Whitney statistic, ties counted one half.
    public static double Auc(double[] p, bool[] y)
    {
        var order = Enumerable.Range(0, p.Length).OrderBy(i => p[i]).ToArray();
        var ranks = new double[p.Length];
        for (var i = 0; i < order.Length;)
        {
            var j = i; while (j + 1 < order.Length && p[order[j + 1]] == p[order[i]]) j++;
            for (var k = i; k <= j; k++) ranks[order[k]] = (i + j) / 2.0 + 1;
            i = j + 1;
        }
        double pos = y.Count(v => v), neg = y.Length - pos;
        if (pos == 0 || neg == 0) return double.NaN;
        var sum = Enumerable.Range(0, y.Length).Where(i => y[i]).Sum(i => ranks[i]);
        return (sum - pos * (pos + 1) / 2) / (pos * neg);
    }

    // 95% percentile intervals of AUC, accuracy, sensitivity and specificity: the patients of the check set drawn with replacement (all the images of each).
    private static (double Lo, double Hi)[] Bootstrap(List<Image> check, double[] p, bool[] y, double t, int draws)
    {
        var byPatient = check.Select((img, i) => (img.Patient, i)).GroupBy(x => x.Patient).Select(g => g.Select(x => x.i).ToArray()).ToArray();
        var r = new Random(1);
        var values = new List<double>[4] { new(), new(), new(), new() };
        for (var d = 0; d < draws; d++)
        {
            var idx = new List<int>();
            for (var k = 0; k < byPatient.Length; k++) idx.AddRange(byPatient[r.Next(byPatient.Length)]);
            var m = Measures(idx.Select(i => p[i]).ToArray(), idx.Select(i => y[i]).ToArray(), t);
            var all = new[] { m.Auc, m.Acc, m.Sens, m.Spec };
            for (var k = 0; k < 4; k++) if (!double.IsNaN(all[k])) values[k].Add(all[k]);
        }
        return values.Select(v => { v.Sort(); return v.Count == 0 ? (double.NaN, double.NaN) : (v[(int)(0.025 * (v.Count - 1))], v[(int)(0.975 * (v.Count - 1))]); }).ToArray();
    }
}
