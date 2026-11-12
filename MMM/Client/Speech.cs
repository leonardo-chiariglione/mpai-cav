using System.Diagnostics;
using System.Net.WebSockets;
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
    private static JsonObject Qualifier(string id, string? streamFormat) => new()
    {
        ["Header"] = "TFA-SPQ-V1.5", ["MInstanceID"] = "MI1", ["SpeechQualifierID"] = $"{id}-Q",
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
            ["BasicSpeechDataQualifier"] = Qualifier(id, null)
        };
    }

    // The Speech Qualifier of speech that is a stream, delivered over a WebSocket. It says what kind of data this is; how long the speech lasts is not its business but the Speech
    // Object's, and a stream has no duration until it is over.
    public static JsonObject StreamQualifier(string id) => Qualifier(id, "WebSocket");

    // A USER'S SPEECH, AS THE STANDARD HAS IT (the Verification Use Cases of MMM-TEC V2.2: a human's captured Data becomes an Item and is placed in the M-Instance). The human's Device
    // UM-Captures the speech at the human's U-Location, With the Speech Qualifier; the Import Service has the Data Identified as an Item (a Speech Object, its Identifier the one the Data was
    // named with); the User MM-Adds it at its seat; the data follows, as it is captured, from the client to the M-Instance (a WebSocket). Returns the Result of the first Process Action that
    // was refused, or that of the MM-Add; with the data complete in the Speech Object when it returns.
    public static async Task<MmmClient.Result> CaptureAsync(MmmClient who, string id, string uLocation, string seat, byte[] pcm16k)
    {
        var q = StreamQualifier(id);
        var captured = await who.PerformAsync("UM-Capture", new Complements { Nil = id, At = uLocation, With = [q] });
        if (captured.Http != 201) return captured;
        var identified = await who.PerformAsync("Identify", new Complements { Nil = id, With = [q.DeepClone()] });
        if (identified.Http != 201) return identified;
        var added = await who.PerformAsync("MM-Add", new Complements { Nil = id, At = seat });
        if (added.Http != 200) return added;
        var origin = who.Server!;
        using var feed = new ClientWebSocket();
        await feed.ConnectAsync(new Uri($"ws://{origin.Authority}/ws/v2.2/speech/{id}?token={Uri.EscapeDataString(who.Token!)}"), CancellationToken.None);
        for (var i = 0; i < pcm16k.Length; i += 3200)
            await feed.SendAsync(pcm16k.AsMemory(i, Math.Min(3200, pcm16k.Length - i)), WebSocketMessageType.Binary, true, CancellationToken.None);
        await feed.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        for (var i = 0; i < 100; i++)                                                                  // the M-Instance closes the stream: the data is then the Speech Object's
        {
            if (PcmOf((await who.ReadAsync(id)).Item).Length > 0) break;
            await Task.Delay(30);
        }
        return added;
    }

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

// What recognises the words of PCM: whisper-server (from Access: the L3 of ASR declares it), or whatever a test puts in its place.
public delegate Task<string> Recognizer(byte[] pcm16k);

// What condenses a piece of the minutes into a summary (English), what translates it, and what speaks it (16 kHz, 16 bits, mono): the Processes the Secretary asks for a summary.
public delegate Task<string> Summariser(string minutes);
public delegate Task<string> Translator(string english, string language);
public delegate Task<byte[]> Synthesiser(string text, string language);

public sealed class WhisperAsr : IDisposable
{
    private readonly string program, model;
    private readonly int port;
    private readonly HttpClient http;
    private Process? process;
    private readonly SemaphoreSlim one = new(1, 1);          // one utterance at a time: the CPU is not made for more

