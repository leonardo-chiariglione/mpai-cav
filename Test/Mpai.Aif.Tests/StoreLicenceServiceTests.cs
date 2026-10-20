using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.Store;
using Mpai.Aih;
using Mpai.Aih.Nnm;

namespace Mpai.Aif.Tests;

// THE AIM LICENCE OVER HTTPS: the Store Service issues the AIM Licence (MPAI_AIFS_GetLicence, POST /MPAI/Store/Licence) under the Implementer's AIMLicence in the L3 it published, at
// the price with the Store's mark-up, signed with its key; the same one is given when it is asked again; its status (MPAI_AIFS_VerifyLicence) says whether it was revoked; the public
// key of the Store can be fetched. A Controller with a folder of its own obtains the AIM through HttpAimSource, keeps the AIM Licence and builds from it. The Service runs from its own
// build output, on a free port, with a folder of its own; it is stopped and the folder deleted afterwards.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class StoreLicenceServiceTests
{
    private static JsonObject Model() => new()
    {
        ["Header"] = "AIP-MLO-V1.0", ["MLModelObjectID"] = "m-1",
        ["MLModelData"] = new JsonArray(new JsonObject { ["Data"] = Convert.ToBase64String(new[] { 0.9f, 3f }.SelectMany(BitConverter.GetBytes).ToArray()) })
    };

    // The Back End: a machine Implementer that MPAI approved, publishing the AIM that wraps its model - with an offer, or without one.
    private static async Task<(string Aim, StoreTests.Implementer Who)> Publish(StoreTests.RunningStore store, JsonObject? offer)
    {
        var backEnd = await StoreTests.Implementer.Approved(store.Http, "Machine", "Health Back End");
        var publisher = new ModelAimPublisher(store.Url, backEnd.Id, backEnd.Key, Path.Combine(store.Packages, "health-back-end"));
        return (publisher.Upload(Model(), aimLicence: offer), backEnd);
    }

    private static async Task<(HttpStatusCode Status, string Body)> Post(HttpClient http, string path, JsonObject body, string? admin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        if (admin is not null) request.Headers.Add("MPAI-Admin", admin);
        using var answer = await http.SendAsync(request);
        return (answer.StatusCode, await answer.Content.ReadAsStringAsync());
    }

    // A Controller of an organisation: a Trust Authority that admitted it, and a repository of its own that asks the Store over HTTPS.
    private static (AIF.Trust.TrustAuthority Org, AimLicences.ControllerIdentity Identity) Controller(string name = "acme")
    {
        var org = new AIF.Trust.TrustAuthority(name, () => DateTimeOffset.UtcNow);
        var admission = org.Admit("c-1", TimeSpan.FromDays(400), DateTimeOffset.UtcNow);
        return (org, new("c-1", admission.Credential, admission.Anchor.Object(), [], a => a == org.Anchor.AnchorId ? org.Anchor.PublicKey : null));
    }

    private static AimRepository Repo(string folder, AimLicences.ControllerIdentity identity, HttpAimSource source) =>
        new(folder) { Identity = identity, StoreKeys = source.StoreKey, Clock = () => DateTimeOffset.UtcNow };

    private static IAimProcessor Build(AimRepository repository, string aim)
    {
        var amds = new AmdStore(repository.Folder); amds.Scan();
        return new AihProvider(amds, repository).Create(aim, new Dictionary<string, string>(), null);
    }

    [SkippableFact]
    public async Task AnAimWithAnOfferIsLicensedOverHttpAtThePriceWithTheStoresMarkupAndAControllerBuildsFromIt()
    {
        using var store = new StoreTests.RunningStore("--MarkupPercent 10");
        var (aim, _) = await Publish(store, AimLicenceSchemaTests.Offer());
        var (org, identity) = Controller();
        using var source = new HttpAimSource(store.Url, null, org.Anchor.AnchorId);
        var repository = Repo(Path.Combine(store.Root, "controller"), identity, source);

        Assert.True(repository.Obtain(aim, source));                                                      // the Metadata, the AIM Licence (issued by the Service), the model
        var licence = repository.LicenceOf(aim)!;
        Assert.Equal(org.Anchor.AnchorId, (string?)licence["Licensee"]!["TrustAnchorID"]);                // the Entity that runs Controllers
        Assert.Equal("mpai-store", (string?)licence["Licensor"]!["LicensorID"]);
        Assert.Equal(132.0, (double)licence["Payment"]!["OneTime"]!["Price"]!);                           // the Implementer proposed 120: with the Store's 10%
        using var l3Answer = await store.Http.GetAsync($"/MPAI/Store/L3/{aim}");
        var offer = JsonNode.Parse(await l3Answer.Content.ReadAsStringAsync())!["AIMLicence"]!.AsObject();
        Assert.Equal(120.0, (double)offer["Payment"]!["OneTime"]!["Price"]!);
        Assert.Equal(AimLicences.OfferHash(offer), (string?)licence["ParentLicence"]!["OfferHash"]);      // under the offer in the Metadata the Store published
        Assert.IsType<NnmAimProcessor>(Build(repository, aim));                                           // the signature checked with the key the Store gave

        // The same AIM Licence when it is asked again, over the route and through the source; what is paid for once is not paid for again.
        var again = await Post(store.Http, "/MPAI/Store/Licence", new JsonObject { ["AIMName"] = aim, ["TrustAnchorID"] = org.Anchor.AnchorId });
        Assert.Equal(HttpStatusCode.OK, again.Status);
        Assert.Equal(licence.ToJsonString(), JsonNode.Parse(again.Body)!.ToJsonString());
        Assert.Equal(licence.ToJsonString(), JsonNode.Parse(source.GetLicence(aim)!)!.ToJsonString());
        var id = (string)licence["AIMLicenceID"]!;
        using var fetched = await store.Http.GetAsync($"/MPAI/Store/Licence/{id}");
        Assert.Equal(licence.ToJsonString(), JsonNode.Parse(await fetched.Content.ReadAsStringAsync())!.ToJsonString());
        Assert.Contains("issued an AIM Licence", store.Log);

        // Another holder of a Trust Anchor is another licensee: another AIM Licence.
        var other = await Post(store.Http, "/MPAI/Store/Licence", new JsonObject { ["AIMName"] = aim, ["TrustAnchorID"] = "another-anchor" });
        Assert.Equal(HttpStatusCode.Created, other.Status);
        Assert.NotEqual(id, (string?)JsonNode.Parse(other.Body)!["AIMLicenceID"]);
    }

    [SkippableFact]
    public async Task TheStatusSaysValidUntilTheOperatorRevokesAndARevokedLicenceIsRefused()
    {
        using var store = new StoreTests.RunningStore();
        var (aim, _) = await Publish(store, AimLicenceSchemaTests.Offer());
        var (org, identity) = Controller();
        using var source = new HttpAimSource(store.Url, null, org.Anchor.AnchorId);
        var repository = Repo(Path.Combine(store.Root, "controller"), identity, source);
        repository.Obtain(aim, source);
        var licence = repository.LicenceOf(aim)!;
        var id = (string)licence["AIMLicenceID"]!;
        Assert.Equal(120.0, (double)licence["Payment"]!["OneTime"]!["Price"]!);                           // no mark-up unless the Store has one
        repository.CheckLicence(aim, source);                                                             // good, and the Store says it is not revoked
        Assert.False(source.IsRevoked(id));

        using (var status = await store.Http.GetAsync($"/MPAI/Store/Licence/{id}/status"))
            Assert.Equal("valid", (string?)JsonNode.Parse(await status.Content.ReadAsStringAsync())!["status"]);

        // Only the operator of the Store revokes, and says why.
        var noKey = await Post(store.Http, $"/MPAI/Store/Licence/{id}/revoke", new JsonObject { ["Reason"] = "a vulnerability was found" });
        Assert.Equal(HttpStatusCode.Unauthorized, noKey.Status);
        var noReason = await Post(store.Http, $"/MPAI/Store/Licence/{id}/revoke", new JsonObject(), StoreTests.Admin);
        Assert.Equal(HttpStatusCode.BadRequest, noReason.Status);
        var revoked = await Post(store.Http, $"/MPAI/Store/Licence/{id}/revoke", new JsonObject { ["Reason"] = "a vulnerability was found" }, StoreTests.Admin);
        Assert.Equal(HttpStatusCode.OK, revoked.Status);

        Assert.True(source.IsRevoked(id));
        using (var status = await store.Http.GetAsync($"/MPAI/Store/Licence/{id}/status"))
        {
            var json = JsonNode.Parse(await status.Content.ReadAsStringAsync())!;
            Assert.Equal("revoked", (string?)json["status"]);
            Assert.Equal("a vulnerability was found", (string?)json["reason"]);
        }
        var refusal = Assert.Throws<InvalidOperationException>(() => repository.CheckLicence(aim, source));
        Assert.Contains("NOT_LICENSED", refusal.Message);
        Assert.Contains("revoked", refusal.Message);
    }

    [SkippableFact]
    public async Task AnOfferThatDoesNotAllowTheStoreToSublicenseIsRefusedWithItsReason()
    {
        using var store = new StoreTests.RunningStore();
        var offer = AimLicenceSchemaTests.Offer(); offer["Sublicensing"] = new JsonObject { ["Allowed"] = false };
        var (aim, _) = await Publish(store, offer);
        var (org, _) = Controller();

        var answer = await Post(store.Http, "/MPAI/Store/Licence", new JsonObject { ["AIMName"] = aim, ["TrustAnchorID"] = org.Anchor.AnchorId });
        Assert.Equal(HttpStatusCode.Forbidden, answer.Status);
        Assert.Contains("sublicense", (string?)JsonNode.Parse(answer.Body)!["refused"]);
        using var source = new HttpAimSource(store.Url, null, org.Anchor.AnchorId);
        Assert.Contains("sublicense", Assert.Throws<InvalidOperationException>(() => source.GetLicence(aim)).Message);
    }

    [SkippableFact]
    public async Task AnAimWithoutAnAimLicenceNeedsNoneAndWhatIsNotInTheStoreHasNone()
    {
        using var store = new StoreTests.RunningStore();
        var (aim, _) = await Publish(store, null);
        var (org, _) = Controller();

        var answer = await Post(store.Http, "/MPAI/Store/Licence", new JsonObject { ["AIMName"] = aim, ["TrustAnchorID"] = org.Anchor.AnchorId });
        Assert.Equal(HttpStatusCode.NotFound, answer.Status);
        Assert.Contains("needs no AIM Licence", (string?)JsonNode.Parse(answer.Body)!["refused"]);
        using var source = new HttpAimSource(store.Url, null, org.Anchor.AnchorId);
        Assert.Null(source.GetLicence(aim));
        var repository = new AimRepository(Path.Combine(store.Root, "controller"));                       // no identity, no key: none is needed
        Assert.True(repository.Obtain(aim, source));
        Assert.Null(repository.LicenceOf(aim));
        Assert.IsType<NnmAimProcessor>(Build(repository, aim));

        Assert.Equal(HttpStatusCode.NotFound, (await Post(store.Http, "/MPAI/Store/Licence", new JsonObject { ["AIMName"] = "no-such-aim", ["TrustAnchorID"] = "x" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(store.Http, "/MPAI/Store/Licence", new JsonObject { ["AIMName"] = aim })).Status);
        using (var missing = await store.Http.GetAsync("/MPAI/Store/Licence/lic-none")) Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using (var missing = await store.Http.GetAsync("/MPAI/Store/Licence/lic-none/status")) Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Null(source.IsRevoked("lic-none"));                                                        // the Store does not know it: it cannot say
    }

    [SkippableFact]
    public async Task TheStoreGivesItsPublicKeyAndNobodyElses()
    {
        using var store = new StoreTests.RunningStore();
        using var source = new HttpAimSource(store.Url);
        Assert.NotNull(source.StoreKey("mpai-store-1"));
        Assert.Null(source.StoreKey("another-key"));
        using var answer = await store.Http.GetAsync("/MPAI/Store/Key/mpai-store-1");
        Assert.Equal("ECDSA-P256", (string?)JsonNode.Parse(await answer.Content.ReadAsStringAsync())!["algorithm"]);
    }

    [Fact]
    public void WhatTheStoreIssuedIsKeptAndTheSameAfterARestart()
    {
        var folder = Path.Combine(Path.GetTempPath(), "licence-book-" + Guid.NewGuid().ToString("N"));
        try
        {
            var made = 0;
            JsonObject Issue() { made++; return new JsonObject { ["AIMLicenceID"] = "lic-" + made }; }
            var first = new LicenceBook(folder);
            var (one, created) = first.GetOrIssue("aim-1", "anchor-1", null, Issue);
            Assert.True(created);
            var restarted = new LicenceBook(folder);
            var (same, createdAgain) = restarted.GetOrIssue("aim-1", "anchor-1", null, Issue);
            Assert.False(createdAgain);
            Assert.Equal(one, same);
            Assert.Equal(1, made);
            Assert.True(restarted.GetOrIssue("aim-1", "anchor-1", "c-7", Issue).Created);                 // one Controller of the Trust Anchor is another licensee
            Assert.Equal(("valid", null, null), restarted.Status("lic-1"));
            Assert.True(restarted.Revoke("lic-1", "breach", DateTimeOffset.UtcNow));
            Assert.Equal("revoked", new LicenceBook(folder).Status("lic-1")!.Value.Status);                // revoked stays revoked after a restart
            Assert.Null(restarted.Status("lic-9"));
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }
}
