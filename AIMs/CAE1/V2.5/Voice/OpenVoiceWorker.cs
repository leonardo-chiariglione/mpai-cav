using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace Mpai.Cae.Usc.Voice;

// The voice engine as a process: openvoice_worker.py (OpenVoice V2, MIT) in a Python that has torch. Settings of the AIM Instance: "OpenVoicePython" (default the venv of D:\AI\pyannote-venv),
// "OpenVoiceHome" (the folder with the source and the converter checkpoint, default D:\AI\openvoice), "OpenVoiceScript" (default the worker next to this assembly). The model is loaded at every
// call (some seconds on a CPU): it is the reference Implementation, not a service.
public sealed class OpenVoiceWorker(IReadOnlyDictionary<string, string>? settings = null)
{
    private readonly string _python = Setting(settings, "OpenVoicePython", @"D:\AI\pyannote-venv\Scripts\python.exe");
    private readonly string _home = Setting(settings, "OpenVoiceHome", @"D:\AI\openvoice");
    private readonly string _script = Setting(settings, "OpenVoiceScript", Path.Combine(Path.GetDirectoryName(typeof(OpenVoiceWorker).Assembly.Location) ?? AppContext.BaseDirectory, "openvoice_worker.py"));

    private static string Setting(IReadOnlyDictionary<string, string>? s, string key, string fallback) => s is not null && s.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    public const int EmbeddingLength = 256;

    // The embedding of the voice in the audio: the mean over the files.
    public float[] Extract(IReadOnlyList<Audio> audio)
    {
        using var work = new Work();
        var files = audio.Select((a, i) => work.Write($"in{i}.wav", a.ToWav())).ToList();
        var output = Path.Combine(work.Path, "se.json");
        Run(["extract", "--out", output, .. files]);
        return ReadEmbedding(File.ReadAllText(output));
    }

    // Each piece of audio in the voice of the embedding.
    public IReadOnlyList<Audio> Clone(float[] embedding, IReadOnlyList<Audio> audio)
    {
        using var work = new Work();
        var target = Path.Combine(work.Path, "tgt.json"); File.WriteAllText(target, new JsonObject { ["se"] = new JsonArray(embedding.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()) }.ToJsonString());
        var pairs = audio.Select((a, i) => (In: work.Write($"in{i}.wav", a.ToWav()), Out: Path.Combine(work.Path, $"out{i}.wav"))).ToList();
        Run(["clone", "--tgt", target, .. pairs.SelectMany(p => new[] { p.In, p.Out })]);
        return pairs.Select(p => Audio.FromWav(File.ReadAllBytes(p.Out))).ToList();
    }

    public static float[] ReadEmbedding(string json) =>
        (JsonNode.Parse(json)?["se"] as JsonArray ?? throw new FormatException("The embedding has no 'se'.")).Select(v => (float)v!.GetValue<double>()).ToArray();

    private void Run(IEnumerable<string> arguments)
    {
        if (!File.Exists(_python)) throw new InvalidOperationException($"The Python of the voice engine is not at {_python} (setting OpenVoicePython).");
        if (!File.Exists(_script)) throw new InvalidOperationException($"The voice engine's worker is not at {_script} (setting OpenVoiceScript).");
        var info = new ProcessStartInfo(_python) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-W", "ignore", _script }.Concat(arguments)) info.ArgumentList.Add(a);
        info.Environment["OPENVOICE_HOME"] = _home;
        using var process = Process.Start(info) ?? throw new InvalidOperationException("The voice engine did not start.");
        var error = process.StandardError.ReadToEndAsync(); process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("The voice engine failed: " + string.Join(" ", error.Result.Split('\n').TakeLast(3)).Trim());
    }

    private sealed class Work : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openvoice-" + Guid.NewGuid().ToString("N"));
        public Work() => Directory.CreateDirectory(Path);
        public string Write(string name, byte[] bytes) { var p = System.IO.Path.Combine(Path, name); File.WriteAllBytes(p, bytes); return p; }
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}