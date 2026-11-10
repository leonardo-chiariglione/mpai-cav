// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

using AIF.Controller;

using UscSpeech = Mpai.Osd.Speech.SpeechObject;
using Mpai.Cae.Usc.Voice;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Osd.Speech;

namespace Mpai.Mmc.Tsd;

// MMC-ESD-V2.5 - Entity Speech Description, the instance whose Speech Descriptors are the TONE-COLOUR EMBEDDING of the voice (OpenVoice V2, 256 numbers) - what Text-to-Speech with Descriptors speaks with.
// The other instance (MMC-ESD-V2.5-I01) gives an ECAPA-TDNN speaker embedding, for recognising a speaker; this gives the descriptors of the voice for speaking in it. Same AIM, same Ports, same Data Type
// (a Speech Descriptors Object): the Qualifier of the Object says which of the two it holds.
public sealed class EsdOpenVoiceAimProcessor : IAimProcessor
{
    private readonly OpenVoiceWorker _worker;
    private readonly string _inputPort, _outputPort;

    public string InstanceId { get; }

    public EsdOpenVoiceAimProcessor(string instanceId, OpenVoiceWorker worker, AimPortReader ports)
    {
        InstanceId = instanceId; _worker = worker;
        _inputPort = ports.Input("OSD-BSO-V1.5");
        _outputPort = ports.Output("MMC-SDO-V2.5");
    }

    public Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_inputPort, out var speechJson) || string.IsNullOrWhiteSpace(speechJson))
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "MMC-ESD-V2.5: no Speech Object on the input Port."));
        try
        {
            var audio = UscSpeech.Read(SchemaForm.Speech(speechJson));
            if (audio.Count == 0) return Task.FromResult(Message.Error(message.MessageId, InstanceId, "MMC-ESD-V2.5: the Speech Object holds no speech."));
            var sdo = SpeechDescriptorsObject.FromEmbedding(_worker.Extract(audio), TsdAimProcessor.DescriptorFormat);
            return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = message.MessageType, Ports = new Dictionary<string, string> { [_outputPort] = MpaiJson.ToJson(sdo) } });
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or JsonException)
        {
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "MMC-ESD-V2.5: " + ex.Message));
        }
    }
}
