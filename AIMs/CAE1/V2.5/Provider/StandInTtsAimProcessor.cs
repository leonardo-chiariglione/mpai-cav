using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Cae.Usc;

// A STAND-IN for MMC-TTS-V2.5 in the Speech Restoration System: it does not speak. It makes, for each text of the Text List (a Basic Text Object: each data item a text), a
// tone of the length of the text and with the level and the pitch the Neural Network Speech Model gives (the weights Speech Model Creation made: mean RMS, mean zero-crossing
// rate and mean duration), and gives them as the Synthesised Speech: ONE Speech Object (Source Synthetic) whose data lists the pieces in the order of the texts. The real
// Text To Speech of MPAI-MMC is Piper's: a voice file. The Speech Model is an input of the AIM in its L2 (AIP-MLO-V1.0, optional).
public sealed class StandInTtsAimProcessor : IAimProcessor
{
    private readonly string _text, _model, _out;

    public string InstanceId { get; }

    public StandInTtsAimProcessor(string instanceId, AimPortReader ports)
    {
        InstanceId = instanceId;
        _text = ports.Input("OSD-BTO-V1.5");
        _model = ports.InputOrDefault("AIP-MLO-V1.0", "");
        _out = ports.Output("OSD-BSO-V1.5");
    }

    public Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_text, out var textJson) || string.IsNullOrWhiteSpace(textJson))
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, $"MMC-TTS-V2.5: no Text on input port '{_text}'."));
        try
        {
            var weights = _model.Length > 0 && message.Ports.TryGetValue(_model, out var m) && !string.IsNullOrWhiteSpace(m) ? Weights(m) : [0.25f, 0.05f, 1f];
            var texts = (JsonNode.Parse(textJson)?["BasicTextData"] as JsonArray ?? throw new FormatException("The Text has no BasicTextData."))
                        .OfType<JsonObject>().Select(i => i["Data"]?.GetValue<string>() ?? "").ToList();
            var level = Math.Clamp(weights[0] * 32768 * Math.Sqrt(2), 100, 30000);
            var hertz = Math.Clamp(weights[1] * 16000 / 2, 80, 400);
            var pieces = texts.Select(t => Tone(Math.Clamp(0.06 * t.Length, 0.2, 5), level, hertz)).ToArray();
            var json = SpeechObject.MakeSynthetic("tts-" + Guid.NewGuid().ToString("N")[..12], pieces);
            return Task.FromResult(new Message
            {
                MessageId = message.MessageId, MessageType = "BasicSpeechObject", DataType = SpeechObject.Header, Payload = json,
                Ports = new Dictionary<string, string> { [_out] = json }
            });
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException) { return Task.FromResult(Message.Error(message.MessageId, InstanceId, "MMC-TTS-V2.5: " + ex.Message)); }
    }

    private static float[] Weights(string modelJson)
    {
        var data = (JsonNode.Parse(modelJson)?["MLModelData"] as JsonArray)?.OfType<JsonObject>().Select(o => o["Data"]?.GetValue<string>()).FirstOrDefault(d => !string.IsNullOrEmpty(d))
                   ?? throw new FormatException("The Speech Model carries no data.");
        var bytes = Convert.FromBase64String(data);
        var w = new float[bytes.Length / 4]; Buffer.BlockCopy(bytes, 0, w, 0, w.Length * 4);
        return w.Length >= 3 ? w : throw new FormatException("The Speech Model is not what Speech Model Creation makes (three weights).");
    }

    private static Audio Tone(double seconds, double level, double hertz)
    {
        const int rate = 16000;
        var s = new short[(int)(seconds * rate)];
        for (var i = 0; i < s.Length; i++) s[i] = (short)(level * Math.Sin(2 * Math.PI * hertz * i / rate));
        return new Audio(s, rate);
    }
}
