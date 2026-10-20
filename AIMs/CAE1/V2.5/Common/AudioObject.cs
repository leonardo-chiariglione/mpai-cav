using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace Mpai.Cae.Usc;

// The Basic Audio Object (OSD-BAO-V1.5) as the AIMs of CAE-USC V2.5 that deal with audio (not speech) read and write it. What its data IS the Audio Qualifier (TFA-AUQ-V1.5) says:
//   Formats.TransportFormat.FileFormats "WAV"                      each data item is a WAV file;
//   Formats.ContentFormat.RawData.SampleSpace (TFA-PCM-V1.5)       each data item is raw PCM (16 bit, little endian, mono): PCM.SamplingFrequency, PCM.SamplePrecision;
// and, if the Qualifier says neither, a WAV file. A data item is by Data (base64) or by DataURI (a local file); an item may carry an ID (the restoration it is the result of).
// These are the forms THIS Implementation decodes - the standard restricts none: it says so for what it cannot decode.
public static class AudioObject
{
    public const string Header = "OSD-BAO-V1.5";

    public sealed record Item(string? Id, Audio Audio);

    public static IReadOnlyList<Item> Read(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("An Audio Object is a JSON object.");
        if (root["Header"]?.GetValue<string>() != Header) throw new FormatException($"Not an Audio Object: Header {root["Header"]}.");
        var list = root["BasicAudioObjectData"] as JsonArray ?? throw new FormatException("The Audio Object has no BasicAudioObjectData.");
        var formats = root["AudioQualifier"]?["Formats"];
        var file = formats?["TransportFormat"]?["FileFormats"]?.GetValue<string>();
        var pcm = formats?["ContentFormat"]?["RawData"]?["SampleSpace"]?["PCM"] as JsonObject;
        if (file is not null && file != "WAV") throw new FormatException($"The Audio Object is an audio file of format {file}: this Implementation does not decode it (it decodes WAV).");
        var raw = file is null && pcm is not null;

        var items = new List<Item>();
        foreach (var item in list.OfType<JsonObject>())
        {
            byte[] bytes;
            if (item["Data"] is JsonValue data) bytes = Convert.FromBase64String(data.GetValue<string>());
            else if (item["DataURI"] is JsonValue uri) bytes = File.ReadAllBytes(new Uri(uri.GetValue<string>()).LocalPath);
            else throw new FormatException("A data item of the Audio Object has neither Data nor DataURI.");
            items.Add(new Item(item["ID"]?.GetValue<string>(), raw ? FromPcm(bytes, pcm!) : Audio.FromWav(bytes)));
        }
        return items;
    }

