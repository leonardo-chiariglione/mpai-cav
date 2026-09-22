using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace AIF.Store;

// MODELS FROM THE PARTIES THAT PUBLISH THEM. An AIM's settings name each model it
// needs by its place on disk. Beside that setting, two more may say where the model
// comes from and what it must be:
//
//   "ModelPath":        "Models\\Whisper\\models\\ggml-small.bin",
//   "Source:ModelPath": "https://huggingface.co/.../ggml-small.bin",
//   "SHA256:ModelPath": "1BE3A9B2...987B"
//
// The marker goes in FRONT of the setting's name: an AIM may read its own settings
// by prefix - Text-To-Speech takes every "Voice:<language>" as a voice - and a
// marker appended to a name would be read as one of them.
//
// A model already on disk is used as it is - a Service with its Models folder in
// place is unaffected. One that is missing is fetched into the SCI's cache and
// checked against its SHA-256; a file that arrives as something else is deleted and
// said to be, and the AIM that needed it will not be built here.
//
// A setting with no source is left exactly as it was.
public static class ModelSource
{
    public static IReadOnlyDictionary<string, string> Resolve(
        string aimName, IReadOnlyDictionary<string, string> settings,
        string root, string cache, Action<string> say)
    {
        Dictionary<string, string>? resolved = null;

        foreach (var (key, value) in settings)
        {
            if (key.StartsWith("Source:", StringComparison.Ordinal) || key.StartsWith("SHA256:", StringComparison.Ordinal)) continue;
            if (!settings.TryGetValue("Source:" + key, out var source) || string.IsNullOrWhiteSpace(value)) continue;

            var here = Path.IsPathRooted(value) ? value : Path.Combine(root, value);
            if (File.Exists(here)) continue;                      // already on this machine

            settings.TryGetValue("SHA256:" + key, out var expected);
            var fetched = Fetch(source, value, cache, expected, say);
            if (fetched is null) continue;                        // said why; the AIM will not be built here

            resolved ??= new Dictionary<string, string>(settings, StringComparer.Ordinal);
            resolved[key] = fetched;
            say($"  {aimName}: {key} from {source}");
        }
        return resolved ?? settings;
    }

    private static string? Fetch(string source, string named, string cache, string? expected, Action<string> say)
    {
        // Kept under the cache by the same relative path the setting names, so two
        // AIMs naming the same model share one copy.
        var into = Path.Combine(cache, named.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(into)) return into;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(into)!);
            using (var http = new HttpClient { Timeout = TimeSpan.FromHours(1) })
            using (var from = http.GetStreamAsync(source).GetAwaiter().GetResult())
            using (var to = File.Create(into))
                from.CopyTo(to);
        }
        catch (Exception ex) { say($"  model {named}: could not be fetched from {source} ({ex.Message})"); return null; }

        if (string.IsNullOrWhiteSpace(expected)) return into;

        string actual;
        using (var stream = File.OpenRead(into))
            actual = Convert.ToHexString(SHA256.HashData(stream));

        if (string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase)) return into;

        File.Delete(into);
        say($"  model {named}: what arrived from {source} is not the model the settings name " +
            $"(SHA-256 {actual[..16]}..., expected {expected.Trim()[..16]}...); it has been deleted.");
        return null;
    }
}
