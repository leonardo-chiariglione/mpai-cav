using System;
using System.Text;
using System.Text.Json;

namespace Mpai.Aif.PortData;

// string - an ID, as the AIF's type system carries it (M3245; the author): a Machine ID, an Avatar ID. NOT AN MPAI DATA TYPE, and so no schema:
// an ID is a declared name, not what identifying gives (that is an Instance Identifier). Inside and on the wire it is the same JSON string.
public sealed class StringCodec : IPortDataCodec
{
    public string DataType => "string";

    public byte[] ToWire(string internalJson) => Encoding.UTF8.GetBytes(Parse(internalJson));

    public string ToInternal(byte[] wire) => JsonSerializer.Serialize(Encoding.UTF8.GetString(wire));

    private static string Parse(string text)
    {
        var trimmed = text.Trim();
        return trimmed.StartsWith('"') ? JsonSerializer.Deserialize<string>(trimmed) ?? "" : trimmed;
    }
}
