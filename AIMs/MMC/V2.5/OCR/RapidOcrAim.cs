using Mpai.Core;
using RapidOcrNet;
using SkiaSharp;

namespace Mpai.Aims.Ocr;

// MMC-OCR-V2.5 with RapidOcrNet: PaddleOCR's PP-OCRv5 detection, orientation and latin
// recognition models (Apache-2.0) on ONNX Runtime; the image decoded by SkiaSharp.
//
// A LINE'S BOX (the author, 2026/10/01): the Space-Time of a line in the Text Object
// gives its box on the page by two Positions - SpatialAttitude1 its top-left corner,
// SpatialAttitude2 its bottom-right corner - in the pixels of the image, X to the
// right, Y down, Z = 0. The page's own Space-Time is the whole image, (0, 0) to
// (width, height). A line's confidence, the mean of its characters', is in its Data
// Exchange Metadata.
public sealed class RapidOcrAim : IOcrAim, IDisposable
{
    private readonly RapidOcr _ocr = new();
    private readonly string _source;

    public RapidOcrAim(string source, RapidOcrConfiguration? config = null)
    {
        _source = source;
        if (config is not null && !string.IsNullOrWhiteSpace(config.DetModel))
            _ocr.InitModels(config.DetModel, config.ClsModel, config.RecModel, config.KeysFile);
        else
            _ocr.InitModels();   // the PP-OCRv5 latin models the package carries
    }

    public Task<TextObject> ProcessAsync(BasicVisualObject image)
    {
        var page = Guid.NewGuid().ToString();
        using var bitmap = image.Data.Length > 0 ? SKBitmap.Decode(image.Data) : null;
        if (bitmap is null)
            return Task.FromResult(new TextObject
            {
                TextObjectID = page, TextObjectSpaceTime = Box(page, 0, 0, 0, 0), BasicTextObjectCount = 0,
                DescrMetadata = image.Data.Length > 0 ? "the image could not be decoded" : "no image"
            });

        var result = _ocr.Detect(bitmap, RapidOcrOptions.Default);
        var lines = new List<(int X0, int Y0, int X1, int Y1, string Text, double Confidence)>();
        foreach (var block in result?.TextBlocks ?? [])
        {
            if (string.IsNullOrWhiteSpace(block.Text)) continue;
            var xs = block.BoxPoints.Select(p => (int)Math.Round((double)p.X)).ToArray();
            var ys = block.BoxPoints.Select(p => (int)Math.Round((double)p.Y)).ToArray();
            var confidence = block.CharScores is { Length: > 0 } scores ? scores.Average() : 0.0;
            lines.Add((xs.Min(), ys.Min(), xs.Max(), ys.Max(), block.Text.Trim(), confidence));
        }

        // Reading order: down the page, then left to right.
        var entries = lines.OrderBy(l => l.Y0).ThenBy(l => l.X0).Select(l =>
        {
            var id = Guid.NewGuid().ToString();
            var line = new BasicTextObject
            {
                BasicTextObjectID = id,
                BasicTextData = [new InlineTextData(l.Text)],
                DataXMData = new DataExchangeMetadata
                {
                    DataID = id, Source = [new ProcessInstanceRef { ImplementerAIMID = _source }],
                    Confidence = Math.Round(Math.Clamp(l.Confidence, 0, 1), 4)
                }
            };
            return new BasicTextObjectEntry { BasicTextObjectSpaceTime = Box(id, l.X0, l.Y0, l.X1, l.Y1), BTObjectIDOrBTObject = [line] };
        }).ToList();

        return Task.FromResult(new TextObject
        {
            TextObjectID = page,
            TextObjectSpaceTime = Box(page, 0, 0, bitmap.Width, bitmap.Height),
            BasicTextObjectCount = entries.Count,
            BasicTextObjects = entries
        });
    }

    // A box on the page: its top-left corner at SpatialAttitude1, its bottom-right at
    // SpatialAttitude2.
    public static SpaceTime Box(string id, double x0, double y0, double x1, double y1) => new()
    {
        SpaceTimeID = id + "-ST",
        SpatialAttitude1 = Corner(id + "-TL", x0, y0),
        SpatialAttitude2 = Corner(id + "-BR", x1, y1),
        Time = new SimpleTime { SimpleTimeID = id + "-T" }
    };

    private static SpatialAttitude Corner(string id, double x, double y) => new()
    {
        ObjectSpatialAttitudeID = id,
        Position = new Position { PositionID = id + "-P", CartPosition = [x, y, 0] },
        Orientation = new Orientation { OrientationID = id + "-O" }
    };

    public void Dispose() => _ocr.Dispose();
}

// The four model files; absent, the models the package carries.
public sealed class RapidOcrConfiguration
{
    public string DetModel { get; init; } = "";
    public string ClsModel { get; init; } = "";
    public string RecModel { get; init; } = "";
    public string KeysFile { get; init; } = "";
}
