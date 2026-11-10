// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Mmc.Etd;

// MMC-ETD-V2.5 - Entity Text Description. Receives a Text Object and gives the Text Descriptors Object of it.
//
// The descriptors are the 28 scores of the GoEmotions classifier (a RoBERTa model), carried as 28 float32 numbers in the Data of the Object (base64), in the order of
// GoEmotionsClassifier.Labels, and the Qualifier names the format. PS-Text Interpretation turns the scores into the Text Personal Status.
public sealed class EtdAimProcessor : IAimProcessor
{
    // A value of TFA/V1.5/formats/TextDescriptorsFormats.json.
    public const string DescriptorFormat = "GoEmotions Scores (RoBERTa-base, 28-d)";

    private readonly GoEmotionsClassifier _model;
    private readonly string _textPort, _outputPort;
    public string InstanceId { get; }

    public EtdAimProcessor(string instanceId, GoEmotionsClassifier model, AimPortReader ports)
    {
        InstanceId = instanceId; _model = model;
        _textPort = ports.Input("OSD-BTO-V1.5", 1);
        _outputPort = ports.Output("MMC-TDO-V2.5");
    }

    public Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_textPort, out var json) || string.IsNullOrWhiteSpace(json))
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "MMC-ETD-V2.5: no Text Object to describe."));
        string text;
        try { text = (MpaiJson.FromJson<BasicTextObject>(json) ?? throw new FormatException("The Text Object cannot be read.")).GetText(); }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        { return Task.FromResult(Message.Error(message.MessageId, InstanceId, "MMC-ETD-V2.5: " + ex.Message)); }
        if (string.IsNullOrWhiteSpace(text))
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "MMC-ETD-V2.5: the Text Object holds no text."));

        var tdo = new TextDescriptorsObject
        {
            TextDescriptorsObjectID = Guid.NewGuid().ToString(),
            TextDescriptorsData = new() { new TextDescriptorsDataItem { Data = EmbeddingCodec.ToBase64(_model.Scores(text)) } },
            TextDescriptorsQualifier = TextDescriptorsQualifier.ForOther(DescriptorFormat)
        };
        var outJson = MpaiJson.ToJson(tdo);
        return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = message.MessageType, Ports = new Dictionary<string, string> { [_outputPort] = outJson } });
    }

    // The scores in a Text Descriptors Object, or why it is not one of this format.
    public static float[] Scores(string tdoJson)
    {
        var tdo = MpaiJson.FromJson<TextDescriptorsObject>(tdoJson) ?? throw new FormatException("The Text Descriptors Object cannot be read.");
        var format = tdo.TextDescriptorsQualifier?.Formats.OtherTextDescriptorsFormats;
        if (format != DescriptorFormat) throw new FormatException($"The Text Descriptors Object is of format '{format ?? "(none)"}': this interpretation takes '{DescriptorFormat}'.");
        var data = tdo.TextDescriptorsData.Find(d => !string.IsNullOrEmpty(d.Data))?.Data ?? throw new FormatException("The Text Descriptors Object carries no data.");
        var scores = EmbeddingCodec.FromBase64(data);
        if (scores.Length != GoEmotionsClassifier.Labels.Length) throw new FormatException($"The Text Descriptors Object holds {scores.Length} numbers, not {GoEmotionsClassifier.Labels.Length}.");
        return scores;
    }
}
