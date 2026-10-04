using System.Text.RegularExpressions;

namespace Mpai.Aif.Tests;

// THE USER AGENT REACHES NO AIM (MPAI-AIF V3.0, User Agent; the author, 2026/10/04: "the
// infrastructure on which the MPAI building is placed"). A User Agent reaches the real
// world only through the Units of its Physical Layer and a Module only through the
// Controller API: linking AIM code, or the Controller's internals, into it breaks zero
// trust. Judged on every project under UserAgent/: what it references - only other
// User Agent projects, the Data Type classes (AIMs/Core) and the Controller API side
// (AIF/ControllerApi, AIF/PortData) - and that no source file constructs or runs an AIM.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class UserAgentIsolationTests
{
    private static readonly string[] Allowed =
    [
        "UserAgent/",
        "AIMs/Core/Mpai.Core.csproj",
        "AIF/ControllerApi/",
        "AIF/PortData/"
    ];

    private static IEnumerable<string> Files(string pattern) =>
        Directory.EnumerateFiles(Path.Combine(Repository.Root, "UserAgent"), pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static string Relative(string path) =>
        Path.GetRelativePath(Repository.Root, path).Replace('\\', '/');

    [Fact]
    public void TheUserAgentReachesNoAim()
    {
        var result = new Dictionary<string, string>();
        foreach (var project in Files("*.csproj").OrderBy(p => p, StringComparer.Ordinal))
        {
            var refs = Regex.Matches(File.ReadAllText(project), "<ProjectReference Include=\"([^\"]+)\"")
                .Select(m => Relative(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, m.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar)))))
                .ToList();
            var outside = refs.Where(r => !Allowed.Any(a => r.StartsWith(a, StringComparison.Ordinal))).ToList();
            result[Relative(project)] = outside.Count == 0 ? "only the User Agent, Data Types and the Controller API" : "REACHES " + string.Join(", ", outside);
        }

        var running = Files("*.cs")
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"\b(AimPortReader|IAimProcessor|[A-Z][A-Za-z]*AimProcessor)\b"))
            .Select(Relative).OrderBy(f => f, StringComparer.Ordinal).ToList();
        result["source files that construct or run an AIM"] = running.Count == 0 ? "none" : string.Join(", ", running);

        Expected.Match("user-agent-isolation.json", result);
    }
}
