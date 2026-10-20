using System.Text.Json.Nodes;

using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Cae.Asm;
using Mpai.Core;
using Mpai.Providers;

namespace Mpai.Aif.Tests;

// THE VOICE OF AUDIO SCENE MANAGEMENT, THROUGH THE CONTROLLER (the author, 2026/10/12: the
// test must not chain the AIMs by hand). The User Agent - here the test - knows Data Types and
// Port Numbers and nothing else: it gives the Module the person's voice at Basic Speech Object
// 2 (and the Scene being edited at Basic Audio Scene 2), asks for the User Command at User
// Command 6, and gives that command back at the Port it addresses, User Command 2; the
// Controller alone decides which AIM does what. ASR, EDP and TTS are AIMs of the Module that
// the Controller starts, not objects the test holds. The voice is made by the Text to Speech
// that the Module itself has, so that the test needs no microphone.
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
public class AsmVoiceThroughControllerTests
{
    private const string Asm = AsmProvider.Module;

    private static ControllerApi Controller()
    {
        var store = new AIF.Store.AmdStore(Repository.Amds); store.Scan();
        return new ControllerApi(Repository.Amds, Path.Combine(Repository.Root, "AIMs", "aim-settings.json"),
            s => new CompositeProvider(new AsmProvider(Repository.Amds), new MadProvider(store), new MatProvider(s)));
    }

