// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using AIF.Controller;
using System.Security.Cryptography;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Mpai.Mmc.Edp;

// MMC-SUM-V2.5 - Summary. The running dialogue memory: a plain-text running summary
// of the conversation (carried in SummaryData), which EDP reads in and writes back
// updated. Mirrors schemas/MMC/V2.5/data/Summary.json (the full schema also carries
// structured SummaryElements; this implementation uses the plain-text SummaryData
// for the LLM's working memory).
public sealed class Summary
{
    public string Header { get; init; } = "MMC-SUM-V2.5";
    public string SummaryID { get; init; } = Guid.NewGuid().ToString();
    public List<SummaryDataItem> SummaryData { get; init; } = new();

    public string Text()
        => SummaryData.Count > 0 && SummaryData[0].Data is not null ? SummaryData[0].Data! : "";

    public static Summary Of(string text) => new()
    {
        SummaryData = new List<SummaryDataItem> { new() { Data = text } }
    };
}

public sealed class SummaryDataItem
{
    public string? Data { get; init; }
    public string? DataURI { get; init; }
    public long? DataLength { get; init; }
    public string? DataID { get; init; }
}

// Minimal client for a local Ollama server (http://127.0.0.1:11434). Uses the
// /api/chat endpoint with stream disabled, and returns the assistant's message
// content. HttpClient + System.Text.Json only - no external package.
//
// THE SAME MODEL, THE SAME WAY, ON EVERY MACHINE. Ollama chooses a context from the
// memory it finds - 4096 tokens on a 4 GB card, 32768 on a 24 GB one, whose cache is
// larger to set up and to fill at every request - and unloads a model 5 minutes after
// its last use, so the first turn after a pause loads it again. The context is
// therefore stated (always the same: a different one reloads the model), and the
// model kept loaded.
public sealed class OllamaClient : IDisposable
{
    private readonly HttpClient _http;
    private string _model;
    private readonly int _context;
    private readonly int _keepAlive;

