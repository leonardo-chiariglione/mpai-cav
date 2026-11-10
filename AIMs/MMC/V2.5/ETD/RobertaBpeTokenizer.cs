// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mpai.Mmc.Etd;

// The byte-level BPE tokeniser of RoBERTa (the one of GPT-2), read from the vocab.json and the merges.txt that the model's repository holds.
// It adds <s> and </s> and cuts the text at 512 tokens, as the model was trained.
public sealed class RobertaBpeTokenizer
{
    private static readonly Regex Pre = new(@"'s|'t|'re|'ve|'m|'ll|'d| ?\p{L}+| ?\p{N}+| ?[^\s\p{L}\p{N}]+|\s+(?!\S)|\s+", RegexOptions.Compiled);
    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<(string, string), int> _ranks = new();
    private readonly Dictionary<string, string[]> _cache = new();
    private static readonly Dictionary<byte, char> ByteToChar = BuildByteMap();
    public const int MaxTokens = 512;

    public RobertaBpeTokenizer(string vocabJsonPath, string mergesPath)
    {
        _vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(vocabJsonPath))!;
        int rank = 0;
        foreach (var line in File.ReadLines(mergesPath))
        {
            if (line.StartsWith("#version") || string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(' ');
            if (parts.Length == 2) _ranks[(parts[0], parts[1])] = rank++;
        }
    }

    public long[] Encode(string text)
    {
        var ids = new List<long> { _vocab["<s>"] };
        foreach (Match m in Pre.Matches(text ?? ""))
        {
            var mapped = new string(Encoding.UTF8.GetBytes(m.Value).Select(b => ByteToChar[b]).ToArray());
            foreach (var piece in Bpe(mapped))
                ids.Add(_vocab.TryGetValue(piece, out var id) ? id : _vocab["<unk>"]);
            if (ids.Count >= MaxTokens - 1) break;
        }
        if (ids.Count > MaxTokens - 1) ids.RemoveRange(MaxTokens - 1, ids.Count - (MaxTokens - 1));
        ids.Add(_vocab["</s>"]);
        return ids.ToArray();
    }

    private string[] Bpe(string word)
    {
        if (_cache.TryGetValue(word, out var known)) return known;
        var parts = word.Select(c => c.ToString()).ToList();
        while (parts.Count > 1)
        {
            int best = int.MaxValue, at = -1;
            for (int i = 0; i < parts.Count - 1; i++)
                if (_ranks.TryGetValue((parts[i], parts[i + 1]), out var r) && r < best) { best = r; at = i; }
            if (at < 0) break;
            parts[at] += parts[at + 1]; parts.RemoveAt(at + 1);
        }
        return _cache[word] = parts.ToArray();
    }

    // GPT-2's reversible map of the 256 bytes to printable characters.
    private static Dictionary<byte, char> BuildByteMap()
    {
        var bs = new List<int>();
        bs.AddRange(Enumerable.Range('!', '~' - '!' + 1)); bs.AddRange(Enumerable.Range(0xA1, 0xAC - 0xA1 + 1)); bs.AddRange(Enumerable.Range(0xAE, 0xFF - 0xAE + 1));
        var cs = bs.ToList(); int n = 0;
        for (int b = 0; b < 256; b++) if (!bs.Contains(b)) { bs.Add(b); cs.Add(256 + n++); }
        return bs.Zip(cs, (b, c) => ((byte)b, (char)c)).ToDictionary(t => t.Item1, t => t.Item2);
    }
}
