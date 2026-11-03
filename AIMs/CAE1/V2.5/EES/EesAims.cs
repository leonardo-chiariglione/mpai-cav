using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Cae.Usc.Ees;

// The Speech Descriptors Object (MMC-SDO-V2.5) as Emotion Enhanced Speech uses it to carry the speech features between its SubAIMs: its data is a text, here JSON with the Kind
// of features (Prosodic: of a model utterance; EmotionlessSpeech: of the input speech; Neural: what Emotion Feature Production makes), the pitch (Hz), the level (dB) and the
// speaking rate (syllables a second).
public static class SpeechDescriptors
{
    public const string Header = "MMC-SDO-V2.5";

    public static string Make(string kind, SpeechFeatures f, string? language = null)
    {
        var data = new JsonObject { ["Kind"] = kind, ["MeanF0Hz"] = f.MeanF0Hz, ["RmsDb"] = f.RmsDb, ["SpeakingRate"] = f.SpeakingRate };
        if (language is not null) data["Language"] = language;
        var id = Guid.NewGuid().ToString("N")[..12];
        return new JsonObject
        {
            ["Header"] = Header, ["SpeechDescriptorsObjectID"] = "sdo-" + id,
            ["SpeechDescriptorsData"] = new JsonArray(new JsonObject { ["Data"] = data.ToJsonString() }),
            ["SpeechDescriptorsQualifier"] = new JsonObject { ["Header"] = "TFA-SDQ-V1.5", ["SpeechDescriptorsQualifierID"] = "sdq-" + id, ["SubTypes"] = new JsonObject(), ["Formats"] = new JsonObject() }
        }.ToJsonString();
    }

    public static (string Kind, SpeechFeatures Features, string? Language) Read(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("Speech Descriptors are a JSON object.");
        if (root["Header"]?.GetValue<string>() != Header) throw new FormatException($"Not Speech Descriptors: Header {root["Header"]}.");
        var text = (root["SpeechDescriptorsData"] as JsonArray)?.OfType<JsonObject>().Select(o => o["Data"]?.GetValue<string>()).FirstOrDefault(d => !string.IsNullOrEmpty(d))
                   ?? throw new FormatException("The Speech Descriptors carry no data.");
        var d = JsonNode.Parse(text) as JsonObject ?? throw new FormatException("The Speech Descriptors data is not what Emotion Enhanced Speech makes.");
        return (d["Kind"]?.GetValue<string>() ?? "", new SpeechFeatures(d["MeanF0Hz"]!.GetValue<double>(), d["RmsDb"]!.GetValue<double>(), d["SpeakingRate"]!.GetValue<double>()), d["Language"]?.GetValue<string>());
    }
}

// An Emotion (MMC-EEM-V2.5): its category (the first one named) and its Degree in [0, 1]; the Medium value, 0.5, if the Degree is not given. Where no category is named, there is no emotion.
public static class EmotionOf
{
    public static (string? Category, double Degree) Read(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("An Emotion is a JSON object.");
        if (root["Header"]?.GetValue<string>() != "MMC-EEM-V2.5") throw new FormatException($"Not an Emotion: Header {root["Header"]}.");
        var category = (root["EmotionCategory"] as JsonObject)?.Select(p => p.Key).FirstOrDefault();
        return (category, root["Degree"]?.GetValue<double>() ?? 0.5);
    }

    // What each category does to speech, at the Medium degree: the pitch and the rate multiplied, the level raised or lowered in dB. A RULE, not a neural network: the stand-in
    // for the network that Emotion Feature Production is in the specification.
    public static readonly IReadOnlyDictionary<string, (double Pitch, double Rate, double Db)> Table = new Dictionary<string, (double, double, double)>
    {
        ["HAPPINESS"] = (1.20, 1.15, 3), ["SADNESS"] = (0.90, 0.80, -4), ["ANGER"] = (1.10, 1.20, 6), ["FEAR"] = (1.25, 1.25, 0),
        ["CALMNESS"] = (0.95, 0.90, -2), ["DISGUST"] = (0.95, 0.90, 1), ["HURT"] = (0.95, 0.90, -1), ["PRIDE/SHAME"] = (0.95, 0.95, -1), ["RETROSPECTION"] = (0.95, 0.90, -2)
    };

