using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Mpai.Abv.Server;

// THE SPEECH RECOGNITION OF THE MINUTES: whisper-server (the repository's Models\Whisper), started once with its model loaded, asked for each utterance of a participant.
// The language is left to the model to detect: the participants may speak several.
public sealed class Whisper : IDisposable
{
    private readonly string program, model;
    private readonly int port;
    private readonly HttpClient http;
    private Process? process;
    private readonly SemaphoreSlim one = new(1, 1);          // one utterance at a time: the CPU is not made for more

    public Whisper(string root, int port = 18178)
    {
        program = Path.Combine(root, "Models", "Whisper", "bin", "whisper-server.exe");
        model = Path.Combine(root, "Models", "Whisper", "models", "ggml-small.bin");
        this.port = port;
        http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(60) };
    }

    public bool Available => File.Exists(program) && File.Exists(model);

    // The languages the participants speak (ISO codes), from ABV_LANGS.
    public static readonly string[] Languages = (Environment.GetEnvironmentVariable("ABV_LANGS") ?? "en,it,pt,es,fr,de").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // whisper-server may name the language in full.
    private static string NameToCode(string name) => name.ToLowerInvariant() switch
    {
        "english" => "en", "italian" => "it", "portuguese" => "pt", "spanish" => "es", "french" => "fr", "german" => "de", _ => name
    };

    public void Start()
    {
        if (!Available) { Console.WriteLine("[ABV] Whisper not found: no minutes from speech."); return; }
        process = Process.Start(new ProcessStartInfo(program, $"-m \"{model}\" --host 127.0.0.1 --port {port} -t 6 -l auto")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(program)!
        });
        process!.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        Console.WriteLine($"[ABV] Whisper started on port {port} (model loads in a few seconds).");
    }

    // The words of 16 kHz mono Int16 PCM, or "" where there are none (or the model is not there).
    public async Task<string> TranscribeAsync(byte[] pcm16k, string? language = null)
    {
        if (!Available || process is null) return "";
        await one.WaitAsync();
        try
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(Wav(pcm16k)); file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, "file", "speech.wav");
            form.Add(new StringContent("verbose_json"), "response_format");
            form.Add(new StringContent("0.0"), "temperature");
            if (!string.IsNullOrEmpty(language) && language != "auto") form.Add(new StringContent(language), "language");      // the participant said which language it speaks
            using var response = await http.PostAsync("inference", form);
            if (!response.IsSuccessStatusCode) return "";
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;

            // Noise and breath make Whisper write words that nobody said, often in another script. The words are kept only if the language it found is one of those the participants
            // speak (ABV_LANGS, default English, Italian, Portuguese, Spanish, French, German) and its own segments are neither "no speech" nor doubtful.
            var detected = root.TryGetProperty("language", out var l) ? (l.GetString() ?? "") : "";
            var language_ = detected;
            if (string.IsNullOrEmpty(language) && language_.Length > 0 && !Languages.Contains(language_, StringComparer.OrdinalIgnoreCase) && !Languages.Contains(NameToCode(language_), StringComparer.OrdinalIgnoreCase)) return "";
            var kept = new StringBuilder();
            if (root.TryGetProperty("segments", out var segments) && segments.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in segments.EnumerateArray())
                {
                    var noSpeech = s.TryGetProperty("no_speech_prob", out var n) && n.TryGetDouble(out var nd) ? nd : 0;
                    var logprob = s.TryGetProperty("avg_logprob", out var a) && a.TryGetDouble(out var ad) ? ad : 0;
                    if (noSpeech > 0.55 || logprob < -1.1) continue;
                    kept.Append(' ').Append(s.TryGetProperty("text", out var t) ? t.GetString() : "");
                }
            }
            else if (root.TryGetProperty("text", out var text)) kept.Append(text.GetString());
            return Clean(kept.ToString());
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { return ""; }
        finally { one.Release(); }
    }

    // Whisper writes captions for what is not speech ("[BLANK_AUDIO]", "(music)"): not words.
    private static string Clean(string text)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(text, @"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*", " ");
        return System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Trim();
    }

    private static byte[] Wav(byte[] pcm)
    {
        using var m = new MemoryStream();
        using var w = new BinaryWriter(m, Encoding.ASCII, true);
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + pcm.Length); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(16000); w.Write(16000 * 2); w.Write((short)2); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(pcm.Length); w.Write(pcm);
        return m.ToArray();
    }

    public void Dispose() { try { if (process is { HasExited: false }) process.Kill(true); } catch { } }
}
