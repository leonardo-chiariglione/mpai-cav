using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;

using AIF.Controller;
using AIF.Store;

using Mpai.Aif.Api;
using Mpai.Cae.AsmVoice;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Mmc.Jsi;
using Mpai.Providers;
using Mpai.Ua;
using Mpai.Wdl;

namespace Mpai.Aif.Tests;

// AUDIO SCENE MANAGEMENT BY VOICE: THE WORKFLOW OF THE APP, WITH THE REAL AIMs (2026/10/27). Apps/CAE/ASM/ASM-VCI.orch is read and run by the interpreter of the User Agent,
// over ONE Controller that holds the one Module, Audio Scene Management, with the Voice Command Interface inside it, built from their L3s, with their real AIMs: the JSON Interface (the
// guide and the schema of the User Command, from the files of this software), Entity Dialogue Processing (the language model is Ollama's HTTP interface, answered by the test),
// the editing AIMs over a Shared Storage. What is faked is the world: the speech recogniser hears what the test says (no microphone), the synthesiser records what it is given
// (no loudspeaker), and the language model of the JSON Interface is a function of the request (no GPU); the devices of the window are a push device for the voice and for the
// sounds, and devices that record what they are delivered.
//
// Judged, step by step, on the state of the scene (the Audio Scene Descriptors ASM delivers) and on what the machine said:
//   two sounds are loaded, "violin" and "drums"; "put the violin at x 2, y 1, z 0"; "put the drums at x 1, y 2, z 0"; "move the drums to x -1, y 3, z 0";
//   "what is in the scene?" (the reply names the objects and where they are); "move the violin a bit to the left" (no number: the reply asks for it, the scene does not change);
//   a second sound named "violin" (it is "violin 2"); "undo that" (the last command is revoked).
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class AsmVoiceWorkflowTests
{
    private static string Orch => Path.Combine(Repository.Root, "Apps", "CAE", "ASM", "ASM-VCI.orch");

    // A device of the window that gives what the test pushes, as it is pushed, for the life of the workflow.
    private sealed class PushDevice(string dataType) : IDevice
    {
        private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
        public void Push(string json) => _queue.Writer.TryWrite(json);
        public Task DeliverAsync(string type, string json, CancellationToken cancel) => Task.CompletedTask;
        public async IAsyncEnumerable<(string DataType, string Json)> ReadAsync([EnumeratorCancellation] CancellationToken cancel)
        {
            await foreach (var json in _queue.Reader.ReadAllAsync(cancel)) yield return (dataType, json);
        }
        public Task SafeStopAsync() => Task.CompletedTask;
    }

    // A device that records what it is delivered.
    private sealed class RecordDevice : IDevice
    {
        public readonly List<string> Got = new();
        public Task DeliverAsync(string type, string json, CancellationToken cancel) { lock (Got) Got.Add(json); return Task.CompletedTask; }
        public async IAsyncEnumerable<(string DataType, string Json)> ReadAsync([EnumeratorCancellation] CancellationToken cancel) { await Task.Delay(Timeout.Infinite, cancel).ConfigureAwait(false); yield break; }
        public Task SafeStopAsync() => Task.CompletedTask;
        public List<string> All() { lock (Got) return Got.ToList(); }
    }

    // The Voice Command Interface with the world faked: the speech it hears is what the test says; the speech it makes is recorded.
    private sealed class Rig : IDisposable
    {
        public readonly Queue<string> Heard = new();
        public readonly List<string> Spoken = new();
        public readonly List<string> JsiTurns = new();
        public readonly ScriptedOllama Ollama;
        public readonly AsmVoiceModules.Prepared Prepared;
        public readonly ControllerApi Api;
        public readonly PushDevice Voice = new("OSD-BSO-V1.5"), SoundIn = new("CAE-UCM-V1.0");
        public readonly RecordDevice HeardText = new(), ReplyText = new(), ReplySpeech = new(), Scene = new(), Unused = new();
        public readonly List<string> Prompts = new();
        private readonly CancellationTokenSource _stop = new();
        private Task? _run;
        public string Said = "";

        public Rig(Func<string, string> languageModel, Func<string, string> dialogue)
        {
            Ollama = new ScriptedOllama(dialogue);
            Prepared = AsmVoiceModules.Prepare(Repository.Root, settings: new JsonObject { ["1MMC-EDP-V2.5-I03"] = new JsonObject { ["OllamaUrl"] = Ollama.Url } });
            // The dialogue's language model is the test's: Ollama is not given the weights from Access (the Access of the Module's other AIMs is not needed either).
            foreach (var name in new[] { "1MMC-EDP-V2.5-I03", "1MMC-ASR-V2.5-I01", "1MMC-TTS-V2.5-I01" })
            {
                var path = Path.Combine(Prepared.Amds, name + ".json");
                var l3 = JsonNode.Parse(File.ReadAllText(path))!; l3["AccessSources"] = new JsonArray(); File.WriteAllText(path, l3.ToJsonString());
            }
            StructuredModel model = (system, turns, schema) =>
            {
                var list = turns.ToList();
                lock (JsiTurns) JsiTurns.Add(list[^1].Content);
                return Task.FromResult(languageModel(list[^1].Content));
            };
            Api = AsmVoiceModules.Controller(Prepared, store => new Provider(store, this, model));
            // The Controller's own business, as the App's: where the Module keeps its Objects and Scenes.
            Assert.Equal(AifError.OK, Api.StartFlow(AsmVoiceModules.Asm));
            Assert.Equal(AifError.OK, Api.SharedStorageInit(AsmVoiceModules.Asm, Path.Combine(Prepared.Folder, "storage")));
        }

        public void Start()
        {
            var devices = new DeviceRegistry();
            devices.RegisterDevice("Voice", Voice).RegisterDevice("SoundIn", SoundIn).RegisterDevice("SceneIn", new PushDevice("OSD-ASD-V1.5"))
                   .RegisterDevice("Heard", HeardText).RegisterDevice("ReplyText", ReplyText).RegisterDevice("ReplySpeech", ReplySpeech)
                   .RegisterDevice("Scene", Scene).RegisterDevice("Unused", Unused);
            devices.RegisterPresent("screen", data => { if (data.TryGetValue("Prompt", out var p)) lock (Prompts) Prompts.Add(p); return Task.CompletedTask; });
            var workflow = new WorkflowReader().Read(File.ReadAllText(Orch));
            var interpreter = new WorkflowInterpreter(Api.Async(), devices, line => { lock (this) Said += line + "\n"; });
            _run = Task.Run(async () => { try { await interpreter.RunAsync(workflow, _stop.Token); } catch (OperationCanceledException) { } });
        }

        // The person says something: the recogniser will hear these words in the sound.
        public void Say(string words)
        {
            lock (Heard) Heard.Enqueue(words);
            Voice.Push(MpaiJson.ToJson(new BasicSpeechObject { BasicSpeechObjectID = "voice" }));
        }

        public void Load(string name, byte seed = 1) => SoundIn.Push(AsmRig.Json(AsmRig.Command(new UserCommandRQ { AcquiredObject = new ManagedObject { BasicAudioObject = AsmRig.Sound(name, 1, seed) } })));      // a sound arrives in a User Command

        public void Dispose()
        {
            _stop.Cancel();
            try { _run?.Wait(TimeSpan.FromSeconds(20)); } catch { }
            Api.Dispose();
            Ollama.Dispose();
            try { Directory.Delete(Prepared.Folder, true); } catch { }
        }

        private sealed class Provider : IAimProvider
        {
            private readonly Rig _rig;
            private readonly VciProvider _real;
            private readonly AmdStore _store;
            public Provider(AmdStore store, Rig rig, StructuredModel model) { _store = store; _rig = rig; _real = new VciProvider(store, model); }
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
                            lock (_rig.Heard) return _rig.Heard.Count == 0 ? new() : new() { [ports.Output("OSD-BTO-V1.5")] = MpaiJson.ToJson(BasicTextObject.FromText(_rig.Heard.Dequeue())) };
                        });
                    case "1MMC-TTS-V2.5-I01":
                        return new Fake(aimName, m =>
                        {
                            var text = MpaiJson.FromJson<BasicTextObject>(m.Ports[ports.Input("OSD-BTO-V1.5")])!.GetText();
                            lock (_rig.Spoken) _rig.Spoken.Add(text);
                            return new() { [ports.Output("OSD-BSO-V1.5")] = MpaiJson.ToJson(new BasicSpeechObject { BasicSpeechObjectID = "said", DescrMetadata = text }) };
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

        // The positions of the objects of the scene, by name, in the last Audio Scene Descriptors ASM delivered.
        public Dictionary<string, double[]> Positions()
        {
            var last = Scene.All().LastOrDefault();
            var result = new Dictionary<string, double[]>();
            if (last is null) return result;
            void Walk(JsonNode? n)
            {
                switch (n)
                {
                    case JsonObject o:
                        if (o["AudioObjectSpaceTime"]?["SpatialAttitude1"]?["Position"]?["CartPosition"] is JsonArray p
                            && (o["AObjectIDOrAObject"] as JsonArray)?.FirstOrDefault()?["BasicAudioObjectID"] is { } name)
                            result[(string)name!] = p.Select(x => x!.GetValue<double>()).ToArray();
                        foreach (var kv in o) Walk(kv.Value);
                        break;
                    case JsonArray a: foreach (var e in a) Walk(e); break;
                }
            }
            Walk(JsonNode.Parse(last));
            return result;
        }

        public string Log() { lock (this) return Said; }
    }

    // A function of what the JSON Interface's model is given: the last turn holds the request and the commands sent before.
    private static string Model(string turn)
    {
        var request = Regex.Match(turn, @"Request: (?<r>.*)$", RegexOptions.Multiline).Groups["r"].Value.Trim().ToLowerInvariant();
        static string Put(string key, string name, double x, double y, double z) =>
            "{\"Working\":\"" + key + " " + name + "\",\"Command\":{\"UserCommandRQ\":{\"" + key + "\":{\"Objects\":[{\"ObjectID\":\"" + name + "\",\"" + (key == "AddedObjects" ? "SpatialAttitude" : "NewSpatialAttitude") +
            "\":{\"Position\":{\"CartPosition\":[" + x + "," + y + "," + z + "]},\"Orientation\":{\"Orientation\":[0,0,180]}}}]}}}}";
        if (request.StartsWith("put the violin at x 2")) return Put("AddedObjects", "violin", 2, 1, 0);
        if (request.StartsWith("put the drums at x 1")) return Put("AddedObjects", "drums", 1, 2, 0);
        if (request.StartsWith("move the drums to x -1")) return Put("MovedObjects", "drums", -1, 3, 0);
        if (request.StartsWith("undo"))
        {
            var id = Regex.Matches(turn, @"^- (?<id>\S+): ""(?<r>.*)""", RegexOptions.Multiline).Where(m => m.Groups["r"].Value.ToLowerInvariant().StartsWith("move") || m.Groups["r"].Value.ToLowerInvariant().StartsWith("put")).LastOrDefault()?.Groups["id"].Value ?? "";
            return "{\"Working\":\"undo\",\"Command\":{\"UserCommandRQ\":{\"RevokedUserCommandID\":\"" + id + "\"}}}";
        }
        return "{\"Working\":\"?\",\"Problem\":\"I did not understand.\"}";
    }

    // What the dialogue is asked to say: the facts it is told, as they are.
    private static string Dialogue(string prompt)
    {
        var at = prompt.IndexOf("Response Hint:", StringComparison.Ordinal);
        return at < 0 ? "Yes." : prompt[(at + 14)..].Trim();
    }

    private static async Task<bool> Until(Func<bool> condition, int milliseconds = 30000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until) { if (condition()) return true; await Task.Delay(100); }
        return condition();
    }


    // THE MODULE AS THE USER AGENT STARTS IT FOR A PERSON: the real AIMs - the recogniser, the JSON Interface (its Access read from the files of this software), the dialogue and the
    // synthesiser - under one Controller, started and given a place to keep its Objects and Scenes. Nothing is said to it: the language model, the microphone and the loudspeaker are not used.
    [SkippableFact]
    public void TheModuleStartsWithTheRealAimsAsThePersonsUserAgentStartsIt()
    {
        Skip.IfNot(TestModels.Can("ggml-small.bin"), "The models cannot be had: the L3s declare them, and Access holds them or cannot obtain them.");
        var prepared = AsmVoiceModules.Prepare(Repository.Root);
        using var api = AsmVoiceModules.Controller(prepared);
        try
        {
            Assert.Equal(AifError.OK, api.StartFlow(AsmVoiceModules.Asm));
            Assert.Equal(AifError.OK, api.SharedStorageInit(AsmVoiceModules.Asm, Path.Combine(prepared.Folder, "storage")));
            Assert.All(api.Status(AsmVoiceModules.Asm).Aims, a => Assert.Equal(AimStatus.Alive, a.Status));

            // The User Agent's own Store, Read, List and Delete on the Module's Shared Storage, through the Controller API (under the rules of the storage).
            Assert.Equal(AifError.OK, api.SharedStorageStore(AsmVoiceModules.Asm, "ua-note", [1, 2, 3]));
            Assert.Contains("ua-note", api.SharedStorageList(AsmVoiceModules.Asm, "ua-"));
            Assert.Equal(AifError.OK, api.SharedStorageRead(AsmVoiceModules.Asm, "ua-note", out var note));
            Assert.Equal([1, 2, 3], note);
            Assert.Equal(AifError.OK, api.SharedStorageDelete(AsmVoiceModules.Asm, "ua-note"));
            Assert.Equal(AifError.NotFound, api.SharedStorageRead(AsmVoiceModules.Asm, "ua-note", out _));
            Assert.Equal(AifError.NotStarted, api.SharedStorageStore("no-such-module", "k", [1]));
        }
        finally { try { api.StopFlow(AsmVoiceModules.Asm); Directory.Delete(prepared.Folder, true); } catch { } }
    }

    [Fact]
    public async Task WhatIsSaidEditsTheSceneAndTheMachineAnswers()
    {
        using var rig = new Rig(Model, Dialogue);
        rig.Start();
        var first = true;
        string Why() => "\n--- interpreter:\n" + string.Join("\n", rig.Log().Split('\n').Where(l => !l.StartsWith("[C] send") && !l.StartsWith("[C] routed")).TakeLast(30)) + "\n--- replies: " + string.Join(" | ", rig.ReplyText.All()) + "\n--- spoken: " + string.Join(" | ", rig.Spoken) + "\n--- JSI turns: " + string.Join(" || ", rig.JsiTurns) + "\n--- heard: " + rig.HeardText.All().Count + " scenes: " + rig.Scene.All().Count + " ollama: " + string.Join(" || ", rig.Ollama.Prompts) + "\n--- status: " + string.Join("; ", rig.Api.Status(AsmVoiceModules.Vci).Aims.Select(a => a.Aim + " " + a.Status + " " + a.Reason + " " + string.Join("/", a.Reports)));
        async Task<string> Say(string words, int replies)
        {
            var before = rig.ReplyText.All().Count;
            rig.Say(words);
            Assert.True(await Until(() => rig.ReplyText.All().Count >= before + replies, 60000), $"\"{words}\" was not answered." + Why());
            first = false;
            return MpaiJson.FromJson<BasicTextObject>(rig.ReplyText.All()[^1])!.GetText();
        }

        // 1. Two sounds are loaded; the Voice Command Interface asks the application what it holds before it works out a request.
        rig.Load("violin"); rig.Load("drums", 2);
        await Task.Delay(2000);

        // 2. Put the violin: the scene is made, with the violin at the place said.
        var reply = await Say("put the violin at x 2, y 1, z 0", 1);
        Assert.False(string.IsNullOrWhiteSpace(reply), Why());
        Assert.True(await Until(() => rig.Positions().ContainsKey("violin")), "the violin is not in the scene." + Why() + " SCENE " + (rig.Scene.All().LastOrDefault() ?? "none"));
        Assert.Equal([2.0, 1.0, 0.0], rig.Positions()["violin"]);
        

        // 3. Put the drums, then move them.
        await Say("put the drums at x 1, y 2, z 0", 1);
        
        await Say("move the drums to x -1, y 3, z 0", 1);
        Assert.True(await Until(() => rig.Positions().TryGetValue("drums", out var p) && p[0] == -1), "the drums did not move." + Why());
        Assert.Equal([-1.0, 3.0, 0.0], rig.Positions()["drums"]);
        Assert.Equal([2.0, 1.0, 0.0], rig.Positions()["violin"]);
        

        // 4. What is in the scene: the reply names the objects, and where they are.
        reply = await Say("What is in the scene?", 1);
        Assert.Contains("violin", reply); Assert.Contains("drums", reply);
        Assert.Contains("2, 1, 0", reply); Assert.Contains("-1, 3, 0", reply);

        // 5. A sentence with no number: the reply asks for it, and the scene does not change.
        var positionsBefore = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(rig.Positions()))!.ToJsonString();
        var scenesBefore = rig.Scene.All().Count;
        reply = await Say("move the violin a bit to the left", 1);
        Assert.Contains("number", reply, StringComparison.OrdinalIgnoreCase);
        await Task.Delay(1500);
        Assert.Equal(positionsBefore, JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(rig.Positions()))!.ToJsonString());
        Assert.Equal(scenesBefore, rig.Scene.All().Count);

        // 6. A second sound given the name violin is violin 2 (a name taken is not given twice).
        rig.Load("violin", 3);
        await Task.Delay(1500);
        
        reply = await Say("Which sounds do I have?", 1);
        Assert.Contains("violin 2", reply);

        // 7. Undo: the last command - the drums moved - is revoked; they are back where they were.
        await Say("undo that", 1);
        Assert.True(await Until(() => rig.Positions().TryGetValue("drums", out var p) && p[0] == 1), "the drums did not come back." + Why());
        Assert.Equal([1.0, 2.0, 0.0], rig.Positions()["drums"]);

        Assert.False(first);
        Assert.Contains("Hello", rig.Prompts.First());                      // the workflow's own words, shown and said by the device
        Assert.NotEmpty(rig.Spoken);                                        // the machine's replies were spoken
    }
}
