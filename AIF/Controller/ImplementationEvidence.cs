using System.Collections.Concurrent;
using System.Security.Cryptography;

using AIF.Store;
using AIF.Trust;

namespace AIF.Controller;

// WHAT AN AIM RUNS, MEASURED AND CHECKED (Phase 13, M3223 3.2). Before an AIM is
// built, the party that builds it - the Controller, or the host of an AIM placed on
// it - measures the binary that implements it and each model its settings name. The
// Controller then checks the measurements: the binary is the one the AIM's L3 names,
// with the fingerprint the Store approved; each model has the SHA-256 its settings
// declare. A model already on disk had been used as it was, unchecked (ModelSource):
// here it is checked too. What does not check refuses the Module.
public static class ImplementationEvidence
{
    // A file measured once for as long as it is unchanged: a model of gigabytes is
    // not hashed again at every Start.
    private static readonly ConcurrentDictionary<(string Path, long Length, DateTime Written), string> measured = new();

    public static string Measure(string file)
    {
        var info = new FileInfo(file);
        return measured.GetOrAdd((info.FullName, info.Length, info.LastWriteTimeUtc), _ => ImplementationFingerprints.Of(file));
    }

    // The measurements of one AIM: its binary, where the provider says which it is,
    // and the models its settings name by "SHA256:<setting>".
    public static List<PtfEvidence.Item> Of(string? implementationFile, IReadOnlyDictionary<string, string> settings)
    {
        var items = new List<PtfEvidence.Item>();
        if (implementationFile is { Length: > 0 } && File.Exists(implementationFile))
            items.Add(new PtfEvidence.Item(PtfEvidence.CodeHash, Path.GetFileNameWithoutExtension(implementationFile), Measure(implementationFile)));
        foreach (var (key, _) in settings.Where(s => s.Key.StartsWith("SHA256:", StringComparison.Ordinal)))
        {
            var setting = key["SHA256:".Length..];
            if (settings.TryGetValue(setting, out var path) && File.Exists(path))
                items.Add(new PtfEvidence.Item(PtfEvidence.ConfigHash, $"{setting}={Path.GetFileName(path)}", Measure(path)));
        }
        return items;
    }

    // What does not check, against the Store's approval and the settings; empty
    // when everything does.
    public static List<string> Check(string aim, IReadOnlyList<PtfEvidence.Item> items, string? binaryName, string? approved,
                                     IReadOnlyDictionary<string, string> settings)
    {
        var problems = new List<string>();
        var code = items.Where(i => i.Type == PtfEvidence.CodeHash).ToList();
        if (binaryName is null) problems.Add($"{aim}: its L3 names no binary");
        else if (approved is null) problems.Add($"{aim}: the Store approved no fingerprint of {binaryName}");
        else if (code.Count == 0) problems.Add($"{aim}: what implements it could not be measured");
        else if (code[0].What != binaryName) problems.Add($"{aim}: implemented by {code[0].What}, its L3 names {binaryName}");
        else if (!string.Equals(code[0].Hash, approved, StringComparison.OrdinalIgnoreCase))
            problems.Add($"{aim}: {binaryName} is not the binary the Store approved (SHA-256 {code[0].Hash[..16]}..., approved {approved[..16]}...)");

        foreach (var (key, declared) in settings.Where(s => s.Key.StartsWith("SHA256:", StringComparison.Ordinal)))
        {
            var setting = key["SHA256:".Length..];
            var model = items.FirstOrDefault(i => i.Type == PtfEvidence.ConfigHash && i.What.StartsWith(setting + "=", StringComparison.Ordinal));
            if (model is null) problems.Add($"{aim}: the model of {setting} could not be measured");
            else if (!string.Equals(model.Hash, declared.Trim(), StringComparison.OrdinalIgnoreCase))
                problems.Add($"{aim}: the model of {setting} is not the one its settings declare");
        }
        return problems;
    }
}
