using AIF.Controller;

namespace Mpai.Aif.Tests;

// Access keeps what MPAI-AIF V3.0 Storage 6 asks of it: a Source never goes back to an earlier state (not past a deletion either), the Version only rises (also when the Source is made
// again), and the file given to a reader (Access_Path) is read, not written.
public class AccessLedgerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mpai-access-ledger-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
        catch { }
    }

    private AccessStore Store() => new(root);

    [Fact]
    public void ADeletedItemDoesNotComeBackWithItsOldContent()
    {
        var store = Store();
        store.Create("S");
        Assert.Equal(AccessOutcome.OK, store.Put("S", "k", [1, 2, 3]));
        Assert.Equal(AccessOutcome.OK, store.Put("S", "k", [4, 5, 6]));
        Assert.Equal(AccessOutcome.OK, store.Delete("S", "k"));
        Assert.Equal(AccessOutcome.Refused, store.Put("S", "k", [1, 2, 3]));
        Assert.Equal(AccessOutcome.Refused, store.Put("S", "k", [4, 5, 6]));
        Assert.Equal(AccessOutcome.OK, store.Put("S", "k", [7]));
    }

    [Fact]
    public void TheVersionOfASourceMadeAgainGoesOnFromWhereItWas()
    {
        var store = Store();
        store.Create("S");
        store.Put("S", "k", [1]);
        store.Put("S", "j", [2]);
        var before = store.Version("S");
        Assert.True(before > 0);
        Assert.Equal(AccessOutcome.OK, store.Remove("S"));
        store.Create("S");
        Assert.True(store.Version("S") >= before);
        Assert.Equal(AccessOutcome.Refused, store.Put("S", "k", [1]));      // what the Source held is not given back
    }

    [Fact]
    public void TheFileGivenToAReaderIsReadOnlyAndStillReplaceable()
    {
        var store = Store();
        store.Create("S");
        store.Put("S", "k", [1]);
        Assert.Equal(AccessOutcome.OK, store.LocalPath("S", "k", out var path));
        Assert.True(new FileInfo(path).IsReadOnly);
        Assert.Equal(AccessOutcome.OK, store.Put("S", "k", [2]));
        Assert.Equal(AccessOutcome.OK, store.Delete("S", "k"));
    }
}
