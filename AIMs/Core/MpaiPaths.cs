namespace Mpai.Core;

// Resolves the application root at runtime so the app is portable.
//
// The root is the first ancestor of the running executable that contains the
// parts present in EVERY clone: the "AIMs" folder AND the "AIF/UserAgent" folder. Models/
// and AIF/Access/ (its data) are machine-local (distributed separately) and are NOT
// required to locate the root. A clone unzipped anywhere therefore finds its own
// AIMs/AMDs, AIMs/aim-settings.json and AIF/UserAgent/Assets, whether or not models are present.
public static class MpaiPaths
{
    private static string? _root;
    public static string Root => _root ??= FindRoot();

    // A directory is the application root if it holds both the AIMs and the
    // User-Agent side. These ship in the repository; Models/ need not.
    private static bool IsRoot(string dir) =>
        System.IO.Directory.Exists(System.IO.Path.Combine(dir, "AIMs")) &&
        System.IO.Directory.Exists(System.IO.Path.Combine(dir, "AIF", "UserAgent"));

    private static string FindRoot()
    {
        // Single-file apps extract to a temp dir, so AppContext.BaseDirectory is
        // NOT the install folder; try the real executable location first.
        var starts = new[]
        {
            System.IO.Path.GetDirectoryName(System.Environment.ProcessPath ?? ""),
            System.AppContext.BaseDirectory
        };

        // 1) The repository root: an ancestor holding both AIMs and AIF/UserAgent.
        foreach (var start in starts)
        {
            if (string.IsNullOrEmpty(start)) continue;
            for (var d = new System.IO.DirectoryInfo(start); d != null; d = d.Parent)
                if (IsRoot(d.FullName)) return d.FullName;
        }

        // 2) Looser: an ancestor holding AIMs (in case the AIF/UserAgent layout differs).
        foreach (var start in starts)
        {
            if (string.IsNullOrEmpty(start)) continue;
            for (var d = new System.IO.DirectoryInfo(start); d != null; d = d.Parent)
                if (System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "AIMs")))
                    return d.FullName;
        }

        // 3) Last resort: the executable's own folder. NEVER a hard-coded path.
        return System.IO.Path.GetDirectoryName(System.Environment.ProcessPath ?? "")
               ?? System.AppContext.BaseDirectory;
    }

    // A FILE AN AIM READS (a model, a database, a program): the setting the Controller gives it. The L3 of the AIM declares the file in its
    // AccessSources, the Controller obtains it and gives the AIM the path; there is no folder where a model would be looked for.
    public static string Given(System.Collections.Generic.IReadOnlyDictionary<string, string> settings, string key) =>
        settings.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v
            : throw new System.InvalidOperationException($"The setting '{key}' is missing: the L3 of the AIM declares the file in its AccessSources, and the Controller gives its path.");

    // A SETTING THAT NAMES A PLACE MUST NOT NAME A MACHINE. Models are found under
    // Models\ beside the application, but the programs that read them - piper,
    // whisper-cli - are named by setting and were named absolutely, so a published
    // installation pointed at one person's drive. Resolved here against the root
    // the application already computes from its own location, so a relative
    // setting works wherever the folder is placed, and an absolute one still
    // works for anyone who wants it.
    public static string Resolve(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "" :
        System.IO.Path.IsPathRooted(path) ? path :
        System.IO.Path.Combine(Root, path);
    // THE TEST INPUTS - the pictures and the sounds the tests use, and the demonstrations that need a real recording: Test/Data of the application.
    public static string TestData => System.IO.Path.Combine(Root, "Test", "Data");
    // Recordings of LibriSpeech speakers (1272, 1462, ...), one clip each: Test/Data/Speech/libri_wav.
    public static string Clips => System.IO.Path.Combine(TestData, "Speech", "libri_wav");
    // The Python that has torch (the voice engine, the segmentation of the reference voices): a tool of this machine, kept in Support/Python of the
    // application; MPAI_PYTHON names another; else the python of the PATH.
    public static string Python
    {
        get
        {
            if (System.Environment.GetEnvironmentVariable("MPAI_PYTHON") is { Length: > 0 } p) return p;
            foreach (var rel in new[] { System.IO.Path.Combine("Scripts", "python.exe"), System.IO.Path.Combine("bin", "python") })
            {
                var venv = System.IO.Path.Combine(Root, "Support", "Python", "pyannote-venv", rel);
                if (System.IO.File.Exists(venv)) return venv;
            }
            return "python";
        }
    }
    public static string Gallery       => System.IO.Path.Combine(Root, "TestData", "gallery.json");
    public static string Amds          => System.IO.Path.Combine(Root, "AIMs", "AMDs");
    public static string Settings      => System.IO.Path.Combine(Root, "AIMs", "aim-settings.json");
    public static string Assets        => System.IO.Path.Combine(Root, "AIF", "UserAgent", "Assets");
    // The governed Shared Storage area (AIF Shared Storage backing folder).
    public static string SharedStorage => System.IO.Path.Combine(Root, "AIF", "Access", "SharedStorage");
    // Access, the Controller's static or slowly changing data (AIF V3.0, Storage 6).
    public static string Access        => System.IO.Path.Combine(Root, "AIF", "Access");
}
