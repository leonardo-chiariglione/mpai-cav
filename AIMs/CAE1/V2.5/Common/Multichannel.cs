using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace Mpai.Cae.Usc;

// Multichannel audio as the AIMs of Enhanced Audioconference Experience handle it: the channels of a microphone array, 16-bit samples at a rate.
public sealed record MultiAudio(short[][] Channels, int Rate)
{
    public int Length => Channels.Length == 0 ? 0 : Channels[0].Length;

    public static MultiAudio FromWav(byte[] b)
    {
        if (b.Length < 44 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' || b[8] != 'W' || b[9] != 'A' || b[10] != 'V' || b[11] != 'E') throw new FormatException("The audio is not a WAV file.");
        int channels = 1, rate = 0, bits = 0, dataAt = -1, dataLength = 0;
        for (var p = 12; p + 8 <= b.Length;)
        {
            var id = Encoding.ASCII.GetString(b, p, 4); var size = BitConverter.ToInt32(b, p + 4);
            if (id == "fmt ") { channels = BitConverter.ToInt16(b, p + 10); rate = BitConverter.ToInt32(b, p + 12); bits = BitConverter.ToInt16(b, p + 22); }
            else if (id == "data") { dataAt = p + 8; dataLength = Math.Min(size, b.Length - dataAt); break; }
            p += 8 + size + (size & 1);
        }
        if (dataAt < 0 || bits != 16 || rate <= 0 || channels < 1) throw new FormatException("The audio is not 16-bit PCM.");
        var frames = dataLength / (2 * channels);
        var data = Enumerable.Range(0, channels).Select(_ => new short[frames]).ToArray();
        for (var i = 0; i < frames; i++) for (var c = 0; c < channels; c++) data[c][i] = BitConverter.ToInt16(b, dataAt + (i * channels + c) * 2);
        return new MultiAudio(data, rate);
    }

    public byte[] ToWav()
    {
        var channels = Channels.Length; var frames = Length;
        using var m = new MemoryStream(); using var w = new BinaryWriter(m);
        var data = frames * channels * 2;
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + data); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(Rate); w.Write(Rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(data);
        for (var i = 0; i < frames; i++) for (var c = 0; c < channels; c++) w.Write(Channels[c][i]);
        w.Flush(); return m.ToArray();
    }
}

// The geometry of a microphone array (CAE-MAG-V2.5): where each microphone is (metres, from the centre of the array), how many there are, the sampling frequency and the size of the
// blocks the audio is processed in.
public sealed record ArrayGeometry(string Id, string ArrayType, int Rate, int BlockSize, double[][] Positions)
{
    public int Count => Positions.Length;

    public JsonObject ToJson()
    {
        var mics = new JsonArray();
        for (var m = 0; m < Positions.Length; m++)
            mics.Add(new JsonObject
            {
                ["MicrophoneID"] = m, ["MicrophoneDirectivityFormat"] = "Omnidirectional",
                ["MicrophonePointOfView"] = new JsonObject
                {
                    ["Header"] = "OSD-OPV-V1.5", ["PointOfViewID"] = $"mic-{m}",
                    ["CartPosition"] = new JsonArray(Positions[m][0], Positions[m][1], Positions[m][2]), ["Orientation"] = new JsonArray(0, 0, 0)
                }
            });
        return new JsonObject
        {
            ["Header"] = "CAE-MAG-V2.5", ["MInstanceID"] = "m-1", ["MicrophoneArrayGeometryID"] = Id,
            ["MicrophoneArraySpaceTime"] = new JsonObject { ["Header"] = "OSD-SPT-V1.5" },
            ["MicrophoneArrayAttributes"] = new JsonObject
            {
                ["ArrayType"] = ArrayType, ["SamplingParameters"] = new JsonObject { ["Header"] = "TFA-PCM-V1.5", ["PCM"] = new JsonObject { ["SamplingFrequency"] = Rate, ["SamplePrecision"] = 16 } },
                ["BlockSize"] = BlockSize, ["NumberofMicrophones"] = Positions.Length
            },
            ["MicrophoneAttributes"] = mics
        };
    }

    // A number in a JSON tree, whether it was made as an integer or read as a double.
    private static double Number(JsonNode? n) => double.Parse(n!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);

    public static ArrayGeometry FromJson(JsonNode? node)
    {
        var root = node as JsonObject ?? throw new FormatException("A Microphone Array Geometry is a JSON object.");
        if (root["Header"]?.GetValue<string>() != "CAE-MAG-V2.5") throw new FormatException($"Not a Microphone Array Geometry: Header {root["Header"]}.");
        var a = root["MicrophoneArrayAttributes"] as JsonObject ?? throw new FormatException("The Microphone Array Geometry has no MicrophoneArrayAttributes.");
        var rate = (int)Number(a["SamplingParameters"]?["PCM"]?["SamplingFrequency"] ?? throw new FormatException("The array has no sampling frequency."));
        var positions = (root["MicrophoneAttributes"] as JsonArray ?? throw new FormatException("The array has no MicrophoneAttributes.")).OfType<JsonObject>()
            .OrderBy(m => m["MicrophoneID"]!.GetValue<int>())
            .Select(m => (m["MicrophonePointOfView"]?["CartPosition"] as JsonArray ?? throw new FormatException("A microphone has no CartPosition.")).Select(v => Number(v)).ToArray()).ToArray();
        return new ArrayGeometry(root["MicrophoneArrayGeometryID"]?.GetValue<string>() ?? "", a["ArrayType"]?.GetValue<string>() ?? "", rate, a["BlockSize"] is JsonNode bs ? (int)Number(bs) : 512, positions);
    }
}
