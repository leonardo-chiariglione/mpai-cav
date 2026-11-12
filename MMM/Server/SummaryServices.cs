using System.Text;
using AIF.Controller;
using Mpai.Aims.Tts;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Mmc.Edp;

namespace Mpai.Mmm.Server;

// WHAT THE SECRETARY ASKS FOR A SUMMARY (M3283, MC-4 and MC-5): a language model that condenses the minutes and translates the summary (Ollama, as Entity Dialogue Processing
// uses it), and the Text-to-Speech Module that speaks it in the language chosen (Piper, the voices the L3 of MTS declares in Access: Greek among them). Nothing here starts
// until somebody asks for a summary: no model is loaded and no voice is obtained for a meeting in which nobody does.
public sealed class SummaryServices(string root, string model = "llama3.1") : IDisposable
{
    private readonly Lazy<OllamaClient> llm = new(() => new OllamaClient(model, context: 8192));
    private readonly Lazy<PiperTtsAim> tts = new(() => TtsFactory.Create(ModelFiles.SettingsOf(root, "1MMC-TTS-V2.5-I01", new Dictionary<string, string>())));

    private static readonly Dictionary<string, string> Names = new()
    {
        ["en"] = "English", ["el"] = "Greek", ["fr"] = "French", ["de"] = "German", ["es"] = "Spanish", ["pt"] = "Portuguese", ["it"] = "Italian", ["ja"] = "Japanese", ["zh"] = "Chinese"
    };

    // English summary of a piece of the minutes (or of the summaries of pieces).
    // 2026-11-12: asked for "the decisions taken and the actions agreed", at Ollama's default temperature, the model made them up (an approved project, actions for
    // the speaker) where nobody had decided anything. Now it reports what was said, a decision or an action only when stated, and runs at temperature 0.
    public Task<string> SummariseAsync(string minutes) => llm.Value.ChatAsync(
        "You summarise the minutes of a meeting, in English. Each line of the minutes is one thing a person said, with the speaker's name at the start. " +
        "Some lines may be noise that the speech recognition turned into words; leave out a line that makes no sense. " +
        "Write what was actually said, in a few short sentences, naming who said it. Report a decision only if somebody clearly stated it was decided, and an action only if " +
        "somebody clearly agreed to do it, with who is to do it. If there is no clear decision or action, write \"No decisions or actions were recorded.\" " +
        "Never invent topics, projects, decisions, actions or names; do not guess what was meant. Answer with the summary only, as plain text, with no introduction.",
        minutes, temperature: 0);

    public Task<string> TranslateAsync(string english, string language) => llm.Value.ChatAsync(
        $"You translate into {Names.GetValueOrDefault(language, language)}. Answer with the translation only, as plain text. Keep names and numbers as they are.",
        english, temperature: 0);

    // The text spoken in the language, as 16 kHz, 16 bit, mono PCM (what the clients play).
    public async Task<byte[]> SynthesiseAsync(string text, string language)
    {
        var qualifier = new TextQualifier
        {
            TextQualifierID = Guid.NewGuid().ToString(),
            Attributes = new TextAttributes { Language = new Language { LanguageCode = language, LanguageFormat = LanguageFormat.Iso639_1 } }
        };
        var spoken = await tts.Value.ProcessAsync(BasicTextObject.FromText(text, qualifier));
        var wav = spoken.Data;
        if (wav.Length < 44) throw new InvalidOperationException($"no voice spoke the summary in '{language}'");
        var rate = BitConverter.ToInt32(wav, 24);
        var pcm = Mpai.Mmm.Client.Speech.PcmOfWav(wav);
        return rate == Mpai.Mmm.Client.Speech.Rate ? pcm : Resample(pcm, rate, Mpai.Mmm.Client.Speech.Rate);
    }

    // Linear interpolation: enough for speech.
    private static byte[] Resample(byte[] pcm, int from, int to)
    {
        var n = pcm.Length / 2;
        var m = (int)((long)n * to / from);
        var result = new byte[m * 2];
        for (var i = 0; i < m; i++)
        {
            var at = (double)i * from / to; var k = (int)at; var f = at - k;
            double a = BitConverter.ToInt16(pcm, Math.Min(k, n - 1) * 2), b = BitConverter.ToInt16(pcm, Math.Min(k + 1, n - 1) * 2);
            BitConverter.GetBytes((short)Math.Round(a + (b - a) * f)).CopyTo(result, i * 2);
        }
        return result;
    }

    public void Dispose() { if (llm.IsValueCreated) llm.Value.Dispose(); }
}
