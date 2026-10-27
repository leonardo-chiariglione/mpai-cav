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
    private static readonly Dictionary<string, double> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12,
        ["fifteen"] = 15, ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60, ["ninety"] = 90, ["hundred"] = 100, ["half"] = 0.5, ["a"] = 1
    };

    private const string Number = @"(?:minus |negative |-)?(?:\d+(?:\.\d+)?|zero|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|fifteen|twenty|thirty|forty|fifty|sixty|ninety|hundred|half)";

    // The command the request comes to, or null: no rule matched, a name is not one the application has, or the command does not conform (the caller checks it).
    public static JsonObject? Match(string request, IReadOnlyList<JsonObject> rules, IReadOnlyDictionary<string, IReadOnlyList<string>> nameSets, ApplicationState state, string? lastCommandId = null, IDictionary<string, double>? memory = null)
    {
        var text = Regex.Replace(request.ToLowerInvariant().Trim().TrimEnd('.', '!', '?', ','), @"\s+", " ");
        text = Regex.Replace(text, @"^(?:please|can you|could you|would you|i want you to|i want to|now) ", "");
        text = text.Replace("meters", "metres").Replace("meter", "metre").Replace(" m ", " metres ");
        foreach (var rule in rules)
        {
            var pattern = (string?)rule["Pattern"];
            if (pattern is null || rule["Command"] is not JsonObject template) continue;
            Regex regex;
            try { regex = new Regex("^" + Regex.Replace(pattern, @"<NUM:(\w+)>", m => "(?<" + m.Groups[1].Value + ">" + Number + ")") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)); }
            catch (ArgumentException) { continue; }
            var match = regex.Match(text);
            if (!match.Success) continue;

            var values = new Dictionary<string, object>(StringComparer.Ordinal);
            if (memory is not null) foreach (var (k, v) in memory) values[k] = v;                  // what an earlier request said and a rule keeps (the size of the room)
            if (lastCommandId is not null) values["last"] = lastCommandId;
            var ok = true;
            foreach (var group in regex.GetGroupNames().Where(g => !int.TryParse(g, out _)))
            {
                var word = match.Groups[group].Value;
                if (rule["Names"]?[group] is JsonValue setName && (string?)setName is { } set)
                {
                    var names = nameSets.GetValueOrDefault(set) ?? [];
                    var found = names.FirstOrDefault(n => string.Equals(n, word.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (found is null) { ok = false; break; }
                    values[group] = found;
                }
                else if (TryNumber(word, out var number)) values[group] = number;
                else values[group] = word.Trim();
            }
            if (!ok) continue;
            try
            {
                if (rule["Let"] is JsonObject let)
                    foreach (var (name, expression) in let) values[name] = new Expression((string)expression!, values, state).Value();
                var made = (JsonObject)Fill(template, values);
                if (memory is not null && rule["Keep"] is JsonArray keep)
                    foreach (var k in keep.Select(x => (string)x!)) if (values.TryGetValue(k, out var v) && v is double d) memory[k] = d;
                return made;
            }
            catch (Exception e) when (e is FormatException or InvalidOperationException or KeyNotFoundException or DivideByZeroException) { continue; }
        }
        return null;
    }

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

    // A small expression: numbers, words captured, + - * /, parentheses, pos(name,i) and ori(name,i) - where an object is, from the state of the application.
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
