using System.Text.Json;

using AIF.Controller;
using Mpai.Core;

namespace Mpai.Aif.Tests;

// THE REFERENCE POPULATION IS READ FROM ACCESS: the faces and the voices of known people, already computed, pinned by the
// SHA-256 in the L3 of FIR and SIR; the people ACR enrols are in the gallery (Shared Storage) and are not changed by it.
[Trait("Group", "Fast")]
[Trait("Blocks", "No")]
public class ReferenceFeaturesTests : IDisposable
{
    private readonly string work = Path.Combine(Path.GetTempPath(), "mpai-ref-" + Guid.NewGuid().ToString("N"));
    public ReferenceFeaturesTests() => Directory.CreateDirectory(work);
    public void Dispose() { try { Directory.Delete(work, true); } catch { } }

    private static MemoryStream File2(string kind, int dim, params (string Id, float[] V)[] people)
    {
        var ms = new MemoryStream();
        ReferenceFeatures.Write(ms, new ReferenceFeaturesHeader(kind, "test", dim, "cosine", people.Length), people);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void AReferencePopulationIsMatchedLikeTheEnrolledAndTheEnrolledOneWins()
    {
        var gallery = new SubjectGallery();
        var header = gallery.LoadReference(File2("face", 3, ("ada", [1f, 0f, 0f]), ("bob", [0f, 1f, 0f])));
        Assert.Equal(("face", 3, 2), (header.Kind, header.Dimension, header.Count));
        Assert.Equal(2, gallery.ReferenceCount);
        Assert.Equal(0, gallery.Count);                                                   // nobody is enrolled by this
        Assert.Equal("bob", gallery.IdentifyFace([0.1f, 0.9f, 0f])!.Value.SubjectId);
        Assert.Null(gallery.IdentifyFace([0f, 0f, 1f]));                                  // nobody like it

        gallery.EnrolEmbeddings("ada", face: [0f, 0f, 1f]);                               // enrolled with the same id: that one is used
        Assert.Equal("ada", gallery.IdentifyFace([0f, 0f, 1f])!.Value.SubjectId);
        Assert.Null(gallery.IdentifyFace([1f, 0f, 0f]));
        Assert.Equal(2, gallery.ScoreFace([1f, 0f, 0f]).Count);

        gallery.LoadReference(File2("voice", 2, ("spk1", [1f, 0f])));                     // voices join the faces
        Assert.Equal("spk1", gallery.IdentifyVoice([1f, 0f])!.Value.SubjectId);
        Assert.Equal(3, gallery.ReferenceCount);
    }

    [Fact]
    public void ATruncatedOrInvalidFileIsRefused()
    {
        var ms = File2("face", 3, ("ada", [1f, 0f, 0f]));
        var cut = new MemoryStream(ms.ToArray()[..^4]);
        Assert.ThrowsAny<Exception>(() => new SubjectGallery().LoadReference(cut));
        var wrong = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"Kind\":\"image\",\"Dimension\":3,\"Count\":1}\n"));
        Assert.ThrowsAny<Exception>(() => new SubjectGallery().LoadReference(wrong));
    }

    // WHAT THE L3 OF FIR AND SIR PIN IS WHAT IS THERE: obtained, checked and read through Access as the AIM reads it.
    [Theory]
    [InlineData("1PAF-FIR-V1.6-I01.json", "reference-faces", "face", 5749)]
    [InlineData("1MMC-SIR-V2.5-I01.json", "reference-voices", "voice", 40)]
    public void TheReferenceFeaturesTheL3NamesAreReadThroughAccess(string l3, string name, string kind, int people)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository.Amds, l3)));
        var entry = Assert.Single(AccessEntry.ReadFrom(doc.RootElement), e => e.Name == name);
        Assert.True(entry.Terms!.PersonalData);
        Assert.Equal("", AccessEntry.Check(AccessEntry.ReadFrom(doc.RootElement)));
        if (!File.Exists(new Uri(entry.Uri).LocalPath)) return;                           // a machine without the feature files

        var store = new AccessStore(Path.Combine(work, "access"));
        var fetcher = new AccessFetcher(store);
        Assert.Equal(AccessOutcome.OK, fetcher.Ensure(entry, "aim", out var why));
        Assert.Equal("", why);
        var access = new BoundAccess(store, [entry], fetcher);

        var gallery = new SubjectGallery();
        foreach (var uri in access.SourcesOf(name))
        {
            Assert.Equal(AccessOutcome.OK, access.Open(uri, "", out var stream));
            using (stream) Assert.Equal(kind, gallery.LoadReference(stream).Kind);
        }
        Assert.Equal(people, gallery.ReferenceCount);
    }
}
