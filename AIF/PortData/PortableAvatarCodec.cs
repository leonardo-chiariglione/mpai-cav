using System;
using System.Text;
using System.Text.Json.Nodes;

namespace Mpai.Aif.PortData;

// PAF-PAV-V1.6 - Portable Avatar: an avatar, with what it says and how - as the
// Virtual Meeting Secretary takes it from each participant, and gives its own.
//
// It crosses MPAI-MAS as Portable Avatar Multiplexing writes it: carried as it is,
// its Header checked, as the Portable Avatar is. ITS COMPONENTS ARE IN THE FORM
// THEY HAVE INSIDE THIS IMPLEMENTATION - the Basic Text and Speech Objects where the
// schema has Text and Speech Objects, for one - so another implementation reading
// it would have to know that form. Converting each component to its schema form,
// with the codecs of their Data Types, is to be done before the Portable Avatar is
// exchanged with other implementations.
public sealed class PortableAvatarCodec : IPortDataCodec
{
    public string DataType => "PAF-PAV-V1.6";

    public byte[] ToWire(string internalJson) =>
        Encoding.UTF8.GetBytes(Checked(internalJson).ToJsonString());

    public string ToInternal(byte[] wire) =>
        Checked(Encoding.UTF8.GetString(wire)).ToJsonString();

    private static JsonObject Checked(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject
                   ?? throw new FormatException("Portable Avatar is not a JSON object.");
        var header = (string?)root["Header"];
        if (header is not null && header != "PAF-PAV-V1.6")
            throw new FormatException($"Portable Avatar carries the Header '{header}'.");
        root["Header"] = "PAF-PAV-V1.6";
        return root;
    }
}
