// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

using AIF.SharedStorage;

namespace Mpai.Aih.Hfl;

// THE TRAINING IN THE END USER'S HEALTH FRONT END (federated learning, step 1).
//
// A Front End trains the NETWORK of the NN Model of its End User and its FINAL LAYER together: the network (an ONNX file, run and trained by Network) gives features
// for each image, and the final layer is a logistic regression on them (one weight per feature and a bias). The images are the End User's own past cases (the
// replay): they are kept in the End User's storage (the Secure Data Vault) under "cases/" and are never given to anyone. Each version of the model is kept in the same
// storage as an NN Model (AIP-MLO-V1.0) under "nnmodel/"; only a model leaves the Front End (Export), never an image.
//
// THE NN MODEL (AIP-MLO-V1.0) holds three Data items: the first, the weights of the final layer (32-bit floats, little endian, the weights then the bias); the second,
// the number of cases it learned from (decimal text, see HflAimProcessor.Cases); the third, the network, an ONNX file. A model with the first item only (a final layer
// alone) is trained on the network the trainer was made with.
//
// Until 2026-11-12 the network was FROZEN (fixed random weights) and only the final layer was trained. The network is now trained, as the watermark of the Back End
// (NoConstraint) assumes: the training goes on over the marked weights.
public sealed class FrontEndTrainer : IDisposable
{
    public const int ImageSide = 64;
    public const int NetworkEpochs = 5;                                               // passes over the cases in which the network is trained with the final layer
    private const string CasePrefix = "cases/", ModelPrefix = "nnmodel/";

    private readonly byte[] _baseNetwork;
    private readonly ISharedStorage _vault;
    private readonly int _inputLength;                                                 // 0: an image of ImageSide x ImageSide; otherwise a vector of features of that length
    private int _version;

    public int FeatureCount { get; }
    public int Cases => _vault.MPAI_AIFM_SharedStorage_List(CasePrefix).Count;
    public int Version => _version;

    public FrontEndTrainer(string networkPath, ISharedStorage endUserStorage) : this(networkPath, endUserStorage, 0) { }

    // A trainer whose cases are VECTORS of inputLength values (N-22: the features a frozen chest network gives for an image, cached; the network trained here is
    // a head on them). The vectors are used as they are (not standardised).
    public FrontEndTrainer(string networkPath, ISharedStorage endUserStorage, int inputLength)
    {
        _baseNetwork = System.IO.File.ReadAllBytes(networkPath);
        _inputLength = inputLength;
        FeatureCount = NewNetwork(_baseNetwork).FeatureCount;
        _vault = endUserStorage;
        _version = _vault.MPAI_AIFM_SharedStorage_List(ModelPrefix).Count;
    }

    public int InputLength => _inputLength > 0 ? _inputLength : ImageSide * ImageSide;

    private Network NewNetwork(byte[] onnx) => _inputLength > 0 ? new Network(onnx, _inputLength, 1, 1) : new Network(onnx, ImageSide);

    // What the network is given: an image standardised; a vector as it is.
    private float[] Prepare(float[] input)
    {
        if (input.Length != InputLength) throw new ArgumentException($"an input holds {InputLength} values, not {input.Length}.");
        return _inputLength > 0 ? input : Standardise(input);
    }

    public void Dispose() { }

    // The network the trainer was made with, on one image (ImageSide x ImageSide, grey, 0..1).
    public float[] Features(float[] image) => NewNetwork(_baseNetwork).Features(Prepare(image));

    // The network of a model: its own (third Data item) or, for a final layer alone, the trainer's.
    private Network NetworkFor(JsonObject? model) => NewNetwork(NetworkOf(model ?? new JsonObject()) ?? _baseNetwork);

    // Keeps a past case of the End User (the image and its label) in the End User's storage.
    public void KeepCase(string caseId, float[] image, bool finding)
    {
        var bytes = new byte[1 + image.Length * 4];
        bytes[0] = (byte)(finding ? 1 : 0);
        Buffer.BlockCopy(image, 0, bytes, 1, image.Length * 4);
        _vault.MPAI_AIFM_SharedStorage_Put(CasePrefix + Uri.EscapeDataString(caseId), bytes);
    }

