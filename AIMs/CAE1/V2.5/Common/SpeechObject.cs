using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace Mpai.Cae.Usc;

// The Basic Speech Object (OSD-BSO-V1.5) as the AIMs of CAE-USC V2.5 read and write it. Everything in CAE-USC that is speech is this object; what its data IS - a speech file,
// or samples - the Speech Qualifier (TFA-SPQ-V1.5) says:
//   Formats.TransportFormats.FileFormat "WAV"          each data item is a WAV file;
//   Formats.ContentFormats.RawData (TFA-PCM-V1.5)      each data item is raw PCM: PCM.SamplingFrequency and PCM.SamplePrecision (16 bit, little endian, mono);
// and, if the Qualifier says neither, a WAV file. These are the forms THIS Implementation decodes - the standard restricts none: another speech file format, or another precision, is\n// speech too, and an Implementation that decodes it can be put in its place. What this one cannot decode it says so. Attributes.Source says whether the speech is Real or Synthetic. A data item is by Data (base64) or by DataURI (a file the
// reader can open); the standard says that data travels by reference when it is large, and how a reference is fetched is the Controller's (MPAI_AIFM_Payload_Get).
public static class SpeechObject
{
    public const string Header = "OSD-BSO-V1.5";

    public static IReadOnlyList<Audio> Read(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("A Speech Object is a JSON object.");
        if (root["Header"]?.GetValue<string>() != Header) throw new FormatException($"Not a Speech Object: Header {root["Header"]}.");
        var list = root["BasicSpeechObjectData"] as JsonArray ?? throw new FormatException("The Speech Object has no BasicSpeechObjectData.");
        var formats = root["BasicSpeechDataQualifier"]?["Formats"];
        var file = formats?["TransportFormats"]?["FileFormat"]?.GetValue<string>();
        var pcm = formats?["ContentFormats"]?["RawData"]?["PCM"] as JsonObject;
        if (file is not null && file != "WAV") throw new FormatException($"The Speech Object is a speech file of format {file}: this Implementation does not decode it (it decodes WAV).");
        var raw = file is null && pcm is not null;

        var audio = new List<Audio>();
        foreach (var item in list.OfType<JsonObject>())
        {
            byte[] bytes;
            if (item["Data"] is JsonValue data) bytes = Convert.FromBase64String(data.GetValue<string>());
            else if (item["DataURI"] is JsonValue uri) bytes = File.ReadAllBytes(new Uri(uri.GetValue<string>()).LocalPath);
            else throw new FormatException("A data item of the Speech Object has neither Data nor DataURI.");
            audio.Add(raw ? FromPcm(bytes, pcm!) : Audio.FromWav(bytes));
        }
        return audio;
    }

    private static Audio FromPcm(byte[] bytes, JsonObject pcm)
    {
        var rate = (int)(pcm["SamplingFrequency"]?.GetValue<double>() ?? throw new FormatException("The PCM of the Speech Object has no SamplingFrequency."));
        var precision = (int)(pcm["SamplePrecision"]?.GetValue<double>() ?? 16);
        if (precision != 16) throw new FormatException($"The PCM of the Speech Object has a SamplePrecision of {precision}: this Implementation does not decode it (it decodes 16 bit).");
        var samples = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2);
        return new Audio(samples, rate);
    }

    // A Speech Object of speech files (WAV), its Qualifier saying so and giving the PCM they hold.
    public static string Make(string id, params Audio[] files) => Make(id, null, files);
    public static string MakeSynthetic(string id, params Audio[] files) => Make(id, "Synthetic", files);

    private static string Make(string id, string? source, Audio[] files)
    {
        var rate = files.Length > 0 ? files[0].Rate : 16000;
        var qualifier = new JsonObject
        {
            ["Header"] = "TFA-SPQ-V1.5", ["SpeechQualifierID"] = "q-" + id, ["SubTypes"] = new JsonObject(),
            ["Formats"] = new JsonObject
            {
                ["ContentFormats"] = new JsonObject { ["RawData"] = new JsonObject { ["Header"] = "TFA-PCM-V1.5", ["PCM"] = new JsonObject { ["SamplingFrequency"] = rate, ["SamplePrecision"] = 16 } } },
                ["TransportFormats"] = new JsonObject { ["FileFormat"] = "WAV" }
            }
        };
        if (source is not null) qualifier["Attributes"] = new JsonObject { ["Source"] = source };
        return new JsonObject
        {
            ["Header"] = Header, ["BasicSpeechObjectID"] = id,
            ["BasicSpeechObjectData"] = new JsonArray(files.Select(f => (JsonNode)new JsonObject { ["Data"] = Convert.ToBase64String(f.ToWav()) }).ToArray()),
            ["BasicSpeechDataQualifier"] = qualifier
        }.ToJsonString();
    }

    // A Speech Object of raw PCM samples, its Qualifier giving the sampling frequency and the precision.
    public static string MakeRaw(string id, params Audio[] files) =>
        new JsonObject
        {
            ["Header"] = Header, ["BasicSpeechObjectID"] = id,
            ["BasicSpeechObjectData"] = new JsonArray(files.Select(f => { var b = new byte[f.Samples.Length * 2]; Buffer.BlockCopy(f.Samples, 0, b, 0, b.Length); return (JsonNode)new JsonObject { ["Data"] = Convert.ToBase64String(b) }; }).ToArray()),
            ["BasicSpeechDataQualifier"] = new JsonObject
            {
                ["Header"] = "TFA-SPQ-V1.5", ["SpeechQualifierID"] = "q-" + id, ["SubTypes"] = new JsonObject(),
                ["Formats"] = new JsonObject { ["ContentFormats"] = new JsonObject { ["RawData"] = new JsonObject { ["Header"] = "TFA-PCM-V1.5",
                    ["PCM"] = new JsonObject { ["SamplingFrequency"] = files[0].Rate, ["SamplePrecision"] = 16 } } } }
            }
        }.ToJsonString();
}
