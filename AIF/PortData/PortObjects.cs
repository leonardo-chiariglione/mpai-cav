using System;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Aif.PortData;

// THE OBJECTS AT THE PORTS OF AN AIM.
//
// The model of MPAI: between AIMs flow OBJECTS - Data and its Qualifier, in the form their Data Type defines (the schema). Inside an AIM there is the data it processes, and the AIM
// keeps no more of the Qualifier than it needs. An AIM can be replaced by another that has the same Ports, because what crosses a Port is the same thing whoever made it.
//
// So an AIM that has an Object at a Port READS it as the schema defines it (Port -> data), works on the data, and WRITES what it gives as an Object of the same form (data -> Port).
// The classes of the repository (BasicSpeechObject with a flat Data, ...) are what an AIM works on after the Port; they are not what crosses it.
//
// ReadSpeech/ReadVisual are tolerant: an Object in the form the repository used before this rule (flat Data) is read too, so that a Message from an AIM not yet changed is understood.
// What is written is always the form of the schema.
public static class PortObjects
{
    private static readonly BasicSpeechObjectCodec SpeechCodec = new();
    private static readonly BasicVisualObjectCodec VisualCodec = new();

    // ---- reading -----------------------------------------------------------------------------------------------------------------------------

    public static bool IsStandardSpeech(JsonNode? n) => n is JsonObject o && o["BasicSpeechObjectData"] is not null;
    public static bool IsStandardVisual(JsonNode? n) => n is JsonObject o && o["BasicVisualObjectData"] is not null;

    // A Basic Speech Object at a Port, as the data and Qualifier the AIM works on.
    public static BasicSpeechObject ReadSpeech(string json) =>
        MpaiJson.FromJson<BasicSpeechObject>(IsStandardSpeech(JsonNode.Parse(json)) ? SpeechCodec.ToInternal(Encoding.UTF8.GetBytes(json)) : json);

    // A Basic Visual Object at a Port, as the data and Qualifier the AIM works on.
    public static BasicVisualObject ReadVisual(string json) =>
        MpaiJson.FromJson<BasicVisualObject>(IsStandardVisual(JsonNode.Parse(json)) ? VisualCodec.ToInternal(Encoding.UTF8.GetBytes(json)) : json);

    // A Speech Object at a Port: the Basic Speech Objects it holds are Objects too, and are read the same way.
    public static SpeechObject ReadSpeechObject(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("The Speech Object is not a JSON object.");
        if (root["BasicSpeechObjects"] is JsonArray entries)
            foreach (var entry in entries.OfType<JsonObject>())
                if (entry["BSObjectIDOrBSObject"] is JsonArray held)
                    for (var i = 0; i < held.Count; i++)
                        if (IsStandardSpeech(held[i]))
                            held[i] = JsonNode.Parse(SpeechCodec.ToInternal(Encoding.UTF8.GetBytes(held[i]!.ToJsonString())));
        return MpaiJson.FromJson<SpeechObject>(root.ToJsonString());
    }

    // ---- writing -----------------------------------------------------------------------------------------------------------------------------

    public static string WriteSpeech(BasicSpeechObject speech) => Encoding.UTF8.GetString(SpeechCodec.ToWire(MpaiJson.ToJson(speech)));
    public static string WriteVisual(BasicVisualObject visual) => Encoding.UTF8.GetString(VisualCodec.ToWire(MpaiJson.ToJson(visual)));

    // A Speech Object for a Port: its Basic Speech Objects written as Objects.
    public static string WriteSpeechObject(SpeechObject speech)
    {
        var root = JsonNode.Parse(MpaiJson.ToJson(speech))!.AsObject();
        if (root["BasicSpeechObjects"] is JsonArray entries)
            foreach (var entry in entries.OfType<JsonObject>())
                if (entry["BSObjectIDOrBSObject"] is JsonArray held)
                    for (var i = 0; i < held.Count; i++)
                        if (!IsStandardSpeech(held[i]))
                            held[i] = JsonNode.Parse(Encoding.UTF8.GetString(SpeechCodec.ToWire(held[i]!.ToJsonString())));
        // The schema requires the Space-Time of the Speech Object.
        root["SpeechObjectSpaceTime"] ??= JsonNode.Parse(MpaiJson.ToJson(new SpaceTime { SpaceTimeID = Guid.NewGuid().ToString() }));
        return root.ToJsonString();
    }
}
