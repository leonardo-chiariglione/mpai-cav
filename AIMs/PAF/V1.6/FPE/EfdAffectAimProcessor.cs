// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

using AIF.Controller;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Paf.Fpe;

// PAF-EFD-V1.6, the instance that describes the expression of the face (EFD-V1.6-I02). Receives a Face Object (a picture of one face) and gives the Face Descriptors Object of it: the ten numbers
// that the HSEmotion model reads from the face - the probabilities of the eight emotions of AffectNet (in the order of HSEmotionEstimator.EmotionLabels), then the valence and the arousal -
// as ten float32 numbers in the Data (base64). The Qualifier names the format. PS-Face Interpretation (PAF-PFI) turns them into the Face Personal Status. This is the description half of
// what PAF-FPE does in one step. (The instance I01 of Entity Face Description describes the identity of the face, with ArcFace.)
public sealed class EfdAffectAimProcessor : IAimProcessor
{
    // A value of TFA/V1.5/formats/FaceDescriptorsContentFormats.json.
    public const string DescriptorFormat = "HSEmotion EfficientNet-B0 (8 emotion probabilities, valence, arousal)";

    private readonly HSEmotionEstimator _hse;
    private readonly string _inPort, _outPort;
    public string InstanceId { get; }

    public EfdAffectAimProcessor(string instanceId, HSEmotionEstimator hse, AimPortReader ports)
    {
        InstanceId = instanceId; _hse = hse;
        _inPort = ports.Input("OSD-BVO-V1.5");
        _outPort = ports.Output("PAF-FDO-V1.6");
    }

    public Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_inPort, out var json) || string.IsNullOrWhiteSpace(json))
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "PAF-EFD-V1.6: no Face Object to describe."));
        var visual = Mpai.Aif.PortData.PortObjects.ReadVisual(json);
        if (visual is null || visual.Data.Length == 0)
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "PAF-EFD-V1.6: the Face Object holds no picture."));
        FaceAffect a;
        using (var image = Image.Load<Rgb24>(visual.Data)) a = _hse.Estimate(image);
        var numbers = HSEmotionEstimator.EmotionLabels.Select(l => a.Probabilities[l]).Concat(new[] { a.Valence, a.Arousal }).ToArray();
        var outJson = MpaiJson.ToJson(FaceDescriptorsObject.FromEmbedding(numbers, DescriptorFormat));
        return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = message.MessageType, Ports = new Dictionary<string, string> { [_outPort] = outJson } });
    }

    // The face affect in a Face Descriptors Object, or why it is not of this format.
    public static FaceAffect Affect(string fdoJson)
    {
        var fdo = MpaiJson.FromJson<FaceDescriptorsObject>(fdoJson) ?? throw new FormatException("The Face Descriptors Object cannot be read.");
        var format = fdo.FaceDescriptorsQualifier?.Formats?.ContentFormat;
        if (format != DescriptorFormat) throw new FormatException($"The Face Descriptors Object is of format '{format ?? "(none)"}': this interpretation takes '{DescriptorFormat}'.");
        var v = fdo.Embedding() ?? throw new FormatException("The Face Descriptors Object carries no data.");
        if (v.Length != 10) throw new FormatException($"The Face Descriptors Object holds {v.Length} numbers, not 10.");
        int best = 0; for (int i = 1; i < 8; i++) if (v[i] > v[best]) best = i;
        return new FaceAffect
        {
            Emotion = HSEmotionEstimator.EmotionLabels[best], Confidence = v[best], Valence = v[8], Arousal = v[9],
            Probabilities = HSEmotionEstimator.EmotionLabels.Select((l, i) => (l, p: v[i])).ToDictionary(t => t.l, t => t.p)
        };
    }
}

public sealed class EfdAffectPlugin : IAimPlugin
{
    private HSEmotionEstimator? _dep;
    public string AimName => "PAF-EFD-V1.6";
    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new EfdAffectAimProcessor(AimName, _dep ??= new HSEmotionEstimator(MpaiPaths.Given(settings, "HseModel")), ports);
}
