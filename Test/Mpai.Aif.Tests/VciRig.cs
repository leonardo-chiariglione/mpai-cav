using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using AIF.Controller;
using AIF.Store;

using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Mmc.Jsi;
using Mpai.Providers;

namespace Mpai.Aif.Tests;

// THE VOICE COMMAND INTERFACE, WITH FAKES where the speech is (the recogniser, the synthesiser) and where the language model is - Ollama's HTTP interface, answered by the test.
// The AIMs that are the point - the JSON Interface and Entity Dialogue Processing - are the real ones, built by the provider of the Module (VciProvider) from the L3s of the
// repository; the L3s are copied with what they would have Access obtain (the weights of a model, the schemas) replaced by files at hand, since the wiring is judged here.
internal sealed class VciRig : IDisposable
{
    public const string Vci = "1MMC-VCI-V2.5-I01";
    public const string Bso = "OSD-BSO-V1.5", Bto = "OSD-BTO-V1.5", Ucm = "CAE-UCM-V1.0";

    public readonly string Folder = Path.Combine(Path.GetTempPath(), "vci-rig-" + Guid.NewGuid().ToString("N"));
    public readonly Queue<string> Heard = new();                                  // what the recogniser will hear, in order
    public readonly List<string> Spoken = new();                                  // what the synthesiser was given
    public readonly List<string> JsiTurns = new();                                // the last turn of every request the JSON Interface gave its model
    public readonly ScriptedOllama? Ollama;                                     // none where the dialogue is the real Ollama's
    public readonly Mpai.Aif.Api.ControllerApi Api;
    private readonly StructuredModel _model;

