using AIF.Controller;
using Mpai.Core;

namespace Mpai.Aims.Ocr;

// MMC-OCR-V2.5 as an AIF IAimProcessor: a Basic Visual Object (OSD-BVO) in, a Text
// Object (OSD-TXO) out - the page's lines.
public sealed class OcrAimProcessor : IAimProcessor
{
    private readonly string _inputPort;    // OSD-BVO-V1.5
    private readonly string _pointPort;    // OSD-PNT-V1.5, optional: where on the page to read
    private readonly string _outputPort;   // OSD-TXO-V1.5
    private readonly IOcrAim _ocr;

    public string InstanceId { get; }

    public OcrAimProcessor(string instanceId, IOcrAim ocr, AimPortReader ports)
    {
        InstanceId  = instanceId;
        _ocr        = ocr;
        _inputPort  = ports.Input("OSD-BVO-V1.5");
        _pointPort  = ports.InputOrDefault("OSD-PNT-V1.5", "");
        _outputPort = ports.Output("OSD-TXO-V1.5");
    }

    public async Task<Message> ProcessAsync(Message message)
    {
        var image = message.Ports.TryGetValue(_inputPort, out var json) && !string.IsNullOrWhiteSpace(json)
            ? MpaiJson.FromJson<BasicVisualObject>(json) ?? new BasicVisualObject()
            : new BasicVisualObject();
        (double X, double Y)? around = null;
        if (_pointPort.Length > 0 && message.Ports.TryGetValue(_pointPort, out var pointJson) && !string.IsNullOrWhiteSpace(pointJson))
        {
            var p = System.Text.Json.Nodes.JsonNode.Parse(pointJson);
            var c = p?["Coordinates"] ?? p;                       // the Point: Coordinates {X, Y}; X and Y alone are accepted
            if (c?["X"] is { } x && c["Y"] is { } y) around = ((double)x, (double)y);
        }
        var text = await _ocr.ProcessAsync(image, around);
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
