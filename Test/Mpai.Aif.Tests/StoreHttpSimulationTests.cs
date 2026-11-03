using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.Store;
using AIF.Trust;

namespace Mpai.Aif.Tests;

// THE WORKFLOW ON THIS COMPUTER, WITH EACH PARTY ON ITS OWN PORT OR FOLDER: the MPAI Store Service on a local port; an Implementer that registers, is approved by MPAI, and submits
// its L3 signed; a Controller with a folder of its own that obtains the L3 from the Store over HTTP, obtains the Implementation from the location the L3 gives, measures it and asks
// the Store whether it is what it approved (MPAI_AIFS_VerifyImplementation). The Controller is never given the fingerprint.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class StoreHttpSimulationTests
{
    private const string Aim = "1MMC-ASR-V2.5-I01", Binary = "Mpai.Mmc.Asr";

    [SkippableFact]
    public async Task AnImplementerSubmitsAndAControllerObtainsAndVerifiesWhatItObtainsOverHttp()
    {
        using var store = new StoreTests.RunningStore();

        // THE IMPLEMENTER puts its package where it chooses and submits the L3, signed.
        var me = await StoreTests.Implementer.Approved(store.Http);
        var package = Path.Combine(store.Packages, "asr");
        Directory.CreateDirectory(package);
        var dll = Path.Combine(package, Binary + ".dll");
        File.WriteAllText(dll, "the Implementation, as the Implementer built it");
        var l3 = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Amds, Aim + ".json")))!;
        l3["Identifier"]!["ImplementerID"] = me.Id;
        l3["Implementations"]![0]!["ImplementationURI"] = new Uri(package + Path.DirectorySeparatorChar).AbsoluteUri;
        var submitted = await StoreTests.Submit(store.Http, l3, me.Key);
        Assert.True(submitted.Status == HttpStatusCode.Created, $"{submitted.Status} {submitted.Body}\n{store.Log}");

        // THE CONTROLLER, with a folder of its own, reaches the Store over HTTP.
        using var source = new HttpAimSource(store.Url);
        Assert.NotNull(source.Get(Aim));                                                              // the Store answers
        var controller = Path.Combine(store.Root, "controller-1");
        var repository = new AimRepository(controller);
        Assert.True(repository.Obtain(Aim, source));                                                  // the L3 from the Store, the Implementation from where the L3 says
        Assert.True(repository.Holds(Aim));
        Assert.Equal(File.ReadAllText(dll), File.ReadAllText(Path.Combine(controller, "implementations", Aim, Binary)));
        Assert.False(repository.Obtain(Aim, source));                                                  // what it holds it does not obtain again

        // It measured and asked: the Store says Same, and a different measurement Different. The Controller has no way to read the fingerprint.
        var measured = AimRepository.Fingerprint(File.ReadAllBytes(dll));
        Assert.Equal(VerifyResult.Same, source.Verify(Aim, Binary, measured));
        Assert.Equal(VerifyResult.Different, source.Verify(Aim, Binary, new string('0', 64)));
        Assert.Equal(VerifyResult.NotRecorded, source.Verify(Aim, "Another", measured));

        // What the trust check of the Controller does with that answer (the Verification Pipeline's evidence): the binary it ran, measured, put to the Store.
        var settings = new Dictionary<string, string>();
        var same = ImplementationEvidence.Check(Aim, [new PtfEvidence.Item(PtfEvidence.CodeHash, Binary, measured)], Binary, source, settings);
        Assert.Empty(same);
        var other = ImplementationEvidence.Check(Aim, [new PtfEvidence.Item(PtfEvidence.CodeHash, Binary, new string('A', 64))], Binary, source, settings);
        Assert.Contains("not the binary the Store approved", string.Join(" ", other));

        // THE IMPLEMENTATION CHANGES where the Implementer keeps it, after the Store approved it: another Controller does not keep what it obtains.
        File.WriteAllText(dll, "something else");
        var second = new AimRepository(Path.Combine(store.Root, "controller-2"));
        var refused = Assert.Throws<InvalidOperationException>(() => second.Obtain(Aim, source));
        Assert.Contains("NOT_TRUSTED", refused.Message);
        Assert.False(second.Holds(Aim));

        // A Store that cannot be reached has not said it is the same: nothing is trusted on its silence.
        using var nobody = new HttpAimSource("http://127.0.0.1:1");
        Assert.Equal(VerifyResult.NotRecorded, nobody.Verify(Aim, Binary, measured));
        Assert.Null(nobody.Get(Aim));
        Assert.False(nobody.Reachable);
    }
}
