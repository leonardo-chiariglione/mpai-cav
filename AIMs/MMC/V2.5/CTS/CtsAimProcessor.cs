using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;
using Mpai.Core;

namespace Mpai.Mmc.Cts;

// MMC-CTS-V2.5 - Column Text Selection. Self-contained IAimProcessor; its Ports are read by Data Type from its
// metadata (1MMC-CTS-V2.5-I01.json).
//
// Takes the lines of a page as a Text Object (OSD-TXO-V1.5: each line a Basic Text Object with its box, in the
// pixels of the page) and, when the person has pointed, the Point (OSD-PNT-V1.5: X and Y in those pixels); gives
// the text of the column the point is at the top of as a Basic Text Object (OSD-BTO-V1.5). With no Point, the
// first column under the headline.
public sealed class CtsAimProcessor : IAimProcessor
{
    private readonly string _pageIn, _pointIn, _textOut;
    private readonly ColumnSelector _selector = new();

    public string InstanceId { get; }

    public CtsAimProcessor(string instanceId, AimPortReader ports)
    {
        InstanceId = instanceId;
        _pageIn    = ports.Input("OSD-TXO-V1.5");
        _pointIn   = ports.InputOrDefault("OSD-PNT-V1.5", "");
        _textOut   = ports.Output("OSD-BTO-V1.5");
    }

    public Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_pageIn, out var pageJson) || string.IsNullOrWhiteSpace(pageJson))
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, $"MMC-CTS-V2.5: no page on input port '{_pageIn}'."));

        List<PageLine> lines;
        try { lines = Lines(MpaiJson.FromJson<TextObject>(pageJson)); }
        catch (Exception ex) { return Task.FromResult(Message.Error(message.MessageId, InstanceId, "MMC-CTS-V2.5: the page is not a Text Object: " + ex.Message)); }

        (double X, double Y)? point = null;
        if (_pointIn.Length > 0 && message.Ports.TryGetValue(_pointIn, out var pointJson) && !string.IsNullOrWhiteSpace(pointJson))
        {
            var p = JsonNode.Parse(pointJson);
            var c = p?["Coordinates"] ?? p;                       // the Point: Coordinates {X, Y}; X and Y alone are accepted
            if (c?["X"] is JsonNode x && c["Y"] is JsonNode y) point = ((double)x, (double)y);
        }

        var column = _selector.Select(lines, point);
        var text = column?.Text ?? "";
        // THE LANGUAGE OF THE COLUMN is stamped on its text, in its Text Qualifier: the voice that speaks it is chosen by it.
        var language = LanguageDetector.Detect(text);
        var qualifier = language is null ? null : new TextQualifier
        {
            TextQualifierID = Guid.NewGuid().ToString(),
            Attributes = new TextAttributes { Language = new Language { LanguageCode = language, LanguageFormat = LanguageFormat.Iso639_1 } }
        };
        var json = MpaiJson.ToJson(BasicTextObject.FromText(text, qualifier));
        return Task.FromResult(new Message
        {
            MessageId = message.MessageId, MessageType = "BasicTextObject", DataType = "OSD-BTO-V1.5", Payload = json,
            Ports = new Dictionary<string, string> { [_textOut] = json }
        });
    }

    // The lines of a Text Object that OCR gave: the text and the box of each, from its Space-Time.
    public static List<PageLine> Lines(TextObject page) =>
        page.BasicTextObjects.Select(e =>
        {
            var tl = e.BasicTextObjectSpaceTime!.SpatialAttitude1!.Position.CartPosition!;
            var br = e.BasicTextObjectSpaceTime!.SpatialAttitude2!.Position.CartPosition!;
            return new PageLine(e.BTObjectIDOrBTObject![0].GetText(), tl[0], tl[1], br[0], br[1]);
        }).ToList();
}
