using System.Security.Cryptography;
using System.Text.Json;

using AIF.Controller;
using Mpai.Core;

namespace Mpai.Aif.Tests;

// THE DATABASES THE REFERENCE FACES AND VOICES ARE MADE FROM ARE ACCESS DATA (ReferenceDatabases.access.json): each by its public URI and
// its SHA-256, with its terms. The Controller obtains them, checks them, and does not download them again when it already holds them.
[Trait("Group", "Fast")]
[Trait("Blocks", "No")]
public class ReferenceDatabasesTests
{
    private static List<AccessEntry> Declared()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository.Root, "Test", "Mpai.Aif.Tests", "ReferenceDatabases.access.json")));
        return AccessEntry.ReadFrom(doc.RootElement);
    }

    [Fact]
    public void TheDatabasesAreDeclaredWithTheirSourceHashAndTerms()
    {
        var entries = Declared();
        Assert.Equal(["lfw", "librispeech-dev-clean"], entries.Select(e => e.Name));
        Assert.Equal("", AccessEntry.Check(entries));
        Assert.All(entries, e => { Assert.StartsWith("https://", e.Uri); Assert.Equal(64, e.Sha256.Length); Assert.True(e.Terms!.PersonalData); });
    }

    [SkippableFact]
    [Trait("Group", "Tools")]
    public void TheControllerObtainsTheDatabasesAndDoesNotDownloadThemAgain()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("MPAI_FETCH_DATABASES") == "1", "Downloads 180 MB and 338 MB: set MPAI_FETCH_DATABASES=1.");
        var fetcher = new AccessFetcher(new AccessStore(MpaiPaths.Access));
        foreach (var e in Declared())
        {
            Assert.Equal(AccessOutcome.OK, fetcher.Ensure(e, "reference-builder", out _));
            var clock = System.Diagnostics.Stopwatch.StartNew();      // held now: a second call downloads nothing, so it is quick
            Assert.Equal(AccessOutcome.OK, fetcher.Ensure(e, "reference-builder", out _));
            Assert.True(clock.Elapsed.TotalSeconds < 20, $"{e.Name} took {clock.Elapsed.TotalSeconds:0} s the second time");
        }
    }
}
