using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mpai.Mmc.Jsi;

// THE FIRST STAGE OF UNDERSTANDING: A GRAMMAR THE APPLICATION'S CONTEXT GIVES, no language model. A request that one of its rules matches - "move the violin 2 metres to the right", "put the
// drums at x 1, y 2, z 0" - is a command at once, from the numbers and the names it says and, where the rule says so, from where the objects are; a request none matches goes to the model.
// The AIM knows no application: a rule is a pattern, the names its words have to be (from the state of the application), expressions for the numbers, and the command.
//
//   Rules: [ { "Pattern": "move (?:the )?(?<name>.+?) (?:by )?<NUM:n> m(?:etres?)? to the right",
//              "Names":   { "name": "Members" },                          the sets of names (Present, Members, Stored, Kept) each word has to be one of
//              "Let":     { "x": "pos(name,0)+n", "y": "pos(name,1)" },   numbers worked out: + - * /, numbers, the words captured, pos(name,i), ori(name,i)
//              "Command": { ... "CartPosition": ["{x}", "{y}", 0] ... } } ]   a string "{expr}" is replaced by the number, a string "{name}" by the word
// <NUM:n> is a number in the request - digits, or a word: minus two, half, a hundred - captured as n.
public static class RuleGrammar
{
    // The number of single-letter changes (a letter more, less or other) that turn one word into another.
    private static int Edits(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    public static Action<string>? Trace { get; set; }      // what a rule that matched but could not be worked out says, for whoever looks

    private static readonly HashSet<string> Pronouns = new(StringComparer.OrdinalIgnoreCase) { "it", "that", "this", "that sound", "this sound", "the sound", "that one", "this one" };

    private static readonly Dictionary<string, double> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12,
        ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19, ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60, ["ninety"] = 90, ["hundred"] = 100, ["half"] = 0.5, ["a"] = 1
    };

    private const string Number = @"(?:minus |negative |-)?(?:\d+(?:\.\d+)?|zero|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty|thirty|forty|fifty|sixty|ninety|hundred|half)";

