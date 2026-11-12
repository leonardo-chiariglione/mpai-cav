using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Core;
using Mpai.Providers;

namespace Mpai.Aif.Tests;

// NEWSPAPER READING ON THE TEST PAGES OF Test/Data/Newspapers: synthetic pages (Wikinews texts in en, fr, de, it, clean and degraded as
// a scanner or a camera would give them) and pages of government publications. For each column of each page the Point at its top goes in
// with the page; the text that comes back is the column's, not another's. The aim is to show NPR working, so the judgment is lenient:
// most of the words of the answer are the words of that column, and there are enough of them. The OCR is not ours and is not measured.
[Trait("Group", "Models")]
public class NewspaperPagesTests
{
    private const string NPR = "1MMC-NPR-V2.5-I01";
    private static readonly string Root = Path.Combine(Repository.Root, "Test", "Data", "Newspapers");

    public static IEnumerable<object[]> Columns()
    {
        if (!Directory.Exists(Root)) yield break;
        foreach (var folder in Directory.GetDirectories(Root, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            if (!File.Exists(Path.Combine(folder, "columns.json"))) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "columns.json")));
            var n = doc.RootElement.GetProperty("Columns").GetArrayLength();
            for (var c = 0; c < n; c++) yield return [Path.GetRelativePath(Root, folder).Replace(Path.DirectorySeparatorChar, '/'), c];
        }
    }

    private static string[] Words(string s) => Regex.Matches(s.ToLowerInvariant(), @"[\p{L}\p{N}]+").Select(m => m.Value).ToArray();

    [SkippableTheory]
    [MemberData(nameof(Columns))]
    public void ThePointedColumnOfEachTestPageIsRead(string page, int column)
    {
        Skip.IfNot(TestModels.Can("ocr-det"), "Models/OCR is absent: the model files are obtained separately.");
        var folder = Path.Combine(Root, page);
        var image = Directory.GetFiles(folder, "page.*").Single();
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "columns.json")));
        var col = doc.RootElement.GetProperty("Columns")[column];
        var point = col.GetProperty("Point").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        var truth = col.GetProperty("Text").GetString()!;
        var language = doc.RootElement.GetProperty("Language").GetString()!;

        var store = new AIF.Store.AmdStore(Repository.Amds); store.Scan();
        using var api = new ControllerApi(Repository.Amds, Path.Combine(Repository.Root, "AIMs", "aim-settings.json"), _ => new CompositeProvider(new NprProvider(store)));
        Assert.Equal(AifError.OK, api.StartFlow(NPR));
        try
        {
            var r = api.Advance(NPR, new[]
            {
                new ControllerApi.Datum("OSD-BVO-V1.5", 1, MpaiJson.ToJson(BasicVisualObject.FromFile(Path.GetFileName(image), File.ReadAllBytes(image), "Picture"))),
                new ControllerApi.Datum("OSD-PNT-V1.5", 1, $"{{\"Header\":\"OSD-PNT-V1.5\",\"Coordinates\":{{\"X\":{point[0]},\"Y\":{point[1]}}}}}"),
                new ControllerApi.Datum("string", 1, "\"MPAI as a Service\"")
            });
            Assert.True(r.Ok, $"{page} column {column + 1}: {r.Error}");
            var pav = JsonNode.Parse(r.ByType("PAF-PAV-V1.6") ?? "{}") as JsonObject;
            var answer = pav?["TextObject"] is JsonObject txo ? MpaiJson.FromJson<BasicTextObject>(txo.ToJsonString()).GetText() : "";

            var want = Words(truth).GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
            var read = Words(answer);
            int hit = 0;
            foreach (var w in read) if (want.TryGetValue(w, out var n) && n > 0) { hit++; want[w] = n - 1; }
            var precision = read.Length == 0 ? 0 : (double)hit / read.Length;
            var degraded = page.Contains("degraded");
            Assert.True(precision >= (degraded ? 0.6 : 0.8) && read.Length >= 15,
                $"{page} column {column + 1}: {precision:P0} of its {read.Length} words are the column's.\nANSWER: {answer}\nTRUTH:  {truth}");
            Assert.Equal(language, AvatarUtterance.LanguageOfPortableAvatar(r.ByType("PAF-PAV-V1.6")));   // the language is stamped on the text
        }
        finally { api.StopFlow(NPR); }
    }
}
