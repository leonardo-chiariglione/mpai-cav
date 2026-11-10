// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;

using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Mmc.Sir;   // WavReader

namespace Mpai.Mmc.Spe;

// MMC-ESD-V2.5, the instance that describes the affect of the speech (ESD-V2.5-I03). Receives a Speech Object and gives the Speech Descriptors Object of it: the three numbers that the wav2vec 2.0
// model of audeering reads from the speech (valence, arousal, dominance, each about 0 to 1), as three float32 numbers in the Data (base64), in this order. The Qualifier names the format.
// PS-Speech Interpretation (MMC-PSI) turns them into the Speech Personal Status. This is the description half of what MMC-SPE does in one step.
public sealed class EsdAffectAimProcessor : IAimProcessor
{
    // A value of TFA/V1.5/formats/SpeechDescriptorsFormats.json.
    public const string DescriptorFormat = "wav2vec 2.0 Dimensional Affect (valence, arousal, dominance)";

    private readonly Wav2Vec2EmotionEstimator _estimator;
    private readonly string _inPort, _outPort;
    public string InstanceId { get; }

    public EsdAffectAimProcessor(string instanceId, Wav2Vec2EmotionEstimator estimator, AimPortReader ports)
    {
        InstanceId = instanceId; _estimator = estimator;
        _inPort = ports.Input("OSD-BSO-V1.5");
        _outPort = ports.Output("MMC-SDO-V2.5");
    }

    public Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_inPort, out var json) || string.IsNullOrWhiteSpace(json))
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "MMC-ESD-V2.5: no Speech Object to describe."));
        var speech = SpeechInputs.Basic(json);
        if (speech is null || speech.Data.Length == 0)
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "MMC-ESD-V2.5: the Speech Object holds no speech."));
        var affect = _estimator.Estimate(WavReader.ReadMono16k(speech));
        var sdo = SpeechDescriptorsObject.FromEmbedding(new[] { affect.Valence, affect.Arousal, affect.Dominance }, DescriptorFormat);
        var outJson = MpaiJson.ToJson(sdo);
        return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = message.MessageType, Ports = new Dictionary<string, string> { [_outPort] = outJson } });
    }

    // The affect in a Speech Descriptors Object, or why it is not of this format.
    public static SpeechAffect Affect(string sdoJson)
    {
        var sdo = MpaiJson.FromJson<SpeechDescriptorsObject>(sdoJson) ?? throw new FormatException("The Speech Descriptors Object cannot be read.");
        var format = sdo.SpeechDescriptorsQualifier?.Formats?.OtherSpeechDescriptorsFormats;
        if (format != DescriptorFormat) throw new FormatException($"The Speech Descriptors Object is of format '{format ?? "(none)"}': this interpretation takes '{DescriptorFormat}'.");
        var v = sdo.Embedding() ?? throw new FormatException("The Speech Descriptors Object carries no data.");
        if (v.Length != 3) throw new FormatException($"The Speech Descriptors Object holds {v.Length} numbers, not 3.");
        return new SpeechAffect { Valence = v[0], Arousal = v[1], Dominance = v[2] };
    }
}

public sealed class EsdAffectPlugin : IAimPlugin
{
    private Wav2Vec2EmotionEstimator? _dep;
    public string AimName => "MMC-ESD-V2.5";
    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new EsdAffectAimProcessor(AimName, _dep ??= new Wav2Vec2EmotionEstimator(MpaiPaths.Given(settings, "W2v2Model")), ports);
}
