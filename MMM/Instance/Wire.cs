using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Mpai.Mmm;

// THE OBJECTS OF THE MMM-API (MMM-API chapter, 3): what a Process sends to perform a
// Process Action and what it receives. The names on the wire are those of MMM/Api/
// MMM-API.json: camelCase for the API's own fields, Nil/At/From/To/Of/With as the
// Backus-Naur Form of the Process Actions writes them.

// A Complement of With: an identifier, a Statused Reference, or an Item itself.
public sealed class Complements
{
    [JsonPropertyName("Nil")]  public string? Nil  { get; set; }
    [JsonPropertyName("At")]   public string? At   { get; set; }
    [JsonPropertyName("From")] public string? From { get; set; }
    [JsonPropertyName("To")]   public string? To   { get; set; }
    [JsonPropertyName("Of")]   public string? Of   { get; set; }
    [JsonPropertyName("With")] public List<JsonNode?>? With { get; set; }
}

public sealed class ProcessActionRequest
{
    public string SourceProcessID { get; set; } = "";
    public string DeonticVerb { get; set; } = "May";
    public string Action { get; set; } = "";
    public Complements Complements { get; set; } = new();
}

public sealed class PAStatus
{
    public string Code { get; set; } = "Ack";
    public string? Detail { get; set; }
}

public sealed class ProcessActionResponse
{
    public string DestinationProcessID { get; set; } = "";
    public Complements? Complements { get; set; }
    public PAStatus PaStatus { get; set; } = new();
}

// A Rights, Transaction or Service Pricing Model Item and its status: Model in a
// Request, Final in a Response - the two-phase commit of MMM-TEC V2.2 on the wire.
public sealed class StatusedRef
{
    public string ItemID { get; set; } = "";
    public string Status { get; set; } = "Model";
}

// What performing a Process Action gave: the HTTP status of 4.1 of the MMM-API
// chapter, the Response, and the tokens of the Processes a Registration created.
public sealed record Outcome(int Http, ProcessActionResponse Response)
{
    public IReadOnlyDictionary<string, string> Tokens { get; init; } = new Dictionary<string, string>();
}

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static JsonNode Ref(string itemId, string status) =>
        JsonSerializer.SerializeToNode(new StatusedRef { ItemID = itemId, Status = status }, Json)!;
}

// The Process Action paths of the MMM-API (4): /api/v2.2/<service>/<action>.
public static class Api
{
    public static readonly IReadOnlyDictionary<string, string> Paths = new Dictionary<string, string>
    {
        ["communicate/mm-send"] = "MM-Send", ["communicate/resolve"] = "Resolve",
        ["economy/license"] = "License", ["economy/post"] = "Post", ["economy/transact"] = "Transact",
        ["execute/execute"] = "Execute",
        ["export/mu-actuate"] = "MU-Actuate", ["export/mu-add"] = "MU-Add", ["export/mu-animate"] = "MU-Animate",
        ["export/mu-move"] = "MU-Move", ["export/mu-send"] = "MU-Send",
        ["identity/hide"] = "Hide", ["identity/identify"] = "Identify", ["identity/modify"] = "Modify", ["identity/register"] = "Register",
        ["import/um-actuate"] = "UM-Actuate", ["import/um-capture"] = "UM-Capture", ["import/um-send"] = "UM-Send",
        ["information/authenticate"] = "Authenticate", ["information/discover"] = "Discover", ["information/interpret"] = "Interpret",
        ["item/author"] = "Author", ["item/convert"] = "Convert",
        ["locate/mm-add"] = "MM-Add", ["locate/mm-animate"] = "MM-Animate", ["locate/mm-capture"] = "MM-Capture",
        ["locate/mm-move"] = "MM-Move", ["locate/property-change"] = "Property Change",
        ["rights/rights-change"] = "Rights Change", ["rights/validate"] = "Validate"
    };
}