    // The target the features of the emotionless speech move to for an emotion of a degree: the table at the Medium degree, stronger above it and weaker below.
    public static SpeechFeatures Target(SpeechFeatures own, string? category, double degree)
    {
        if (category is null || !Table.TryGetValue(category, out var t)) return own;
        var s = degree / 0.5;
        return new SpeechFeatures(own.MeanF0Hz * Math.Pow(t.Pitch, s), own.RmsDb + t.Db * s, own.SpeakingRate * Math.Pow(t.Rate, s));
    }
}

internal static class Insertion
{
    // The speech moved from its own features towards the target ones, `mix` of the way (1: all the way).
    public static Audio Apply(Audio speech, SpeechFeatures target, double mix = 1)
    {
        var own = Prosody.Measure(speech);
        double Ratio(double to, double from) => to > 0 && from > 0 ? Math.Pow(to / from, mix) : 1;
        return Prosody.Modify(speech, Ratio(target.MeanF0Hz, own.MeanF0Hz), Ratio(target.SpeakingRate, own.SpeakingRate), Math.Pow(10, (target.RmsDb - own.RmsDb) * mix / 20));
    }

    public static Message Give(Message m, string id, string port, string type, string json) =>
        new() { MessageId = m.MessageId, MessageType = type, DataType = type, Payload = json, Ports = new Dictionary<string, string> { [port] = json } };

    public static string? Get(Message m, string key) => m.Ports.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
}

// CAE-SFA-V2.5, Speech Feature Analysis 1: the Model Utterance (a speech segment that shows the emotion) in, its prosodic speech features out. Runs when it has the Mode Selector
// (which says the prosodic pathway is the one asked for) and the Model Utterance.
public sealed class SfaAimProcessor(string instanceId, string selectorKey, string utteranceKey, string outputKey) : IAimProcessor
{
    public string InstanceId { get; } = instanceId;

    public Task<Message> ProcessAsync(Message message)
    {
        if (Insertion.Get(message, selectorKey) is null || Insertion.Get(message, utteranceKey) is not string utterance)
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "CAE-SFA-V2.5: it needs the Mode Selector and the Model Utterance."));
        try
        {
            var f = Prosody.Measure(SpeechObject.Read(utterance).Single());
            return Task.FromResult(Insertion.Give(message, InstanceId, outputKey, "MMC-SDO-V2.5", SpeechDescriptors.Make("Prosodic", f)));
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException) { return Task.FromResult(Message.Error(message.MessageId, InstanceId, "CAE-SFA-V2.5: " + ex.Message)); }
    }
}

// CAE-SFB-V2.5, Speech Feature Analysis 2: the Emotionless Speech in, its speech features out (for Emotion Feature Production).
public sealed class SfbAimProcessor(string instanceId, string selectorKey, string speechKey, string outputKey) : IAimProcessor
{
    public string InstanceId { get; } = instanceId;

    public Task<Message> ProcessAsync(Message message)
    {
        if (Insertion.Get(message, selectorKey) is null || Insertion.Get(message, speechKey) is not string speech)
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "CAE-SFB-V2.5: it needs the Mode Selector and the Emotionless Speech."));
        try
        {
            var f = Prosody.Measure(SpeechObject.Read(speech).Single());
            return Task.FromResult(Insertion.Give(message, InstanceId, outputKey, "MMC-SDO-V2.5", SpeechDescriptors.Make("EmotionlessSpeech", f)));
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException) { return Task.FromResult(Message.Error(message.MessageId, InstanceId, "CAE-SFB-V2.5: " + ex.Message)); }
    }
}

