using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mpai.Aif.Tests;

// RIGHTS IS A FIRST-CLASS ITEM (the author, 2026/10/01): its RightsData lists Rights,
// each with its Deontic Verb, its Process Actions and its Level - Internal, from
// registration; Acquired, from Process Actions with other Processes; Granted, by a
// User to another for a time, as in Use Case 2. Penalties is its copy, where a May
// becomes a May Not. Each admits an instance of what it describes and refuses one
// without what it requires.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class MmmSchemaTests
{
    private static JsonObject Rights(string name, string header, string verb, string? level, bool withId = true)
    {
        var entry = new JsonObject
        {
            ["DeonticVerb"] = verb,
            ["PAIDOrPA"] = new JsonArray(new JsonObject { ["ProcessActionID"] = "MM-Move-Room1", ["Status"] = "Final" })
        };
        if (level is not null) entry["Level"] = level;
        var o = new JsonObject { ["Header"] = header };
        if (withId) o[$"{name}ID"] = $"{name}-Friend2-Room1";
        o[$"{name}Data"] = new JsonArray(entry);
        return o;
    }

    [Fact]
    public void RightsAndPenalties()
    {
        var schemas = AIF.Metadata.PublishedSchemas.At(Repository.Schemas);
        string Check(string file, JsonObject instance)
        {
            var schema = schemas[Path.GetFullPath(Path.Combine(Repository.Schemas, "MMM4", "V2.2", "data", file + ".json"))];
            using var doc = JsonDocument.Parse(instance.ToJsonString());
            lock (AIF.Metadata.PublishedSchemas.Lock) return schema.Evaluate(doc.RootElement).IsValid ? "valid" : "invalid";
        }
        var result = new Dictionary<string, string>
        {
            ["Rights granted to Friend2 on Room1"] = Check("Rights", Rights("Rights", "MMM-RGT-V2.2", "May", "Granted")),
            ["Rights without the Level of a Right"] = Check("Rights", Rights("Rights", "MMM-RGT-V2.2", "May", null)),
            ["Rights without its identifier"] = Check("Rights", Rights("Rights", "MMM-RGT-V2.2", "May", "Granted", withId: false)),
            ["Rights with the Header as a pattern"] = Check("Rights", Rights("Rights", "^MMM-RGT-V2.2$", "May", "Granted")),
            ["Penalties, a May become a May Not"] = Check("Penalties", Rights("Penalties", "MMM-PNL-V2.2", "May Not", "Acquired")),
            ["Penalties with the Header of Rights"] = Check("Penalties", Rights("Penalties", "MMM-RGT-V2.2", "May Not", "Acquired")),
        };
        Expected.Match("mmm-schemas.json", result);
    }
}
