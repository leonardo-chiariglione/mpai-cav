using AIF.SharedStorage;

namespace Mpai.Aif.Tests;

// The Storage chapter and the Basic API of MPAI-AIF V3.0, as the Controller's stores must keep them: the record of the boundary written by the Controller alone (STO-6) and read by the
// User Agent, a narrowing of the longest time that applies at once (Storage 3), RequestedBy as the client the write
// was made for (4.11.2.6), a Put at an offset atomic with respect to its key (4.11.2.1), and what lapsed with a session or a Module deleted at once (3.7.2, CTR-8).
public class StorageConformanceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mpai-storage-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset clock = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private RuledStore Store(bool everyoneReads, StorageHolder? control = null, string name = "s") =>
        new(() => Path.Combine(root, name), () => clock, everyoneReads, control);

    private static StorageHolder Controller(string module) => new(module, RuledStore.Controller);
    private static StorageHolder Aim(string module, string aim) => new(module, aim);

    public void Dispose() { try { Directory.Delete(root, true); } catch { } }

    [Fact]
    public void OnlyTheControllerWritesTheRecordAndTheUserAgentReadsIt()
    {
        var store = Store(everyoneReads: false);
        var writer = store.For(Controller("M"), "i1", "s1");
        Assert.Equal(StorageOutcome.OK, writer.MPAI_AIFM_RuledStorage_Put("R1/000000001", [1], RuledStore.Boundary, [RuledStore.UserAgent]));

        foreach (var other in new[] { store.For(StorageHolder.UserAgent, "i1", "s1"), store.For(Aim("M", "1TST-A-V1.0-I01"), "i1", "s1") })
        {
            Assert.Equal(StorageOutcome.NotAuthorised, other.MPAI_AIFM_RuledStorage_Put("R1/000000099", [9], RuledStore.Boundary, null));   // a new key in the record
            Assert.Equal(StorageOutcome.NotAuthorised, other.MPAI_AIFM_RuledStorage_Delete("R1/000000001"));
        }
        Assert.Equal(StorageOutcome.OK, store.For(StorageHolder.UserAgent, "i1", "s1").MPAI_AIFM_RuledStorage_Get("R1/000000001", out var read));
        Assert.Equal(new byte[] { 1 }, read);
    }

    [Fact]
    public void ACentralControlDoesNotWriteTheRecordEitherButTheUserAgentStillReadsIt()
    {
        var control = Aim("M", "1TST-CTL-V1.0-I01");
        var store = Store(everyoneReads: false, control);
        Assert.Equal(StorageOutcome.OK, store.For(Controller("M"), "i1", "s1").MPAI_AIFM_RuledStorage_Put("R1/000000001", [1], RuledStore.Boundary, null));
        var central = store.For(control, "i1", "s1");
        Assert.Equal(StorageOutcome.NotAuthorised, central.MPAI_AIFM_RuledStorage_Put("R1/000000002", [2], RuledStore.Boundary, null));
        Assert.Equal(StorageOutcome.NotAuthorised, central.MPAI_AIFM_RuledStorage_Delete("R1/000000001"));
        // no rule for the category Boundary: the User Agent is still the reader of the record
        Assert.Equal(StorageOutcome.OK, store.For(StorageHolder.UserAgent, "i1", "s1").MPAI_AIFM_RuledStorage_Get("R1/000000001", out _));
    }

    [Fact]
    public void ANarrowingOfTheLongestTimeAppliesAtOnceToWhatWasWritten()
    {
        var control = Aim("M", "1TST-CTL-V1.0-I01");
        var store = Store(everyoneReads: false, control);
        var central = store.For(control, "i1", "s1");
        var aim = store.For(Aim("M", "1TST-A-V1.0-I01"), "i1", "s1");
        Assert.Equal(StorageOutcome.OK, central.MPAI_AIFM_RuledStorage_SetRule("Log", new StorageRule(["1TST-A-V1.0-I01"], ["1TST-A-V1.0-I01"], StorageTime.Scope)));
        Assert.Equal(StorageOutcome.OK, aim.MPAI_AIFM_RuledStorage_Put("entry", [1], "Log", null, null));
        Assert.Equal(StorageOutcome.OK, aim.MPAI_AIFM_RuledStorage_Get("entry", out _));
        clock = clock.AddMinutes(10);
        Assert.Equal(StorageOutcome.OK, aim.MPAI_AIFM_RuledStorage_Get("entry", out _));            // nothing narrowed: kept
        Assert.Equal(StorageOutcome.OK, central.MPAI_AIFM_RuledStorage_SetRule("Log", new StorageRule(["1TST-A-V1.0-I01"], ["1TST-A-V1.0-I01"], StorageTime.Parse("5s"))));
        Assert.Equal(StorageOutcome.NotFound, aim.MPAI_AIFM_RuledStorage_Get("entry", out _));      // the longest time is now 5 s: it has lapsed
    }

    [Fact]
    public void KeyInfoGivesTheClientTheWriteWasMadeFor()
    {
        var store = Store(everyoneReads: true);
        var onBehalf = store.PlainFor(Aim("M", "1TST-A-V1.0-I01"), "i1", "client-7", "client-7");
        var plain = store.PlainFor(Aim("M", "1TST-B-V1.0-I01"), "i1", "s1", "");
        onBehalf.MPAI_AIFM_SharedStorage_Put("k", [1, 2, 3]);
        plain.MPAI_AIFM_SharedStorage_Put("own", [1]);
        var info = plain.MPAI_AIFM_SharedStorage_GetKeyInfo("k");                                   // read by another handle: the Put's client, not the reader's
        Assert.Equal("client-7", info.RequestedBy);
        Assert.Equal("M/1TST-A-V1.0-I01", info.StoredBy);
        Assert.Equal(3, info.Length);
        Assert.Equal("", plain.MPAI_AIFM_SharedStorage_GetKeyInfo("own").RequestedBy);
    }

    [Fact]
    public void APutAtAnOffsetIsAtomicWithRespectToItsKey()
    {
        var store = Store(everyoneReads: true);
        var aims = Enumerable.Range(0, 8).Select(i => store.PlainFor(Aim("M", $"1TST-A{i}-V1.0-I01"), "i1", "s1", "")).ToArray();
        aims[0].MPAI_AIFM_SharedStorage_Put("k", Enumerable.Repeat((byte)1, 10).ToArray(), 0);                  // at offset 0 a Put replaces the whole value
        Parallel.For(1, 8, i => aims[i].MPAI_AIFM_SharedStorage_Put("k", Enumerable.Repeat((byte)(i + 1), 10).ToArray(), i * 10));
        var value = aims[0].MPAI_AIFM_SharedStorage_Get("k");
        Assert.Equal(80, value.Length);
        for (var i = 0; i < 8; i++) Assert.All(value.Skip(i * 10).Take(10), b => Assert.Equal((byte)(i + 1), b));       // no range lost to another's read-then-write
    }

    [Fact]
    public void WhatWasKeptForASessionOrAModuleIsDeletedWhenItEndsEvenInAPrivateStorage()
    {
        var module = Store(everyoneReads: false, name: "module");
        var aim = module.For(Aim("M", "1TST-A-V1.0-I01"), "instance-1", "session-1");
        RuledStore.Started("session-1"); RuledStore.Started("instance-1");
        aim.MPAI_AIFM_RuledStorage_Put("for-session", [1], RuledStore.Default, null, StorageTime.Parse("Session"));
        aim.MPAI_AIFM_RuledStorage_Put("for-module", [1], RuledStore.Default, null, StorageTime.Parse("Module"));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(root, "module"), "*.info").Length);

        RuledStore.Ended("session-1"); RuledStore.SweepAll();
        Assert.Single(Directory.GetFiles(Path.Combine(root, "module"), "*.info"));                  // the session's datum is gone at once, with no one asking for it
        RuledStore.Ended("instance-1"); RuledStore.SweepAll();
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "module"), "*.info"));                   // and the Module's, when it stopped
    }
}
