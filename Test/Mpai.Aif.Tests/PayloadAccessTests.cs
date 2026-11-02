using AIF.Channels;

namespace Mpai.Aif.Tests;

// Basic API 4.8: MPAI_AIFM_Payload_Get is NOT_AUTHORISED unless the caller is at the far end of the Channel the payload was placed for, and a payload is freed when every
// reader has released it - a reader counts once, however often it releases.
public class PayloadAccessTests
{
    private static ChannelSpec Channel(params string[] readers) =>
        new("M", new PortEnd("1TST-W-V1.0-I01", "TST-TXT-V1.0"), readers.Select(r => new ChannelReaderSpec(new PortEnd(r, "TST-TXT-V1.0"), PortBehaviour.Default)).ToList(), "InProcess");

    [Fact]
    public void OnlyAReaderOfTheChannelIsAtItsFarEnd()
    {
        var spec = Channel("1TST-A-V1.0-I01", "1TST-B-V1.0-I01");
        var reference = new PayloadStore().Put(spec, new byte[] { 1, 2, 3 });
        Assert.Equal(spec.Id, PayloadStore.ChannelOf(reference));
        Assert.True(PayloadStore.IsAtFarEnd(reference, "1TST-A-V1.0-I01", [spec]));
        Assert.True(PayloadStore.IsAtFarEnd(reference, "1TST-B-V1.0-I01", [spec]));
        Assert.False(PayloadStore.IsAtFarEnd(reference, "1TST-W-V1.0-I01", [spec]));        // the writer is not at the far end
        Assert.False(PayloadStore.IsAtFarEnd(reference, "1TST-X-V1.0-I01", [spec]));        // nor is an AIM of another Channel
        Assert.False(PayloadStore.IsAtFarEnd("aif:payload/nothing/1", "1TST-A-V1.0-I01", [spec]));
        Assert.False(PayloadStore.IsAtFarEnd("not a reference", "1TST-A-V1.0-I01", [spec]));
    }

    [Fact]
    public void AReaderCountsOnceHoweverOftenItReleases()
    {
        var store = new PayloadStore();
        var reference = store.Put(Channel("1TST-A-V1.0-I01", "1TST-B-V1.0-I01"), new byte[] { 1 });
        store.Release(reference, "1TST-A-V1.0-I01");
        store.Release(reference, "1TST-A-V1.0-I01");
        Assert.True(store.TryGet(reference, out _));       // B has not released it
        store.Release(reference, "1TST-B-V1.0-I01");
        Assert.False(store.TryGet(reference, out _));      // every reader has: freed
    }
}
