using System.Text.Encodings.Web;
using System.Text.Json;

namespace Mpai.Aif.Tests;

// THE EXPECTED FILES ARE THE RECORD OF THE WORK (M3207 3.2). Each holds what
// today's code actually does, failures included. A test passes when the code does
// exactly that, and fails on any difference in either direction: a new failure is
// a regression; a new success is an improvement, accepted by rerunning with
// MPAI_UPDATE_EXPECTED=1 and committing the updated file with the change that
// caused it.
public static class Expected
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static bool Updating =>
        Environment.GetEnvironmentVariable("MPAI_UPDATE_EXPECTED") == "1";

    // Compares what the code does with Test/Expected/<file>. Keys are subjects
    // (an L3, an App); values say what was found for each.
    public static void Match(string file, IReadOnlyDictionary<string, string> actual)
    {
        var path = Path.Combine(Repository.Root, "Test", "Expected", file);
        var sorted = new SortedDictionary<string, string>(actual.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal);

        if (Updating || !File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(sorted, Json) + Environment.NewLine);
            if (!Updating)
                Assert.Fail($"{file} did not exist and has been written from what the code does now. Review it, commit it, and run again.");
            return;
        }

        var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                       ?? new Dictionary<string, string>();

        var differences = new List<string>();
        foreach (var key in expected.Keys.Union(sorted.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            expected.TryGetValue(key, out var was);
            sorted.TryGetValue(key, out var now);
            if (was == now) continue;
            differences.Add($"  {key}\n    expected: {was ?? "(absent)"}\n    now:      {now ?? "(absent)"}");
        }

        if (differences.Count > 0)
            Assert.Fail(
                $"{file}: {differences.Count} difference(s) from the recorded state. " +
                "A new failure is a regression; a new success is accepted with MPAI_UPDATE_EXPECTED=1.\n" +
                string.Join("\n", differences));
    }
}

// Where the repository is: the first folder above the test's output that holds
// both AIMs/AMDs and schemas.
public static class Repository
{
    public static string Root { get; } = Find();

    private static string Find()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, "AIMs", "AMDs")) && Directory.Exists(Path.Combine(dir, "schemas")))
                return dir;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new InvalidOperationException("Repository root not found above " + AppContext.BaseDirectory);
    }

    public static string Amds => Path.Combine(Root, "AIMs", "AMDs");
    public static string Schemas => Path.Combine(Root, "schemas");
}
