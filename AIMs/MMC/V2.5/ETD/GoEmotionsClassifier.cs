// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using Mpai.Onnx;

namespace Mpai.Mmc.Etd;

// GoEmotions text classifier (RoBERTa-base fine-tuned on the GoEmotions data; ONNX version of SamLowe/roberta-base-go_emotions-onnx, MIT).
// One text in, 28 scores out: one per label of GoEmotions, each the probability (a sigmoid: the labels are not exclusive) that the text expresses it.
public sealed class GoEmotionsClassifier : IDisposable
{
    public static readonly string[] Labels =
    {
        "admiration", "amusement", "anger", "annoyance", "approval", "caring", "confusion", "curiosity", "desire", "disappointment", "disapproval", "disgust", "embarrassment",
        "excitement", "fear", "gratitude", "grief", "joy", "love", "nervousness", "optimism", "pride", "realization", "relief", "remorse", "sadness", "surprise", "neutral"
    };

    private readonly InferenceSession _session;
    private readonly RobertaBpeTokenizer _tokenizer;

    public GoEmotionsClassifier(string modelPath, string vocabPath, string mergesPath)
    {
        _session = OnnxSessions.Create(modelPath);
        _tokenizer = new RobertaBpeTokenizer(vocabPath, mergesPath);
    }

    public float[] Scores(string text)
    {
        var ids = _tokenizer.Encode(text);
        var inputIds = new DenseTensor<long>(ids, new[] { 1, ids.Length });
        var mask = new DenseTensor<long>(Enumerable.Repeat(1L, ids.Length).ToArray(), new[] { 1, ids.Length });
        var inputs = new List<NamedOnnxValue>();
        foreach (var name in _session.InputMetadata.Keys)
            inputs.Add(NamedOnnxValue.CreateFromTensor(name, name.Contains("mask") ? mask : inputIds));
        using var results = _session.Run(inputs);
        var logits = results.First().AsTensor<float>().ToArray();
        if (logits.Length != Labels.Length) throw new InvalidOperationException($"The model gave {logits.Length} scores, not {Labels.Length}.");
        return logits.Select(l => (float)(1.0 / (1.0 + Math.Exp(-l)))).ToArray();
    }

    public void Dispose() => _session.Dispose();
}
