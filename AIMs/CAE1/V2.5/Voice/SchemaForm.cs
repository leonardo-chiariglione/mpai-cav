using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;
using Mpai.Aif.PortData;

namespace Mpai.Cae.Usc.Voice;

// The Speech Object as the schema writes it. The Text To Speech of MPAI-MMC (Piper) gives its Speech Object in the form it uses inside, which has not the names of the schema (the
// codec of the Data Type is what turns one into the other); the AIMs of CAE-USC read the schema's form.
public static class SchemaForm
{
    public static string Speech(string json) =>
        JsonNode.Parse(json)?["BasicSpeechObjectData"] is not null ? json : System.Text.Encoding.UTF8.GetString(new BasicSpeechObjectCodec().ToWire(json));
}

// THE TEXT TO SPEECH OF MPAI-MMC, AS THE SPEECH RESTORATION SYSTEM USES IT. The AIM speaks the whole Text Object as ONE utterance (it joins the texts), and the Restoration Assembly needs
// a piece of speech for each text, in order, to put in each damaged section: this asks the AIM for one text at a time and gives the pieces as one Speech Object (Source Synthetic), in the
// schema's form.
public sealed class TextByTextTtsAimProcessor(IAimProcessor inner, string textKey, string outputKey) : IAimProcessor
{
    public string InstanceId => inner.InstanceId;

    public async Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(textKey, out var textJson) || string.IsNullOrWhiteSpace(textJson))
            return Message.Error(message.MessageId, InstanceId, $"MMC-TTS-V2.5: no Text on input port '{textKey}'.");
        try
        {
            var root = JsonNode.Parse(textJson) as JsonObject ?? throw new FormatException("The Text is a JSON object.");
            var items = (root["BasicTextData"] as JsonArray ?? throw new FormatException("The Text has no BasicTextData.")).OfType<JsonObject>().ToList();
            var pieces = new List<Audio>();
            foreach (var item in items)
            {
                var one = (JsonObject)root.DeepClone(); one["BasicTextData"] = new JsonArray(item.DeepClone());
                var ports = new Dictionary<string, string>(message.Ports) { [textKey] = one.ToJsonString() };
                var reply = await inner.ProcessAsync(new Message { MessageId = message.MessageId, MessageType = message.MessageType, DataType = message.DataType, Payload = message.Payload, Ports = ports });
                if (reply.IsError) return reply;
                pieces.AddRange(SpeechObject.Read(SchemaForm.Speech(reply.Payload)));
            }
            var json = SpeechObject.MakeSynthetic("tts-" + Guid.NewGuid().ToString("N")[..12], pieces.ToArray());
            return new Message { MessageId = message.MessageId, MessageType = "BasicSpeechObject", DataType = SpeechObject.Header, Payload = json, Ports = new Dictionary<string, string> { [outputKey] = json } };
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException) { return Message.Error(message.MessageId, InstanceId, "MMC-TTS-V2.5: " + ex.Message); }
    }
}