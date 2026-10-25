// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using System.Text.Json;

using AIF.SharedStorage;

namespace AIF.Controller;

// WHAT AN AIM READS, for code that stands in for the Controller of one AIM: a test that builds an Implementation itself and gives it
// its settings, a tool that does what the Controller does for the AIM. There is no Models folder: what an AIM reads is declared in the
// AccessSources of its L3, and the Controller obtains it, checks its SHA-256, keeps it in Access and gives the Implementation the files,
// under the read rules of each datum. This does the same for the AIM Instance named, as that AIM and no other holder, and gives the
// settings the Implementation would receive. Programs that are Apps (a User Agent) never come here: they use the Controller API.
public static class ModelFiles
{
    private sealed record Declared(string L3, AccessEntry Entry);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, List<Declared>> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static List<Declared> All(string root)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(root, out var known)) return known;
            var found = new List<Declared>();
            var folder = Path.Combine(root, "AIMs", "AMDs");
            if (Directory.Exists(folder))
                foreach (var file in Directory.GetFiles(folder, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(file));
                        foreach (var e in AccessEntry.ReadFrom(doc.RootElement).Where(e => !e.IsPlaceholder))
                            found.Add(new Declared(Path.GetFileNameWithoutExtension(file), e));
                    }
                    catch (JsonException) { }
                }
            return Cache[root] = found;
        }
    }

    private static bool Is(AccessEntry e, string name) =>
        e.Name == name || e.AsFile == name || e.FileName == name || (e.Inside.Length > 0 && Path.GetFileName(e.Inside) == name);

    private static Declared? Find(string root, string name, string? l3) =>
        All(root).FirstOrDefault(d => (l3 is null || d.L3 == l3) && Is(d.Entry, name));

    // Whether a L3 declares it (a test that needs a model asks first, and is skipped when nobody declared it).
    public static bool Declares(string root, string name) => Find(root, name, null) is not null;

    // The file of the entry called name (or giving that file name), obtained if need be; the L3 named, when given, is the one that
    // declares it, and its folder of views is the one the AIM sees (an ONNX model and its external data, a voice and its configuration).
    public static string Of(string root, string name, string? l3 = null)
    {
        var d = Find(root, name, l3) ?? throw new FileNotFoundException($"No L3 of {Path.Combine(root, "AIMs", "AMDs")} declares '{name}' in its AccessSources.");
        var store = new AccessStore(Path.Combine(root, "AIF", "Access"));
        var fetcher = new AccessFetcher(store);
        var outcome = fetcher.Ensure(d.Entry, d.L3, out var why);
        if (outcome != AccessOutcome.OK) throw new IOException($"'{name}' ({d.Entry.Uri}) could not be obtained from Access: {outcome} {why}");
        var access = new BoundAccess(store.As(new StorageHolder(d.L3, d.L3)), [d.Entry], fetcher, d.L3);
        var set = AccessEntry.Overlay(new Dictionary<string, string>(), [d.Entry with { Setting = "path" }], access, d.L3);
        return set.TryGetValue("path", out var path) ? path : throw new IOException($"'{name}' is not in Access.");
    }

    public static bool TryOf(string root, string name, out string path)
    {
        path = "";
        if (!Declares(root, name)) return false;
        try { path = Of(root, name); return true; }
        catch (IOException) { return false; }
    }

    // The settings of an AIM as the Controller gives them to its Implementation: the settings with, for every entry of the L3 that
    // names a Setting, the file in Access (all of them obtained). For a test that builds the Implementation itself.
    public static IReadOnlyDictionary<string, string> SettingsOf(string root, string l3, IReadOnlyDictionary<string, string> settings)
    {
        var entries = All(root).Where(d => d.L3 == l3).Select(d => d.Entry).ToList();
        if (entries.Count == 0) return settings;
        var store = new AccessStore(Path.Combine(root, "AIF", "Access"));
        var fetcher = new AccessFetcher(store);
        foreach (var e in entries.Where(e => !e.IsOptional))
            if (fetcher.Ensure(e, l3, out var why) != AccessOutcome.OK) throw new IOException($"'{e.Name}' ({e.Uri}) could not be obtained from Access: {why}");
        foreach (var e in entries.Where(e => e.IsOptional))
            fetcher.Ensure(e, l3, out _);
        var access = new BoundAccess(store.As(new StorageHolder(l3, l3)), entries, fetcher, l3);
        return AccessEntry.Overlay(settings, entries, access, l3);
    }
}
