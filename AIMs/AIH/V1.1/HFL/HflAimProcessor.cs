using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Aih.Hfl;

// AIH-HFL-V1.1 - Health Federated Learning: "requests trained NN Models and produces a new NN Model". A Federated Learn request (to which Front End, which model)
// goes out as it is - the round has one more answer to wait for; a Federated Learn response (the model the Front End trained, as an ML Model Object) comes in and
// is kept; when every answer asked for is in, the models are averaged (federated averaging) and the new NN Model leaves (AIP-MLO-V1.0).
//
// The weights of a model are read as 32-bit floats, little endian, in the first Data item of the ML Model Object (base64): the specification does not say what a
// model holds, and an average needs that. A model of another length than the first answer is refused.
public sealed class HflAimProcessor : IAimProcessor
{
    private readonly string _fdlIn, _fdlOut, _modelOut;
    private int _expected, _round;
    private string _pre = "";
    private readonly List<float[]> _answers = new();

    public string InstanceId { get; }

    public HflAimProcessor(string instanceId, AimPortReader ports)
    {
        InstanceId = instanceId;
        _fdlIn    = ports.Input(AihJson.FederatedLearn);
        _fdlOut   = ports.Output(AihJson.FederatedLearn);
        _modelOut = ports.Output("AIP-MLO-V1.0");
    }

    public Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_fdlIn, out var json) || string.IsNullOrWhiteSpace(json))
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, $"AIH-HFL-V1.1: no Federated Learn on input port '{_fdlIn}'."));
        try
        {
            var outputs = new Dictionary<string, string>();
            var fdl = AihJson.Parse(json, "Federated Learn");
            var item = (fdl["FederatedLearnData"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault() ?? throw new FormatException("the Federated Learn holds nothing.");

            if (item["Request"] is JsonObject request)
            {
                _pre = request["PreLearnNNModelID"]?.GetValue<string>() ?? _pre;
                _expected++;
                outputs[_fdlOut] = json;                                             // to the Front End
            }
            else if (item["Response"] is JsonObject response)
            {
                var weights = Weights(response["PostLearnNNModel"] as JsonObject);
                if (_answers.Count > 0 && _answers[0].Length != weights.Length) throw new FormatException("a model of another size than the first answer.");
                _answers.Add(weights);
                if (_expected > 0 && _answers.Count >= _expected)
                {
                    var mean = new float[weights.Length];
                    foreach (var a in _answers) for (var i = 0; i < mean.Length; i++) mean[i] += a[i] / _answers.Count;
                    var model = new JsonObject
                    {
                        ["Header"] = "AIP-MLO-V1.0", ["MLModelObjectID"] = $"{_pre}-federated-{++_round}",
                        ["MLModelData"] = new JsonArray(new JsonObject { ["Data"] = Convert.ToBase64String(mean.SelectMany(BitConverter.GetBytes).ToArray()) }),
                        ["DescrMetadata"] = $"Federated averaging of {_answers.Count} models"
                    };
                    outputs[_modelOut] = model.ToJsonString();
                    _answers.Clear(); _expected = 0;
                }
            }
            return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = "FederatedLearn", DataType = AihJson.FederatedLearn, Payload = outputs.Values.FirstOrDefault() ?? "", Ports = outputs });
        }
        catch (Exception ex) { return Task.FromResult(Message.Error(message.MessageId, InstanceId, "AIH-HFL-V1.1: " + ex.Message)); }
    }

    private static float[] Weights(JsonObject? model)
    {
        var data = (model?["MLModelData"] as JsonArray)?.OfType<JsonObject>().Select(o => o["Data"]?.GetValue<string>()).FirstOrDefault(d => !string.IsNullOrEmpty(d))
                   ?? throw new FormatException("the model carries no data.");
        var bytes = Convert.FromBase64String(data);
        if (bytes.Length % 4 != 0) throw new FormatException("the model is not 32-bit floats.");
        var weights = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, weights, 0, bytes.Length);
        return weights;
    }
}
