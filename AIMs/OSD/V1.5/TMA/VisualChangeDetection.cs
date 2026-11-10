using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Mpai.Osd.Tma;

// CVE-VCD-V1.0 - Visual Change Detection. The video is the entries of the Data of a Basic Visual Object, one frame (an image) each, in order. A scene cut is found where a frame
// differs enough from the one before it, as PySceneDetect's ContentDetector, the library of the reference software of V1.4, does: the mean over the pixels of the mean of the
// absolute differences of Hue, Saturation and Value reaches the threshold (27), and the scene before is at least MinSceneLength frames long (15). For each scene the first
// changed frame is given, and, where ImagesPerScene (1; 5 in the reference software of V1.4) is more, frames spread over the scene after it, in one Basic Visual Object a scene.
public sealed class VisualChangeDetection(string instanceId, IReadOnlyDictionary<string, string>? settings = null) : TmaAim(instanceId)
{
    public const double DefaultThreshold = 27.0;
    public const int DefaultMinSceneLength = 15, DefaultFramesPerSecond = 25;

    private readonly double threshold = Setting(settings, "Threshold", DefaultThreshold);
    private readonly int minSceneLength = (int)Setting(settings, "MinSceneLength", DefaultMinSceneLength);
    private readonly int imagesPerScene = Math.Max(1, (int)Setting(settings, "ImagesPerScene", 1));

    private static double Setting(IReadOnlyDictionary<string, string>? s, string key, double fallback) =>
        s is not null && s.TryGetValue(key, out var v) && double.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : fallback;

    protected override (string, int)[] Inputs => [(TmaTypes.Bvo, 1)];

    // Where the scenes start: frame numbers, the first being 0.
    public static List<int> Cuts(IReadOnlyList<Image<Rgb24>> frames, double threshold = DefaultThreshold, int minSceneLength = DefaultMinSceneLength)
    {
        var starts = new List<int> { 0 };
        float[]? previous = null;
        for (var i = 0; i < frames.Count; i++)
        {
            var hsv = Hsv(frames[i]);
            if (previous is not null && previous.Length == hsv.Length)
            {
                double sum = 0;
                for (var k = 0; k < hsv.Length; k++) sum += Math.Abs(hsv[k] - previous[k]);
                var score = sum / hsv.Length;                      // mean over pixels and the three channels
                if (score >= threshold && i - starts[^1] >= minSceneLength) starts.Add(i);
            }
            previous = hsv;
        }
        return starts;
    }

    // Hue 0-179 as OpenCV, Saturation and Value 0-255, of the frame reduced to 160 pixels at most, interleaved.
    private static float[] Hsv(Image<Rgb24> frame)
    {
        using var small = frame.Clone(c => c.Resize(new ResizeOptions { Size = new Size(160, 160), Mode = ResizeMode.Max }));
        var result = new float[small.Width * small.Height * 3];
        var n = 0;
        small.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
                foreach (var p in rows.GetRowSpan(y))
                {
                    float r = p.R / 255f, g = p.G / 255f, b = p.B / 255f;
                    float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min, h = 0;
                    if (d > 0)
                    {
                        if (max == r) h = 60 * (((g - b) / d) % 6);
                        else if (max == g) h = 60 * ((b - r) / d + 2);
                        else h = 60 * ((r - g) / d + 4);
                        if (h < 0) h += 360;
                    }
                    result[n++] = h / 2;
                    result[n++] = (max == 0 ? 0 : d / max) * 255;
                    result[n++] = max * 255;
                }
        });
        return result;
    }

    public override IEnumerable<(string DataType, int Port, JsonObject Json)> Handle(string dataType, int port, JsonObject json)
    {
        if (json["BasicVisualObjectData"] is not JsonArray entries) yield break;
        var frames = new List<Image<Rgb24>>();
        try
        {
            foreach (var e in entries)
                if ((string?)e?["Data"] is { } b64) frames.Add(Image.Load<Rgb24>(Convert.FromBase64String(b64)));
            if (frames.Count == 0) yield break;

            var fps = (double?)json["BasicVisualObjectProperties"]?["FrameRate"] ?? DefaultFramesPerSecond;
            var begin = DateTimeOffset.UtcNow;
            var starts = Cuts(frames, threshold, minSceneLength);
            for (var s = 0; s < starts.Count; s++)
            {
                var from = starts[s];
                var to = s + 1 < starts.Count ? starts[s + 1] : frames.Count;     // the scene is [from, to)
                var picked = new List<int> { from };
                for (var k = 1; k < imagesPerScene; k++)
                {
                    var f = from + (int)Math.Round(k * (to - from) / (double)imagesPerScene);
                    if (f > picked[^1] && f < to) picked.Add(f);
                }
                var data = new JsonArray();
                foreach (var f in picked)
                {
                    using var ms = new System.IO.MemoryStream();
                    frames[f].SaveAsPng(ms);
                    data.Add(new JsonObject { ["Data"] = Convert.ToBase64String(ms.ToArray()) });
                }
                yield return (TmaTypes.Bvo, 1, new JsonObject
                {
                    ["Header"] = "OSD-BVO-V1.5",
                    ["MInstanceID"] = (string?)json["MInstanceID"] ?? "tma",
                    ["BasicVisualObjectID"] = TmaTypes.NewId("scene"),
                    ["BasicVisualObjectTime"] = TmaTypes.SpaceTime(begin.AddSeconds(from / fps)),
                    ["BasicVisualObjectData"] = data,
                    ["BasicVisualObjectProperties"] = new JsonObject { ["FrameRate"] = fps, ["FirstFrame"] = from, ["Frames"] = to - from }
                });
            }
        }
        finally { foreach (var f in frames) f.Dispose(); }
    }
}
