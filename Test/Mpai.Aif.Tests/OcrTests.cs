using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mpai.Aims.Ocr;
using Mpai.Core;

namespace Mpai.Aif.Tests;

// NPR STEP 1 (M3246 3.1, 3.8): OPTICAL CHARACTER RECOGNITION IN D:\DI. MMC-OCR-V2.5
// reads the author's page - an image of a headline and columns, kept outside the
// repository as the photographs are (D:\Images\NPR, or MPAI_TEST_IMAGES) with the true
// text of each part - and gives its lines as a Text Object. Judged: the Text Object
// valid against OSD-TXO-V1.5; every line a box within the page and a confidence; the
// words of the truth read (at least 95% of them, in any order: the lines are in the
// page's order, down the page across the columns - a column's order is Column Text
// Selection's, step 2); the headline the first line. A Visual Object that only names a file, without its data, is not read
// from the machine. Reported: the time of the page's recognition, the words missed.
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
public class OcrTests
{
    private static readonly string Page = Path.Combine(AccessTests.Images, "NPR", "npr-page.png");
    private static readonly string Truth = Path.Combine(AccessTests.Images, "NPR", "npr-page-truth.txt");
    private static string Model(string file) => Path.Combine(Repository.Root, "Models", "OCR", file);

    private static readonly Dictionary<string, string> Settings = new()
    {
        ["DetModel"] = Model("ch_PP-OCRv5_server_det.onnx"),
        ["ClsModel"] = Model("ch_ppocr_mobile_v2.0_cls_infer.onnx"),
        ["RecModel"] = Model("latin_PP-OCRv5_rec_mobile_infer.onnx"),
        ["KeysFile"] = Model("ppocrv5_latin_dict.txt")
    };

