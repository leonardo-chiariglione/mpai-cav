using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Aih.Nnm;

// AIH-NNM-V1.1 - the AIM that wraps a Neural Network Model and processes AIH Data with it: AIH Data in, the AIH Data resulting from the processing out (a new AIH Data, its
// ParentAIHDataID the one processed). The model (an ML Model Object, AIP-MLO-V1.0, a file the Store keeps with the AIM) does not leave the AIM: the AIM is what is exchanged.
//
// The model of the first round is the stand-in the Front Ends train (see HFL): two 32-bit floats, the mean risk score of the AIH Data the Front Ends held and how many
// they were. What the AIM does with it is the same kind of stand-in: it judges the Health Data by the rule of Anomaly and Risk Alerting and, where the rule can say
// nothing (Blue: too little data), gives the colour of the model's mean risk score; what it finds is the ARA Data of the resulting AIH Data. Finding risks is one processing
// among others: a real model replaces this body and nothing else of the AIM.
public sealed class NnmAimProcessor : IAimProcessor
{
    private readonly string _in, _out, _modelOut;
    // WHAT THE MODEL LEARNS from the AIH Data this AIM processes (the stand-in: the risk score of each, Red 1, Orange 0.75, Yellow 0.5, Green 0.25, Blue 0). The model it holds after
    // that - the mean of those scores and their number - is what it gives at its ML Model Object output: that is what a Front End returns to the Back End, inside the AIM.
    private readonly List<float> _learned = new();
    private readonly float _mean;
    private readonly string _modelId;

    public string InstanceId { get; }

    public NnmAimProcessor(string instanceId, AimPortReader ports, string modelPath)
    {
        InstanceId = instanceId;
        _in  = ports.Input(AihJson.AihData);
        _out = ports.Output(AihJson.AihData);
        _modelOut = ports.Output("AIP-MLO-V1.0");
        var model = JsonNode.Parse(File.ReadAllText(modelPath)) as JsonObject ?? throw new FormatException($"{modelPath}: not an ML Model Object.");
        _modelId = model["MLModelObjectID"]?.GetValue<string>() ?? "";
        var weights = Weights(model);
        _mean = weights.Length > 0 ? weights[0] : 0f;
    }

    public Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_in, out var json) || string.IsNullOrWhiteSpace(json))
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, $"AIH-NNM-V1.1: no AIH Data on input port '{_in}'."));
        try
        {
            var data = AihJson.Parse(json, "AIH Data");
            var id = data["AIHDataID"]?.GetValue<string>() ?? throw new FormatException("the AIH Data has no AIHDataID.");
            // AIH Data that an AIM made (it names its parent) is not processed again.
            if (data["AIHData"]?["ParentAIHDataID"] is not null)
                return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = "AIHData", DataType = AihJson.AihData });
            var (risk, anomaly) = Ara.AraAimProcessor.Assess(data["AIHData"]?["HealthData"] as JsonObject);
            if (risk == "Blue") (risk, anomaly) = FromModel(_mean);
            var found = new JsonObject
            {
                ["Header"] = AihJson.AraData, ["ARADataID"] = AihJson.NewId("ara"), ["ARADataTime"] = AihJson.Time(),
                ["UserID"] = data["UserID"]?.GetValue<string>(),
                ["ARAData"] = new JsonObject { ["AIHDataID"] = id, ["Risk"] = risk }
            };
            if (anomaly is not null) found["ARAData"]!["Anomalies"] = new JsonObject { ["Anomaly"] = anomaly };
            // THE RESULTING AIH DATA: the AIH Data processed, under an identifier of its own, naming it as its parent, with what the model found.
            var result = (JsonObject)data.DeepClone();
            result["AIHDataID"] = $"{id}-{InstanceId}";
            result["AIHDataTime"] = AihJson.Time();
            var content = (JsonObject)result["AIHData"]!;
            content["ParentAIHDataID"] = id;
            content["ARAData"] = found;
            var text = result.ToJsonString();
            _learned.Add(risk switch { "Red" => 1f, "Orange" => 0.75f, "Yellow" => 0.5f, "Green" => 0.25f, _ => 0f });
            return Task.FromResult(new Message
            {
                MessageId = message.MessageId, MessageType = "AIHData", DataType = AihJson.AihData, Payload = text,
                Ports = new Dictionary<string, string> { [_out] = text, [_modelOut] = Trained().ToJsonString() }
            });
        }
        catch (Exception ex) { return Task.FromResult(Message.Error(message.MessageId, InstanceId, "AIH-NNM-V1.1: " + ex.Message)); }
    }

    // The model the AIM holds now: the one it wraps, trained on what it processed (the stand-in: the mean risk score and the number of AIH Data).
    public JsonObject Trained()
    {
        var weights = new[] { _learned.Count == 0 ? _mean : _learned.Average(), (float)_learned.Count };
        return new JsonObject
        {
            ["Header"] = "AIP-MLO-V1.0", ["MLModelObjectID"] = $"{_modelId}-trained",
            ["MLModelData"] = new JsonArray(new JsonObject { ["Data"] = Convert.ToBase64String(weights.SelectMany(BitConverter.GetBytes).ToArray()) }),
            ["DescrMetadata"] = "Trained in the Front End by the AIM that wraps it (stand-in: mean risk score and number of AIH Data)"
        };
    }

    // The model that is wrapped: its identifier, and its mean risk score.
    public string ModelId => _modelId;
    public float MeanRiskScore => _mean;

    private static (string Risk, string? Anomaly) FromModel(float mean) => mean switch
    {
        >= 0.875f => ("Red", "Medical Condition Anomaly"),
        >= 0.625f => ("Orange", "Medical Condition Anomaly"),
        >= 0.375f => ("Yellow", "Contextual Anomaly"),
        >= 0.125f => ("Green", null),
        _ => ("Blue", null)
    };

    // The weights of a model: 32-bit floats, little endian, in the first Data item of the ML Model Object (base64), as HFL reads them.
    public static float[] Weights(JsonObject model)
    {
        var data = (model["MLModelData"] as JsonArray)?.OfType<JsonObject>().Select(o => o["Data"]?.GetValue<string>()).FirstOrDefault(d => !string.IsNullOrEmpty(d))
                   ?? throw new FormatException("the model carries no data.");
        var bytes = Convert.FromBase64String(data);
        if (bytes.Length % 4 != 0) throw new FormatException("the model is not 32-bit floats.");
        var weights = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, weights, 0, bytes.Length);
        return weights;
    }
}
