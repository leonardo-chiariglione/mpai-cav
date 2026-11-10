using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using AIF.Controller;

using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Mmc.Etd;

namespace Mpai.Mmc.Pti;

// MMC-PTI-V2.5 - PS-Text Interpretation. Receives the Text Descriptors Object of a text (the 28 GoEmotions scores that Entity Text Description gives) and gives the Text Personal Status.
//
// The 28 labels of GoEmotions are mapped to the Factors of the Personal Status, each to a label of the standard vocabulary (MMC-EEM Emotion, MMC-ECS Cognitive State, MMC-ESA Social Attitude):
//
//   Emotion:         anger, annoyance, disgust, fear, nervousness, joy, amusement, love, relief, sadness, grief, disappointment, embarrassment, remorse, pride
//   Cognitive State: confusion, curiosity, realization, surprise, excitement, optimism
//   Social Attitude: admiration, approval, disapproval, caring
//   Not mapped (nothing in the vocabulary says them): desire, gratitude, neutral.
//
// From each Factor, the label with the highest score is taken, with its score as the Degree, if the score reaches Threshold; a Factor below it is left out. This is a mapping of a classifier's
// labels onto the vocabulary, not a model of the three Factors: where GoEmotions has no label for a label of the vocabulary, that label is never given, and where the vocabulary has no
// label for a label of GoEmotions the information is lost (see Lost).
public sealed class PtiAimProcessor : IAimProcessor
{
    // The least score for a Factor to be given.
    public const float Threshold = 0.25f;

    // GoEmotions label -> (Factor, Category, GeneralAdjectival, SpecificAdjectival); the words are those of MMC/V2.5/data/{Emotion,CognitiveState,SocialAttitude}.json.
    private static readonly Dictionary<string, (string Factor, string Category, string? General, string? Specific)> Map = new()
    {
        ["anger"]          = ("E", "ANGER", "angry", null),
        ["annoyance"]      = ("E", "ANGER", "angry", "irritated"),
        ["disgust"]        = ("E", "DISGUST", "disgusted", null),
        ["fear"]           = ("E", "FEAR", "fearful/scared", null),
        ["nervousness"]    = ("E", "FEAR", "fearful/scared", "anxious/uneasy"),
        ["joy"]            = ("E", "HAPPINESS", "happy", "joyful"),
        ["amusement"]      = ("E", "HAPPINESS", "happy", "amused"),
        ["love"]           = ("E", "HAPPINESS", "happy", null),
        ["relief"]         = ("E", "CALMNESS", "calm", null),
        ["sadness"]        = ("E", "SADNESS", "sad", null),
        ["grief"]          = ("E", "SADNESS", "sad", "grief-stricken"),
        ["disappointment"] = ("E", "SADNESS", "sad", "disappointed"),
        ["embarrassment"]  = ("E", "PRIDE/SHAME", "proud", "embarrassed"),
        ["remorse"]        = ("E", "PRIDE/SHAME", "proud", "guilty/remorseful/sorry"),
        ["pride"]          = ("E", "PRIDE/SHAME", "proud", null),
        ["confusion"]      = ("C", "UNDERSTANDING", "comprehending", "bewildered/puzzled"),
        ["realization"]    = ("C", "UNDERSTANDING", "comprehending", null),
        ["curiosity"]      = ("C", "INTEREST", "interested", "curious"),
        ["surprise"]       = ("C", "SURPRISE", "surprised", null),
        ["excitement"]     = ("C", "AROUSAL", "aroused/excited/energetic", null),
        ["optimism"]       = ("C", "ATTENTION", "attentive", "hopeful/optimistic"),
        ["admiration"]     = ("S", "APPROVAL/DISAPPROVAL", "admiring/approving", null),
        ["approval"]       = ("S", "APPROVAL/DISAPPROVAL", "admiring/approving", null),
        ["disapproval"]    = ("S", "APPROVAL/DISAPPROVAL", "disapproving", null),
        ["caring"]         = ("S", "EMPATHY", "empathetic/caring", "kind"),
    };

    // The labels of GoEmotions that the Personal Status cannot say.
    public static readonly string[] Lost = { "desire", "gratitude", "neutral" };

    private readonly string _firstPort, _secondPort, _outputPort;
    public string InstanceId { get; }

    public PtiAimProcessor(string instanceId, AimPortReader ports)
    {
        InstanceId = instanceId;
        _firstPort = ports.Input("MMC-TDO-V2.5", 1);
        _secondPort = ports.InputOrDefault("MMC-TDO-V2.5", 2, "");
        _outputPort = ports.Output("MMC-TPS-V2.5");
    }

    public Task<Message> ProcessAsync(Message message)
    {
        // The Descriptors come from Entity Text Description, or directly as input: whichever port holds them.
        string? json = null;
        foreach (var port in new[] { _firstPort, _secondPort })
            if (port.Length > 0 && message.Ports.TryGetValue(port, out var j) && !string.IsNullOrWhiteSpace(j)) { json = j; break; }
        if (json is null) return Task.FromResult(Message.Error(message.MessageId, InstanceId, "MMC-PTI-V2.5: no Text Descriptors Object to interpret."));
        float[] scores;
        try { scores = EtdAimProcessor.Scores(json); }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        { return Task.FromResult(Message.Error(message.MessageId, InstanceId, "MMC-PTI-V2.5: " + ex.Message)); }

        var outJson = MpaiJson.ToJson(Interpret(scores));
        return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = message.MessageType, Ports = new Dictionary<string, string> { [_outputPort] = outJson } });
    }

    // The Text Personal Status of 28 scores (in the order of GoEmotionsClassifier.Labels).
    public static TextPersonalStatus Interpret(IReadOnlyList<float> scores)
    {
        FactorLabel? Best(string factor)
        {
            FactorLabel? best = null; float top = Threshold;
            for (int i = 0; i < GoEmotionsClassifier.Labels.Length; i++)
                if (Map.TryGetValue(GoEmotionsClassifier.Labels[i], out var m) && m.Factor == factor && scores[i] >= top)
                { top = scores[i]; best = FactorLabel.Of(m.Category, m.General, m.Specific, scores[i]); }
            return best;
        }
        var emotion = Best("E"); var cognitive = Best("C"); var social = Best("S");
        return new TextPersonalStatus
        {
            TextEmotion = emotion is null ? null : Emotion.Of(emotion),
            TextCognitiveState = cognitive is null ? null : CognitiveState.Of(cognitive),
            TextSocialAttitude = social is null ? null : SocialAttitude.Of(social)
        };
    }
}