    // context: tokens of prompt and answer. keepAliveSeconds: how long the model stays
    // loaded after a request; negative, for as long as Ollama runs.
    public OllamaClient(string model = "llama3.1", string baseUrl = "http://127.0.0.1:11434",
                        int context = 4096, int keepAliveSeconds = -1)
    {
        _model = model;
        _context = context;
        _keepAlive = keepAliveSeconds;
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(5) };
    }

    // Send a system + user prompt, return the assistant's text reply.
    public Task<string> ChatAsync(string system, string user) =>
        PostAsync(model => new
        {
            model,
            stream = false,
            keep_alive = _keepAlive,
            options = new { num_ctx = _context },
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user",   content = user }
            }
        });

    // The same, the reply a JSON object, and the same reply to the same prompt
    // (temperature 0): for understanding, where what is understood is then checked.
    public Task<string> ChatJsonAsync(string system, string user) =>
        PostAsync(model => new
        {
            model,
            stream = false,
            format = "json",
            keep_alive = _keepAlive,
            options = new { temperature = 0, seed = 1, num_ctx = _context },
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user",   content = user }
            }
        });

    // The same, the reply forced to a JSON Schema (Ollama's structured output): the
    // model can only answer with what the schema allows.
    public Task<string> ChatSchemaAsync(string system, IEnumerable<(string Role, string Content)> turns, JsonNode schema) =>
        PostAsync(model => new
        {
            model,
            stream = false,
            format = schema,
            keep_alive = _keepAlive,
            options = new { temperature = 0, seed = 1, num_ctx = _context },
            messages = new[] { new { role = "system", content = system } }
                .Concat(turns.Select(t => new { role = t.Role, content = t.Content })).ToArray()
        });

    private async Task<string> PostAsync(Func<string, object> build)
    {
        await ProvideAsync().ConfigureAwait(false);
        var json = JsonSerializer.Serialize(build(_model));
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("/api/chat", content).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        // Ollama /api/chat (non-stream): { "message": { "role":"assistant", "content":"..." }, ... }
        if (doc.RootElement.TryGetProperty("message", out var msg) &&
            msg.TryGetProperty("content", out var contentEl))
            return contentEl.GetString() ?? "";
        return "";
    }

    // ---- THE MODEL FROM ACCESS ---------------------------------------------------------------------------------------------
    // The weights of the language model are data of Access, not something Ollama fetches: the Metadata of the AIM declares them
    // (AccessSources "llm-weights", with "llm-template" and "llm-parameters" where the model has them), by the URI they are
    // published at and their SHA-256, and the Controller has obtained and checked them. Ollama is the program that runs the
    // model: it is given the weights from Access and creates the model from them, once - under a name that comes from the
    // weights - and the AIM then uses that model. Ollama itself is part of the Implementation, not of Access.
    public const string WeightsName = "llm-weights", TemplateName = "llm-template", ParametersName = "llm-parameters";

    private IAccessReader? _access;
    private bool _provided;
    private readonly SemaphoreSlim _providing = new(1, 1);

    public void UseAccess(IAccessReader? access)
    {
        if (access is not null && access.SourcesOf(WeightsName).Count > 0) _access = access;
    }

    // The name of the model Ollama creates from weights obtained from that URI.
    public static string ModelNameOf(string weightsUri) =>
        "mpai-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(weightsUri)))[..12].ToLowerInvariant();

    private async Task ProvideAsync()
    {
        if (_access is null || _provided) return;
        await _providing.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_provided) return;
            _model = await CreateFromAccessAsync(_access).ConfigureAwait(false);
            _provided = true;
        }
        finally { _providing.Release(); }
    }

    private async Task<string> CreateFromAccessAsync(IAccessReader access)
    {
        var weights = access.SourcesOf(WeightsName)[0];
        var name = ModelNameOf(weights);
        using var http = new HttpClient { BaseAddress = _http.BaseAddress, Timeout = TimeSpan.FromHours(2) };

        // Already created from these weights.
        using (var show = await http.PostAsync("/api/show", Json(new { model = name })).ConfigureAwait(false))
            if (show.IsSuccessStatusCode) return name;

        // The weights are read from where Access keeps them, never held in memory.
        string digest;
        if (access.Open(weights, "", out var hashing) != AccessOutcome.OK)
            throw new InvalidOperationException($"The weights of the language model ({weights}) cannot be read from Access.");
        using (hashing) digest = Convert.ToHexString(await SHA256.HashDataAsync(hashing).ConfigureAwait(false)).ToLowerInvariant();

        using (var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/api/blobs/sha256:{digest}")).ConfigureAwait(false))
            if (!head.IsSuccessStatusCode)
            {
                access.Open(weights, "", out var upload);
                using (upload)
                {
                    using var body = new StreamContent(upload);
                    using var sent = await http.PostAsync($"/api/blobs/sha256:{digest}", body).ConfigureAwait(false);
                    sent.EnsureSuccessStatusCode();
                }
            }

        string? Text(string entry) =>
            access.SourcesOf(entry) is { Count: > 0 } uris && access.Get(uris[0], "", out var data) == AccessOutcome.OK
                ? Encoding.UTF8.GetString(data) : null;

        var request = new JsonObject
        {
            ["model"] = name,
            ["files"] = new JsonObject { ["weights.gguf"] = $"sha256:{digest}" },
            ["stream"] = false
        };
        if (Text(TemplateName) is { } template) request["template"] = template;
        if (Text(ParametersName) is { } parameters) request["parameters"] = JsonNode.Parse(parameters);
        using var created = await http.PostAsync("/api/create", Json(request)).ConfigureAwait(false);
        created.EnsureSuccessStatusCode();
        return name;
    }

    private static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    public void Dispose() => _http.Dispose();
}
