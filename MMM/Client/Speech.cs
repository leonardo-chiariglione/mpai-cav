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

    // A Speech Object of the PCM the User captured, starting at startMs (Unix time, milliseconds) and lasting as long as the PCM does.
    public static JsonObject Make(string id, byte[] pcm16k, long startMs) => new()
    {
        ["Header"] = Header, ["MInstanceID"] = "MI1", ["BasicSpeechObjectID"] = id,
        ["BasicSpeechObjectTime"] = new JsonObject
        {
            ["Header"] = "OSD-STM-V1.5", ["SimpleTimeID"] = $"{id}-T",
            ["SimpleTimeData"] = new JsonArray(new JsonObject
            {
                ["FlagsByte"] = 3, ["StartTime"] = startMs, ["EndTime"] = startMs + pcm16k.Length / 2 * 1000L / Rate, ["AccuracyMode"] = "single", ["AccuracyPlusMinus"] = 1
            })
        },
        ["BasicSpeechObjectData"] = new JsonArray(new JsonObject { ["Data"] = Convert.ToBase64String(pcm16k) })
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
            form.Add(new StringContent("json"), "response_format");
            form.Add(new StringContent("0.0"), "temperature");
            using var response = await http.PostAsync("inference", form);
            if (!response.IsSuccessStatusCode) return "";
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.TryGetProperty("text", out var t) ? (t.GetString() ?? "").Trim() : "";
        }
        finally { one.Release(); }
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
