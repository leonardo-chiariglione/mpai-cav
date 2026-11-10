// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;

using Mpai.Aims.Tts;
using UscSpeech = Mpai.Osd.Speech.SpeechObject;
using Mpai.Cae.Usc.Voice;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Osd.Speech;

namespace Mpai.Mmc.Tsd;

// MMC-TSD-V2.5 - Text-to-Speech with Descriptors, as an AIF IAimProcessor.
//
// The Text Object is spoken - by the Text-To-Speech of MPAI-MMC (Piper), in the voice of its own - and each piece of that speech is then given the voice described by the Speech Descriptors Object: a tone-colour
// embedding of the voice (OpenVoice V2, 256 numbers), which the Qualifier of the Object names (DescriptorFormat). A Speech Descriptors Object whose Qualifier names another format - an ECAPA-TDNN speaker
// embedding, say - cannot drive this voice engine and is refused, rather than speak in another voice.
//
// The AIM is the Speech Model route of Text-To-Speech (VoiceCloningTtsAimProcessor) with the voice given by the Speech Descriptors Object of the Entity instead of by a Neural Network Speech Model: what Entity Speech
// Description (the OpenVoice one, EsdOpenVoiceAimProcessor) gives from the speech of the Entity is what this takes.
public sealed class TsdAimProcessor : IAimProcessor
{
    // A value of TFA/V1.5/formats/SpeechDescriptorsFormats.json.
    public const string DescriptorFormat = "OpenVoice Tone-Colour Embedding (256-d)";

    private readonly PiperTtsAim _tts;
    private readonly OpenVoiceWorker _worker;
    private readonly string _textPort, _descriptorsPort, _selectorPort, _outputPort;

    public string InstanceId { get; }

    public TsdAimProcessor(string instanceId, PiperTtsAim tts, OpenVoiceWorker worker, AimPortReader ports)
    {
        InstanceId = instanceId; _tts = tts; _worker = worker;
        _textPort = ports.Input("OSD-BTO-V1.5");
        _descriptorsPort = ports.Input("MMC-SDO-V2.5");
        _selectorPort = ports.InputOrDefault("OSD-SEL-V1.5", "");
        _outputPort = ports.Output("OSD-BSO-V1.5");
    }

    public async Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_textPort, out var textJson) || string.IsNullOrWhiteSpace(textJson))
            return Message.Error(message.MessageId, InstanceId, "MMC-TSD-V2.5: no Text Object to speak.");
        if (!message.Ports.TryGetValue(_descriptorsPort, out var sdoJson) || string.IsNullOrWhiteSpace(sdoJson))
            return Message.Error(message.MessageId, InstanceId, "MMC-TSD-V2.5: no Speech Descriptors Object to speak with.");

        float[] embedding;
        try { embedding = Embedding(sdoJson); }
        catch (Exception ex) when (ex is FormatException or JsonException) { return Message.Error(message.MessageId, InstanceId, "MMC-TSD-V2.5: " + ex.Message); }

        try
        {
            var text = MpaiJson.FromJson<BasicTextObject>(textJson) ?? throw new FormatException("The Text Object cannot be read.");
            var spoken = await _tts.ProcessAsync(text, "");
            var pieces = UscSpeech.Read(SchemaForm.Speech(MpaiJson.ToJson(spoken)));
            // The Selector (optional) says whether the output Speech keeps the input Speech Descriptors (OSD-SEL-V1.5 'SpeechDescriptors'); without a Selector, or without that field, it does.
            var keep = KeepsDescriptors(message);
            var cloned = (keep ? _worker.Clone(embedding, pieces) : pieces).Select(a => a.At(16000)).ToArray();
            var json = UscSpeech.MakeSynthetic("tsd-" + Guid.NewGuid().ToString("N")[..12], cloned);
            return new Message { MessageId = message.MessageId, MessageType = "BasicSpeechObject", DataType = UscSpeech.Header, Payload = json, Ports = new Dictionary<string, string> { [_outputPort] = json } };
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or JsonException) { return Message.Error(message.MessageId, InstanceId, "MMC-TSD-V2.5: " + ex.Message); }
    }

    private bool KeepsDescriptors(Message message)
    {
        if (_selectorPort.Length == 0 || !message.Ports.TryGetValue(_selectorPort, out var json) || string.IsNullOrWhiteSpace(json)) return true;
        try { return (JsonNode.Parse(json) as JsonObject)?["SpeechDescriptors"]?.GetValue<bool>() ?? true; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return true; }
    }

    // The tone-colour embedding in a Speech Descriptors Object, or why it is not one.
    public static float[] Embedding(string sdoJson)
    {
        var root = JsonNode.Parse(sdoJson) as JsonObject ?? throw new FormatException("The Speech Descriptors Object is not a JSON object.");
        var format = root["SpeechDescriptorsQualifier"]?["Formats"]?["OtherSpeechDescriptorsFormats"]?.GetValue<string>();
        if (format != DescriptorFormat)
            throw new FormatException($"The Speech Descriptors Object is of format '{format ?? "(none)"}': this voice engine takes '{DescriptorFormat}'.");
        var data = (root["SpeechDescriptorsData"] as JsonArray)?.OfType<JsonObject>().Select(o => o["Data"]?.GetValue<string>()).FirstOrDefault(d => !string.IsNullOrEmpty(d))
                   ?? throw new FormatException("The Speech Descriptors Object carries no data (the Data of an item; a reference is for the Controller to fetch).");
        var embedding = EmbeddingCodec.FromBase64(data);
        if (embedding.Length != OpenVoiceWorker.EmbeddingLength)
            throw new FormatException($"The Speech Descriptors Object has {embedding.Length} numbers: it is not a tone-colour embedding ({OpenVoiceWorker.EmbeddingLength}).");
        return embedding;
    }
}