    public WhisperAsr(string root, int port = 18179)
    {
        // the program and the model are those the L3 of Automatic Speech Recognition declares (asr-server, asr-model): Access gives their files
        program = AIF.Controller.ModelFiles.TryOf(root, "asr-server", out var server) ? server : "";
        model = AIF.Controller.ModelFiles.TryOf(root, "asr-model", out var ggml) ? ggml : "";
        this.port = port;
        http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(120) };
    }

    public bool Available => program.Length > 0 && model.Length > 0 && File.Exists(program) && File.Exists(model);

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
        KillWithMe.Add(process);
    }

    // WHISPER ENDS WITH ITS SERVER (2026-11-12): a server whose window was closed left its whisper-server running - three of them after one meeting, one taking 6 cores, and the next
    // server's requests went to it, seconds behind. The process is put in a Windows Job that ends it when this process ends, however it ends (Dispose is not called when a window is closed).
    private static class KillWithMe
    {
        private static IntPtr job;
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, int length);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
            public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        public static void Add(Process p)
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                lock (typeof(KillWithMe))
                {
                    if (job == IntPtr.Zero)
                    {
                        var j = CreateJobObject(IntPtr.Zero, null);
                        var info = new ExtendedLimits { LimitFlags = 0x2000 };                // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: the handle closes when this process ends
                        if (j == IntPtr.Zero || !SetInformationJobObject(j, 9, ref info, System.Runtime.InteropServices.Marshal.SizeOf<ExtendedLimits>())) return;      // 9: extended limits
                        job = j;                                                              // kept open for the life of this process, on purpose
                    }
                    AssignProcessToJobObject(job, p.Handle);
                }
            }
            catch { }
        }
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
            async Task<string?> Ask(string? language)
            {
                using var form = new MultipartFormDataContent();
                var file = new ByteArrayContent(Wav(pcm16k)); file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                form.Add(file, "file", "speech.wav");
                form.Add(new StringContent("verbose_json"), "response_format");
                form.Add(new StringContent("0.0"), "temperature");
                if (language is not null) form.Add(new StringContent(language), "language");
                using var response = await http.PostAsync("inference", form);
                if (!response.IsSuccessStatusCode) return null;
                var said = await response.Content.ReadAsStringAsync();
                Keep(pcm16k, said);
                return said;
            }
            var answer = await Ask(null);
            if (answer is null) return "";
            var json = JsonDocument.Parse(answer);
            var root = json.RootElement;

            // Noise and breath make Whisper write words that nobody said, often in another script; and on a short piece it often takes the language for another (English for
            // Latin, Korean or Nynorsk: seen 2026-11-12). Where the language it found is not one the participants speak (ABV_LANGS, default English, Italian, Portuguese, Spanish,
            // French, German), it is asked again with the first of those set: speech is then written in it, and noise becomes a caption ("[BLANK_AUDIO]", "(laughs)"), which is dropped.
            // The segments kept are those that are neither "no speech" nor doubtful.
            var detected = root.TryGetProperty("language", out var l) ? (l.GetString() ?? "") : "";
            var again = false;
            if (detected.Length > 0 && !Languages.Contains(detected, StringComparer.OrdinalIgnoreCase) && !Languages.Contains(NameToCode(detected), StringComparer.OrdinalIgnoreCase))
            {
                json.Dispose();
                answer = await Ask(Languages.FirstOrDefault() ?? "en"); again = true;
                if (answer is null) return "";
                json = JsonDocument.Parse(answer); root = json.RootElement;
            }
            using var _ = json;
            var kept = new StringBuilder();
            if (root.TryGetProperty("segments", out var segments) && segments.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in segments.EnumerateArray())
                {
                    var noSpeech = s.TryGetProperty("no_speech_prob", out var n) && n.TryGetDouble(out var nd) ? nd : 0;
                    var logprob = s.TryGetProperty("avg_logprob", out var a) && a.TryGetDouble(out var ad) ? ad : 0;
                    // Asked again with the language set, Whisper writes real words right but often rates them "no speech" ("Stop sharing my screen.": 0.73, with a log
                    // probability of -0.19, 2026-11-12): there a segment it is sure of (log probability -0.4 or more) is kept up to 0.8. Silence stays out ([BLANK_AUDIO] at 0.82).
                    var noSpeechLimit = again && logprob >= -0.4 ? 0.8 : 0.55;
                    if (noSpeech > noSpeechLimit || logprob < -1.1) continue;
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

    // FOR FINDING WHY SPEECH IS NOT RECOGNISED (2026-11-12): each piece sent to Whisper is kept, as a .wav with Whisper's answer beside it (.json), in %TEMP%\mmm-asr - the last 100.
    // They are the participants' own words: the folder is on this PC only and is emptied as it goes.
    private static readonly string KeepDir = Path.Combine(Path.GetTempPath(), "mmm-asr");
    private static void Keep(byte[] pcm16k, string answer)
    {
        try
        {
            Directory.CreateDirectory(KeepDir);
            var name = Path.Combine(KeepDir, DateTime.Now.ToString("HHmmss-fff"));
            File.WriteAllBytes(name + ".wav", Wav(pcm16k)); File.WriteAllText(name + ".json", answer);
            foreach (var old in new DirectoryInfo(KeepDir).GetFiles().OrderByDescending(f => f.Name).Skip(200)) old.Delete();
        }
        catch { }
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
