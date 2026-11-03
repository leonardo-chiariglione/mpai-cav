using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Mmc.Edp;
using Mpai.Providers;

namespace Mpai.Aif.Tests;

// THE VIRTUAL MEETING SECRETARY (PAF-VMS-V1.6), running continuously as its L3 says:
// the Portable Avatars of two participants written one after the other - one typed,
// one spoken - and the Summary read after each; then one participant speaks to the
// secretary, which answers with its own Portable Avatar and notes what it was asked
// to note. The AIMs are the repository's, except those that need models: speech
// recognition, speech synthesis and the face and body generators are stand-ins,
// and the language model is a stand-in server that answers as Ollama does.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class VmsTests
{
    private const string Vms = "1PAF-VMS-V1.6-I01";
    private const string Answer = "The launch moves to May, as Alice proposed.";
    private const string Note = "The launch moves to May.";

    [Fact]
    public void Meeting()
    {
        using var llm = new FakeOllama(new JsonObject
        {
            ["response"] = Answer, ["emotion"] = "CALMNESS", ["attitude"] = "respectful", ["note"] = Note
        }.ToJsonString());
        using var api = new ControllerApi(Repository.Amds, Path.Combine(Repository.Amds, "no-settings.json"), store => new Provider(store, llm.Url));
        Assert.Equal(AifError.OK, api.StartFlow(Vms));
        try
        {
            // Alice types, happily; nobody speaks to the secretary.
            Assert.Equal(AifError.OK, api.InputWrite(Vms, "PAF-PAV-V1.6", 1, Typed("Alice", "We should move the launch to May.", "HAPPINESS", "happy"), 5000));
            Assert.Equal("Alice (happy): We should move the launch to May.", SummaryOf(api));

            // Bob speaks, with no Personal Status in his Portable Avatar: the Summary goes on all the same (Personal Status is optional to Summary Creation).
            Assert.Equal(AifError.OK, api.InputWrite(Vms, "PAF-PAV-V1.6", 1, Spoken("Bob"), 5000));
            Assert.Equal("Alice (happy): We should move the launch to May.\nBob: I agree with Alice.", SummaryOf(api));

            // Alice speaks to the secretary: it notes.
            Assert.Equal(AifError.OK, api.InputWrite(Vms, "PAF-PAV-V1.6", 1, Typed("Alice", "Secretary, please note that the launch moves to May.", "CALMNESS", "calm"), 5000));
            Assert.Equal("Alice (happy): We should move the launch to May.\nBob: I agree with Alice.\n" +
                         "Alice (calm): Secretary, please note that the launch moves to May.\n" +
                         $"Secretary's note: {Note}", SummaryOf(api));

            // The language model was asked once, with the Summary as it then stood.
            Assert.Single(llm.Prompts);
            Assert.Contains("Bob: I agree with Alice.", llm.Prompts[0]);
        }
        finally { api.StopFlow(Vms); }
    }

    // THE SECRETARY'S PORTABLE AVATAR. Portable Avatar Multiplexing (PAF-PMX-V1.6-I02) runs continuously on its own Ports and updates the avatar with whatever arrives, under the
    // ID it holds: the Model once, then what changes. The Secretary is quiet while nobody speaks to it; when it is addressed, the updates that follow - folded as a receiver
    // folds them - give the avatar with its Model, its voice, its face and body and its emotion.
    [Fact]
    public void SecretarySpeaks()
    {
        using var llm = new FakeOllama(new JsonObject
        {
            ["response"] = Answer, ["emotion"] = "CALMNESS", ["attitude"] = "respectful", ["note"] = Note
        }.ToJsonString());
        using var api = new ControllerApi(Repository.Amds, Path.Combine(Repository.Amds, "no-settings.json"), store => new Provider(store, llm.Url));
        Assert.Equal(AifError.OK, api.StartFlow(Vms));
        try
        {
            Assert.Equal(AifError.OK, api.InputWrite(Vms, "string", 1, "\"Secretary\"", 5000));
            Assert.Equal(AifError.OK, api.InputWrite(Vms, "PAF-PAV-V1.6", 1, Typed("Alice", "We should move the launch to May.", "HAPPINESS", "happy"), 5000));
            Assert.DoesNotContain(Drain(api), u => u["SPOOrSPOID"] is not null);

            Assert.Equal(AifError.OK, api.InputWrite(Vms, "string", 1, "\"Secretary\"", 5000));
            Assert.Equal(AifError.OK, api.InputWrite(Vms, "PAF-PAV-V1.6", 1, Typed("Alice", "Secretary, please note that the launch moves to May.", "CALMNESS", "calm"), 5000));
            var updates = Drain(api);
            Assert.Contains(updates, u => u["SPOOrSPOID"] is not null);                                        // an update in which it speaks
            var pav = Folded(updates);                                                                         // the avatar as a receiver holds it after them
            Assert.Equal("PAF-PAV-V1.6", (string?)pav["Header"]);
            Assert.Contains("Secretary", pav["AvatarOrAvatarID"]!.AsArray().Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null));   // the secretary's own ID
            Assert.Equal("cav-avatar.glb", (string?)pav["AvatarOrAvatarID"]!.AsArray().OfType<JsonObject>().First()["Basic3DModelObjectID"]);
            Assert.Equal("1MMC-TTS-V2.5-I01", (string?)pav["SPOOrSPOID"]![0]!["StandIn"]);
            Assert.Equal("1PAF-GFD-V1.6-I01", (string?)pav["FaceDescriptorsObject"]!["StandIn"]);
            Assert.Equal("1PAF-GBD-V1.6-I01", (string?)pav["BodyDescriptorsObject"]!["StandIn"]);
            Assert.Equal("CALMNESS", (string?)pav["EntityState"]!["Emotion"]!["Category"]);
        }
        finally { api.StopFlow(Vms); }
    }
    // THE APP (Apps/ABV/VMS): its own workflow, run by the User Agent's interpreter over the
    // Module, with the three devices it binds - a Participant that says what two
    // people say, waiting for the minutes before the next speaks; the Minutes; the
    // Secretary, whose Portable Avatar a client would play.
    [Fact]
    public async Task App()
    {
        var workflow = new Mpai.Wdl.WorkflowReader().Read(File.ReadAllText(Path.Combine(Repository.Root, "Apps", "ABV", "VMS", "PAF-VMS.orch")));
        var app = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Root, "Apps", "ABV", "VMS", "app.json")))!;
        Assert.Equal(Vms, (string?)app["Module"]);

        using var llm = new FakeOllama(new JsonObject
        {
            ["response"] = Answer, ["emotion"] = "CALMNESS", ["attitude"] = "respectful", ["note"] = Note
        }.ToJsonString());
        using var api = new ControllerApi(Repository.Amds, Path.Combine(Repository.Amds, "no-settings.json"), store => new Provider(store, llm.Url));

        var happy = MpaiJson.ToJson(new EntityPersonalStatus
        {
            TextPersonalStatus = new TextPersonalStatus { TextEmotion = Emotion.Of(FactorLabel.Of("HAPPINESS", "happy", null, 0.8)) }
        });
        var said = new Queue<Mpai.Rca.MeetingUtterance>(new[]
        {
            new Mpai.Rca.MeetingUtterance("Alice", Text: "We should move the launch to May.", PersonalStatusJson: happy),
            new Mpai.Rca.MeetingUtterance("Bob", SpeechJson: new JsonObject { ["Header"] = "OSD-BSO-V1.5", ["Words"] = "I agree with Alice." }.ToJsonString()),
            new Mpai.Rca.MeetingUtterance("Alice", Text: "Secretary, please note that the launch moves to May.")
        });
        var minutesSeen = new SemaphoreSlim(1);
        var minutes = new List<string>();
        var secretary = new List<string>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var devices = new Mpai.Ua.DeviceRegistry()
            .RegisterDevice("Participant", new Mpai.Rca.ParticipantDevice(async cancel =>
            {
                await minutesSeen.WaitAsync(cancel);                  // each speaks after the minutes of the last
                lock (said) return said.Count > 0 ? said.Dequeue() : null;
            }))
            .RegisterDevice("Minutes", new Mpai.Rca.MinutesDevice(text =>
            {
                lock (minutes) minutes.Add(text);
                minutesSeen.Release();
                return Task.CompletedTask;
            }))
            .RegisterDevice("Secretary", new Mpai.Rca.SecretaryDevice(pav =>
            {
                lock (secretary) secretary.Add(pav);
                if (pav.Contains("SPOOrSPOID")) stop.CancelAfter(500);   // it has spoken: the meeting's business is done (its other updates are quiet)
                return Task.CompletedTask;
            }));

        var interpreter = new Mpai.Ua.WorkflowInterpreter(api.Async(), devices, _ => { });
        try { await interpreter.RunAsync(workflow, stop.Token); } catch (OperationCanceledException) { }

        Assert.Equal(new[]
        {
            "Alice (happy): We should move the launch to May.",
            "Alice (happy): We should move the launch to May.\nBob: I agree with Alice.",
            "Alice (happy): We should move the launch to May.\nBob: I agree with Alice.\nAlice: Secretary, please note that the launch moves to May.\n" +
            $"Secretary's note: {Note}"
        }, minutes);
        var pav = Assert.Single(secretary.Where(p => p.Contains("SPOOrSPOID")));
        Assert.Equal("1MMC-TTS-V2.5-I01", (string?)JsonNode.Parse(pav)!["SPOOrSPOID"]![0]!["StandIn"]);
    }

    // A Portable Avatar codec carries the Portable Avatar as it is, its Header checked.
    [Fact]
    public void PortableAvatarCrossesMas()
    {
        var codecs = Mpai.Aif.PortData.PortDataCodecs.Default();
        var pav = Typed("Alice", "Hello.", "HAPPINESS", "happy");
        Assert.Equal(JsonNode.Parse(pav)!.ToJsonString(), codecs.ToInternal("PAF-PAV-V1.6", codecs.ToWire("PAF-PAV-V1.6", pav)));
    }

    // The updates folded into one avatar, as a receiver does: what an update carries replaces what was held; the Avatar's Model and its ID are one list.
    private static JsonObject Folded(IEnumerable<JsonObject> updates)
    {
        var avatar = new JsonObject();
        JsonNode? model = null; string? id = null;
        foreach (var update in updates)
            foreach (var (name, value) in update)
            {
                if (name != "AvatarOrAvatarID") { avatar[name] = value?.DeepClone(); continue; }
                foreach (var item in value!.AsArray())
                    if (item is JsonObject) model = item.DeepClone(); else if (item is JsonValue v && v.TryGetValue<string>(out var s)) id = s;
            }
        var list = new JsonArray();
        if (model is not null) list.Add(model);
        if (id is not null) list.Add(JsonValue.Create(id));
        avatar["AvatarOrAvatarID"] = list;
        return avatar;
    }

    // The updates of the secretary's Portable Avatar there are to read.
    private static List<JsonObject> Drain(ControllerApi api)
    {
        var updates = new List<JsonObject>();
        for (var read = api.OutputRead(Vms, "PAF-PAV-V1.6", 1, 10000); read.Error == AifError.OK; read = api.OutputRead(Vms, "PAF-PAV-V1.6", 1, 1000))
            updates.Add(JsonNode.Parse(read.Json!)!.AsObject());
        return updates;
    }

    private static string SummaryOf(ControllerApi api)
    {
        var read = api.OutputRead(Vms, "MMC-SUM-V2.5", 1, 10000);
        Assert.Equal(AifError.OK, read.Error);
        return MpaiJson.FromJson<Summary>(read.Json!)!.Text();
    }

    private static string Typed(string who, string text, string category, string adjective) => new JsonObject
    {
        ["Header"] = "PAF-PAV-V1.6",
        ["PortableAvatarID"] = Guid.NewGuid().ToString(),
        ["AvatarOrAvatarID"] = new JsonArray(who),
        ["TextObject"] = JsonNode.Parse(MpaiJson.ToJson(BasicTextObject.FromText(text))),
        ["EntityState"] = EntityStates.OfPersonalStatus(new EntityPersonalStatus
        {
            TextPersonalStatus = new TextPersonalStatus { TextEmotion = Emotion.Of(FactorLabel.Of(category, adjective, null, 0.8)) }
        })
    }.ToJsonString();

    private static string Spoken(string who) => new JsonObject
    {
        ["Header"] = "PAF-PAV-V1.6",
        ["PortableAvatarID"] = Guid.NewGuid().ToString(),
        ["AvatarOrAvatarID"] = new JsonArray(who),
        ["SPOOrSPOID"] = new JsonArray(new JsonObject { ["Header"] = "OSD-BSO-V1.5", ["Words"] = "I agree with Alice." })
    }.ToJsonString();

    // The VMS AIMs as VmsProvider builds them, but for those that need models; the
    // secretary's language model is the stand-in server.
    private sealed class Provider(AIF.Store.AmdStore store, string ollamaUrl) : IAimProvider
    {
        private readonly VmsProvider real = new(store);
        private readonly OllamaClient llm = new("stand-in", ollamaUrl);

        public bool CanCreate(string aimName) => real.CanCreate(aimName);

        public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage)
        {
            Message Reply(Message m, string port, string json) => new() { MessageId = m.MessageId, MessageType = m.MessageType, Ports = new() { [port] = json } };
            string Out(string type) => AimPortReader.Load(store, aimName).Output(type);
            string StandIn(string type) => new JsonObject { ["Header"] = type, ["StandIn"] = aimName }.ToJsonString();
            return aimName switch
            {
                // What the participant said, as the stand-in speech carries it.
                "1MMC-ASR-V2.5-I01" => new StandIn(aimName, m => Reply(m, AimPortReader.Load(store, aimName).Output("OSD-BTO-V1.5", 1),
                    MpaiJson.ToJson(BasicTextObject.FromText((string?)m.Ports.Values.Select(v => JsonNode.Parse(v)).First()!["Words"] ?? "")))),
                "1MMC-TTS-V2.5-I01" => new StandIn(aimName, m => Reply(m, Out("OSD-BSO-V1.5"), StandIn("OSD-BSO-V1.5"))),
                "1PAF-GFD-V1.6-I01" => new StandIn(aimName, m => Reply(m, Out("PAF-FDO-V1.6"), StandIn("PAF-FDO-V1.6"))),
                "1PAF-GBD-V1.6-I01" => new StandIn(aimName, m => Reply(m, Out("PAF-BDO-V1.6"), StandIn("PAF-BDO-V1.6"))),
                "1MMC-EDP-V2.5-I02" => new EdpAimProcessor(aimName, llm, AimPortReader.Load(store, aimName), persona: "the Secretary"),
                _ => real.Create(aimName, settings, storage)
            };
        }
    }

    private sealed class StandIn(string id, Func<Message, Message> run) : IAimProcessor
    {
        public string InstanceId { get; } = id;
        public Task<Message> ProcessAsync(Message message) => Task.FromResult(run(message));
    }

    // Answers /api/chat as Ollama does, always with the same reply; keeps the prompts.
    private sealed class FakeOllama : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly string reply;
        public string Url { get; }
        public List<string> Prompts { get; } = new();

        public FakeOllama(string reply)
        {
            this.reply = reply;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}";
            listener.Prefixes.Add(Url + "/");
            listener.Start();
            _ = Task.Run(Serve);
        }

        private async Task Serve()
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); } catch { return; }
                using var reader = new StreamReader(context.Request.InputStream);
                var request = JsonNode.Parse(await reader.ReadToEndAsync())!;
                lock (Prompts) Prompts.Add((string?)request["messages"]!.AsArray().Last()!["content"] ?? "");
                var body = Encoding.UTF8.GetBytes(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = reply } }.ToJsonString());
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
        }

        public void Dispose() { listener.Stop(); listener.Close(); }
    }
}









