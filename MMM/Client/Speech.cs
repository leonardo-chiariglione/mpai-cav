using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mpai.Mmm.Client;

// SPEECH, AS THE MMM CARRIES IT (MMM Phase 4). A User that speaks does not send the microphone's chunks one request each: it cuts what it said - the voice
// activity detection is its client's - and sends it, once, as a Speech Object (a Basic Speech Object, OSD-BSO-V1.5) that has its own time: it starts when
// the speech starts and lasts as long as the speech does. The M-Instance does not need to be asked again for each part of it.
public static class Speech
{
    public const int Rate = 16000;     // Hz, mono, Int16: what the speech recognition takes

    public const string Header = "OSD-BSO-V1.5";

    private static JsonObject Time(string id, long startMs, long endMs) => new()
    {
        ["Header"] = "OSD-STM-V1.5", ["SimpleTimeID"] = id,
        ["SimpleTimeData"] = new JsonArray(new JsonObject { ["FlagsByte"] = 3, ["StartTime"] = startMs, ["EndTime"] = endMs, ["AccuracyMode"] = "single", ["AccuracyPlusMinus"] = 1 })
    };

    // The Speech Qualifier of the data (TFA-SPQ-V1.5): its own time - the same as the Speech Object's -, the content (raw PCM, 16 kHz, 16 bits), what it was captured with
    // (a microphone, one channel) and, if the data is a stream, the stream format it is delivered in - a parameter of the Qualifier.
    private static JsonObject Qualifier(string id, long startMs, long endMs, string? streamFormat) => new()
    {
        ["Header"] = "TFA-SPQ-V1.5", ["MInstanceID"] = "MI1", ["SpeechQualifierID"] = $"{id}-Q",
        ["SpeechQualifierTime"] = Time($"{id}-QT", startMs, endMs),
        ["SubTypes"] = new JsonObject(),
        ["Formats"] = new JsonObject
        {
            ["ContentFormats"] = new JsonObject { ["RawData"] = new JsonObject { ["Header"] = "TFA-PCM-V1.5", ["PCM"] = new JsonObject { ["SamplingFrequency"] = Rate, ["SamplePrecision"] = 16 } } },
            ["TransportFormats"] = streamFormat is null ? new JsonObject() : new JsonObject { ["StreamFormat"] = streamFormat }
        },
        ["Attributes"] = new JsonObject
        {
            ["Source"] = "Real",
            ["Device"] = new JsonObject
            {
                ["DeviceRole"] = "Capture", ["DeviceType"] = "Microphone",
                ["CaptureConfiguration"] = new JsonObject { ["ChannelCount"] = 1, ["SamplingMode"] = "Mono" }
            }
        }
    };

    // A Speech Object of the PCM the User captured, starting at startMs (Unix time, milliseconds) and lasting as long as the PCM does.
    public static JsonObject Make(string id, byte[] pcm16k, long startMs)
    {
        var end = startMs + pcm16k.Length / 2 * 1000L / Rate;
        return new JsonObject
        {
            ["Header"] = Header, ["MInstanceID"] = "MI1", ["BasicSpeechObjectID"] = id,
            ["BasicSpeechObjectTime"] = Time($"{id}-T", startMs, end),
            ["BasicSpeechObjectData"] = new JsonArray(new JsonObject { ["Data"] = Convert.ToBase64String(pcm16k) }),
            ["BasicSpeechDataQualifier"] = Qualifier(id, startMs, end, null)
        };
    }

    // A Speech Object that is a stream: declared when the speech starts, with the time it lasts (it does not have to be redefined until that time is over) and the address its
    // data is delivered from; the data arrives as it is captured. The Qualifier says the stream format: WebSocket. Whoever wants to hear it - the receiver decides, from this User
    // and not from that one - opens that address, so that it understands what it receives.
    public static JsonObject Declare(string id, long startMs, long durationMs, string dataUri) => new()
    {
        ["Header"] = Header, ["MInstanceID"] = "MI1", ["BasicSpeechObjectID"] = id,
        ["BasicSpeechObjectTime"] = Time($"{id}-T", startMs, startMs + durationMs),
        ["BasicSpeechObjectData"] = new JsonArray(new JsonObject { ["DataURI"] = dataUri }),
        ["BasicSpeechDataQualifier"] = Qualifier(id, startMs, startMs + durationMs, "WebSocket")
    };

