using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;

using Mpai.Core;
using Mpai.Mmc.Spe;

namespace Mpai.Mmc.Psi;

// MMC-PSI-V2.5 - PS-Speech Interpretation. Receives the Speech Descriptors Object of a speech (the valence, arousal and dominance that Entity Speech Description reads from it with wav2vec 2.0,
// ESD-V2.5-I03) and gives the Speech Personal Status. The interpretation is that of MMC-SPE, which does description and interpretation in one step: the (valence, arousal) point is
// mapped to an Emotion by the quadrants of the affective circumplex, and the dominance to a Social Attitude (see SpeAimProcessor.ToSpeechPersonalStatus).
public sealed class PsiAimProcessor : IAimProcessor
{
    private readonly string _inPort, _outPort;
    public string InstanceId { get; }

    public PsiAimProcessor(string instanceId, AimPortReader ports)
    {
        InstanceId = instanceId;
        _inPort = ports.Input("MMC-SDO-V2.5");
        _outPort = ports.Output("MMC-SPS-V2.5");
    }

    public Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_inPort, out var json) || string.IsNullOrWhiteSpace(json))
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "MMC-PSI-V2.5: no Speech Descriptors Object to interpret."));
        SpeechAffect affect;
        try { affect = EsdAffectAimProcessor.Affect(json); }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        { return Task.FromResult(Message.Error(message.MessageId, InstanceId, "MMC-PSI-V2.5: " + ex.Message)); }
        var outJson = MpaiJson.ToJson(SpeAimProcessor.ToSpeechPersonalStatus(affect));
        return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = message.MessageType, Ports = new Dictionary<string, string> { [_outPort] = outJson } });
    }
}

public sealed class PsiPlugin : IAimPlugin
{
    public string AimName => "MMC-PSI-V2.5";
    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings) => new PsiAimProcessor(AimName, ports);
}