    private static string[] Words(string text) =>
        Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]+").Select(m => m.Value).ToArray();

    // The longest common subsequence of two word lists: the words read in their order.
    private static (int Common, List<string> Missed) InOrder(string[] truth, string[] read)
    {
        var l = new int[truth.Length + 1, read.Length + 1];
        for (var i = truth.Length - 1; i >= 0; i--)
            for (var j = read.Length - 1; j >= 0; j--)
                l[i, j] = truth[i] == read[j] ? l[i + 1, j + 1] + 1 : Math.Max(l[i + 1, j], l[i, j + 1]);
        var missed = new List<string>();
        for (int i = 0, j = 0; i < truth.Length;)
        {
            if (j < read.Length && truth[i] == read[j]) { i++; j++; }
            else if (j < read.Length && l[i, j + 1] >= l[i + 1, j]) j++;
            else missed.Add(truth[i++]);
        }
        return (l[0, 0], missed);
    }

    [SkippableFact]
    public async Task ThePageReadAsLines()
    {
        Skip.IfNot(File.Exists(Page) && File.Exists(Truth), $"The page of the tests is not in {Path.GetDirectoryName(Page)}.");
        Skip.IfNot(Settings.Values.All(File.Exists), "Models/OCR is absent: the model files are obtained separately.");

        var store = new AIF.Store.AmdStore(Repository.Amds);
        store.Scan();
        var ports = AIF.Controller.AimPortReader.Load(store, "1MMC-OCR-V2.5-I01");
        var input = ports.Input("OSD-BVO-V1.5");
        var ocr = new OcrAimProcessor("1MMC-OCR-V2.5-I01", OcrFactory.Create("1MMC-OCR-V2.5-I01", Settings), ports);
        AIF.Controller.Message With(BasicVisualObject image) =>
            new() { MessageId = Guid.NewGuid().ToString(), Ports = new() { [input] = MpaiJson.ToJson(image) } };

        var bytes = File.ReadAllBytes(Page);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var output = await ocr.ProcessAsync(With(BasicVisualObject.FromFile("npr-page.png", bytes)));
        var seconds = clock.Elapsed.TotalSeconds;
        var json = output.Payload;

        var schemas = AIF.Metadata.PublishedSchemas.At(Repository.Schemas);
        var schema = schemas[Path.GetFullPath(Path.Combine(Repository.Schemas, "OSD/V1.5/data/TextObject.json"))];
        bool valid;
        using (var doc = JsonDocument.Parse(json))
            lock (AIF.Metadata.PublishedSchemas.Lock) valid = schema.Evaluate(doc.RootElement).IsValid;

        var text = MpaiJson.FromJson<TextObject>(json)!;
        var page = text.TextObjectSpaceTime!.SpatialAttitude2!.Position.CartPosition!;
        var lines = text.BasicTextObjects.Select(e =>
        {
            var tl = e.BasicTextObjectSpaceTime!.SpatialAttitude1!.Position.CartPosition!;
            var br = e.BasicTextObjectSpaceTime!.SpatialAttitude2!.Position.CartPosition!;
            var line = e.BTObjectIDOrBTObject![0];
            return (Text: line.GetText(), X0: tl[0], Y0: tl[1], X1: br[0], Y1: br[1], Confidence: line.DataXMData?.Confidence);
        }).ToList();
        var boxed = lines.All(l => l.X0 >= 0 && l.Y0 >= 0 && l.X1 <= page[0] && l.Y1 <= page[1] && l.X1 > l.X0 && l.Y1 > l.Y0
                                   && l.Confidence is >= 0 and <= 1);

        var truthText = File.ReadAllLines(Truth);
        var headline = truthText.SkipWhile(l => l.Trim() != "[headline]").Skip(1).First(l => l.Trim().Length > 0).Trim();
        var truthWords = Words(string.Join(" ", truthText.Where(l => !l.TrimStart().StartsWith('['))));
        var readWords = Words(string.Join(" ", lines.Select(l => l.Text)));
        var (inOrder, _) = InOrder(truthWords, readWords);
        var pool = readWords.GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
        var missed = new List<string>();
        foreach (var w in truthWords)
            if (pool.TryGetValue(w, out var n) && n > 0) pool[w] = n - 1; else missed.Add(w);
        var common = truthWords.Length - missed.Count;
        var recall = (double)common / truthWords.Length;
        var precision = readWords.Length == 0 ? 0 : (double)common / readWords.Length;

        // A Visual Object naming a file of this machine, without data: nothing is read.
        var named = await ocr.ProcessAsync(With(new BasicVisualObject { BasicVisualObjectID = "named", FileName = Page }));
        var namedText = MpaiJson.FromJson<TextObject>(named.Payload)!;

        var report = new Dictionary<string, object>
        {
            ["Page"] = $"{page[0]} x {page[1]} px",
            ["Lines"] = lines.Count,
            ["Seconds"] = Math.Round(seconds, 2),
            ["WordsOfTheTruth"] = truthWords.Length,
            ["WordsRead"] = readWords.Length,
            ["Recall"] = Math.Round(recall, 4),
            ["Precision"] = Math.Round(precision, 4),
            ["RecallInThePagesOrder"] = Math.Round((double)inOrder / truthWords.Length, 4),
            ["MeanConfidence"] = Math.Round(lines.Average(l => l.Confidence ?? 0), 4),
            ["Missed"] = missed.Take(40).ToArray(),
            ["FirstLines"] = lines.Take(6).Select(l => $"({l.X0},{l.Y0})-({l.X1},{l.Y1}) {l.Text}").ToArray()
        };
        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "ocr-npr-page.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);

        Expected.Match("ocr-npr-page.json", new Dictionary<string, string>
        {
            ["the ports of the L3"] = $"{input} in, {ports.Output("OSD-TXO-V1.5")} out",
            ["what OCR outputs"] = output.DataType,
            ["the Text Object valid against OSD-TXO-V1.5"] = valid ? "yes" : "no",
            ["every line a box within the page and a confidence"] = boxed ? "yes" : "no",
            ["the words of the truth read, at least 95%"] = recall >= 0.95 ? "yes" : $"no ({recall:P1})",
            ["the headline, the first line"] = lines.Count > 0 && Words(lines[0].Text).SequenceEqual(Words(headline)) ? "yes" : $"no: {(lines.Count > 0 ? lines[0].Text : "no lines")}",
            ["a Visual Object naming a file, without its data"] = $"{namedText.BasicTextObjectCount} lines, {namedText.DescrMetadata}"
        });
    }
}
