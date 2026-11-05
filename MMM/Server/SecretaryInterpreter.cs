using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.Store;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Mmc.Edp;
using Mpai.Mmc.Jsi;

namespace Mpai.Mmm.Server;

// WHAT THE SECRETARY UNDERSTANDS (DRAFT). The Secretary is a User of the meeting; what a participant says to it is turned into a request by the Voice Command Interface - here its JSON Interface
// (MMC-JSI), the real AIM, as the L3 1MMC-JSI-V2.5-I03 gives it the Application Context of the Secretary: the schema of the User Command of MMM (MMM-UCM-V2.2) and the guide
// (Access/MMM/SecretaryApplicationContext.json). The Secretary carries out the request and says the answer in a few words. One JSON Interface for each participant: it remembers what that
// participant asked before ("in Greek" after a summary). The language model is Ollama's, loaded when somebody first speaks to the Secretary.
public sealed class SecretaryInterpreter(string root, string model = "llama3.2:3b") : IDisposable
{
    public const string Aim = "1MMC-JSI-V2.5-I03", CommandType = "MMM-UCM-V2.2";
    private readonly Lazy<OllamaClient> llm = new(() => new OllamaClient(model, context: 4096));
    private readonly Lazy<AimPortReader> ports = new(() =>
    {
        var store = new AmdStore(Path.Combine(root, "AIMs", "AMDs")); store.Scan();
        return AimPortReader.Load(store, Aim);
    });
    private readonly Dictionary<string, JsiAimProcessor> byUser = new();

    public static string CommandSchemaFile(string root) => Path.Combine(root, "schemas", "MMM4", "V2.2", "data", "UserCommand.json");
    public static string ContextFile(string root) => Path.Combine(root, "Access", "MMM", "SecretaryApplicationContext.json");
    private const string SchemaUri = "https://schemas.mpai.community/MMM4/V2.2/data/UserCommand.json";

    private JsiAimProcessor For(string user)
    {
        lock (byUser)
        {
            if (byUser.TryGetValue(user, out var jsi)) return jsi;
            var configuration = new JsiConfiguration(SchemaUri, CommandSchemaFile(root), SchemaUri, CommandSchemaFile(root), ContextFile(root), Path.Combine(root, "schemas"), CommandDataType: CommandType);
            StructuredModel structured = (system, turns, schema) => llm.Value.ChatSchemaAsync(system, turns, schema);
            return byUser[user] = new JsiAimProcessor(Aim + "/" + user, ports.Value, configuration, structured);
        }
    }

    // What the participant said -> the request (UserCommandRQ of an MMM-UCM), or, when no request can be made of it, what the Secretary says instead (the Response Hint).
    public async Task<(JsonObject? Command, string? Hint)> InterpretAsync(string user, string words)
    {
        var p = ports.Value;
        var message = new Message { MessageId = Guid.NewGuid().ToString(), Ports = new Dictionary<string, string> { [p.Input("OSD-BTO-V1.5")] = MpaiJson.ToJson(BasicTextObject.FromText(words)) } };
        var answer = await For(user).ProcessAsync(message);
        JsonObject? command = answer.Ports.TryGetValue(p.OutputOrDefault(CommandType, 1, ""), out var c) ? JsonNode.Parse(c) as JsonObject : null;
        string? hint = answer.Ports.TryGetValue(p.Output("OSD-BTO-V1.5"), out var h) ? MpaiJson.FromJson<BasicTextObject>(h)?.GetText() : null;
        return (command, hint);
    }

    // The response of the Secretary (an MMM-UCM with a UserCommandRS), given back to the JSON Interface of the participant: the Response Hint it gives is what the Secretary says.
    public async Task<string?> AnswerAsync(string user, JsonObject response)
    {
        var p = ports.Value;
        var message = new Message { MessageId = Guid.NewGuid().ToString(), Ports = new Dictionary<string, string> { [p.InputOrDefault(CommandType, 1, "")] = response.ToJsonString() } };
        var answer = await For(user).ProcessAsync(message);
        return answer.Ports.TryGetValue(p.Output("OSD-BTO-V1.5"), out var h) ? MpaiJson.FromJson<BasicTextObject>(h)?.GetText() : null;
    }

    public void Dispose() { if (llm.IsValueCreated) llm.Value.Dispose(); }
}
