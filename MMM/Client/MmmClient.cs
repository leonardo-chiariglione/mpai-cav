using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mpai.Mmm.Client;

// A PROCESS ON THE MMM-API: it performs Process Actions in its own name, with the
// token the M-Instance gave it, each Request with a new Idempotency-Key unless the
// caller retries one; and it reads the Items it may read.
public sealed class MmmClient(HttpClient http, string processId, string? token = null)
{
    public string ProcessID { get; } = processId;
    public string? Token { get; set; } = token;

    private static readonly Dictionary<string, string> PathOf =
        Api.Paths.ToDictionary(p => p.Value, p => p.Key);

    public sealed record Result(int Http, ProcessActionResponse Response, IReadOnlyDictionary<string, string> Tokens);

    public async Task<Result> PerformAsync(string action, Complements complements, string? idempotencyKey = null)
    {
        var rq = new ProcessActionRequest { SourceProcessID = ProcessID, Action = action, Complements = complements };
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"/api/v2.2/{PathOf[action]}")
        {
            Content = JsonContent.Create(rq, options: Wire.Json)
        };
        msg.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString("N"));
        if (Token is not null) msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var rs = await http.SendAsync(msg);
        var body = await rs.Content.ReadFromJsonAsync<ProcessActionResponse>(Wire.Json) ?? new ProcessActionResponse();
        var tokens = rs.Headers.TryGetValues("MMM-Tokens", out var t)
            ? string.Join(",", t).Split(',').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1])
            : new Dictionary<string, string>();
        return new Result((int)rs.StatusCode, body, tokens);
    }

    // An Item, as the M-Instance lets this Process read it; the HTTP status otherwise.
    public async Task<(int Http, JsonObject? Item)> ReadAsync(string itemId)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, $"/api/v2.2/items/{Uri.EscapeDataString(itemId)}");
        if (Token is not null) msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var rs = await http.SendAsync(msg);
        return ((int)rs.StatusCode, rs.IsSuccessStatusCode ? JsonNode.Parse(await rs.Content.ReadAsStringAsync()) as JsonObject : null);
    }
}
