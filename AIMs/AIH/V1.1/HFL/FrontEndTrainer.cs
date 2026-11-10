// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

using AIF.SharedStorage;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Mpai.Aih.Hfl;

// THE TRAINING IN THE END USER'S HEALTH FRONT END (federated learning, step 1).
//
// A Front End trains only the FINAL LAYER of the NN Model of its End User: a logistic regression (one weight per feature and a bias) on the features that a FROZEN
// network (an ONNX file, opened by Mpai.Onnx) gives for each image. The network under it never changes. The images are the End User's own past cases (the replay):
// they are kept in the End User's storage (the Secure Data Vault) under "cases/" and are never given to anyone. Each version of the model is kept in the same
// storage as an NN Model (AIP-MLO-V1.0, 32-bit floats, little endian, in the first Data item: the weights, then the bias, as HFL averages them) under "nnmodel/";
// only a model leaves the Front End (Export), never an image.
public sealed class FrontEndTrainer : IDisposable
{
    public const int ImageSide = 64;
    private const string CasePrefix = "cases/", ModelPrefix = "nnmodel/";

    private readonly InferenceSession _network;
    private readonly ISharedStorage _vault;
    private int _version;
    private readonly string _networkPath;

    public int FeatureCount { get; }
    public int Cases => _vault.MPAI_AIFM_SharedStorage_List(CasePrefix).Count;
    public int Version => _version;

    public FrontEndTrainer(string frozenNetworkPath, ISharedStorage endUserStorage)
    {
        _networkPath = frozenNetworkPath;
        _network = Mpai.Onnx.OnnxSessions.Create(frozenNetworkPath);
        _vault = endUserStorage;
        FeatureCount = _network.OutputMetadata.First().Value.Dimensions.Last();
        _version = _vault.MPAI_AIFM_SharedStorage_List(ModelPrefix).Count;
    }

    public void Dispose() => _network.Dispose();

    // The frozen network on one image (ImageSide x ImageSide, grey, 0..1).
    public float[] Features(float[] image)
    {
        if (image.Length != ImageSide * ImageSide) throw new ArgumentException($"an image holds {ImageSide * ImageSide} values.");
        var input = new DenseTensor<float>(image, new[] { 1, 1, ImageSide, ImageSide });
        using var results = _network.Run(new[] { NamedOnnxValue.CreateFromTensor(_network.InputMetadata.First().Key, input) });
        return results.First().AsEnumerable<float>().ToArray();
    }

    // Keeps a past case of the End User (the image and its label) in the End User's storage.
    public void KeepCase(string caseId, float[] image, bool finding)
    {
        var bytes = new byte[1 + image.Length * 4];
        bytes[0] = (byte)(finding ? 1 : 0);
        Buffer.BlockCopy(image, 0, bytes, 1, image.Length * 4);
        _vault.MPAI_AIFM_SharedStorage_Put(CasePrefix + Uri.EscapeDataString(caseId), bytes);
    }

    // The replay: all the past cases of the End User, as features and labels (in the order of the keys, so the training is repeatable).
    private List<(float[] X, float Y)> Replay() =>
        _vault.MPAI_AIFM_SharedStorage_List(CasePrefix).OrderBy(k => k, StringComparer.Ordinal).Select(k =>
        {
            var b = _vault.MPAI_AIFM_SharedStorage_Get(k);
            var image = new float[(b.Length - 1) / 4];
            Buffer.BlockCopy(b, 1, image, 0, b.Length - 1);
            return (Features(image), (float)b[0]);
        }).ToList();

    // Trains the final layer, starting from the NN Model given (null: from zero), on the End User's own cases; keeps the new version; returns it.
    public JsonObject Train(JsonObject? start, int epochs = 300, float learningRate = 0.01f)
    {
        var w = start is null ? new float[FeatureCount + 1] : Weights(start);
        if (w.Length != FeatureCount + 1) throw new FormatException($"the NN Model holds {w.Length} values, the final layer needs {FeatureCount + 1}.");
        var data = Replay();
        if (data.Count == 0) throw new InvalidOperationException("the End User has no case to learn from.");
        FitHead(w, data, epochs, learningRate);
        return Keep(w, $"final layer trained by one Health Front End on {data.Count} of its End User's own cases; the network under it is frozen", start?["MLModelObjectID"]?.GetValue<string>(), data.Count);
    }

