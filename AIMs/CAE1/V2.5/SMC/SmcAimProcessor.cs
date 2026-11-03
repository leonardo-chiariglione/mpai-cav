using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Cae.Usc.Smc;

// CAE-SMC-V2.5, Speech Model Creation: the Speech Segments for Modelling (a Basic Speech Object, OSD-BSO-V1.5: the Audio Files in its data) in, the Neural Network Speech
// Model (an ML Model Object, AIP-MLO-V1.0) out. The AIM runs once for each set of segments it receives; what the model is made of is what the trainer says.
//
// If the segments hold less speech than the model needs to be of the intended quality, the AIM says so (the report: MPAI_AIFM_AIM_Report) and still gives the model it
// could create: a report is not an error.
public sealed class SmcAimProcessor : IAimProcessor
{
    public const string InputType = "OSD-BSO-V1.5", OutputType = "AIP-MLO-V1.0";

    // Less speech than this, in seconds, in all the segments: the model is of reduced quality.
    public const double MinimumSeconds = 1.0;

    private readonly string _in, _out;
    private readonly ISpeechModelTrainer _trainer;
    private readonly Action<string>? _report;

    public string InstanceId { get; }

    public SmcAimProcessor(string instanceId, string inputKey, string outputKey, ISpeechModelTrainer trainer, Action<string>? report = null)
    {
        InstanceId = instanceId; _in = inputKey; _out = outputKey; _trainer = trainer; _report = report;
    }

    public Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_in, out var json) || string.IsNullOrWhiteSpace(json))
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, $"CAE-SMC-V2.5: no Speech Segments for Modelling on input port '{_in}'."));
        try
        {
            var segments = SpeechObject.Read(json).ToList();
            var model = _trainer.Train(segments);
            if (model.Seconds < MinimumSeconds)
                _report?.Invoke($"CAE-SMC-V2.5: {model.Seconds:0.##} s of speech in {model.SegmentCount} segment(s): the model is of reduced quality.");
            var text = ModelObject(model).ToJsonString();
            return Task.FromResult(new Message
            {
                MessageId = message.MessageId, MessageType = "MLModelObject", DataType = OutputType, Payload = text,
                Ports = new Dictionary<string, string> { [_out] = text }
            });
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "CAE-SMC-V2.5: " + ex.Message));
        }
    }

    // The ML Model Object: the weights as the data of the model, the qualifier saying what it is, the description saying how it was made.
    public static JsonObject ModelObject(TrainedSpeechModel model)
    {
        var bytes = new byte[model.Weights.Length * 4];
        Buffer.BlockCopy(model.Weights, 0, bytes, 0, bytes.Length);
        return new JsonObject
        {
            ["Header"] = OutputType,
            ["MLModelObjectID"] = "smc-" + Guid.NewGuid().ToString("N")[..12],
            ["MLModelData"] = new JsonArray(new JsonObject { ["Data"] = Convert.ToBase64String(bytes) }),
            ["MLModelQualifier"] = new JsonObject
            {
                ["Header"] = "TFA-MMQ-V1.5", ["MLModelQualifierID"] = "smc-q-" + Guid.NewGuid().ToString("N")[..8],
                ["SubTypes"] = new JsonObject(), ["Formats"] = new JsonObject()
            },
            ["DescrMetadata"] = $"Neural Network Speech Model made from {model.SegmentCount} Speech Segment(s) for Modelling ({model.Seconds:0.##} s) by {model.Method}."
        };
    }
}
