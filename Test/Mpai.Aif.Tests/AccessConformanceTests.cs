using System.Security.Cryptography;
using System.Text;

using AIF.Controller;
using AIF.SharedStorage;

namespace Mpai.Aif.Tests;

// AIF V3.0 Storage 6.5 and 6.6: data is identified by its SHA-256 - what a Uri holds that is not what the entry pins is replaced by the data found by the hash, and what an AIM is not given
// (its data is valid only with data that is missing) it does not read.
public class AccessConformanceTests : IDisposable
{
    private readonly string work = Path.Combine(Path.GetTempPath(), "mpai-access-c-" + Guid.NewGuid().ToString("N"));
    private readonly AccessStore store;
    private static readonly AccessTerms Terms = new("test licence", "https://example.org/terms", false);
    private static readonly StorageHolder Me = new("M", "aim");

    public AccessConformanceTests() { Directory.CreateDirectory(work); store = new AccessStore(Path.Combine(work, "access")); }
    public void Dispose() { try { Directory.Delete(work, true); } catch { } }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    private (string Uri, string Sha) File1(string name, string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        var path = Path.Combine(work, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        return (new Uri(path).AbsoluteUri, Sha(data));
    }

    [Fact]
    public void WhatAUriHoldsThatIsNotWhatTheEntryPinsIsReplacedByTheDataFoundByItsHash()
    {
        var old = File1("a/model.bin", "version one");
        var wanted = File1("b/model.bin", "version two");
        var fetcher = new AccessFetcher(store);
        Assert.Equal(AccessOutcome.OK, fetcher.Ensure(new AccessEntry("model", old.Uri, old.Sha, [], false, Terms: Terms), "aim", out _));
        Assert.Equal(AccessOutcome.OK, fetcher.Ensure(new AccessEntry("model", wanted.Uri, wanted.Sha, [], false, Terms: Terms), "aim", out _));
        File.Delete(Path.Combine(work, "b", "model.bin"));                                           // the new data is held in Access under another Uri only

        // the entry of the AIM now pins the new bytes at the old Uri
        var updated = new AccessEntry("model", old.Uri, wanted.Sha, [], false, Terms: Terms);
        Assert.Equal(AccessOutcome.OK, fetcher.Ensure(updated, "aim", out var why));
        Assert.Equal("", why);
        Assert.Equal(AccessOutcome.OK, new BoundAccess(store.As(Me), [updated], null, "aim").Get(old.Uri, "", out var data));
        Assert.Equal("version two", Encoding.UTF8.GetString(data));                                  // not the old bytes, which do not match the SHA-256 declared
    }

    [Fact]
    public void AnEntryAnAimIsNotGivenIsNotReadByIt()
    {
        var model = File1("m/model.bin", "weights");
        var entry = new AccessEntry("model", model.Uri, model.Sha, [], true, Terms: Terms);
        var fetcher = new AccessFetcher(store);
        Assert.Equal(AccessOutcome.OK, fetcher.Ensure(entry, "aim", out _));
        Assert.Equal(AccessOutcome.OK, new BoundAccess(store.As(Me), [entry], null, "aim").Get(model.Uri, "", out _));

        fetcher.Withhold(entry, "aim");                                                              // its partner is missing: the Controller does not give it
        Assert.Equal(AccessOutcome.NotAuthorised, new BoundAccess(store.As(Me), [], null, "aim").Get(model.Uri, "model.bin", out _));
    }
}