// CAE-EFP-V2.5, Emotion Feature Production: the features of the Emotionless Speech, the Language Selector and the Emotion in; the Neural Speech Features out: where the features of
// speech with that emotion lie, for that language. The specification has a neural network here; this is the rule of EmotionOf.Table (the language is carried, not used).
public sealed class EfpAimProcessor(string instanceId, string featuresKey, string languageKey, string emotionKey, string outputKey) : IAimProcessor
{
    public string InstanceId { get; } = instanceId;

    public Task<Message> ProcessAsync(Message message)
    {
        if (Insertion.Get(message, featuresKey) is not string features || Insertion.Get(message, emotionKey) is not string emotion)
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "CAE-EFP-V2.5: it needs the Emotionless Speech Features and the Emotion."));
        try
        {
            var own = SpeechDescriptors.Read(features).Features;
            var (category, degree) = EmotionOf.Read(emotion);
            var language = Insertion.Get(message, languageKey) is string l ? (JsonNode.Parse(l)?["Language"]?["OutputLanguage"]?.GetValue<string>()) : null;
            return Task.FromResult(Insertion.Give(message, InstanceId, outputKey, "MMC-SDO-V2.5", SpeechDescriptors.Make("Neural", EmotionOf.Target(own, category, degree), language)));
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException) { return Task.FromResult(Message.Error(message.MessageId, InstanceId, "CAE-EFP-V2.5: " + ex.Message)); }
    }
}

// CAE-PEI-V2.5, Prosodic Emotion Insertion: the Emotionless Speech and the Prosodic Speech Features of the model utterance in; the speech with the model's pitch, level and rate
// out. The Emotion List says how far: the Medium degree is all the way, a lower one part of the way, a higher one (to 1) a little beyond.
public sealed class PeiAimProcessor(string instanceId, string speechKey, string featuresKey, string emotionKey, string outputKey) : IAimProcessor
{
    public string InstanceId { get; } = instanceId;

    public Task<Message> ProcessAsync(Message message)
    {
        if (Insertion.Get(message, speechKey) is not string speech || Insertion.Get(message, featuresKey) is not string features)
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "CAE-PEI-V2.5: it needs the Emotionless Speech and the Prosodic Speech Features."));
        try
        {
            var mix = Insertion.Get(message, emotionKey) is string e ? Math.Clamp(EmotionOf.Read(e).Degree / 0.5, 0, 1.5) : 1.0;
            var result = Insertion.Apply(SpeechObject.Read(speech).Single(), SpeechDescriptors.Read(features).Features, mix);
            return Task.FromResult(Insertion.Give(message, InstanceId, outputKey, SpeechObject.Header, SpeechObject.Make("pei-" + Guid.NewGuid().ToString("N")[..12], result)));
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException) { return Task.FromResult(Message.Error(message.MessageId, InstanceId, "CAE-PEI-V2.5: " + ex.Message)); }
    }
}

// CAE-NEI-V2.5, Neural Emotion Insertion: the Emotionless Speech and the Neural Speech Features (the target) in; the speech moved to the target out.
public sealed class NeiAimProcessor(string instanceId, string speechKey, string featuresKey, string outputKey) : IAimProcessor
{
    public string InstanceId { get; } = instanceId;

    public Task<Message> ProcessAsync(Message message)
    {
        if (Insertion.Get(message, speechKey) is not string speech || Insertion.Get(message, featuresKey) is not string features)
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "CAE-NEI-V2.5: it needs the Emotionless Speech and the Neural Speech Features."));
        try
        {
            var result = Insertion.Apply(SpeechObject.Read(speech).Single(), SpeechDescriptors.Read(features).Features);
            return Task.FromResult(Insertion.Give(message, InstanceId, outputKey, SpeechObject.Header, SpeechObject.Make("nei-" + Guid.NewGuid().ToString("N")[..12], result)));
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException) { return Task.FromResult(Message.Error(message.MessageId, InstanceId, "CAE-NEI-V2.5: " + ex.Message)); }
    }
}