    // The command the request comes to, or null: no rule matched, a name is not one the application has, or the command does not conform (the caller checks it).
    public static JsonObject? Match(string request, IReadOnlyList<JsonObject> rules, IReadOnlyDictionary<string, IReadOnlyList<string>> nameSets, ApplicationState state, string? lastCommandId = null, IDictionary<string, double>? memory = null, Action<string>? named = null, IReadOnlyDictionary<string, string>? strings = null, Action<string, string>? sets = null)
    {
        var text = Regex.Replace(request.ToLowerInvariant().Trim().TrimEnd('.', '!', '?', ','), @"\s+", " ");
        text = Regex.Replace(text, @"^(?:please|can you|could you|would you|i want you to|i want to|now) ", "");
        text = text.Replace("meters", "metres").Replace("meter", "metre").Replace(" m ", " metres ");
        text = Regex.Replace(text, @"(\d)\s*(?:metres?|m)\b", "$1 metres");                  // "2m", "2 m", "2 metre": the same
        text = Regex.Replace(text, @"\b(\d+) point (\d+)\b", "$1.$2");
        text = Regex.Replace(text, @"(\d)\s*(?:seconds?|secs?|s)\b", "$1 seconds");                  // "5s", "5 sec": seconds
        text = Regex.Replace(text, @"(\d)\s*x\s*(\d)", "$1 by $2");                          // "4x4" is "4 by 4"
        text = Regex.Replace(text, @"\bfor (?=metres?\b)", "four ");                         // a recogniser writes "for metres" for "four metres", "to metres" for "two metres"
        text = Regex.Replace(text, @"\b(?:to|too) (?=metres?\b)", "two ");
        text = Regex.Replace(text, @"\b([xyz]) (?:to|too)\b", "$1 two");                  // a recogniser writes "x to" for "x 2", "y for" for "y 4"
        text = Regex.Replace(text, @"\b([xyz]) for\b", "$1 four");
        // A RECOGNISER'S PUNCTUATION IS NOT THE PERSON'S: a full stop between two parts of one sentence, "x = 2" for "x 2", "plays" for "place".
        text = Regex.Replace(text, @"(?<!\d)\.(?!\d)", " ").Replace("=", " ");
        text = Regex.Replace(Regex.Replace(text, @"\s+", " ").Trim(), @"^plays ", "place ");
        text = RoomOfThree(text);
        foreach (var rule in rules)
        {
            var pattern = (string?)rule["Pattern"];
            if (pattern is null || rule["Command"] is not JsonObject template) continue;
            Regex regex;
            try { regex = new Regex("^(?:" + Regex.Replace(pattern, @"<NUM:(\w+)>", m => "(?<" + m.Groups[1].Value + ">" + Number + ")") + ")$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)); }
            catch (ArgumentException) { continue; }
            var match = regex.Match(text);
            if (!match.Success) continue;

            var values = new Dictionary<string, object>(StringComparer.Ordinal);
            if (memory is not null) foreach (var (k, v) in memory) values[k] = v;
            if (strings is not null) foreach (var (k, v) in strings) values[k] = v;                    // words the application has now (the scene it is in)                  // what an earlier request said and a rule keeps (the size of the room)
            if (lastCommandId is not null) values["last"] = lastCommandId;
            var ok = true;
            foreach (var group in regex.GetGroupNames().Where(g => !int.TryParse(g, out _)))
            {
                var word = match.Groups[group].Value;
                if (rule["Raw"] is JsonArray raw && raw.Any(r => (string?)r == group) && request.IndexOf(word, StringComparison.OrdinalIgnoreCase) is var at and >= 0 && word.Length > 0)
                { values[group] = request.Substring(at, word.Length).Trim(); continue; }          // a name the person gives is kept as they write it
                if (rule["Names"]?[group] is JsonValue setName && (string?)setName is { } set)
                {
                    var names = nameSets.GetValueOrDefault(set) ?? [];
                    var flat = Flat(word);                                                          // clip 84, Clip84 and clip84 are one name
                    var found = names.FirstOrDefault(n => string.Equals(n, word.Trim(), StringComparison.OrdinalIgnoreCase)) ?? (flat.Length > 0 ? names.FirstOrDefault(n => Flat(n) == flat) : null);
                    if (found is null && flat.Length >= 4 && !nameSets.Values.Any(s => s.Any(n => Flat(n) == flat)) && names.Where(n => Flat(n).Length >= 4 && Edits(Flat(n), flat) <= 1).ToList() is { Count: 1 } near) found = near[0];      // a slip of a key: "plutom" is pluto (but "pippo 2", a name the application has in another set, is not "pippo")
                    if (found is null && ByPlace(word, state) is { } placed && names.Contains(placed)) found = placed;                  // "the object on the left", "the one in front": by where it is in the scene
                    if (found is null && Pronouns.Contains(word.Trim()) && nameSets.GetValueOrDefault("Newest") is { Count: > 0 } newest && names.Contains(newest[^1])) found = newest[^1];      // "it", "that": the sound last spoken of or loaded
                    if (found is null) { ok = false; break; }
                    values[group] = found;
                }
                else if (TryNumber(word, out var number)) values[group] = number;
                else values[group] = word.Trim();
            }
            if (ok && rule["Implicit"] is JsonObject implicitNames)                               // a name the words do not say: the one the application has waiting (Latest)
                foreach (var (variable, setNode) in implicitNames)
                {
                    var one = (nameSets.GetValueOrDefault((string)setNode!) ?? []).LastOrDefault();
                    if (one is null) { ok = false; break; }
                    values[variable] = one;
                }
            if (!ok) continue;
            try
            {
                if (rule["Let"] is JsonObject let)
                    foreach (var (name, expression) in let) values[name] = new Expression((string)expression!, values, state).Value();
                var made = (JsonObject)Fill(template, values);
                if (values.TryGetValue("name", out var spoken) && spoken is string spokenName) named?.Invoke(spokenName);
                if (sets is not null && rule["Sets"] is JsonObject setting)
                    foreach (var (variable, source) in setting) if ((string?)source is { } from && values.TryGetValue(from, out var wanted) && wanted is string wantedText) sets(variable, wantedText);
                if (memory is not null && rule["Keep"] is JsonArray keep)
                    foreach (var k in keep.Select(x => (string)x!)) if (values.TryGetValue(k, out var v) && v is double d) memory[k] = d;
                return made;
            }
            catch (Exception e) when (e is FormatException or InvalidOperationException or KeyNotFoundException or DivideByZeroException) { Trace?.Invoke($"a rule matched and could not be worked out ({e.GetType().Name}: {e.Message}): {pattern}"); continue; }
        }
        return null;
    }

    // A ROOM STATED BY THREE NUMBERS ("a room 6, 4, 3 m in the centre", "a room size 6m, 4, 3m", or only "6m by 4m by 3m"): width, depth and height, said as the rule for a room with a ceiling says it.
    private static readonly string Dimension = @"(\d+(?:\.\d+)?)(?: metres?| m)?";
    private static string RoomOfThree(string text)
    {
        var plain = Regex.Replace(Regex.Replace(text, "[,;:]", " "), @"\s+", " ").Trim();        // "size: 6m, 4m, 3m": the commas and the colon only separate
        if (plain != text && Regex.IsMatch(plain, @"(?:room|area|space).*\d.*\d.*\d")) text = plain;
        var withWord = Regex.Match(text, @"^(.*?\b(?:room|area|space))(.*?)(?: (?:size|dimensions?)(?: is| are)?| of)? " + Dimension + @"(?: by)? " + Dimension + @"(?: by)? " + Dimension + @"(?=\s|$)(.*)$", RegexOptions.IgnoreCase);
        if (withWord.Success) return $"{withWord.Groups[1].Value} {withWord.Groups[3].Value} by {withWord.Groups[4].Value} with a ceiling of {withWord.Groups[5].Value}{withWord.Groups[2].Value}{withWord.Groups[6].Value}";
        var bare = Regex.Match(text, @"^(?:it is |it's |make it |a |the )?" + Dimension + @" (?:by|x) " + Dimension + @" (?:by|x) " + Dimension + "$", RegexOptions.IgnoreCase);
        return bare.Success ? $"place a room {bare.Groups[1].Value} by {bare.Groups[2].Value} with a ceiling of {bare.Groups[3].Value}" : text;
    }

    // A SOUND BY WHERE IT IS: "the object on the left", "the sound on the right", "the left one", "the one in front", "the nearest": the member of the scene that is furthest to the left (smallest x),
    // to the right, ahead (greatest y), behind, nearest to the listener's place or furthest from it. Two at the same place on that side are not told apart: none is chosen.
    private static string? ByPlace(string word, ApplicationState? state)
    {
        var m = Regex.Match(word.Trim().ToLowerInvariant(), @"^(?:the )?(?:(?:object|sound|source|one)s? )?(?:(?:on|at|to|in|from) )?(?:the )?(?<side>left|right|front|ahead|back|behind|nearest|closest|furthest|farthest)(?:most)?(?: (?:one|object|sound|source|side))?$");
        if (!m.Success || state is null) return null;
        var placed = new List<(string Name, double X, double Y)>();
        foreach (var member in state.Scenes.SelectMany(s => s.Members))
        {
            if (member.Facts.FirstOrDefault(f => f.Label == "position").Value is not { } fact) continue;
            var numbers = Regex.Matches(fact, @"-?\d+(?:\.\d+)?").Select(n => double.Parse(n.Value, CultureInfo.InvariantCulture)).ToList();
            if (numbers.Count >= 2) placed.Add((member.Name, numbers[0], numbers[1]));
        }
        if (placed.Count == 0) return null;
        Func<(string Name, double X, double Y), double> score = m.Groups["side"].Value switch
        {
            "left" => p => -p.X,
            "right" => p => p.X,
            "front" or "ahead" => p => p.Y,
            "back" or "behind" => p => -p.Y,
            "nearest" or "closest" => p => -(p.X * p.X + p.Y * p.Y),
            _ => p => p.X * p.X + p.Y * p.Y
        };
        var best = placed.Max(score);
        var winners = placed.Where(p => Math.Abs(score(p) - best) < 1e-9).ToList();
        return winners.Count == 1 ? winners[0].Name : null;
    }

    private static string Flat(string text) => Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]", "");

    private static JsonNode Fill(JsonNode node, Dictionary<string, object> values)
    {
        switch (node)
        {
            case JsonObject o: { var copy = new JsonObject(); foreach (var (k, v) in o) copy[k] = v is null ? null : Fill(v, values); return copy; }
            case JsonArray a: return new JsonArray(a.Select(x => x is null ? null : Fill(x, values)).ToArray());
            case JsonValue v when v.TryGetValue<string>(out var s) && s.Length > 2 && s[0] == '{' && s[^1] == '}':
                var key = s[1..^1];
                if (values.TryGetValue(key, out var direct)) return direct is double d ? JsonValue.Create(d)! : JsonValue.Create((string)direct)!;
                return JsonValue.Create(new Expression(key, values, null).Value())!;
            default: return node.DeepClone();
        }
    }

    public static bool TryNumber(string text, out double number)
    {
        text = text.Trim().ToLowerInvariant();
        var negative = false;
        foreach (var prefix in new[] { "minus ", "negative ", "-" }) if (text.StartsWith(prefix, StringComparison.Ordinal)) { negative = true; text = text[prefix.Length..].Trim(); break; }
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) || Words.TryGetValue(text, out number)) { if (negative) number = -number; return true; }
        number = 0; return false;
    }

    // A small expression: numbers, words captured, + - * /, parentheses, sqrt(e), pos(name,i) and ori(name,i) - where an object is, from the state of the application.
    private sealed class Expression(string text, Dictionary<string, object> values, ApplicationState? state)
    {
        private int _at;
        public double Value() { var v = Sum(); Skip(); if (_at < text.Length) throw new FormatException("unexpected " + text[_at]); return v; }
        private void Skip() { while (_at < text.Length && text[_at] == ' ') _at++; }
        private double Sum()
        {
            var v = Product();
            for (Skip(); _at < text.Length && (text[_at] == '+' || text[_at] == '-'); Skip()) { var op = text[_at++]; var r = Product(); v = op == '+' ? v + r : v - r; }
            return v;
        }
        private double Product()
        {
            var v = Atom();
            for (Skip(); _at < text.Length && (text[_at] == '*' || text[_at] == '/'); Skip()) { var op = text[_at++]; var r = Atom(); v = op == '*' ? v * r : v / r; }
            return v;
        }
        private double Atom()
        {
            Skip();
            if (_at < text.Length && text[_at] == '-') { _at++; return -Atom(); }
            if (_at < text.Length && text[_at] == '(') { _at++; var v = Sum(); Skip(); _at++; return v; }
            var start = _at;
            while (_at < text.Length && (char.IsLetterOrDigit(text[_at]) || text[_at] == '.' || text[_at] == '_')) _at++;
            var token = text[start.._at];
            if (token.Length == 0) throw new FormatException("an expression is expected");
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return number;
            Skip();
            if (_at < text.Length && text[_at] == '(')
            {
                if (token == "sqrt") { _at++; var inner = Sum(); Skip(); _at++; return Math.Sqrt(inner); }      // sqrt(x*x+y*y): the distance of an object from the listener
                _at++; var name = (string)values[Word()]; Skip(); _at++; var i = (int)Sum(); Skip(); _at++;
                return Fact(token, name, i);
            }
            return values[token] is double d ? d : throw new FormatException(token + " is not a number");
        }
        private string Word() { Skip(); var s = _at; while (_at < text.Length && (char.IsLetterOrDigit(text[_at]) || text[_at] == '_')) _at++; return text[s.._at]; }

        // pos(name,i): the i-th number of the position the application says the object is at; ori(name,i): of its orientation.
        private double Fact(string function, string name, int i)
        {
            var label = function == "pos" ? "position" : function == "ori" ? "orientation" : throw new FormatException(function);
            var member = state?.Scenes.SelectMany(s => s.Members).FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) ?? throw new KeyNotFoundException(name);
            var fact = member.Facts.FirstOrDefault(f => f.Label == label).Value ?? throw new KeyNotFoundException(label);
            var numbers = Regex.Matches(fact, @"-?\d+(?:\.\d+)?").Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
            return numbers.Count > i ? numbers[i] : throw new KeyNotFoundException(label);
        }
    }
}
