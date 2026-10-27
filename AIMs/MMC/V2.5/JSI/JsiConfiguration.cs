using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Mpai.Mmc.Jsi;

// The language model of the JSON Interface: a system text, the turns so far, and the schema the answer has to be in (structured output); it gives the answer as text.
public delegate Task<string> StructuredModel(string system, IEnumerable<(string Role, string Content)> turns, JsonNode schema);

// WHAT MAKES THE JSON INTERFACE AN INTERFACE TO ONE APPLICATION: the entries of the Access of its L3 (AccessSources), as the Controller resolves them - the address of each
// schema and the file where Access keeps it - and the guide. Nothing else of the AIM is about an application.
public sealed record JsiConfiguration(
    string CommandSchemaUri, string? CommandSchemaFile,
    string ResponseSchemaUri, string? ResponseSchemaFile,
    string? ContextFile, string? SchemasRoot = null, bool RefreshesState = false, int Responders = 1)
{
    // The settings of an AIM that the Controller gave it: the file of each Setting. The addresses come from the Metadata. Responders: how many AIMs answer one command (the
    // editing AIMs of Audio Scene Management all do), so that their answers are one response.
    public static JsiConfiguration From(IReadOnlyDictionary<string, string> settings, string commandSchemaUri, string responseSchemaUri)
    {
        string? Get(string key) => settings.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        return new JsiConfiguration(commandSchemaUri, Get("CommandSchema"), responseSchemaUri, Get("ResponseSchema"), Get("ApplicationContext"), Get("SchemasRoot"), string.Equals(Get("RefreshState"), "true", StringComparison.OrdinalIgnoreCase),
            int.TryParse(Get("Responders"), out var responders) && responders > 1 ? responders : 1);
    }
}
