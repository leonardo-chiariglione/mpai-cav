using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Cae.Usc.Voice;

// The Text To Speech that speaks in the voice of the Speech Model: the AIM it wraps (the Piper Text To Speech of MPAI-MMC) says the texts in ITS voice; each piece is then given the tone
// colour of the Neural Network Speech Model (the embedding Speech Model Creation made), at 16 kHz as the Speech Restoration System has it. The Speech Model is an input of the AIM in its L2 (optional there);
// without it, or with a model that is not an embedding (the stand-in's), this refuses rather than speak in another voice.
public sealed class VoiceCloningTtsAimProcessor(string instanceId, IAimProcessor speaker, string modelKey, string outputKey, OpenVoiceWorker worker) : IAimProcessor
{
    public string InstanceId { get; } = instanceId;

    public async Task<Message> ProcessAsync(Message message)
    {
        if (modelKey.Length == 0 || !message.Ports.TryGetValue(modelKey, out var modelJson) || string.IsNullOrWhiteSpace(modelJson))
            return Message.Error(message.MessageId, InstanceId, "MMC-TTS-V2.5: no Speech Model to speak in the voice of.");
        float[] embedding;
        try
        {
            var data = (JsonNode.Parse(modelJson)?["MLModelData"] as JsonArray)?.OfType<JsonObject>().Select(o => o["Data"]?.GetValue<string>()).FirstOrDefault(d => !string.IsNullOrEmpty(d)) ?? throw new FormatException("The Speech Model carries no data.");
            var bytes = Convert.FromBase64String(data);
            embedding = new float[bytes.Length / 4]; Buffer.BlockCopy(bytes, 0, embedding, 0, embedding.Length * 4);
            if (embedding.Length != OpenVoiceWorker.EmbeddingLength) throw new FormatException($"The Speech Model has {embedding.Length} numbers: it is not a tone-colour embedding ({OpenVoiceWorker.EmbeddingLength}).");
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException) { return Message.Error(message.MessageId, InstanceId, "MMC-TTS-V2.5: " + ex.Message); }

        var spoken = await speaker.ProcessAsync(message);
        if (spoken.IsError) return spoken;
        try
        {
            var pieces = SpeechObject.Read(SchemaForm.Speech(spoken.Payload));
            var cloned = worker.Clone(embedding, pieces).Select(a => a.At(16000)).ToArray();
            var json = SpeechObject.MakeSynthetic("tts-" + Guid.NewGuid().ToString("N")[..12], cloned);
            return new Message { MessageId = message.MessageId, MessageType = "BasicSpeechObject", DataType = SpeechObject.Header, Payload = json, Ports = new Dictionary<string, string> { [outputKey] = json } };
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException) { return Message.Error(message.MessageId, InstanceId, "MMC-TTS-V2.5: " + ex.Message); }
    }
}