    private static bool Ollama()
    {
        try { using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(3) }; return h.GetAsync("http://127.0.0.1:11434/api/tags").Result.IsSuccessStatusCode; }
        catch { return false; }
    }

    private static SpatialAttitude At(double x, double y) => new()
    {
        ObjectSpatialAttitudeID = Guid.NewGuid().ToString(),
        Position = new Position { PositionID = Guid.NewGuid().ToString(), CartPosition = [x, y, 0] },
        Orientation = new Orientation { OrientationID = Guid.NewGuid().ToString(), EulerAngles = [0, 0, 180] }
    };

    private static BasicAudioObject Sound(string name) => new()
    {
        BasicAudioObjectID = name, BasicAudioObjectData = [new InlineAudioData(Convert.ToBase64String(new byte[64]))], DescrMetadata = name
    };

    private static string Command(UserCommandData data) => MpaiJson.ToJson(new UserCommand
    {
        UserCommandID = Guid.NewGuid().ToString(), UserCommandTime = SimpleTime.At(DateTimeOffset.UtcNow), UserCommandData = data
    });

    private static double? XOf(JsonNode? scene, string name) =>
        scene?["BasicAudioSceneDescriptorsData"]?.AsArray()
            .Where(e => (string?)e?["AObjectIDOrAObject"]?[0]?["DescrMetadata"] == name)
            .Select(e => e?["AudioObjectSpaceTime"]?["SpatialAttitude1"]?["Position"]?["CartPosition"]?[0] is { } x ? (double?)x.GetValue<double>() : null)
            .FirstOrDefault();

    // SUSPENDED (the author, 2026-10-17): the ASM Module no longer has the vocal interface (ASR, EDP, TTS); it comes back as an extension of its own.
    // The Controller starts the Module with the voice AIMs in it: the Module is the L2, all of it.
    [SkippableFact(Skip = "The vocal interface is not part of the ASM Module now; it returns as an extension.")]
    public void TheControllerStartsTheModuleWithItsVoice()
    {
        var work = Path.Combine(Path.GetTempPath(), "asm-voice-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        using var api = Controller();
        try
        {
            Assert.Equal(AifError.OK, api.StartFlow(Asm));
            Assert.Equal(AifError.OK, api.SharedStorageInit(Asm, work));
            var status = api.Status(Asm);
            Assert.True(status.Ok, "no status: " + status.Error);
            var names = status.Aims.Select(a => a.Aim).ToList();
            foreach (var voice in new[] { "1MMC-ASR-V2.5-I01", "1MMC-EDP-V2.5-I01", "1MMC-TTS-V2.5-I01" })
                Assert.Contains(names, n => n == voice);
        }
        finally { api.StopFlow(Asm); try { Directory.Delete(work, true); } catch { } }
    }

    [SkippableFact(Skip = "The vocal interface is not part of the ASM Module now; it returns as an extension.")]
    [Trait("Duration", "Long")]
    public void WhatThePersonSaysMovesTheObjectThroughTheController()
    {
        Skip.IfNot(Ollama(), "Ollama is not running.");
        var work = Path.Combine(Path.GetTempPath(), "asm-voice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        using var api = Controller();
        try
        {
            Assert.Equal(AifError.OK, api.StartFlow(Asm));
            api.SharedStorageInit(Asm, work);

            // The scene being edited, built through the Module: a violin at x = 1, drums at x = 3.
            var ids = new List<string>();
            foreach (var name in new[] { "violin", "drums" })
            {
                Assert.Equal(AifError.OK, api.InputWrite(Asm, "OSD-BAO-V1.5", 1, MpaiJson.ToJson(Sound(name))));
                var opened = api.OutputRead(Asm, "OSD-BAO-V1.5", 1, 60_000);
                Assert.Equal(AifError.OK, opened.Error);
                ids.Add((string)JsonNode.Parse(opened.Json!)!["BasicAudioObjectID"]!);
            }
            api.InputWrite(Asm, "CAE-UCM-V1.0", 2, Command(new UserCommandData
            {
                AddedObjects = new ObjectPlacements { Objects = [ new ObjectPlacement { ObjectID = new ManagedObject { ObjectID = ids[0] }, SpatialAttitude = At(1, 2) },
                                                                  new ObjectPlacement { ObjectID = new ManagedObject { ObjectID = ids[1] }, SpatialAttitude = At(3, 2) } ] }
            }));
            var scene = api.OutputRead(Asm, "OSD-BAS-V1.5", 1, 60_000);
            Assert.Equal(AifError.OK, scene.Error);
            var violinAt = XOf(JsonNode.Parse(scene.Json!), "violin")!.Value;

            // The person's voice, a Speech Object, at Basic Speech Object 2, with the Scene at Basic Audio Scene 2.
            var tts = Mpai.Aims.Tts.TtsFactory.Create(VoiceSettings("1MMC-TTS-V2.5-I01"));
            var voice = tts.ProcessAsync(BasicTextObject.FromText("move the violin to the left")).GetAwaiter().GetResult();
            var voiceScene = api.InputWrite(Asm, "OSD-BAS-V1.5", 2, scene.Json!);
            Assert.True(voiceScene == AifError.OK, voiceScene + "; " + string.Join(" | ", api.Status(Asm).Aims.Select(a => $"{a.Aim} {a.Status} {a.Reason} [{string.Join(",", a.Reports)}]")) + " LastRefusal: " + api.Controller.LastRefusal);
            Assert.Equal(AifError.OK, api.InputWrite(Asm, "OSD-BSO-V1.5", 2, MpaiJson.ToJson(voice)));

            // The User Command that comes out is given back where the User Agent addresses it.
            var understood = api.OutputRead(Asm, "CAE-UCM-V1.0", 6, 180_000);
            Assert.True(understood.Error == AifError.OK, "no User Command: " + understood.Error);
            Assert.NotNull(JsonNode.Parse(understood.Json!)!["UserCommandData"]!["MovedObjects"]);
            Assert.Equal(AifError.OK, api.InputWrite(Asm, "CAE-UCM-V1.0", 2, understood.Json!));

            var report = api.OutputRead(Asm, "CAE-UCM-V1.0", 2, 60_000);
            Assert.Equal("Done", MpaiJson.FromJson<UserCommand>(report.Json!).UserCommandReport!.Outcome);
            var edited = api.OutputRead(Asm, "OSD-BAS-V1.5", 1, 60_000);
            Assert.True(XOf(JsonNode.Parse(edited.Json!), "violin")!.Value < violinAt, "the violin moved to the left");
        }
        finally { api.StopFlow(Asm); try { Directory.Delete(work, true); } catch { } }
    }

    private static Dictionary<string, string> VoiceSettings(string aim)
    {
        var all = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Root, "AIMs", "aim-settings.json")))!;
        return all[aim]!.AsObject().ToDictionary(p => p.Key, p =>
        {
            var v = (string)p.Value!;
            return File.Exists(Path.Combine(Repository.Root, v)) ? Path.Combine(Repository.Root, v) : v;
        });
    }
}
