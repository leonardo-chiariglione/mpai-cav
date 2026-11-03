using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Mpai.Cae.Usc;

// Audio in the transform domain, as the Audio Objects of Enhanced Audioconference Experience carry it between the AIMs: a series of BLOCKS, each the spectrum (BlockSize / 2 + 1 bins) of
// each channel. In the Basic Audio Object each block is a data item (ID "block-<k>") of 32-bit floats, for each bin for each channel its real and imaginary parts (Sequence
// "Interleaved"); the Audio Qualifier says so (Formats.ContentFormat.RawData.TransformSpace) - or, for the coefficients of a spherical harmonic decomposition, SpherlHarmDecompos with the
// SHDOrder - and carries the Microphone Array Geometry (Attributes.Device), whose BlockSize and sampling frequency the blocks have. The number of samples of the audio they come from,
// which the Qualifier has no place for, is in DescrMetadata ("samples=N").
public sealed record TransformBlocks(int Rate, int BlockSize, int Samples, IReadOnlyList<Complex[][]> Blocks)
{
    public int Bins => BlockSize / 2 + 1;
    public int Channels => Blocks.Count == 0 ? 0 : Blocks[0].Length;
}

public static class TransformObject
{
    private static readonly string[] Orders = ["0thOrder", "1stOrder", "2ndOrder", "3rdOrder", "4thOrder", "5thOrder", "6thOrder", "7thOrder"];

    // kind: "Transform" (the channels of the array, or separated sources) or "SHD" (spherical harmonic coefficients: Channels = (order + 1)^2).
    public static string Make(string id, TransformBlocks t, string kind, JsonNode? geometry = null)
    {
        var items = new JsonArray();
        for (var k = 0; k < t.Blocks.Count; k++)
        {
            var floats = new float[t.Bins * t.Channels * 2];
            for (var b = 0; b < t.Bins; b++) for (var c = 0; c < t.Channels; c++) { var v = t.Blocks[k][c][b]; floats[(b * t.Channels + c) * 2] = (float)v.Real; floats[(b * t.Channels + c) * 2 + 1] = (float)v.Imaginary; }
            var bytes = new byte[floats.Length * 4]; Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
            items.Add(new JsonObject { ["ID"] = $"block-{k}", ["Data"] = Convert.ToBase64String(bytes) });
        }
        var raw = new JsonObject();
        if (kind == "SHD")
        {
            var order = (int)Math.Round(Math.Sqrt(t.Channels)) - 1;
            raw["SpherlHarmDecompos"] = new JsonObject { ["SHDOrder"] = Orders[Math.Clamp(order, 0, 7)], ["Precision"] = "float32" };
        }
        else raw["TransformSpace"] = new JsonObject { ["Sequence"] = "Interleaved", ["Precision"] = "float32" };
        var qualifier = new JsonObject
        {
            ["Header"] = "TFA-AUQ-V1.5", ["AudioQualifierID"] = "q-" + id, ["SubTypes"] = "Mixed",
            ["Formats"] = new JsonObject { ["ContentFormat"] = new JsonObject { ["RawData"] = raw } }
        };
        if (geometry is not null) qualifier["Attributes"] = new JsonObject { ["Device"] = new JsonObject { ["DeviceRole"] = "Capture", ["DeviceType"] = "MicrophoneArray", ["MicrophoneArrayGeometry"] = JsonNode.Parse(geometry.ToJsonString()) } };
        return new JsonObject
        {
            ["Header"] = AudioObject.Header, ["BasicAudioObjectID"] = id, ["BasicAudioObjectData"] = items, ["AudioQualifier"] = qualifier,
            ["DescrMetadata"] = $"samples={t.Samples};block={t.BlockSize};rate={t.Rate}"
        }.ToJsonString();
    }

    public static (TransformBlocks Blocks, JsonNode? Geometry) Read(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("An Audio Object is a JSON object.");
        if (root["Header"]?.GetValue<string>() != AudioObject.Header) throw new FormatException($"Not an Audio Object: Header {root["Header"]}.");
        var raw = root["AudioQualifier"]?["Formats"]?["ContentFormat"]?["RawData"] as JsonObject;
        if (raw?["TransformSpace"] is null && raw?["SpherlHarmDecompos"] is null) throw new FormatException("The Audio Object is not in the transform domain (its Qualifier has neither TransformSpace nor SpherlHarmDecompos).");
        var precision = (raw["TransformSpace"] ?? raw["SpherlHarmDecompos"])?["Precision"]?.GetValue<string>();
        if (precision != "float32") throw new FormatException($"The transform has a Precision of {precision}: this Implementation does not decode it (it decodes float32).");
        var meta = (root["DescrMetadata"]?.GetValue<string>() ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=')).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
        var geometry = root["AudioQualifier"]?["Attributes"]?["Device"]?["MicrophoneArrayGeometry"];
        int blockSize = meta.TryGetValue("block", out var bs) ? int.Parse(bs) : geometry is not null ? ArrayGeometry.FromJson(geometry).BlockSize : throw new FormatException("The block size of the transform is not given.");
        int rate = meta.TryGetValue("rate", out var r) ? int.Parse(r) : geometry is not null ? ArrayGeometry.FromJson(geometry).Rate : throw new FormatException("The sampling frequency of the transform is not given.");
        var samples = meta.TryGetValue("samples", out var s) ? int.Parse(s) : 0;
        var bins = blockSize / 2 + 1; var blocks = new List<Complex[][]>();
        foreach (var (_, bytes) in AudioObject.Files(json, "BasicAudioObjectData"))
        {
            var floats = new float[bytes.Length / 4]; Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
            if (floats.Length % (bins * 2) != 0) throw new FormatException("A block of the transform is not a whole number of channels of bins.");
            var channels = floats.Length / (bins * 2);
            var block = Enumerable.Range(0, channels).Select(_ => new Complex[bins]).ToArray();
            for (var b = 0; b < bins; b++) for (var c = 0; c < channels; c++) block[c][b] = new Complex(floats[(b * channels + c) * 2], floats[(b * channels + c) * 2 + 1]);
            blocks.Add(block);
        }
        return (new TransformBlocks(rate, blockSize, samples, blocks), geometry);
    }
}
