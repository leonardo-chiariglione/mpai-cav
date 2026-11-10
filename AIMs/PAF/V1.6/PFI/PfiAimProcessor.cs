using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;

using Mpai.Core;
using Mpai.Paf.Fpe;

namespace Mpai.Paf.Pfi;

// PAF-PFI-V1.6 - PS-Face Interpretation. Receives the Face Descriptors Object of a face (the HSEmotion probabilities, valence and arousal that Entity Face Description reads from it,
// EFD-V1.6-I02) and gives the Face Personal Status. The interpretation is that of PAF-FPE, which does description and interpretation in one step (FpeAimProcessor.ToFacePersonalStatus):
// the most probable emotion of AffectNet is mapped to an Emotion of the vocabulary (surprise to the Cognitive State Surprise), with its probability as Degree.
public sealed class PfiAimProcessor : IAimProcessor
{
    private readonly string _firstPort, _secondPort, _outPort;
    public string InstanceId { get; }

    public PfiAimProcessor(string instanceId, AimPortReader ports)
    {
        InstanceId = instanceId;
        _firstPort = ports.Input("PAF-FDO-V1.6", 1);
        _secondPort = ports.InputOrDefault("PAF-FDO-V1.6", 2, "");
        _outPort = ports.Output("MMC-FPS-V2.5");
    }

    public Task<Message> ProcessAsync(Message message)
    {
        string? json = null;
        foreach (var port in new[] { _firstPort, _secondPort })
            if (port.Length > 0 && message.Ports.TryGetValue(port, out var j) && !string.IsNullOrWhiteSpace(j)) { json = j; break; }
        if (json is null) return Task.FromResult(Message.Error(message.MessageId, InstanceId, "PAF-PFI-V1.6: no Face Descriptors Object to interpret."));
        FaceAffect affect;
        try { affect = EfdAffectAimProcessor.Affect(json); }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        { return Task.FromResult(Message.Error(message.MessageId, InstanceId, "PAF-PFI-V1.6: " + ex.Message)); }
        var outJson = MpaiJson.ToJson(FpeAimProcessor.ToFacePersonalStatus(affect));
        return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = message.MessageType, Ports = new Dictionary<string, string> { [_outPort] = outJson } });
    }
}

public sealed class PfiPlugin : IAimPlugin
{
    public string AimName => "PAF-PFI-V1.6";
    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings) => new PfiAimProcessor(AimName, ports);
}