    // The replay: all the past cases of the End User, as images and labels (in the order of the keys, so the training is repeatable).
    private List<(float[] Image, float Y)> Replay() =>
        _vault.MPAI_AIFM_SharedStorage_List(CasePrefix).OrderBy(k => k, StringComparer.Ordinal).Select(k =>
        {
            var b = _vault.MPAI_AIFM_SharedStorage_Get(k);
            var image = new float[(b.Length - 1) / 4];
            Buffer.BlockCopy(b, 1, image, 0, b.Length - 1);
            return (Prepare(image), (float)b[0]);
        }).ToList();

    // Trains the model given (null: a final layer from zero, on the trainer's network) on the End User's own cases; keeps the new version; returns it.
    // First the network and the final layer are trained together for networkEpochs passes (Adam, batches of 10, in a fixed order); then the final layer is fitted on the
    // features of the trained network (epochs, full batch). networkEpochs = 0 leaves the network as it is.
    // mark: DURING-TRAINING WATERMARK INSERTION (DWI). Given (the ONNX file of the network -> the same, watermarked), the mark is inserted in the network before it is trained, and the
    // weights it set are kept at the values it gave them after every update: the network learns around the mark, which is part of the trained model, not put on it afterwards.
    public JsonObject Train(JsonObject? start, int epochs = 300, float learningRate = 0.01f, int networkEpochs = NetworkEpochs, float networkRate = 0.0002f, Func<byte[], byte[]>? mark = null)
    {
        var w = start is null ? new float[FeatureCount + 1] : Weights(start);
        if (w.Length != FeatureCount + 1) throw new FormatException($"the NN Model holds {w.Length} values, the final layer needs {FeatureCount + 1}.");
        var cases = Replay();
        if (cases.Count == 0) throw new InvalidOperationException("the End User has no case to learn from.");
        var net = NetworkFor(start);
        var held = mark is null ? null : Held(net, mark);
        if (networkEpochs > 0) TrainTogether(net, w, cases, networkEpochs, networkRate, learningRate, held);
        var data = cases.Select(c => (net.Features(c.Image), c.Y)).ToList();
        FitHead(w, data, epochs, learningRate);
        var what = networkEpochs > 0 ? "network and final layer" : "final layer (the network unchanged)";
        return Keep(w, net.ToOnnx(), $"{what} trained by one Health Front End on {data.Count} of its End User's own cases", start?["MLModelObjectID"]?.GetValue<string>(), data.Count);
    }

    // The NN Models kept, oldest first.
    public IEnumerable<JsonObject> Versions() =>
        _vault.MPAI_AIFM_SharedStorage_List(ModelPrefix).OrderBy(k => k, StringComparer.Ordinal)
              .Select(k => JsonNode.Parse(Encoding.UTF8.GetString(_vault.MPAI_AIFM_SharedStorage_Get(k)))!.AsObject());

    // What leaves the Front End: the latest NN Model. Never an image.
    public JsonObject Export() => Versions().LastOrDefault() ?? throw new InvalidOperationException("no NN Model has been trained.");

    // The latest NN Model, which carries its network (kept for the callers of the time when the network was frozen and travelled apart).
    public JsonObject ExportWithNetwork() => Export();

    // The network's file in a model (null where it carries none: a final layer alone).
    public static byte[]? NetworkOf(JsonObject model)
    {
        var items = (model["MLModelData"] as JsonArray)?.OfType<JsonObject>().Select(o => o["Data"]?.GetValue<string>()).Where(d => !string.IsNullOrEmpty(d)).ToList() ?? [];
        return items.Count >= 3 ? Convert.FromBase64String(items[2]!) : null;
    }