    public VciRig(StructuredModel model, Func<string, string>? dialogue)
    {
        _model = (system, turns, schema) =>
        {
            var list = turns.ToList();
            lock (JsiTurns) JsiTurns.Add(list[^1].Content);
            return model(system, list, schema);
        };
        Ollama = dialogue is null ? null : new ScriptedOllama(dialogue);
        Directory.CreateDirectory(Folder);
        var amds = Path.Combine(Folder, "AMDs"); Directory.CreateDirectory(amds);

        // The L3s of the Module, as they are, but for the Access: the model's weights are not needed, and the schemas and the guide are the files of the repository.
        string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        foreach (var name in new[] { Vci, "1MMC-ASR-V2.5-I01", "1MMC-JSI-V2.5-I02", "1MMC-EDP-V2.5-I03", "1MMC-TTS-V2.5-I01" })
        {
            var l3 = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Amds, name + ".json")))!;
            var sources = new JsonArray();
            if (name == "1MMC-JSI-V2.5-I02")
                foreach (var entry in l3["AccessSources"]!.AsArray().Where(e => !((string)e!["Name"]!).StartsWith("llm-")))
                {
                    var file = (string)entry!["Name"]! == "ApplicationContext" ? JsiRig.ContextFile : JsiRig.UserCommandFile;
                    var copy = entry.DeepClone();
                    copy["Sha256"] = Hash(file);
                    copy["Mirrors"] = new JsonArray(new Uri(file).AbsoluteUri);
                    sources.Add(copy);
                }
            l3["AccessSources"] = sources;
            File.WriteAllText(Path.Combine(amds, name + ".json"), l3.ToJsonString());
        }
        var settings = Path.Combine(Folder, "settings.json");
        File.WriteAllText(settings, new JsonObject
        {
            ["1MMC-EDP-V2.5-I03"] = Ollama is null ? new JsonObject() : new JsonObject { ["OllamaUrl"] = Ollama.Url },
            ["1MMC-JSI-V2.5-I02"] = new JsonObject { ["SchemasRoot"] = Repository.Schemas, ["Responders"] = "1" }
        }.ToJsonString());
        Api = new Mpai.Aif.Api.ControllerApi(amds, settings, store => new Provider(store, this));
    }

    // What the Controller asks the provider for: the JSON Interface and the dialogue are the provider's; the speech is the test's.
    private sealed class Provider : IAimProvider
    {
        private readonly VciRig _rig;
        private readonly VciProvider _real;
        private readonly AmdStore _store;
        public Provider(AmdStore store, VciRig rig) { _store = store; _rig = rig; _real = new VciProvider(store, rig._model); }

        public bool CanCreate(string aimName) => _real.CanCreate(aimName);

        public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage) => Create(aimName, settings, storage, null, null, null);

        public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage,
            AIF.SharedStorage.ISharedStorage? privateStorage, AIF.SharedStorage.IRuledStorage? moduleStorage, IAccessReader? access)
        {
            var ports = AimPortReader.Load(_store, aimName);
            switch (aimName)
            {
                case "1MMC-ASR-V2.5-I01":
                    return new Fake(aimName, m =>
                    {
                        lock (_rig.Heard) return _rig.Heard.Count == 0 ? new() : new() { [ports.Output(Bto)] = MpaiJson.ToJson(BasicTextObject.FromText(_rig.Heard.Dequeue())) };
                    });
                case "1MMC-TTS-V2.5-I01":
                    return new Fake(aimName, m =>
                    {
                        var text = MpaiJson.FromJson<BasicTextObject>(m.Ports[ports.Input(Bto)])!.GetText();
                        lock (_rig.Spoken) _rig.Spoken.Add(text);
                        return new() { [ports.Output(Bso)] = MpaiJson.ToJson(new BasicSpeechObject { BasicSpeechObjectID = "said", DescrMetadata = text }) };
                    });
                default:
                    return _real.Create(aimName, settings, storage, privateStorage, moduleStorage, access);
            }
        }
    }

    private sealed class Fake(string name, Func<Message, Dictionary<string, string>> body) : IAimProcessor
    {
        public string InstanceId => name;
        public Task<Message> ProcessAsync(Message message) =>
            Task.FromResult(new Message { MessageId = message.MessageId, MessageType = message.MessageType, Ports = body(message) });
    }

    // A person says something: the recogniser is given the sound and hears the words.
    public string Say(string words)
    {
        lock (Heard) Heard.Enqueue(words);
        return Datum(Bso, 1, MpaiJson.ToJson(new BasicSpeechObject { BasicSpeechObjectID = "voice" }));
    }

    private string Datum(string type, int port, string json)
    {
        var error = Api.InputWrite(Vci, type, port, json, 3000);
        return error.ToString();
    }

    public AifError Write(string type, int port, string json) => Api.InputWrite(Vci, type, port, json, 3000);

    public Mpai.Aif.Api.ControllerApi.Read Read(string type, int port, int timeoutMs = 4000) => Api.OutputRead(Vci, type, port, timeoutMs);

    public void Dispose()
    {
        try { Api.StopFlow(Vci); } catch { }
        Api.Dispose();
        Ollama?.Dispose();
        try { Directory.Delete(Folder, true); } catch { }
    }
}

// Ollama's /api/chat, answered by the test: the reply to a request is what the function says it is; the requests are kept.
internal sealed class ScriptedOllama : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<string, string> _reply;
    public string Url { get; }
    public List<string> Prompts { get; } = new();          // the last message of every request, the text the model was given to answer
    public List<string> Systems { get; } = new();

    public ScriptedOllama(Func<string, string> reply)
    {
        _reply = reply;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Url = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _ = Task.Run(Serve);
    }

    private async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); } catch { return; }
            using var reader = new StreamReader(context.Request.InputStream);
            var request = JsonNode.Parse(await reader.ReadToEndAsync())!;
            var messages = request["messages"]!.AsArray();
            var last = (string?)messages.Last()!["content"] ?? "";
            lock (Prompts) { Prompts.Add(last); Systems.Add((string?)messages[0]!["content"] ?? ""); }
            var body = Encoding.UTF8.GetBytes(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = _reply(last) } }.ToJsonString());
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }
    }

    public void Dispose() { _listener.Stop(); _listener.Close(); }
}
