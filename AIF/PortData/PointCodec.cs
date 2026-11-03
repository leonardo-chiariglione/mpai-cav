using System;
using System.Text;
using System.Text.Json.Nodes;

namespace Mpai.Aif.PortData;

// OSD-PNT-V1.5 - Point, on the wire as OSD/V1.5/data/Point.json describes it: a Header and the Coordinates X and Y
// (Z where there is one). The User Agent writes it in that form - the pixel of the picture shown that the person
// pointed at - and the Module reads it as it is, so inside and on the wire it is the same instance.
public sealed class PointCodec : IPortDataCodec
{
    public string DataType => "OSD-PNT-V1.5";

    public byte[] ToWire(string internalJson) => Encoding.UTF8.GetBytes(Normalise(internalJson).ToJsonString());

    public string ToInternal(byte[] wire) => Normalise(Encoding.UTF8.GetString(wire)).ToJsonString();

    private static JsonObject Normalise(string json)
    {
        var point = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("Point is not a JSON object.");
        var at = point["Coordinates"] as JsonObject;
        if (at is null || at["X"] is null || at["Y"] is null) throw new FormatException("Point has no Coordinates X and Y.");
        point["Header"] = "OSD-PNT-V1.5";
        return point;
    }
}