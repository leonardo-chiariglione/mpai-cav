// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;
using Mpai.Core;

namespace Mpai.Aims.Asr;

// MMC-ASR-V2.5 â€” self-contained IAimProcessor.
// Reads its own port names from 1MMC-ASR-V2.5-I01.json at startup.
public sealed class AsrAimProcessor : IAimProcessor
{
    // TWO PATHS THROUGH ONE RECOGNISER. Speech at Port 1 is a question about an
    // image and its text goes on to whatever consumes it; speech at Port 2 is a
    // reply to something the User Agent asked, and its text is for the User Agent
    // to read. The Port the speech arrives on says which it is, so nothing has to
    // infer it from what happens to be missing.
    private readonly string        _imageIn;
    private readonly string        _replyIn;
    private readonly string        _imageOut;
    private readonly string        _replyOut;
    private readonly WhisperAsrAim _asr;

    // TELEVISION MEDIA ANALYSIS (OSD-TMA). The speech to recognise arrives at Port 3 and its text goes out at Port 1, the Recognised Text; the Auxiliary Text, the Speaker ID and the
    // Speech Overlap are optional Inputs of an instance of the AIM that declares them, kept as they arrive: the Auxiliary Text is whisper's initial prompt, the Speaker ID and the
    // Overlap are noted in the log. An instance that does not declare them has the two Ports of before.
    private readonly string _toRecognise, _textIn, _speakerIn, _overlapIn;
    private string? _context, _speaker;
    private bool _overlap;

    public string InstanceId { get; }

    public AsrAimProcessor(
        string        instanceId,
        WhisperAsrAim asr,
        AimPortReader      ports)
    {
        InstanceId   = instanceId;
        _asr         = asr;
        _imageIn  = ports.Input("OSD-BSO-V1.5", 1);
        _replyIn  = ports.Input("OSD-BSO-V1.5", 2);
        _imageOut = ports.Output("OSD-BTO-V1.5", 1);
        _replyOut = ports.Output("OSD-BTO-V1.5", 2);
        _toRecognise = ports.InputOrDefault("OSD-BSO-V1.5", 3, "");
        _textIn      = ports.InputOrDefault("OSD-TXO-V1.5", 1, "");
        _speakerIn   = ports.InputOrDefault("OSD-IID-V1.5", 1, "");
        _overlapIn   = ports.InputOrDefault("MMC-SOL-V2.5", 1, "");
    }

    public async Task<Message> ProcessAsync(Message message)
    {
        // Whichever Port carried it decides where the text goes.
        System.Console.WriteLine($"[ASR] ports in: {string.Join(", ", message.Ports.Keys)}; looking for reply={_replyIn} image={_imageIn}");
        if (_textIn.Length > 0 && message.Ports.TryGetValue(_textIn, out var contextJson)) _context = ContextOf(contextJson);
        if (_speakerIn.Length > 0 && message.Ports.TryGetValue(_speakerIn, out var speakerJson)) _speaker = System.Text.Json.Nodes.JsonNode.Parse(speakerJson)?["InstanceIdentifier"]?.GetValue<string>();
        if (_overlapIn.Length > 0 && message.Ports.ContainsKey(_overlapIn)) _overlap = true;
        var reply  = message.Ports.ContainsKey(_replyIn);
        var television = _toRecognise.Length > 0 && message.Ports.ContainsKey(_toRecognise);
        var source = television ? _toRecognise : reply ? _replyIn : _imageIn;
        if (!message.Ports.TryGetValue(source, out var speechJson)) return null!;
        var speech = Mpai.Aif.PortData.PortObjects.ReadSpeech(speechJson);
        try { int __r7=(speech?.SpeechQualifier?.Format?.ContentFormats?.RawData?.SamplingFrequency is double __rf && __rf>0)?(int)__rf:16000; Mpai.Core.MpaiDiag.DumpPcm(speech?.Data, __r7, "7_ASR_in"); } catch {}
        try { if (speech?.Data?.Length > 0) Mpai.Core.MpaiDiag.WriteBytes("C_asr_in.wav", speech.Data); Mpai.Core.MpaiDiag.Append("trace.log", "ASR-IN bytes=" + (speech?.Data?.Length ?? -1) + System.Environment.NewLine); } catch {}
        try { Mpai.Core.MpaiDiag.Append("hci-diag.log", System.DateTime.Now.ToString("HH:mm:ss.fff") + "  [ASR-IN] bytes=" + (speech?.Data?.Length ?? -1) + System.Environment.NewLine); if (speech?.Data?.Length > 0) Mpai.Core.MpaiDiag.WriteBytes("asr-input.wav", speech.Data); } catch {}

        Trace("[ASR-IN] speechBytes=" + (speech?.Data?.Length ?? -1));

        try { /*AMQTRACE_ASR_IN*/ if(speech?.Data?.Length>0) Mpai.Core.MpaiDiag.WriteBytes("3_ASR_in.wav", speech.Data); } catch {}
        var text = await _asr.ProcessAsync(speech, television ? _context : null);
        if (television && (_speaker is not null || _overlap)) System.Console.WriteLine("[MMC-ASR-V2.5] speaker " + (_speaker ?? "unknown") + (_overlap ? ", overlapped speech" : ""));

        Trace("[ASR-OUT] text=<" + (text?.GetText() ?? "<null>") + ">");

        var json = MpaiJson.ToJson(text);
        try { /*AMQTRACE_ASR_OUT*/ Mpai.Core.MpaiDiag.WriteText("4_ASR_out.txt", text?.GetText() ?? "(null)"); } catch {}

        return new Message
        {
            MessageId   = message.MessageId,
            MessageType = "BasicTextObject",
            DataType    = text.Header,
            Payload     = json,
            Ports       = new Dictionary<string, string> { [reply ? _replyOut : _imageOut] = json }
        };
    }

    // The words of an Auxiliary Text: those of its Basic Text Objects (or of the Basic Text Object itself).
    private static string? ContextOf(string json)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json);
            var words = new List<string>();
            void Take(System.Text.Json.Nodes.JsonNode? n)
            {
                if (n is System.Text.Json.Nodes.JsonObject o)
                {
                    if (o["BasicTextObjects"] is System.Text.Json.Nodes.JsonArray many) foreach (var b in many) Take(b);
                    else if (o["BasicTextData"] is System.Text.Json.Nodes.JsonArray data) foreach (var d in data) if ((string?)d?["Data"] is { } t) words.Add(t);
                }
            }
            Take(node);
            return words.Count > 0 ? string.Join(" ", words) : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static void Trace(string m)
    {
        Mpai.Core.MpaiDiag.Append("asr-trace.log",
            System.DateTime.Now.ToString("HH:mm:ss.fff") + "  " + m + "\n");
    }
}
