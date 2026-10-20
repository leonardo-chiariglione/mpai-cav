using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.Store;
using Mpai.Aih;
using Mpai.Aih.Nnm;

namespace Mpai.Aif.Tests;

// THE CONTROLLER'S REPOSITORY AND THE AIM LICENCE (AIF-LIC-V3.0): an AIM whose Metadata offers a licence (AIMLicence) is licensed to the holder of a PTF Trust Anchor by an AIM
// Licence the MPAI Store issues and signs; every Controller under that Trust Anchor is covered. The Controller keeps it with the AIM and checks it before it builds the AIM.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class AimLicenceRepositoryTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "aih-licence-" + Guid.NewGuid().ToString("N"));
    private readonly AIF.Metadata.L2Conformance conformance = new(Repository.Schemas, _ => null);
    private readonly ECDsa storeKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private DateTimeOffset now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() { storeKey.Dispose(); try { Directory.Delete(folder, true); } catch { } }

    private string StoreFolder => Path.Combine(folder, "store");
    private ECDsa? KeyOf(string id) => id == "store-key-1" ? storeKey : null;
    private AimLicences.Issuer Issuer(string? maxAge = null) => new(storeKey, "store-key-1", "mpai-store", maxAge);

    // An organisation: a Trust Authority (a PTF Trust Anchor) that admits its Controllers, each with an Instance Credential under it.
    private AIF.Trust.TrustAuthority Org(string name) => new(name, () => now);

    private AimLicences.ControllerIdentity Controller(AIF.Trust.TrustAuthority org, string id)
    {
        var admission = org.Admit(id, TimeSpan.FromDays(400), now);
        return new(id, admission.Credential, admission.Anchor.Object(), [], a => a == org.Anchor.AnchorId ? org.Anchor.PublicKey : null);
    }

    private static JsonObject Model() => new()
    {
        ["Header"] = "AIP-MLO-V1.0", ["MLModelObjectID"] = "m-1",
        ["MLModelData"] = new JsonArray(new JsonObject { ["Data"] = Convert.ToBase64String(new[] { 0.9f, 3f }.SelectMany(BitConverter.GetBytes).ToArray()) })
    };

    // The Back End (the Implementer): its models are where it keeps them, and it publishes the AIM that wraps one in the MPAI Store, with an offer of a licence.
    private ModelAimPublisher Back() => new(StoreFolder, Path.Combine(folder, "back-end"), l3 => conformance.Check(l3));

    private string Publish(JsonObject? offer = null) => Back().Upload(Model(), aimLicence: offer ?? AimLicenceSchemaTests.Offer());

    // The MPAI Store, asked by the holder of a Trust Anchor.
    private StoreAimSource Source(AIF.Trust.TrustAuthority org, string? maxAge = null, string? onlyController = null) =>
        new(StoreFolder, org.Anchor.AnchorId, Issuer(maxAge), onlyController) { Clock = () => now };

    private AimRepository Repo(AimLicences.ControllerIdentity identity, string name) =>
        new(Path.Combine(folder, name)) { Identity = identity, StoreKeys = KeyOf, Clock = () => now };

    private static IAimProcessor Build(AimRepository repository, string aim)
    {
        var amds = new AmdStore(repository.Folder); amds.Scan();
        return new AihProvider(amds, repository).Create(aim, new Dictionary<string, string>(), null);
    }

    private static string Valid(JsonObject licence)
    {
        var all = AIF.Metadata.PublishedSchemas.At(Repository.Schemas);
        var s = all[Path.GetFullPath(Path.Combine(Repository.Schemas, "AIF", "V3.0", "data", "AIMLicence.json"))];
        using var doc = JsonDocument.Parse(licence.ToJsonString());
        lock (AIF.Metadata.PublishedSchemas.Lock) return s.Evaluate(doc.RootElement).IsValid ? "valid" : "not valid";
    }

    [Fact]
    public void TheStoreIssuesTheOfferAsAnAimLicenceTheControllerKeepsAndBuildsFrom()
    {
        var aim = Publish();
        var org = Org("acme"); var source = Source(org);
        var repository = Repo(Controller(org, "c-1"), "c-1");
        Assert.True(repository.Obtain(aim, source));

        var licence = repository.LicenceOf(aim)!;
        Assert.Equal("valid", Valid(licence));                                                           // it is an AIM Licence as the schema says
        Assert.Equal(org.Anchor.AnchorId, (string?)licence["Licensee"]!["TrustAnchorID"]);               // the licensee is the holder of the Trust Anchor
        Assert.Equal(aim, (string?)licence["AIM"]!["AIMName"]);
        Assert.Equal("mpai-store", (string?)licence["Licensor"]!["LicensorID"]);                          // the Store grants
        Assert.Equal("2027-10-18T12:00:00Z", (string?)licence["Term"]!["NotAfter"]);                    // the offer's P1Y, from now
        Assert.Equal("Final", (string?)licence["Payment"]!["SPMStatus"]);                                // what was priced is paid...
        Assert.StartsWith("tx-", (string?)licence["Payment"]!["Settlement"]!["Transaction"]!["TransactionID"]);   // ...and this is the proof of the purchase
        Assert.Equal(repository.RightOf(aim), (string?)licence["AIMLicenceID"]);
        Assert.IsType<NnmAimProcessor>(Build(repository, aim));

        // Paid for once: the repository has it, and so has a Controller that restarts.
        Assert.False(repository.Obtain(aim, source));
        Assert.False(Repo(Controller(org, "c-1"), "c-1").Obtain(aim, source));
        Assert.Single(source.Issued);
    }

    // A Controller can be created and destroyed: the licence is the organisation's, not the Controller's.
    [Fact]
    public void EveryControllerUnderTheTrustAnchorIsCoveredAndNothingIsPaidForAgain()
    {
        var aim = Publish();
        var org = Org("acme"); var source = Source(org);
        var first = Repo(Controller(org, "c-1"), "c-1");
        first.Obtain(aim, source);

        var second = Repo(Controller(org, "c-2"), "c-2");                                                // another Controller of the organisation, created later
        Assert.True(second.Obtain(aim, source));                                                         // it obtains the AIM...
        Assert.Single(source.Issued);                                                                    // ...under the licence already issued: nothing is bought again
        Assert.Equal(first.RightOf(aim), second.RightOf(aim));
        Assert.IsType<NnmAimProcessor>(Build(second, aim));
    }

    [Fact]
    public void AControllerThatIsNotUnderTheTrustAnchorIsNotCovered()
    {
        var aim = Publish();
        var org = Org("acme"); var other = Org("rival");
        var source = Source(org);                                                                        // issues to acme
        var stranger = Repo(Controller(other, "c-9"), "stranger");
        var refused = Assert.Throws<InvalidOperationException>(() => stranger.Obtain(aim, new Given(source.Get(aim)!, source.GetLicence(aim), StoreFolder)));
        Assert.Contains("NOT_LICENSED", refused.Message);
        Assert.Contains("not under the Trust Anchor", refused.Message);
        Assert.False(stranger.Holds(aim));
    }

    [Fact]
    public void WhenTheLicenceNamesOneControllerOfTheOrganisationOnlyThatOneIsCovered()
    {
        var aim = Publish();
        var org = Org("acme");
        var source = Source(org, onlyController: "c-1");                                                 // the licence covers one Controller of acme only
        Assert.True(Repo(Controller(org, "c-1"), "c-1").Obtain(aim, source));
        var refused = Assert.Throws<InvalidOperationException>(() => Repo(Controller(org, "c-2"), "c-2").Obtain(aim, source));
        Assert.Contains("covers only the Controller c-1", refused.Message);
    }

    [Fact]
    public void AnAimLicenceNotSignedByTheStoreIsRefused()
    {
        var aim = Publish();
        var org = Org("acme");
        var source = Source(org);
        var forged = JsonNode.Parse(source.GetLicence(aim)!)!.AsObject();
        forged["Grant"]!["MaxControllers"] = 1000;                                                       // changed after the Store signed it
        var refused = Assert.Throws<InvalidOperationException>(() => Repo(Controller(org, "c-1"), "c-1").Obtain(aim, new Given(source.Get(aim)!, forged.ToJsonString(), StoreFolder)));
        Assert.Contains("signature", refused.Message);
        // ... and a key the Controller does not know as the Store's:
        var stranger = new AimRepository(Path.Combine(folder, "stranger")) { Identity = Controller(org, "c-1"), StoreKeys = _ => null, Clock = () => now };
        Assert.Contains("signature", Assert.Throws<InvalidOperationException>(() => stranger.Obtain(aim, Source(org))).Message);
    }

    [Fact]
    public void AnAimThatOffersALicenceIsNotKeptWithoutOne()
    {
        var aim = Publish();
        var org = Org("acme");
        var refused = Assert.Throws<InvalidOperationException>(() => Repo(Controller(org, "c-1"), "c-1").Obtain(aim, new Given(Source(org).Get(aim)!, null, StoreFolder)));
        Assert.Contains("the Store gave no AIM Licence", refused.Message);
    }

    [Fact]
    public void AnAimLicenceThatDoesNotCoverWhatIsHeldIsRefused()
    {
        var aim = Publish();
        var org = Org("acme");
        var source = Source(org);
        var licence = JsonNode.Parse(source.GetLicence(aim)!)!.AsObject();
        licence["AIM"]!["Implementations"]![0]!["Hash"] = new string('0', 64);                          // another file than the one the Store approved...
        AIF.Trust.PtfSignature.Sign(licence, storeKey, "store-key-1");                                  // ...though the Store signed it
        var refused = Assert.Throws<InvalidOperationException>(() => Repo(Controller(org, "c-1"), "c-1").Obtain(aim, new Given(source.Get(aim)!, licence.ToJsonString(), StoreFolder)));
        Assert.Contains("other fingerprint", refused.Message);
    }

    [Fact]
    public void AnAimLicenceThatHasEndedIsNotUsed()
    {
        var aim = Publish();
        var org = Org("acme");
        var repository = Repo(Controller(org, "c-1"), "c-1");
        repository.Obtain(aim, Source(org));
        Build(repository, aim);                                                                          // in its Term

        now = now.AddYears(1).AddDays(1);                                                                // a year and a day later
        var refused = Assert.Throws<InvalidOperationException>(() => Build(repository, aim));
        Assert.Contains("Term ended", refused.Message);
    }

    // REVOCATION: the Store revokes an AIM Licence - a vulnerability found in the AIM, a breach of its terms, a payment reversed, a key compromised.
    [Fact]
    public void ARevokedAimLicenceIsNotUsedWhenTheStoreIsAsked()
    {
        var aim = Publish();
        var org = Org("acme"); var source = Source(org);
        var repository = Repo(Controller(org, "c-1"), "c-1");
        repository.Obtain(aim, source);
        source.Revoke(repository.RightOf(aim)!);

        repository.CheckLicence(aim);                                                                    // the Store is not asked: it is used
        source.Reachable = false;
        repository.CheckLicence(aim, source);                                                            // asked and unreachable: not told of the revocation
        source.Reachable = true;
        var refused = Assert.Throws<InvalidOperationException>(() => repository.CheckLicence(aim, source));
        Assert.Contains("revoked", refused.Message);
    }

    // STATUS MaxAge: an offline Controller goes on only for as long as the licence allows since the Store last confirmed it.
    [Fact]
    public void AnOfflineControllerGoesOnUntilTheStatusIsTooOldAndThenAsks()
    {
        var aim = Publish();
        var org = Org("acme"); var source = Source(org, maxAge: "P7D");
        var repository = Repo(Controller(org, "c-1"), "c-1");
        repository.Obtain(aim, source);
        Assert.Equal("P7D", (string?)repository.LicenceOf(aim)!["Status"]!["MaxAge"]);

        source.Reachable = false;                                                                        // offline from here
        now = now.AddDays(5); Build(repository, aim);                                                    // day 5: within a week of when it was issued
        repository.CheckLicence(aim, source);                                                            // asked, unreachable: it does not count as confirmed
        now = now.AddDays(3);                                                                            // day 8
        var refused = Assert.Throws<InvalidOperationException>(() => Build(repository, aim));
        Assert.Contains("the MPAI Store has to be asked", refused.Message);

        source.Reachable = true;                                                                         // the Store is back
        repository.CheckLicence(aim, source);                                                            // not revoked: confirmed now (day 8)
        Build(repository, aim);                                                                          // and another week begins
        source.Reachable = false;
        now = now.AddDays(6); Build(repository, aim);                                                    // day 14
        now = now.AddDays(2);
        Assert.Throws<InvalidOperationException>(() => Build(repository, aim));                          // day 16: more than a week since day 8

        // A licence that was revoked is not confirmed by asking.
        source.Reachable = true; source.Revoke(repository.RightOf(aim)!);
        Assert.Contains("revoked", Assert.Throws<InvalidOperationException>(() => repository.CheckLicence(aim, source)).Message);
    }

    [Fact]
    public void ALicenceThatDoesNotGrantTheRightToKeepCannotBeKeptWithTheAim()
    {
        var offer = AimLicenceSchemaTests.Offer();
        offer["Grant"] = new JsonObject { ["Rights"] = new JsonArray("Run") };
        var aim = Publish(offer);
        var org = Org("acme");
        var refused = Assert.Throws<InvalidOperationException>(() => Repo(Controller(org, "c-1"), "c-1").Obtain(aim, Source(org)));
        Assert.Contains("right to keep", refused.Message);
    }

    // The Controller reads the Metadata first - the offer and its price - and pays (asks for the AIM Licence) only if it goes on.
    [Fact]
    public void NothingIsPaidForUntilTheLicenceIsAskedFor()
    {
        var aim = Publish();
        var org = Org("acme"); var source = Source(org);
        var package = source.Get(aim)!;
        Assert.Empty(source.Issued);                                                                     // the Metadata was read: nothing issued
        var offer = JsonNode.Parse(package.L3Json)!["AIMLicence"]!;
        Assert.Equal(120.0, (double)offer["Payment"]!["OneTime"]!["Price"]!);                           // the price is in it
        Assert.Equal("Model", (string?)offer["Payment"]!["SPMStatus"]);                                  // as a pricing model on offer
        Assert.Equal("P1Y", (string?)offer["Term"]!["Duration"]);

        Assert.NotNull(source.GetLicence(aim));                                                          // the Controller goes on: the AIM Licence is issued
        Assert.Single(source.Issued);
        Assert.Equal(source.GetLicence(aim), source.GetLicence(aim));                                    // and is the same when asked again
        Assert.Single(source.Issued);
    }

    // WHO CONTROLS THE EXECUTION: an AIM that executes apart from the Controller is kept by what controls it. Under the licensee's Trust Anchor the right to Run covers it; under
    // another party's control the licence has to grant that.
    [Fact]
    public void AnAimExecutingApartIsCoveredByRunWhereTheLicenseeControlsItAndByRunUnderOtherControlWhereAnotherPartyDoes()
    {
        var org = Org("acme"); var rival = Org("rival");

        var own = Publish(AimLicenceSchemaTests.Offer());
        var underLicensee = Repo(Controller(org, "module-1"), "apart-own"); underLicensee.ExecutesApart = true;
        Assert.True(underLicensee.Obtain(own, Source(org)));                                             // under the licensee's Trust Anchor: Run is enough

        var offer = AimLicenceSchemaTests.Offer(); offer["Grant"] = new JsonObject { ["Rights"] = new JsonArray("Run", "Keep") };
        var withoutGrant = Publish(offer);
        var source = Source(org);
        var underOther = Repo(Controller(rival, "module-9"), "apart-other"); underOther.ExecutesApart = true;
        var refused = Assert.Throws<InvalidOperationException>(() => underOther.Obtain(withoutGrant, new Given(source.Get(withoutGrant)!, source.GetLicence(withoutGrant), StoreFolder)));
        Assert.Contains("under the control of another party", refused.Message);                         // another party's control, not granted

        var granted = Publish();                                                                          // the default offer grants RunUnderOtherControl
        var allowed = Repo(Controller(rival, "module-9"), "apart-allowed"); allowed.ExecutesApart = true;
        Assert.True(allowed.Obtain(granted, new Given(source.Get(granted)!, source.GetLicence(granted), StoreFolder)));
    }

    [Fact]
    public void AnAimWithoutAnOfferNeedsNoLicence()
    {
        var aim = Back().Upload(Model());                                                                // no AIMLicence in its Metadata
        var repository = new AimRepository(Path.Combine(folder, "open"));                                // not even an identity or a Store key
        Assert.True(repository.Obtain(aim, new StoreAimSource(StoreFolder, "anyone")));
        Assert.Null(repository.LicenceOf(aim));
        Assert.IsType<NnmAimProcessor>(Build(repository, aim));
    }

    private string Folder => StoreFolder;                                        // the Store's folder: the Store verifies what the Controller measured
    // A source that gives what it is told to: the package, and the AIM Licence (or none).
    private sealed class Given(AimPackage package, string? licence, string storeFolder) : IAimSource
    {
        public AimPackage? Get(string aimName) => package;
        public VerifyResult Verify(string aimName, string binaryName, string fingerprint) => new ImplementationFingerprints(storeFolder).Verify(aimName, binaryName, fingerprint);
        public string? GetLicence(string aimName) => licence;
        public string? CurrentVersion(string aimName) => package.Version;
        public bool? IsRevoked(string aimLicenceId) => null;
    }

    // THE TWO LICENCES: the Implementer's offer is its licence to the MPAI Store; the AIM Licence the Store issues is its sublicence, under that offer.
    [Fact]
    public void TheIssuedAimLicenceIsASublicenceUnderTheOfferAndItsPriceHasTheStoresMarkup()
    {
        var aim = Publish();
        var org = Org("acme");
        var markup = new AimLicences.Issuer(storeKey, "store-key-1", "mpai-store", null, "https://store.example/MPAI/Store/Licence", 10m);     // the Store's mark-up: 10%
        var source = new StoreAimSource(StoreFolder, org.Anchor.AnchorId, markup) { Clock = () => now };
        var repository = Repo(Controller(org, "c-1"), "c-1");
        repository.Obtain(aim, source);

        var licence = repository.LicenceOf(aim)!;
        var offer = JsonNode.Parse(File.ReadAllText(Path.Combine(StoreFolder, aim + ".json")))!["AIMLicence"]!.AsObject();
        Assert.Equal("valid", Valid(licence));
        Assert.Equal(AimLicences.OfferHash(offer), (string?)licence["ParentLicence"]!["OfferHash"]);      // the offer it is under
        Assert.Equal("1", (string?)licence["ParentLicence"]!["ImplementerID"]);
        Assert.Equal(120.0, (double)offer["Payment"]!["OneTime"]!["Price"]!);                             // what the Implementer proposed
        Assert.Equal(132.0, (double)licence["Payment"]!["OneTime"]!["Price"]!);                           // what the Controller's owner pays: with the mark-up
        Assert.IsType<NnmAimProcessor>(Build(repository, aim));
    }

    [Fact]
    public void TheStoreIssuesNothingUnderAnOfferThatDoesNotAllowItToSublicense()
    {
        var offer = AimLicenceSchemaTests.Offer(); offer["Sublicensing"] = new JsonObject { ["Allowed"] = false };
        var aim = Publish(offer);
        var org = Org("acme");
        var repository = Repo(Controller(org, "c-1"), "c-1");
        var error = Assert.Throws<InvalidOperationException>(() => repository.Obtain(aim, Source(org)));
        Assert.Contains("sublicense", error.Message);
    }

    [Fact]
    public void ALicenceUnderAnotherOfferThanTheOneInTheMetadataIsRefused()
    {
        var aim = Publish();
        var org = Org("acme"); var source = Source(org);
        var repository = Repo(Controller(org, "c-1"), "c-1");
        repository.Obtain(aim, source);
        // The Metadata the Controller holds is changed after the licence was issued under the original offer (a cheaper price).
        var path = Path.Combine(repository.Folder, aim + ".json");
        var l3 = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        l3["AIMLicence"]!["Payment"]!["OneTime"]!["Price"] = 1.0;
        File.WriteAllText(path, l3.ToJsonString());
        var error = Assert.Throws<InvalidOperationException>(() => repository.CheckLicence(aim));
        Assert.Contains("OfferHash", error.Message);
    }
}
