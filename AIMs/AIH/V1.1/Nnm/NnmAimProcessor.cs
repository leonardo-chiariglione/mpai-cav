using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Aih.Nnm;

// AIH-NNM-V1.1 - the AIM that wraps a Neural Network Model. AIH Data in, ARA Data (AIH-ARD-V1.1) out, as Anomaly and Risk Alerting; what differs is that the AIM
// carries a model (an ML Model Object, AIP-MLO-V1.0, a file the Store keeps with the AIM) which it consults.
//
// The model of the first round is the stand-in the Front Ends train (see HFL): two 32-bit floats, the mean risk score of the AIH Data the Front Ends held and how many
// they were. What the AIM does with it is the same kind of stand-in: it judges the Health Data by the rule of Anomaly and Risk Alerting and, where the rule can say
// nothing (Blue: too little data to judge), gives the colour of the model's mean risk score. A real model replaces this body and nothing else of the AIM.
public sealed class NnmAimProcessor : IAimProcessor
{
    private readonly string _in, _out;
    private readonly float _mean;
    private readonly string _modelId;

    public string InstanceId { get; }

    public NnmAimProcessor(string instanceId, AimPortReader ports, string modelPath)
    {
        InstanceId = instanceId;
        _in  = ports.Input(AihJson.AihData);
        _out = ports.Output(AihJson.AraData);
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
            // AIH Data that carries its ARA Data has been judged: it is not judged again (the Module's wiring returns it here, as it does to Anomaly and Risk Alerting).
            if (data["AIHData"]?["ARAData"] is not null)
                return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = "ARAData", DataType = AihJson.AraData });
            var (risk, anomaly) = Ara.AraAimProcessor.Assess(data["AIHData"]?["HealthData"] as JsonObject);
            if (risk == "Blue") (risk, anomaly) = FromModel(_mean);
            var result = new JsonObject
            {
                ["Header"] = AihJson.AraData, ["ARADataID"] = AihJson.NewId("ara"), ["ARADataTime"] = AihJson.Time(),
                ["UserID"] = data["UserID"]?.GetValue<string>(),
                ["ARAData"] = new JsonObject { ["AIHDataID"] = id, ["Risk"] = risk }
            };
            if (anomaly is not null) result["ARAData"]!["Anomalies"] = new JsonObject { ["Anomaly"] = anomaly };
            var text = result.ToJsonString();
            return Task.FromResult(new Message
            {
                MessageId = message.MessageId, MessageType = "ARAData", DataType = AihJson.AraData, Payload = text,
                Ports = new Dictionary<string, string> { [_out] = text }
            });
        }
        catch (Exception ex) { return Task.FromResult(Message.Error(message.MessageId, InstanceId, "AIH-NNM-V1.1: " + ex.Message)); }
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