    // The Speech Object as the M-Instance returns an Item (the Body is the first of its ItemValueOrRef), or as it is.
    private static JsonNode? Body(JsonObject? item) => item?["ItemValueOrRef"]?[0] ?? item;

    public static bool Is(JsonObject? item) => Body(item)?["Header"]?.GetValue<string>() == Header;

    public static byte[] PcmOf(JsonObject? item) =>
        Body(item)?["BasicSpeechObjectData"]?[0]?["Data"]?.GetValue<string>() is { } d ? Convert.FromBase64String(d) : [];

    // How long it lasts, milliseconds: its own time says so.
    public static long DurationMs(JsonObject? item) =>
        Body(item)?["BasicSpeechObjectTime"]?["SimpleTimeData"]?[0] is JsonNode t ? t["EndTime"]!.GetValue<long>() - t["StartTime"]!.GetValue<long>() : 0;

    // The PCM of a 16-bit mono WAV file (the header is skipped to its "data" chunk).
    public static byte[] PcmOfWav(byte[] wav)
    {
        for (var i = 12; i + 8 <= wav.Length;)
        {
            var size = BitConverter.ToInt32(wav, i + 4);
            if (Encoding.ASCII.GetString(wav, i, 4) == "data") return wav[(i + 8)..Math.Min(wav.Length, i + 8 + size)];
            i += 8 + size + (size & 1);
        }
        return [];
    }
}

// What recognises the words of PCM: whisper-server (the repository's Models\Whisper), or whatever a test puts in its place.
public delegate Task<string> Recognizer(byte[] pcm16k);

public sealed class WhisperAsr : IDisposable
{
    private readonly string program, model;
    private readonly int port;
    private readonly HttpClient http;
    private Process? process;
    private readonly SemaphoreSlim one = new(1, 1);          // one utterance at a time: the CPU is not made for more

    public WhisperAsr(string root, int port = 18179)
    {
        program = Path.Combine(root, "Models", "Whisper", "bin", "whisper-server.exe");
        model = Path.Combine(root, "Models", "Whisper", "models", "ggml-small.bin");
        this.port = port;
        http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(120) };
    }

    public bool Available => File.Exists(program) && File.Exists(model);

    public void Start()
    {
        if (!Available || process is not null) return;
        process = Process.Start(new ProcessStartInfo(program, $"-m \"{model}\" --host 127.0.0.1 --port {port} -t 6 -l auto")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(program)!
        });
        process!.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
    }

    // Waits for the model to load (a few seconds) and asks the server the first time.
    public async Task<bool> ReadyAsync(int seconds = 60)
    {
        for (var i = 0; i < seconds * 2; i++)
        {
            try { using var r = await http.GetAsync(""); if (r.IsSuccessStatusCode) return true; } catch { }
            await Task.Delay(500);
        }
        return false;
    }

    public async Task<string> RecognizeAsync(byte[] pcm16k)
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
            using var response = await http.PostAsync("inference", form);
            if (!response.IsSuccessStatusCode) return "";
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;

            // Noise and breath make Whisper write words that nobody said, often in another script. The words are kept only if the language it found is one of those the
            // participants speak (ABV_LANGS, default English, Italian, Portuguese, Spanish, French, German) and its own segments are neither "no speech" nor doubtful.
            var detected = root.TryGetProperty("language", out var l) ? (l.GetString() ?? "") : "";
            if (detected.Length > 0 && !Languages.Contains(detected, StringComparer.OrdinalIgnoreCase) && !Languages.Contains(NameToCode(detected), StringComparer.OrdinalIgnoreCase)) return "";
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
        finally { one.Release(); }
    }

    // The languages the participants speak (ISO codes), from ABV_LANGS; whisper-server may name the language in full.
    public static readonly string[] Languages = (Environment.GetEnvironmentVariable("ABV_LANGS") ?? "en,it,pt,es,fr,de").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NameToCode(string name) => name.ToLowerInvariant() switch
    {
        "english" => "en", "italian" => "it", "portuguese" => "pt", "spanish" => "es", "french" => "fr", "german" => "de", _ => name
    };

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
