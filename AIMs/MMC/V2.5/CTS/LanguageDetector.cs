using System;
using System.Collections.Generic;
using System.Linq;

namespace Mpai.Mmc.Cts;

// THE LANGUAGE OF A TEXT, simply: the script for Japanese and Chinese (kana mean Japanese, Han alone Chinese), and for the
// languages written in Latin letters the share of the words that are among the commonest words of each. Gives the ISO 639-1
// code, or null when the text is too short or no language stands out. Not made too intelligent: it chooses the voice that
// speaks a column, and a text it cannot place is spoken in the default voice.
public static class LanguageDetector
{
    private static readonly Dictionary<string, HashSet<string>> Common = new()
    {
        ["en"] = Set("the of and to in is that it for as with was on are by this be at from or an have not but they which one you were all their has been there can more if will would about"),
        ["fr"] = Set("le la les de des du un une et en est que qui dans pour pas sur au aux ce cette ces il elle nous vous ils par plus se son sa ses mais ou avec sont comme tout été fait"),
        ["de"] = Set("der die das und in den von zu mit sich des auf für ist im dem nicht ein eine als auch es an werden aus er hat dass sie nach wird bei einer um am sind noch wie einem"),
        ["es"] = Set("el la los las de del y en que un una es por con para no se su al lo como más pero sus le ya o fue este ha sí porque esta son entre cuando muy sin sobre"),
        ["pt"] = Set("o a os as de do da dos das e em que um uma é por com para não se seu sua ao como mais mas foi são ou também já pelo pela entre quando muito sem sobre"),
        ["it"] = Set("il lo la i gli le di del della dei delle e in che un una è per con non si su al come più ma sono anche da ha questo questa nel nella suo sua tra fra")
    };

    private static readonly char[] Separators = " \t\r\n,.;:!?()\"'«»-".ToCharArray();

    private static HashSet<string> Set(string words) => words.Split(' ').ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static string? Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        int kana = 0, han = 0, letters = 0;
        foreach (var c in text)
        {
            if (c >= '぀' && c <= 'ヿ') kana++;
            else if (c >= '一' && c <= '鿿') han++;
            if (char.IsLetter(c)) letters++;
        }
        if (letters > 0 && kana + han >= 0.3 * letters) return kana > 0 ? "ja" : "zh";

        var words = text.Split(Separators, StringSplitOptions.RemoveEmptyEntries).Where(w => w.All(char.IsLetter)).ToList();
        if (words.Count < 8) return null;

        var scores = Common.Select(kv => (Language: kv.Key, Share: (double)words.Count(w => kv.Value.Contains(w)) / words.Count))
                           .OrderByDescending(s => s.Share).ToList();
        return scores[0].Share >= 0.12 && scores[0].Share >= 1.3 * scores[1].Share ? scores[0].Language : null;
    }
}
