using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Cae.Usc.Eae;

// CAE-AMX-V2.5, Audio Descriptors Multiplexing: the Enhanced Audio Objects (in the time domain: a mono file for each source, its ID the ID of the source), the Audio Scene Geometry (Basic Audio
// Scene Descriptors that name the sources) and, if there is one, the Space-Time of the scene in; the Audio Scene Descriptors out - "the combination of Audio Scene Geometry and Audio Objects"
// (page): each source of the Geometry carries its Audio Object in place of its ID. A source with no audio keeps its ID; an Audio Object no source names is added as a member with no Space-Time.
public sealed class AmxAimProcessor(string instanceId, string spaceTimeKey, string audioKey, string geometryKey, string outputKey) : IAimProcessor
{
    public string InstanceId { get; } = instanceId;

    public Task<Message> ProcessAsync(Message message)
    {
        string? Get(string key) => message.Ports.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        var audioJson = Get(audioKey); var geometryJson = Get(geometryKey);
        if (audioJson is null || geometryJson is null)
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "CAE-AMX-V2.5: it needs the Enhanced Audio Objects and the Audio Scene Geometry."));
        try
        {
            var items = AudioObject.Read(audioJson);
            var scene = JsonNode.Parse(geometryJson) as JsonObject ?? throw new FormatException("The Audio Scene Geometry is a JSON object.");
            if (scene["Header"]?.GetValue<string>() != SceneDescriptors.Header) throw new FormatException($"The Audio Scene Geometry is not Basic Audio Scene Descriptors: Header {scene["Header"]}.");
            var members = scene["BasicAudioSceneDescriptorsData"] as JsonArray ?? throw new FormatException("The Audio Scene Geometry has no members.");
            var unused = items.ToList();
            foreach (var member in members.OfType<JsonObject>())
            {
                var id = member["AObjectIDOrAObject"] is JsonArray a && a.Count == 1 && a[0] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                var audio = unused.FirstOrDefault(i => i.Id == id);
                if (audio is null) continue;
                unused.Remove(audio);
                member["AObjectIDOrAObject"] = new JsonArray(JsonNode.Parse(AudioObject.Make("ao-" + id, audio)));
            }
            foreach (var audio in unused)
                members.Add(new JsonObject { ["AObjectIDOrAObject"] = new JsonArray(JsonNode.Parse(AudioObject.Make("ao-" + audio.Id, audio))) });
            scene["AudioObjectCount"] = members.Count;
            scene["BasicAudioSceneDescriptorsID"] = "amx-" + Guid.NewGuid().ToString("N")[..12];
            if (Get(spaceTimeKey) is { } spaceTime)
            {
                var st = JsonNode.Parse(spaceTime) as JsonObject;
                if (st?["Header"]?.GetValue<string>() != "OSD-SPT-V1.5") throw new FormatException("The Space-Time is not an OSD-SPT-V1.5.");
                scene["BASSpaceTime"] = st;
            }
            var text = scene.ToJsonString();
            return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = "BasicAudioSceneDescriptors", DataType = SceneDescriptors.Header, Payload = text, Ports = new Dictionary<string, string> { [outputKey] = text } });
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "CAE-AMX-V2.5: " + ex.Message));
        }
    }
}
