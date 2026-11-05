using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Mpai.Pgm;

// The language model BKN asks. The AIM knows only this contract; which model answers is configuration (settings "Llm.Model", "Llm.Url").
public interface ILanguageModel
{
    // The answer to the system and user messages, as a JSON text that complies with the schema.
    Task<string> AnswerAsync(string system, string user, JsonObject schema);
}

// Ollama, over its HTTP interface. The model name is a setting; nothing here names one.
public sealed class OllamaModel : ILanguageModel
{
    public const string ModelSetting = "Llm.Model", UrlSetting = "Llm.Url";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly string _model, _url;

    public OllamaModel(string model, string url = "http://localhost:11434")
    {
        _model = model;
        _url = url.TrimEnd('/');
    }

    public string Model => _model;

    public static OllamaModel From(IReadOnlyDictionary<string, string> settings) =>
        new(settings.TryGetValue(ModelSetting, out var m) && m.Length > 0 ? m : throw new InvalidOperationException($"BKN needs the setting '{ModelSetting}'."),
            settings.TryGetValue(UrlSetting, out var u) && u.Length > 0 ? u : "http://localhost:11434");

    public async Task<string> AnswerAsync(string system, string user, JsonObject schema)
    {
        var body = new JsonObject
        {
            ["model"] = _model,
            ["stream"] = false,
            ["format"] = schema.DeepClone(),
            ["options"] = new JsonObject { ["temperature"] = 0 },
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user })
        };
        using var response = await Http.PostAsync(_url + "/api/chat", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Ollama answered {(int)response.StatusCode}: {text}");
        return (string?)JsonNode.Parse(text)?["message"]?["content"] ?? throw new InvalidOperationException("Ollama gave no content.");
    }
}