    // Score of a NN Model on images with labels: the fraction right and the mean log loss (on the model's own network, or on the trainer's for a final layer alone).
    public (double Accuracy, double LogLoss) Score(JsonObject model, IEnumerable<(float[] Image, bool Finding)> cases)
    {
        var w = Weights(model); var net = NetworkFor(model);
        double right = 0, loss = 0; var n = 0;
        foreach (var (image, finding) in cases)
        {
            var p = Sigmoid(Dot(w, net.Features(Prepare(image))));
            if ((p >= 0.5) == finding) right++;
            loss -= Math.Log(Math.Max(1e-7, finding ? p : 1 - p)); n++;
        }
        return (right / n, loss / n);
    }

    // The probability of a finding the model gives for each input.
    public double[] Probabilities(JsonObject model, IEnumerable<float[]> inputs)
    {
        var w = Weights(model); var net = NetworkFor(model);
        return inputs.Select(x => Sigmoid(Dot(w, net.Features(Prepare(x))))).ToArray();
    }

    public static float[] Weights(JsonObject model)
    {
        var data = (model["MLModelData"] as JsonArray)?.OfType<JsonObject>().Select(o => o["Data"]?.GetValue<string>()).FirstOrDefault(d => !string.IsNullOrEmpty(d))
                   ?? throw new FormatException("the model carries no data.");
        var bytes = Convert.FromBase64String(data);
        var weights = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, weights, 0, weights.Length * 4);
        return weights;
    }

    public static JsonObject ModelObject(string id, float[] w, string description, int? cases = null, byte[]? network = null) => new()
    {
        ["Header"] = "AIP-MLO-V1.0", ["MLModelObjectID"] = id,
        ["MLModelData"] = Data(w, network is null ? cases : cases ?? 1, network),
        ["DescrMetadata"] = description
    };

    // The weights in the first Data item; the number of cases the model learned from, as decimal text, in the second (see HflAimProcessor.Cases).
    private static JsonArray Data(float[] w, int? cases, byte[]? network)
    {
        var data = new JsonArray(new JsonObject { ["Data"] = Convert.ToBase64String(w.SelectMany(BitConverter.GetBytes).ToArray()) });
        if (cases is int n) data.Add(new JsonObject { ["Data"] = n.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        if (network is not null) data.Add(new JsonObject { ["Data"] = Convert.ToBase64String(network) });
        return data;
    }

    private JsonObject Keep(float[] w, byte[] network, string description, string? from, int? cases = null)
    {
        _version++;
        var id = $"{(from ?? "start")}>v{_version}";
        var model = ModelObject(id, w, description, cases, network);
        _vault.MPAI_AIFM_SharedStorage_Put(ModelPrefix + _version.ToString("D6"), Encoding.UTF8.GetBytes(model.ToJsonString()));
        return model;
    }

    // THE IMAGE AS THE NETWORK SEES IT: standardised (its mean taken away, divided by its spread), so that the brightness and the contrast of a device do not count.
    public static float[] Standardise(float[] image)
    {
        var mean = image.Average(v => (double)v); var sd = Math.Sqrt(image.Sum(v => (v - mean) * (v - mean)) / image.Length);
        var k = sd > 1e-6 ? 1 / sd : 1;
        return image.Select(v => (float)((v - mean) * k)).ToArray();
    }

    private static double Dot(float[] w, float[] x) { double s = w[^1]; for (var i = 0; i < x.Length; i++) s += w[i] * x[i]; return s; }
    private static double Sigmoid(double z) => 1.0 / (1.0 + Math.Exp(-z));

    // The network and the final layer together: Adam on the logistic loss, batches of 10 in the order of the cases; the network at networkRate, the final layer at headRate.
    // The weights the mark sets, as (tensor, index, value), and the mark applied: the weights of the marked network that differ from those of the network before.
    private Hold Held(Network net, Func<byte[], byte[]> mark)
    {
        var before = net.Weights();
        var after = NewNetwork(mark(net.ToOnnx())).Weights();
        var parameters = net.Parameters().ToList();
        var held = new List<(int P, int I, float V)>();
        for (int p = 0, at = 0; p < parameters.Count; at += parameters[p].Values.Length, p++)
            for (var i = 0; i < parameters[p].Values.Length; i++)
                if (after[at + i] != before[at + i]) held.Add((p, i, after[at + i]));
        if (held.Count == 0) throw new InvalidOperationException("the mark changed no weight of the network.");
        var hold = new Hold(held);
        hold.Apply(parameters);
        return hold;
    }

    private sealed class Hold(List<(int P, int I, float V)> held)
    {
        public int Count => held.Count;
        public void Apply(List<(float[] Values, float[] Grads)> parameters) { foreach (var (p, i, v) in held) parameters[p].Values[i] = v; }
    }

    private static void TrainTogether(Network net, float[] w, List<(float[] Image, float Y)> cases, int epochs, float networkRate, float headRate, Hold? held = null)
    {
        var parameters = net.Parameters().ToList();
        var m = parameters.Select(p => new double[p.Values.Length]).ToList(); var v = parameters.Select(p => new double[p.Values.Length]).ToList();
        var hm = new double[w.Length]; var hv = new double[w.Length]; var hg = new double[w.Length];
        const int batch = 10; var t = 0; var f = w.Length - 1;
        for (var e = 0; e < epochs; e++)
            for (var at = 0; at < cases.Count; at += batch)
            {
                net.ClearGradients(); Array.Clear(hg);
                var n = Math.Min(batch, cases.Count - at);
                for (var c = at; c < at + n; c++)
                {
                    var acts = net.Run(cases[c].Image); var x = acts[^1];
                    var err = (float)((Sigmoid(Dot(w, x)) - cases[c].Y) / n);
                    var dx = new float[f];
                    for (var i = 0; i < f; i++) { hg[i] += err * x[i]; dx[i] = err * w[i]; }
                    hg[f] += err;
                    net.Backward(acts, dx);
                }
                t++;
                double c1 = 1 - Math.Pow(0.9, t), c2 = 1 - Math.Pow(0.999, t);
                for (var i = 0; i <= f; i++)
                {
                    var g = hg[i] + (i < f ? 1e-3 * w[i] : 0);
                    hm[i] = 0.9 * hm[i] + 0.1 * g; hv[i] = 0.999 * hv[i] + 0.001 * g * g;
                    w[i] -= (float)(headRate * (hm[i] / c1) / (Math.Sqrt(hv[i] / c2) + 1e-8));
                }
                for (var p = 0; p < parameters.Count; p++)
                {
                    var (values, grads) = parameters[p]; var pm = m[p]; var pv = v[p];
                    for (var i = 0; i < values.Length; i++)
                    {
                        double g = grads[i];
                        pm[i] = 0.9 * pm[i] + 0.1 * g; pv[i] = 0.999 * pv[i] + 0.001 * g * g;
                        values[i] -= (float)(networkRate * (pm[i] / c1) / (Math.Sqrt(pv[i] / c2) + 1e-8));
                    }
                }
                held?.Apply(parameters);                                               // DWI: the marked weights stay as the mark set them
            }
    }

    // Adam on the logistic loss, full batch, with a small L2 term (not on the bias).
    private static void FitHead(float[] w, List<(float[] X, float Y)> data, int epochs, float lr)
    {
        var m = new double[w.Length]; var v = new double[w.Length]; var g = new double[w.Length];
        for (var t = 1; t <= epochs; t++)
        {
            Array.Clear(g);
            foreach (var (x, y) in data)
            {
                var err = Sigmoid(Dot(w, x)) - y;
                for (var i = 0; i < x.Length; i++) g[i] += err * x[i];
                g[^1] += err;
            }
            for (var i = 0; i < w.Length; i++)
            {
                g[i] = g[i] / data.Count + (i < w.Length - 1 ? 1e-3 * w[i] : 0);
                m[i] = 0.9 * m[i] + 0.1 * g[i]; v[i] = 0.999 * v[i] + 0.001 * g[i] * g[i];
                w[i] -= (float)(lr * (m[i] / (1 - Math.Pow(0.9, t))) / (Math.Sqrt(v[i] / (1 - Math.Pow(0.999, t))) + 1e-8));
            }
        }
    }
}
