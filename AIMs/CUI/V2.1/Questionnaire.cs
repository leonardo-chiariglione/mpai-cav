using System.Globalization;
using System.Text.Json.Nodes;

namespace Mpai.Cui;

// THE DATA OF A COMPANY, as CUI-CPP V2.0 and V2.1 give it: the Financial and Governance Descriptors (and Statements, which have the same format) are the answers to a
// questionnaire - records whose `input` lists items with a `code` (SPACTOT, CEA1, QUOTE...) and a `value`; the semantics of each code are those of the Data Type's page. This
// reads the items by code, wherever in the datum they are, and the numbers as the questionnaire gives them - "1234.5", "1.234,5" or "1234,5".
public sealed class Questionnaire
{
    private readonly Dictionary<string, List<string>> values = new(StringComparer.Ordinal);

    public Questionnaire(JsonNode? datum) => Collect(datum);

    private void Collect(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                if ((string?)o["code"] is { } code && o.ContainsKey("value")) Add(code, o["value"]);
                foreach (var (_, child) in o) Collect(child);
                break;
            case JsonArray a:
                foreach (var child in a) Collect(child);
                break;
        }
    }

    private void Add(string code, JsonNode? value)
    {
        if (value is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) Put(code, s);
        else if (value is JsonValue n && n.TryGetValue<double>(out var d)) Put(code, d.ToString(CultureInfo.InvariantCulture));
        else if (value is JsonArray a) foreach (var item in a) Add(code, item);
    }

    private void Put(string code, string value)
    {
        if (!values.TryGetValue(code, out var list)) values[code] = list = [];
        list.Add(value.Trim());
    }

    public bool Has(string code) => values.ContainsKey(code);
    public IReadOnlyList<string> All(string code) => values.TryGetValue(code, out var l) ? l : [];
    public string? Text(string code) => values.TryGetValue(code, out var l) ? l[0] : null;
    public double? Number(string code) => values.TryGetValue(code, out var l) ? Parse(l[0]) : null;
    public IEnumerable<double> Numbers(string code) => All(code).Select(Parse).Where(x => x.HasValue).Select(x => x!.Value);

    // A yes: 1, yes, si, true.
    public bool? Yes(string code) => Text(code)?.ToLowerInvariant() switch { "1" or "yes" or "si" or "sì" or "true" or "y" or "s" => true, "0" or "no" or "false" or "n" => false, _ => null };

    public static double? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().Replace(" ", "").Replace("%", "");
        if (s.Contains(',') && s.Contains('.')) s = s.Replace(".", "").Replace(',', '.');
        else if (s.Contains(',')) s = s.Replace(',', '.');
        else if (s.Count(c => c == '.') > 1) s = s.Replace(".", "");
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }
}
