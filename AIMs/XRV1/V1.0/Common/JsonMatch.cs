using System.Linq;
using System.Text.Json.Nodes;

namespace Mpai.Xrv;

// WHEN DOES A STATUS MEET THE TARGET OF A CUE? The simplest reading: every element the target names is in the Status, and equal. An object of the target is met by an
// object of the Status that has all its properties met; an array of the target is met by an array of the Status that has, for each element of the target, an element that meets it;
// a value by an equal value (numbers by their value). What identifies an instance of a nested Object (its Header, its own ID, its Time, the MInstanceID, the UEnvironmentID, the
// DataXMData and the DescrMetadata) is not a Status element and is not compared.
public static class JsonMatch
{
    public static bool Meets(JsonNode? target, JsonNode? current)
    {
        switch (target)
        {
            case null: return current is null;
            case JsonObject t:
                if (current is not JsonObject c) return false;
                var headered = t.ContainsKey("Header");
                foreach (var (key, value) in t)
                {
                    if (Ignored(key, headered)) continue;
                    if (!c.TryGetPropertyValue(key, out var there) || !Meets(value, there)) return false;
                }
                return true;
            case JsonArray ta:
                return current is JsonArray ca && ta.All(x => ca.Any(y => Meets(x, y)));
            default:
                if (current is not JsonValue) return false;
                if (target.GetValueKind() == System.Text.Json.JsonValueKind.Number && current.GetValueKind() == System.Text.Json.JsonValueKind.Number)
                    return target.GetValue<double>() == current.GetValue<double>();
                return JsonNode.DeepEquals(target, current);
        }
    }

    private static bool Ignored(string key, bool headered) =>
        key is "Header" or "MInstanceID" or "UEnvironmentID" or "DataXMData" or "DescrMetadata"
        || (headered && (key.EndsWith("Time") || key.EndsWith("SpaceTime") || (key.EndsWith("ID") && key != "ControllerID")));
}
