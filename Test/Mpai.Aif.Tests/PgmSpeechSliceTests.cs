using System.Diagnostics;
using System.Text.Json.Nodes;

using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Pgm;

namespace Mpai.Aif.Tests;

// The slice with SPEECH: the human says "go and open the doors" (a voice synthesised by the TTS AIM, to have a speech that is known), the User Agent gives it as a Speech Object,
// Context Description recognises it (Whisper, through the ASR AIM) and the rest goes on as with text. Needs the Whisper and Piper models; skipped where they are absent.
[Trait("Group", "Models")]
[Trait("Blocks", "No")]
public class PgmSpeechSliceTests
{
    // The recogniser CXT is given: the ASR AIM of MMC (Whisper) over the Speech Object.
    private sealed class WhisperRecogniser : ISpeechRecogniser
    {
        private readonly Mpai.Aims.Asr.WhisperAsrAim _asr = Mpai.Aims.Asr.AsrFactory.Create(TestModels.Settings("1MMC-ASR-V2.5-I01"));

        public async Task<string> RecogniseAsync(JsonObject speechObject) =>
            (await _asr.ProcessAsync(MpaiJson.FromJson<BasicSpeechObject>(speechObject.ToJsonString())!)).GetText() ?? "";
    }

    [SkippableFact]
    public async Task OpenTheDoorsWithSpeech()
    {
        Skip.IfNot(TestModels.Can("ggml-small.bin"), "the Whisper model is absent.");
        var voice = TestModels.Voice("en_GB-alan-medium");
        BasicSpeechObject spoken;
        try { spoken = await Mpai.Aims.Tts.TtsFactory.Create(voice).ProcessAsync(BasicTextObject.FromText("Go and open the doors.")); }
        catch (Exception e) { Skip.If(true, "the Piper voice is not available: " + e.Message); return; }

        var model = new ScriptedModel();
        var rig = new PgmRig(model, new WhisperRecogniser());
        var speech = (JsonObject)JsonNode.Parse(MpaiJson.ToJson(spoken))!;
        Assert.Equal("valid", PgmRig.Check(speech));
        var clock = Stopwatch.StartNew();
        rig.Write("PGM-HCM-V1.0", PgmRig.HumanCommand());
        rig.Write("OSD-BSO-V1.5", speech);
        await rig.RunAsync();

        var bad = rig.Seen.Where(s => s.Verdict != "valid").Select(s => $"{s.Aim} {s.Direction} {s.DataType}: {s.Verdict}").Distinct().ToList();
        var folder = Path.Combine(Repository.Root, "Test", "Results", "2026-11-05");
        Directory.CreateDirectory(folder);
        File.AppendAllText(Path.Combine(folder, "pgm-slice-speech.txt"),
            $"{DateTime.Now:s}\t{clock.Elapsed.TotalSeconds:F2} s\tASR heard: \"{model.Asked.FirstOrDefault()}\"\t{rig.Seen.Count} data seen\tinvalid: {bad.Count}\n");
        Assert.True(bad.Count == 0, string.Join("\n", bad));
        Assert.Equal("accepted", (string?)rig.At("PGM-HCM-V1.0").Single()["ExecutionResult"]);
        Assert.Equal("MU-Actuate", (string?)rig.At("MMM-ACT-V2.2").Single()["Action"]);
    }
}