    // The NN Models kept, oldest first.
    public IEnumerable<JsonObject> Versions() =>
        _vault.MPAI_AIFM_SharedStorage_List(ModelPrefix).OrderBy(k => k, StringComparer.Ordinal)
              .Select(k => JsonNode.Parse(Encoding.UTF8.GetString(_vault.MPAI_AIFM_SharedStorage_Get(k)))!.AsObject());

    // What leaves the Front End: the latest NN Model. Never an image.
    public JsonObject Export() => Versions().LastOrDefault() ?? throw new InvalidOperationException("no NN Model has been trained.");

    // What a Front End returns to the Back End when the whole model is wanted (the Back End looks for its watermark in the frozen network): the latest NN Model with the
    // frozen network's file as a THIRD Data item (the first holds the weights of the final layer, the second the number of cases).
    public JsonObject ExportWithNetwork()
    {
        var model = Export().DeepClone().AsObject();
        ((JsonArray)model["MLModelData"]!).Add(new JsonObject { ["Data"] = Convert.ToBase64String(System.IO.File.ReadAllBytes(_networkPath)) });
        return model;
    }

    // The frozen network's file in a returned model (null where it carries none).
    public static byte[]? NetworkOf(JsonObject model)
    {
        var items = (model["MLModelData"] as JsonArray)?.OfType<JsonObject>().Select(o => o["Data"]?.GetValue<string>()).Where(d => !string.IsNullOrEmpty(d)).ToList() ?? [];
        return items.Count >= 3 ? Convert.FromBase64String(items[2]!) : null;
    }

    // Score of a NN Model on images with labels: the fraction right and the mean log loss.
    public (double Accuracy, double LogLoss) Score(JsonObject model, IEnumerable<(float[] Image, bool Finding)> cases)
    {
        var w = Weights(model);
        double right = 0, loss = 0; var n = 0;
        foreach (var (image, finding) in cases)
        {
            var p = Sigmoid(Dot(w, Features(image)));
            if ((p >= 0.5) == finding) right++;
            loss -= Math.Log(Math.Max(1e-7, finding ? p : 1 - p)); n++;
        }
        return (right / n, loss / n);
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

    public static JsonObject ModelObject(string id, float[] w, string description, int? cases = null) => new()
    {
        ["Header"] = "AIP-MLO-V1.0", ["MLModelObjectID"] = id,
        ["MLModelData"] = Data(w, cases),
        ["DescrMetadata"] = description
    };

    // The weights in the first Data item; the number of cases the model learned from, as decimal text, in the second (see HflAimProcessor.Cases).
    private static JsonArray Data(float[] w, int? cases)
    {
        var data = new JsonArray(new JsonObject { ["Data"] = Convert.ToBase64String(w.SelectMany(BitConverter.GetBytes).ToArray()) });
        if (cases is int n) data.Add(new JsonObject { ["Data"] = n.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        return data;
    }

    private JsonObject Keep(float[] w, string description, string? from, int? cases = null)
    {
        _version++;
        var id = $"{(from ?? "start")}>v{_version}";
        var model = ModelObject(id, w, description, cases);
        _vault.MPAI_AIFM_SharedStorage_Put(ModelPrefix + _version.ToString("D6"), Encoding.UTF8.GetBytes(model.ToJsonString()));
        return model;
    }

    private static double Dot(float[] w, float[] x) { double s = w[^1]; for (var i = 0; i < x.Length; i++) s += w[i] * x[i]; return s; }
    private static double Sigmoid(double z) => 1.0 / (1.0 + Math.Exp(-z));

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
