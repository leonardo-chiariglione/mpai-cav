using AIF.SharedStorage;

namespace Mpai.Aif.Tests;

// KEEP IT UNTIL A DATE (additive, 2026/10/27: M3245 3.3 StorageTime). Given no time a datum is kept until it is deleted; a deployment, or a writer, may state a date after which it lapses:
// "until 2026-12-31" is the end of that day. A datum past its date is deleted when the store is next looked at (swept), as one past its Within is. A writer may keep a datum for less, never for more.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class StorageUntilTests
{
    [Fact]
    public void ADateIsParsedAsTheEndOfThatDayAndSaid()
    {
        var time = StorageTime.Parse("until 2026-12-31");
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), time.Until);
        Assert.Contains("until 2027-01-01", time.ToString());
        Assert.Null(StorageTime.Parse("Session 5s").Until);
        Assert.Null(StorageTime.Scope.Until);                                              // none: unchanged
        Assert.Throws<FormatException>(() => StorageTime.Parse("until"));
    }

    [Fact]
    public void ADateIsNotLongerThanAnotherAndNoDateIsLongerThanADate()
    {
        var soon = StorageTime.Parse("until 2026-11-30"); var later = StorageTime.Parse("until 2026-12-31");
        Assert.True(soon.NotLongerThan(later));
        Assert.False(later.NotLongerThan(soon));
        Assert.False(StorageTime.Scope.NotLongerThan(later));                              // forever is longer than a date
        Assert.True(later.NotLongerThan(StorageTime.Scope));
    }

    [Fact]
    public void ADatumPastItsDateIsDeletedAndOneWithoutADateIsKept()
    {
        var folder = Path.Combine(Path.GetTempPath(), "storage-until-" + Guid.NewGuid().ToString("N"));
        var clock = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        try
        {
            var store = RuledStore.SharedAt(folder, () => clock);
            var ua = store.For(StorageHolder.UserAgent, "instance", "session");
            store.Keep("Data", StorageTime.Parse("until 2026-12-31"));                       // the deployment's decision for the category
            Assert.Equal(StorageOutcome.OK, ua.MPAI_AIFM_RuledStorage_Put("kept-by-the-deployment", [1], "Data"));
            Assert.Equal(StorageOutcome.OK, ua.MPAI_AIFM_RuledStorage_Put("shorter", [2], "Data", null, StorageTime.Parse("until 2026-11-01")));       // a writer may keep it for less
            Assert.Equal(StorageOutcome.OK, ua.MPAI_AIFM_RuledStorage_Put("longer-asked", [3], "Data", null, StorageTime.Parse("until 2027-06-30")));   // for more: the deployment's date stands
            Assert.Equal(StorageOutcome.OK, ua.MPAI_AIFM_RuledStorage_Trace("longer-asked", out var trace));
            Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), trace!.Time.Until);

            Assert.Equal(StorageOutcome.OK, ua.MPAI_AIFM_RuledStorage_Get("shorter", out _));
            clock = new DateTimeOffset(2026, 11, 2, 0, 0, 1, TimeSpan.Zero);                // past the shorter date
            Assert.Equal(StorageOutcome.NotFound, ua.MPAI_AIFM_RuledStorage_Get("shorter", out _));
            Assert.Equal(StorageOutcome.OK, ua.MPAI_AIFM_RuledStorage_Get("kept-by-the-deployment", out _));
            clock = new DateTimeOffset(2027, 1, 2, 0, 0, 0, TimeSpan.Zero);                 // past 31 December 2026
            store.Sweep();                                                                    // deleted when the store is swept
            Assert.Empty(ua.MPAI_AIFM_RuledStorage_List());
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }

    [Fact]
    public void WithNoTimeStatedADatumIsKeptUntilItIsDeleted()
    {
        var folder = Path.Combine(Path.GetTempPath(), "storage-forever-" + Guid.NewGuid().ToString("N"));
        var clock = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        try
        {
            var store = RuledStore.SharedAt(folder, () => clock);
            var ua = store.For(StorageHolder.UserAgent, "instance", "session");
            Assert.Equal(StorageOutcome.OK, ua.MPAI_AIFM_RuledStorage_Put("held", [1], "Data"));
            clock = clock.AddYears(30);
            Assert.Equal(StorageOutcome.OK, ua.MPAI_AIFM_RuledStorage_Get("held", out _));
            Assert.Equal(StorageOutcome.OK, ua.MPAI_AIFM_RuledStorage_Delete("held"));
            Assert.Equal(StorageOutcome.NotFound, ua.MPAI_AIFM_RuledStorage_Get("held", out _));
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }
}
