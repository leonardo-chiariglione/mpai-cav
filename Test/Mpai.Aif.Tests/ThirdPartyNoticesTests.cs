using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using AIF.Controller;

namespace Mpai.Aif.Tests;

// THE CODE IS BSD 3-CLAUSE; THE MODELS AND THE DATA ARE NOT. A courtesy to those who use the code: THIRD-PARTY-NOTICES.md lists,
// from the Metadata of the AIMs, every model and every piece of data the code obtains from a public source, with where, its SHA-256,
// its licence and its restrictions; and every source file that uses them says so in its first lines. Both are checked here, so
// that neither is forgotten when something is added. MPAI_UPDATE_EXPECTED=1 writes the list.
[Trait("Group", "Fast")]
[Trait("Blocks", "No")]
public class ThirdPartyNoticesTests
{
    private const string Begin = "<!-- BEGIN GENERATED: models and data of the Access entries (Test/Mpai.Aif.Tests/ThirdPartyNoticesTests.cs) -->";
    private const string End = "<!-- END GENERATED -->";
    internal const string Mark = "NOTICE (third-party models and data)";

    private static readonly Regex UsesAModel = new(
        "InferenceSession|OllamaClient|WhisperAsrAim|PiperProcessRunner|OpenVoiceWorker|ReferenceFeatures|YoloxObjectDetector|HSEmotionEstimator|SpeakerEmbedder|ArcFaceRecogniser|ScrfdFaceDetector|Wav2Vec2EmotionEstimator|M2M100|RapidOcr|BlipTiqAim|TIQEngine|SoundClassifier");

    private static string Cell(string text) => text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    internal static string Section()
    {
        var rows = new SortedDictionary<string, (AccessEntry Entry, SortedSet<string> UsedBy)>(StringComparer.Ordinal);
        var sources = Directory.EnumerateFiles(Repository.Amds, "*.json").Select(f => (File: f, Used: Regex.Replace(Path.GetFileNameWithoutExtension(f), "^1", ""))).OrderBy(s => s.File, StringComparer.Ordinal);
        foreach (var (file, aim) in sources)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var e in AccessEntry.ReadFrom(doc.RootElement).Where(e => !e.IsPlaceholder))   // an entry still to be filled names no source
            {
                var key = $"{e.Uri}|{e.Member}|{e.Sha256}";
                if (!rows.TryGetValue(key, out var row)) rows[key] = row = (e, []);
                row.UsedBy.Add(aim);
            }
        }
        var sb = new StringBuilder();
        sb.AppendLine(Begin);
        sb.AppendLine();
        sb.AppendLine("## Models and data obtained from their public sources");
        sb.AppendLine();
        sb.AppendLine("**The code of this repository is released under the BSD 3-Clause licence. The models and the databases it uses are not part of it, and neither is the data derived from them.** Each has its own licence, and some restrict use - for example to non-commercial research. The code obtains them from the sources below, as the Metadata of each AIM declares (`AccessSources`: the URI, the SHA-256, and the terms). Read the licence of each before you use it or pass it on; this list is a courtesy, not legal advice.");
        sb.AppendLine();
        sb.AppendLine("| Data | Used by | Source | SHA-256 | Licence | Personal data |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var (e, usedBy) in rows.Values.OrderBy(r => r.Entry.Description.Length > 0 ? r.Entry.Description : r.Entry.Name, StringComparer.Ordinal).ThenBy(r => r.Entry.Uri, StringComparer.Ordinal))
        {
            var source = e.Uri.StartsWith("file:", StringComparison.Ordinal) ? "not published yet (a file of the machine that made it)" : $"<{e.Uri}>" + (e.Member.Length > 0 ? $", file `{e.Member}`" : "");
            var others = e.Mirrors is { Count: > 0 } m ? $" (also {string.Join(", ", m.Select(x => $"<{x}>"))})" : "";
            var licence = e.Terms is null ? "NOT STATED" : $"{e.Terms.Licence} (<{e.Terms.Uri}>)";
            sb.AppendLine($"| {Cell(e.Description.Length > 0 ? e.Description : e.Name)} | {Cell(string.Join(", ", usedBy))} | {Cell(source + others)} | `{e.Sha256}` | {Cell(licence)} | {(e.Terms?.PersonalData == true ? "yes" : "no")} |");
        }
        sb.AppendLine();
        sb.AppendLine("The databases the reference data come from are public too: Labeled Faces in the Wild (<http://vis-www.cs.umass.edu/lfw/>), which states no licence and is published for research use, and LibriSpeech (<https://www.openslr.org/12/>), under CC BY 4.0. Their pictures and recordings are of real people.");
        sb.AppendLine();
        sb.AppendLine(End);
        return sb.ToString().ReplaceLineEndings("\n");
    }

    [Fact]
    public void TheNoticesListEveryModelAndEveryPieceOfDataTheMetadataDeclares()
    {
        var file = Path.Combine(Repository.Root, "THIRD-PARTY-NOTICES.md");
        var text = File.ReadAllText(file).ReplaceLineEndings("\n");
        var generated = Section();
        var update = Environment.GetEnvironmentVariable("MPAI_UPDATE_EXPECTED") == "1";
        var i = text.IndexOf(Begin, StringComparison.Ordinal);
        if (i < 0)
        {
            Assert.True(update, "THIRD-PARTY-NOTICES.md has no generated section: run with MPAI_UPDATE_EXPECTED=1.");
            File.WriteAllText(file, text.TrimEnd() + "\n\n" + generated);
            return;
        }
        var j = text.IndexOf(End, i, StringComparison.Ordinal) + End.Length;
        var current = text[i..j] + "\n";
        if (update && current != generated)
        {
            File.WriteAllText(file, text[..i] + generated.TrimEnd('\n') + text[j..]);
            return;
        }
        Assert.Equal(generated, current);
        Assert.DoesNotContain("NOT STATED", generated);
    }

    [Fact]
    public void EverySourceFileThatUsesAModelOrDataSaysSoInItsFirstLines()
    {
        var missing = new List<string>();
        foreach (var top in new[] { "AIMs", "Apps" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(Repository.Root, top), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                var text = File.ReadAllText(file);
                if (UsesAModel.IsMatch(text) && !string.Join('\n', text.Split('\n').Take(5)).Contains(Mark)) missing.Add(Path.GetRelativePath(Repository.Root, file));
            }
        Assert.True(missing.Count == 0, "These files use a model or data and carry no notice in their first lines:" + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }
}
