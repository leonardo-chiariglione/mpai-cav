using AIF.Controller;
using Mpai.Core;

namespace Mpai.Aims.Ocr;

// MMC-OCR-V2.5 as an AIF IAimProcessor: a Basic Visual Object (OSD-BVO) in, a Text
// Object (OSD-TXO) out - the page's lines.
public sealed class OcrAimProcessor : IAimProcessor
{
    private readonly string _inputPort;    // OSD-BVO-V1.5
    private readonly string _outputPort;   // OSD-TXO-V1.5
    private readonly IOcrAim _ocr;

    public string InstanceId { get; }

    public OcrAimProcessor(string instanceId, IOcrAim ocr, AimPortReader ports)
    {
        InstanceId  = instanceId;
        _ocr        = ocr;
        _inputPort  = ports.Input("OSD-BVO-V1.5");
        _outputPort = ports.Output("OSD-TXO-V1.5");
    }

    public async Task<Message> ProcessAsync(Message message)
    {
        var image = message.Ports.TryGetValue(_inputPort, out var json) && !string.IsNullOrWhiteSpace(json)
            ? MpaiJson.FromJson<BasicVisualObject>(json) ?? new BasicVisualObject()
            : new BasicVisualObject();
        var text = await _ocr.ProcessAsync(image);
        var output = MpaiJson.ToJson(text);
        return new Message
        {
            MessageId   = message.MessageId,
            MessageType = "TextObject",
            DataType    = "OSD-TXO-V1.5",
            Payload     = output,
            Ports       = new Dictionary<string, string> { [_outputPort] = output }
        };
    }
}
