using System;
using System.Text;
using System.Text.Json.Nodes;

namespace Mpai.Aif.PortData;

// OSD-B3O-V1.5 - Basic 3D Model Object: the Avatar Model a reply animates - offered by a client with each utterance, since the client holds the model and
// its page draws it.
//
// It crosses MPAI-MAS in the form its schema gives it: carried as it is, its Header checked. Without this codec a Remote Client Application could
// not give it, and every Module that renders with RSR was out of reach over MPAI-MAS (found testing the Linux update, 2026/10/04).
public sealed class Basic3DModelObjectCodec : IPortDataCodec
{
    public string DataType => "OSD-B3O-V1.5";

    public byte[] ToWire(string internalJson) =>
        Encoding.UTF8.GetBytes(Checked(internalJson).ToJsonString());

    public string ToInternal(byte[] wire) =>
        Checked(Encoding.UTF8.GetString(wire)).ToJsonString();

    private static JsonObject Checked(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject
                   ?? throw new FormatException("Basic 3D Model Object is not a JSON object.");
        var header = (string?)root["Header"];
        if (header is not null && header != "OSD-B3O-V1.5")
            throw new FormatException($"Basic 3D Model Object carries the Header '{header}'.");
        root["Header"] = "OSD-B3O-V1.5";
        return root;
    }
}
