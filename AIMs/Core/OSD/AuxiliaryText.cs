using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mpai.Core.OSD;

// THE AUXILIARY TEXT of a programme (OSD-TMA) as the identity AIMs use it: the words of its Basic Text Objects, and the names in it raising the candidates that bear them.
public static class AuxiliaryText
{
    // The words of a Text Object or of a Basic Text Object, from the Data of its Basic Text Objects; null where it has none.
    public static string? Words(string json)
    {
        var words = new List<string>();
        void Take(JsonNode? n)
        {
            if (n is not JsonObject o) return;
            if (o["BasicTextObjects"] is JsonArray many) foreach (var b in many) Take(b);
            else if (o["BasicTextData"] is JsonArray data) foreach (var d in data) if ((string?)d?["Data"] is { } t) words.Add(t);
        }
        try { Take(JsonNode.Parse(json)); } catch (JsonException) { return null; }
        return words.Count > 0 ? string.Join(" ", words) : null;
    }

    // The candidates whose label is in the text go up by bonus (to 1 at most), and the list is ordered again, the highest first.
    public static InstanceIdentifier RaiseNamed(InstanceIdentifier identity, string text, double bonus = 0.1)
    {
        var raised = identity.InstanceIdentifierData
            .Select(c => new InstanceCandidate
            {
                InstanceLabel = c.InstanceLabel,
                LabelConfidenceLevel = c.InstanceLabel.Length > 0 && text.Contains(c.InstanceLabel, StringComparison.OrdinalIgnoreCase) ? Math.Min(1, c.LabelConfidenceLevel + bonus) : c.LabelConfidenceLevel,
                Taxonomy = c.Taxonomy,
                TaxonomyConfidenceLevel = c.TaxonomyConfidenceLevel
            })
            .OrderByDescending(c => c.LabelConfidenceLevel)
            .ToList();
        identity.InstanceIdentifierData.Clear();
        identity.InstanceIdentifierData.AddRange(raised);
        return identity;
    }
}
