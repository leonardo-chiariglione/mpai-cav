using System.Text.Json.Nodes;

using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Mmc.Edp;

namespace Mpai.Aif.Tests;

// WHAT A USER SAYS ABOUT AN AUDIO SCENE, THROUGH THE VOICE COMMAND INTERFACE (the author, 2026/10/03; ported to MMC-VCI 2026/10/26). It was Entity Dialogue Processing that
// understood the requests and made the User Commands for CAE-ASM; the Voice Command Interface, MMC-VCI, does it now: the JSON Interface makes the command from the words and the
// scene, Entity Dialogue Processing says what happened. Here the Module is run by the Controller with the recogniser and the synthesiser faked (the words are given as heard) and
// the language model of Ollama, llama3.2:3b, for the Interface and for the dialogue. For a handful of requests of Test/AsmVoice - a move, a turn, a loudness, a sound added, one
// taken out, the listener, the scene played, and four that cannot become a command - judged: the command that leaves at the Application Command Port is the
// one expected (the whole set of 59 is measured, with the Interface alone, by JsiAsmVoiceTests); a request that gives no command is answered at once; a command that was sent is
// answered when the application answers, and the answer is spoken.
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
public class EdpAsmTests
{
    private const string Model = "llama3.2:3b";

    private static bool Ollama()
    {
        try { using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(3) }; return h.GetStringAsync("http://127.0.0.1:11434/api/tags").Result.Contains("\"" + Model + "\""); }
        catch { return false; }
    }

    private static readonly string[] Requests =
    [
        "move the violin 1 metre to the left", "rotate the drums to 180 degrees", "put the cello at minus 18 LUFS", "add the flute at 2, 1, 0", "take out the drums",
        "I want to listen from x 0, y 4, z 0", "play the scene",
        "move the violin a bit to the left", "make it louder", "rotate the guitar to 90 degrees", "what is the weather like"
    ];

    [SkippableFact]
    [Trait("Duration", "Long")]
    public async Task VoiceCommandsForTheScene()
    {
        Skip.IfNot(Ollama(), $"Ollama is not running with {Model}.");
        var set = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Root, "Test", "AsmVoice", "cases.json")))!;
        var scene = set["scene"]!;
        var cases = set["cases"]!.AsArray().Concat(set["withoutNumbers"]!.AsArray()).ToDictionary(c => (string)c!["request"]!, c => (JsonObject)c!["expected"]!);
        using var llm = new OllamaClient(Model, context: 4096);
        using var rig = new VciRig((s, t, schema) => llm.ChatSchemaAsync(s, t, schema), null);
        Assert.Equal(AIF.Controller.AifError.OK, rig.Api.StartFlow(VciRig.Vci));
        Assert.Equal(AIF.Controller.AifError.OK, rig.Write(VciRig.Ucm, 1, JsiRig.Context(scene).ToJsonString()));

        var result = new Dictionary<string, string>();
        foreach (var request in Requests)
        {
            rig.Say(request);
            Assert.True(rig.Read(VciRig.Bto, 1, 15000).Ok, "not heard: " + request);
            var expected = (JsonObject)cases[request].DeepClone();
            var command = rig.Read(VciRig.Ucm, 1, expected["none"] is not null ? 1500 : 40000);

            if (expected["none"] is not null)
            {
                // No command, and the person is answered at once.
                Assert.False(command.Ok, "a command was sent for: " + request);
                var answered = rig.Read(VciRig.Bto, 2, 40000);
                Assert.True(answered.Ok, "no answer to: " + request);
                Assert.True(rig.Read(VciRig.Bso, 1, 15000).Ok, "the answer was not spoken: " + request);
                result[request] = "no command; answered at once";
                continue;
            }

            Assert.True(command.Ok, "no command for: " + request);
            var aji = JsonNode.Parse(command.Json!)!;
            var (ok, kind, got) = JsiAsmVoiceTests.Judge(expected, JsiRig.Normal(aji), scene);
            Assert.True(ok, $"{request}: {kind} {got?.ToJsonString()}");
            Assert.Empty(new Mpai.Mmc.Jsi.SchemaCheck(JsiRig.UserCommandUri, JsiRig.UserCommandFile, Repository.Schemas).Violations(aji.ToJsonString())!);

            // The command is not said; the application answers; the answer is.
            Assert.False(rig.Read(VciRig.Bto, 2, 500).Ok, "said before the application answered: " + request);
            var response = JsiRig.ResponseTo((string)aji["UserCommandID"]!, "Done", ("MovedObjects", "violin", "Done", null));
            Assert.Equal(AIF.Controller.AifError.OK, rig.Write(VciRig.Ucm, 1, response.ToJsonString()));
            var reply = rig.Read(VciRig.Bto, 2, 40000);
            Assert.True(reply.Ok, "no answer once the application answered: " + request);
            Assert.False(string.IsNullOrWhiteSpace(MpaiJson.FromJson<BasicTextObject>(reply.Json!)!.GetText()));
            Assert.True(rig.Read(VciRig.Bso, 1, 15000).Ok, "the answer was not spoken: " + request);
            result[request] = "the expected command; answered when the application answered";
        }

        Expected.Match("vci-asm-voice.json", result);
    }
}