    private static Audio FromPcm(byte[] bytes, JsonObject pcm)
    {
        var rate = (int)(pcm["SamplingFrequency"]?.GetValue<double>() ?? throw new FormatException("The PCM of the Audio Object has no SamplingFrequency."));
        var precision = (int)(pcm["SamplePrecision"]?.GetValue<double>() ?? 16);
        if (precision != 16) throw new FormatException($"The PCM of the Audio Object has a SamplePrecision of {precision}: this Implementation does not decode it (it decodes 16 bit).");
        var samples = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2);
        return new Audio(samples, rate);
    }

    // The data of an object as it is - the bytes of each item and its ID - whatever the object (an Audio Object, an Audio-Visual Object, a Visual Object: dataName says which data) and
    // whatever the format: nothing is decoded. What a Preservation Master keeps.
    public static IReadOnlyList<(string? Id, byte[] Bytes)> Files(string json, string dataName)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("An object is a JSON object.");
        var list = root[dataName] as JsonArray ?? throw new FormatException($"The object has no {dataName}.");
        var files = new List<(string?, byte[])>();
        foreach (var item in list.OfType<JsonObject>())
        {
            byte[] bytes;
            if (item["Data"] is JsonValue data) bytes = Convert.FromBase64String(data.GetValue<string>());
            else if (item["DataURI"] is JsonValue uri) bytes = File.ReadAllBytes(new Uri(uri.GetValue<string>()).LocalPath);
            else throw new FormatException($"A data item of the object has neither Data nor DataURI.");
            files.Add((item["ID"]?.GetValue<string>(), bytes));
        }
        return files;
    }

    // The file format a qualifier names (the first "FileFormat", "FileFormats" or - for an image - "Static" string in it), lower case, or null: the extension of the files of the object.
    public static string? FileFormatOf(string json, string qualifierName)
    {
        string? Find(JsonNode? n) => n switch
        {
            JsonObject o => o.Select(p => (p.Key is "FileFormat" or "FileFormats" or "Static" && p.Value is JsonValue v && v.TryGetValue<string>(out var s)) ? s : Find(p.Value)).FirstOrDefault(s => s is not null),
            JsonArray a => a.Select(Find).FirstOrDefault(s => s is not null),
            _ => null
        };
        return Find((JsonNode.Parse(json) as JsonObject)?[qualifierName])?.ToLowerInvariant();
    }

    // An Audio Object whose data items are FILES of any kind, each with its name as ID: the files of a Preservation Master. Its Qualifier names no format: the name says what a file is.
    public static string MakeFiles(string id, IEnumerable<(string Name, byte[] Bytes)> files) =>
        new JsonObject
        {
            ["Header"] = Header, ["BasicAudioObjectID"] = id,
            ["BasicAudioObjectData"] = new JsonArray(files.Select(f => (JsonNode)new JsonObject { ["ID"] = f.Name, ["Data"] = Convert.ToBase64String(f.Bytes) }).ToArray()),
            ["AudioQualifier"] = new JsonObject { ["Header"] = "TFA-AUQ-V1.5", ["AudioQualifierID"] = "q-" + id, ["SubTypes"] = "Mixed", ["Formats"] = new JsonObject() }
        }.ToJsonString();

    // The audio of a microphone array: ONE WAV file whose channels are those of the array, and the Microphone Array Geometry the Qualifier carries (Attributes.Device), if it does.
    public static string MakeMultichannel(string id, MultiAudio audio, ArrayGeometry? geometry = null) =>
        new JsonObject
        {
            ["Header"] = Header, ["BasicAudioObjectID"] = id,
            ["BasicAudioObjectData"] = new JsonArray(new JsonObject { ["Data"] = Convert.ToBase64String(audio.ToWav()) }),
            ["AudioQualifier"] = new JsonObject
            {
                ["Header"] = "TFA-AUQ-V1.5", ["AudioQualifierID"] = "q-" + id, ["SubTypes"] = "Mixed",
                ["Formats"] = new JsonObject
                {
                    ["ContentFormat"] = new JsonObject { ["RawData"] = new JsonObject { ["SampleSpace"] = new JsonObject { ["Header"] = "TFA-PCM-V1.5", ["PCM"] = new JsonObject { ["SamplingFrequency"] = audio.Rate, ["SamplePrecision"] = 16 } } } },
                    ["TransportFormat"] = new JsonObject { ["FileFormats"] = "WAV" }
                },
                ["Attributes"] = geometry is null ? new JsonObject() : new JsonObject { ["Device"] = new JsonObject { ["DeviceRole"] = "Capture", ["DeviceType"] = "MicrophoneArray", ["MicrophoneArrayGeometry"] = geometry.ToJson() } }
            }
        }.ToJsonString();

    public static (MultiAudio Audio, ArrayGeometry? Geometry) ReadMultichannel(string json)
    {
        var files = Files(json, "BasicAudioObjectData");
        if (files.Count != 1) throw new FormatException($"The multichannel audio is one audio file: it holds {files.Count}.");
        var node = JsonNode.Parse(json)?["AudioQualifier"]?["Attributes"]?["Device"]?["MicrophoneArrayGeometry"];
        return (MultiAudio.FromWav(files[0].Bytes), node is null ? null : ArrayGeometry.FromJson(node));
    }

    // An Audio Object of WAV files, its Qualifier saying so and giving the PCM they hold (that of the first).
    public static string Make(string id, params Audio[] files) => Make(id, files.Select(f => new Item(null, f)).ToArray());

    public static string Make(string id, params Item[] items)
    {
        var rate = items.Length > 0 ? items[0].Audio.Rate : 48000;
        return new JsonObject
        {
            ["Header"] = Header, ["BasicAudioObjectID"] = id,
            ["BasicAudioObjectData"] = new JsonArray(items.Select(i =>
            {
                var o = new JsonObject { ["Data"] = Convert.ToBase64String(i.Audio.ToWav()) };
                if (i.Id is not null) o["ID"] = i.Id;
                return (JsonNode)o;
            }).ToArray()),
            ["AudioQualifier"] = new JsonObject
            {
                ["Header"] = "TFA-AUQ-V1.5", ["AudioQualifierID"] = "q-" + id, ["SubTypes"] = "Mixed",
                ["Formats"] = new JsonObject
                {
                    ["ContentFormat"] = new JsonObject { ["RawData"] = new JsonObject { ["SampleSpace"] = new JsonObject { ["Header"] = "TFA-PCM-V1.5",
                        ["PCM"] = new JsonObject { ["SamplingFrequency"] = rate, ["SamplePrecision"] = 16 } } } },
                    ["TransportFormat"] = new JsonObject { ["FileFormats"] = "WAV" }
                }
            }
        }.ToJsonString();
    }
}
