using System;
using System.Text;
using System.Text.Json.Nodes;

namespace Mpai.Aif.PortData;

// A Data Type whose instance is the JSON object its schema describes, the same inside the process and on the wire: the Objects, Scenes and User Commands of
// Audio Scene Management (OSD-BAO, OSD-BAS, OSD-BSS, OSD-BMS, OSD-AUO, OSD-OPV, CAE-UCM) are written and read in that form by the AIMs, so there is nothing to
// convert; what the codec does is refuse what is not a JSON object, or is the instance of another Data Type (its Header says which).
public sealed class JsonObjectCodec(string dataType) : IPortDataCodec
{
    public string DataType => dataType;

    public byte[] ToWire(string internalJson) => Encoding.UTF8.GetBytes(Checked(internalJson));

    public string ToInternal(byte[] wire) => Checked(Encoding.UTF8.GetString(wire));

    private string Checked(string json)
    {
        var instance = JsonNode.Parse(json) as JsonObject ?? throw new FormatException($"{dataType} is not a JSON object.");
        if (instance["Header"]?.ToString() is { Length: > 0 } header && header != dataType)
            throw new FormatException($"{dataType} carries the Header {header}.");
        return json;
    }
}
