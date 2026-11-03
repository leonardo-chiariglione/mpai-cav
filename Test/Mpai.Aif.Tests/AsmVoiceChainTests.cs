using System.Text.Json.Nodes;

using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Mmc.Edp;

namespace Mpai.Aif.Tests;

// A VOICE COMMAND, END TO END (the author, 2026/10/11: put a Whisper that feeds EDP; ported to MMC-VCI 2026/10/26). The person's voice is a Speech Object; Speech Recognition
// (Whisper, MMC-ASR) makes it text; the Voice Command Interface (MMC-VCI) - its JSON Interface, given the scene the application tells it (a User Command response that lists the
// scene) - makes the command, a User Command request (CAE-UCM); the Editing AIMs of the application execute it, and their responses, merged into one, go back to the Voice Command Interface, which says it. The AIMs do not talk to one another: the User Agent, here the test, gives each what the one
// before gave out, as it does through the Controller. The voice is made by Text-To-Speech, so that the test needs no microphone; the Voice Command Interface is the Module of the
// repository run with the recogniser given the words Whisper heard (the sound has been through Whisper here, outside the Module) and a synthesiser that records.
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
public class AsmVoiceChainTests
{
    private const string Model = "llama3.2:3b";

    private static bool Ollama()
    {
        try { using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(3) }; return h.GetStringAsync("http://127.0.0.1:11434/api/tags").Result.Contains("\"" + Model + "\""); }
        catch { return false; }
    }

    private static Dictionary<string, string> Settings(string aim) => TestModels.Settings(aim);

    // Where the member of the Scene named so is, along x, in the response that lists the scene.
    private static double? XOf(JsonNode? listing, string name) =>
        listing?["UserCommandRS"]?["ListedObjects"]?.AsArray().SelectMany(o => o?["Members"]?.AsArray() ?? [])
            .Where(m => (string?)m?["ObjectID"] == name)
            .Select(m => m?["PoV"]?["Position"]?["CartPosition"]?[0] is { } x ? (double?)x.GetValue<double>() : null).FirstOrDefault();

    [SkippableFact]
    [Trait("Duration", "Long")]
    public async Task WhatThePersonSaysMovesTheObjectInTheScene() => await Run();

    private static async Task Run()
    {
        Skip.IfNot(Ollama(), "Ollama is not running.");
        Skip.IfNot(TestModels.Can("ggml-small.bin"), "The Whisper model cannot be had here.");
        var tts = Settings("1MMC-TTS-V2.5-I01"); var asr = Settings("1MMC-ASR-V2.5-I01");
        Skip.IfNot(File.Exists(tts["PiperExecutable"]) && File.Exists(asr["ModelPath"]) && File.Exists(asr["ExecutablePath"]), "Piper or the Whisper model is absent: the model files are obtained separately.");

        using var rig = new AsmRig();
        // The scene being edited: a violin at x = 1 and drums at x = 3, two metres ahead, built as the User Agent would: the sounds arrive in User Commands, and are placed by one.
        foreach (var name in new[] { "violin", "drums" })
            await rig.Ask(AsmRig.Command(new UserCommandRQ { AcquiredObject = new ManagedObject { BasicAudioObject = AsmRig.Sound(name) } }));
        var build = AsmRig.Command(new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = "stage", Objects = [AsmRig.Put("violin", 1), AsmRig.Put("drums", 3)] } });
        var built = (await rig.Ask(build)).Merged(build.UserCommandID);
        Assert.Equal("Done", built.UserCommandRS!.Outcome);

        async Task<string> Listing()
        {
            var ask = AsmRig.Command(new UserCommandRQ { ListedObjects = new ObjectListing { Kind = "Audio Scene", ObjectID = "stage" } });
            return AsmRig.Json((await rig.Ask(ask)).Merged(ask.UserCommandID));
        }
        var scene = await Listing();
        var violinAt = XOf(JsonNode.Parse(scene), "violin")!.Value;
        Assert.Equal(1, violinAt);

        // 1. The voice: what the person says, as a sound. 2. Whisper hears it.
        const string said = "move the violin 1 metre to the left";
        var synthesizer = Mpai.Aims.Tts.TtsFactory.Create(tts);
        var recogniser = Mpai.Aims.Asr.AsrFactory.Create(asr);
        var voice = await synthesizer.ProcessAsync(BasicTextObject.FromText(said));
        var heard = (await recogniser.ProcessAsync(voice)).GetText();
        Assert.True(Alike(said, heard) >= 0.7, $"Whisper heard \"{heard}\"");

        // 3. The Voice Command Interface, given the scene, makes the command from the words.
        using var llm = new OllamaClient(Model, context: 4096);
        using var vci = new VciRig((s, t, schema) => llm.ChatSchemaAsync(s, t, schema), null);
        Assert.Equal(AIF.Controller.AifError.OK, vci.Api.StartFlow(VciRig.Vci));
        Assert.Equal(AIF.Controller.AifError.OK, vci.Write(VciRig.Ucm, 1, scene));
        vci.Say(heard);
        var command = vci.Read(VciRig.Ucm, 1, 40000);
        Assert.True(command.Ok, "the Voice Command Interface gave no command for \"" + heard + "\"");
        Assert.NotNull(JsonNode.Parse(command.Json!)!["UserCommandRQ"]!["MovedObjects"]);

        // 4. The editing AIMs execute it, and say so; their responses are one, which goes back, and is said.
        var given = JsonNode.Parse(command.Json!)!;
        var id = (string)given["UserCommandID"]!;
        var response = AsmRig.Json((await rig.Ask(command.Json!)).Merged(id));
        var rs = JsonNode.Parse(response)!;
        if ((string?)rs["UserCommandRS"]!["Outcome"] != "Done") Assert.Fail(command.Json + " >>> " + response);
        var after = XOf(JsonNode.Parse(await Listing()), "violin")!.Value;
        Assert.True(after < violinAt, $"the violin moved to the left: it was at {violinAt}, it is at {after}");
        Assert.Equal(0, after, 1);                                                            // 1 metre to the left of x = 1

        Assert.Equal(AIF.Controller.AifError.OK, vci.Write(VciRig.Ucm, 1, response));
        var reply = vci.Read(VciRig.Bto, 2, 40000);
        Assert.True(reply.Ok, "the application's answer was not said");
        Assert.True(vci.Read(VciRig.Bso, 1, 15000).Ok);
    }

    // Letters only, from 0 to 1.
    private static double Alike(string a, string b)
    {
        static string Plain(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        a = Plain(a); b = Plain(b);
        if (a.Length == 0 || b.Length == 0) return 0;
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return 1 - (double)d[a.Length, b.Length] / Math.Max(a.Length, b.Length);
    }
}
