using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mpai.Aif.Tests;

// THE AIM LICENCE (AIF-LIC-V3.0, schemas/AIF/V3.0/data/AIMLicence.json), one Data Type in two states: the Implementer's OFFER (what the AIMLicence field of AIM Metadata
// holds: no Licensee, no Signature) and the ISSUED AIM Licence (what the MPAI Store signs for a Controller that obtains the AIM).
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class AimLicenceSchemaTests
{
    private static readonly IReadOnlyDictionary<string, Json.Schema.JsonSchema> All = AIF.Metadata.PublishedSchemas.At(Repository.Schemas);

    private static string Check(JsonNode value)
    {
        var s = All[Path.GetFullPath(Path.Combine(Repository.Schemas, "AIF", "V3.0", "data", "AIMLicence.json"))];
        using var doc = JsonDocument.Parse(value.ToJsonString());
        Json.Schema.EvaluationResults r;
        lock (AIF.Metadata.PublishedSchemas.Lock)
            r = s.Evaluate(doc.RootElement, new Json.Schema.EvaluationOptions { OutputFormat = Json.Schema.OutputFormat.List });
        return r.IsValid ? "valid" : "not valid";
    }

    // The price, as an MPAI-MMM Service Pricing Model: a one-time price of 120 euros, updates included.
    private static JsonObject Pricing() => new()
    {
        ["Header"] = "MMM-SPM-V2.2", ["SPMID"] = "spm-1", ["SPMStatus"] = "Model", ["ModelType"] = "OneTime",
        ["CurrencyObject"] = new JsonObject { ["Header"] = "MMM-CUO-V2.2", ["CurrencyObjectID"] = "eur", ["CurrencyData"] = "EUR" },
        ["OneTime"] = new JsonObject { ["Price"] = 120.0, ["IncludesUpdates"] = true }
    };

    // The offer of an Implementer: a paid AIM, for a year, updates included for six months, to a Controller that may provide a service with it.
    public static JsonObject Offer() => new()
    {
        ["Header"] = "AIF-LIC-V3.0", ["TermsURI"] = "https://store.example/terms/AIH-NNM-V1.1", ["Sublicensing"] = new JsonObject { ["Allowed"] = true },
        ["Grant"] = new JsonObject
        {
            ["Rights"] = new JsonArray("Run", "Keep", "RunUnderOtherControl", "ProvideService"),
            ["MaxControllers"] = 2, ["MaxConcurrentModules"] = 50, ["ExcludedPurposes"] = new JsonArray("military")
        },
        ["Term"] = new JsonObject { ["Duration"] = "P1Y" },
        ["Updates"] = new JsonObject { ["Included"] = true, ["Until"] = "2027-04-18T00:00:00Z" },
        ["Payment"] = Pricing(),
        ["Obligations"] = new JsonObject { ["UsageReporting"] = true, ["ReportTo"] = "https://store.example/usage", ["Attestation"] = true }
    };

    // The same, issued by the MPAI Store to a Controller.
    public static JsonObject Issued()
    {
        var licence = Offer();
        licence.Remove("TermsURI");
        licence["AIMLicenceID"] = "lic-0001";
        licence["AIMLicenceTime"] = new JsonObject { ["Header"] = "OSD-TIM-V1.5", ["TimeID"] = "t", ["Data"] = "2026-10-18T10:00:00Z" };
        licence["AIM"] = new JsonObject
        {
            ["AIMName"] = "1AIH-NNM-V1.1-I02", ["Versions"] = new JsonObject { ["From"] = "1.1", ["To"] = "1.9" },
            ["Implementations"] = new JsonArray(new JsonObject { ["BinaryName"] = "1AIH-NNM-V1.1-I02.model.json", ["HashAlgorithm"] = "PTF-ALGO-HASH-SHA256", ["Hash"] = new string('A', 64) })
        };
        licence["ParentLicence"] = new JsonObject { ["ImplementerID"] = "1", ["HashAlgorithm"] = "PTF-ALGO-HASH-SHA256", ["OfferHash"] = new string('C', 64) };   // the Implementer's offer it is under
        licence["Licensor"] = new JsonObject { ["LicensorID"] = "mpai-store" };                            // the MPAI Store grants
        licence["Licensee"] = new JsonObject { ["TrustAnchorID"] = "org-anchor" };                                                  // an organisation: every Controller under its Trust Anchor
        licence["Term"] = new JsonObject { ["NotBefore"] = "2026-10-18T00:00:00Z", ["NotAfter"] = "2027-10-18T00:00:00Z" };
        // Issued: what is priced is paid - the model chosen, and the Settlement, the Transaction that proves it.
        var payment = (JsonObject)licence["Payment"]!;
        payment["SPMStatus"] = "Final"; payment["Chosen"] = new JsonObject { ["ModelType"] = "OneTime", ["Parameters"] = new JsonObject() };
        payment["Settlement"] = new JsonObject { ["TargetID"] = "1AIH-NNM-V1.1-I02", ["Transaction"] = new JsonObject
        {
            ["Header"] = "MMM-TRA-V2.2", ["MInstanceID"] = "mpai-store", ["MEnvironmentID"] = "mpai-store", ["TransactionID"] = "tx-8841",
            ["SenderData"] = new JsonObject { ["SenderID"] = "org-anchor", ["SenderWalletID"] = "w-1" }, ["ReceiverData"] = new JsonObject { ["ReceiverID"] = "mpai-store", ["ReceiverWalletID"] = "w-2" }
        } };
        licence["Status"] = new JsonObject { ["StatusURI"] = "https://store.example/MPAI/Store/Licence/lic-0001/status", ["MaxAge"] = "P7D" };
        licence["Signature"] = new string('B', 128); licence["KeyID"] = "store-key-1";
        return licence;
    }

    [Fact] public void AnOfferIsValid() => Assert.Equal("valid", Check(Offer()));

    [Fact] public void AnIssuedAimLicenceIsValid() => Assert.Equal("valid", Check(Issued()));

    [Fact]
    public void TheLeastAnOfferNeedsIsValid()
    {
        var least = new JsonObject
        {
            ["Header"] = "AIF-LIC-V3.0", ["TermsURI"] = "https://store.example/terms/free", ["Sublicensing"] = new JsonObject { ["Allowed"] = true },
            ["Grant"] = new JsonObject { ["Rights"] = new JsonArray("Run") }
        };
        Assert.Equal("valid", Check(least));                                                             // free, and it does not end
    }

    [Fact]
    public void TheLeastAnIssuedAimLicenceNeedsIsValid()
    {
        var least = Issued();
        foreach (var optional in new[] { "MInstance", "TermsURI", "Updates", "Payment", "Obligations", "DescrMetadata" }) least.Remove(optional);
        ((JsonObject)least["AIM"]!).Remove("Versions");
        ((JsonObject)least["Status"]!).Remove("MaxAge");
        least["Grant"] = new JsonObject { ["Rights"] = new JsonArray("Run") };
        least["Term"] = new JsonObject { ["NotBefore"] = "2026-10-18T00:00:00Z" };                         // it does not end
        Assert.Equal("valid", Check(least));
    }

    [Theory]
    [InlineData("Header")] [InlineData("Grant")]
    public void AnOfferWithoutWhatItRequiresIsNotValid(string property)
    {
        var copy = Offer(); copy.Remove(property);
        Assert.Equal("not valid", Check(copy));
    }

    // The offer is the Implementer's licence to the MPAI Store: it says whether the Store may sublicense.
    [Fact]
    public void AnOfferStatesWhetherTheStoreMaySublicense()
    {
        var without = Offer(); without.Remove("Sublicensing");
        Assert.Equal("not valid", Check(without));
        var vague = Offer(); vague["Sublicensing"] = new JsonObject();
        Assert.Equal("not valid", Check(vague));
        var no = Offer(); no["Sublicensing"] = new JsonObject { ["Allowed"] = false };
        Assert.Equal("valid", Check(no));                                                                // valid as an offer; the Store issues nothing under it
    }

    [Fact]
    public void TheParentLicenceNamesTheImplementerAndTheHashOfTheOffer()
    {
        var noHash = Issued(); ((JsonObject)noHash["ParentLicence"]!).Remove("OfferHash");
        Assert.Equal("not valid", Check(noHash));
        var lower = Issued(); lower["ParentLicence"]!["OfferHash"] = new string('c', 64);
        Assert.Equal("not valid", Check(lower));
    }

    [Fact]
    public void AnOfferSaysWhereItsTermsAre()
    {
        var copy = Offer(); copy.Remove("TermsURI");
        Assert.Equal("not valid", Check(copy));
    }

    [Theory]
    [InlineData("Header")] [InlineData("AIMLicenceID")] [InlineData("AIMLicenceTime")] [InlineData("AIM")] [InlineData("Licensor")] [InlineData("Licensee")] [InlineData("ParentLicence")]
    [InlineData("Grant")] [InlineData("Term")] [InlineData("Status")] [InlineData("Signature")] [InlineData("KeyID")]
    public void AnIssuedAimLicenceWithoutWhatItRequiresIsNotValid(string property)
    {
        var copy = Issued(); copy.Remove(property);
        Assert.Equal("not valid", Check(copy));
    }

    [Theory]
    [InlineData("AIMLicenceID", "lic-1")] [InlineData("Licensor", null)] [InlineData("Licensee", null)] [InlineData("ParentLicence", null)] [InlineData("Status", null)] [InlineData("KeyID", "k")] [InlineData("AIM", null)]
    public void WhatOnlyAnIssuedAimLicenceHasComesWithItsSignature(string property, string? text)
    {
        var offer = Offer();
        offer[property] = text is not null ? text : Issued()[property]!.DeepClone();
        Assert.Equal("not valid", Check(offer));                                                         // an offer that holds it, unsigned
    }

    [Fact]
    public void AnIssuedAimLicenceHasWhenItBeginsAndProvesThePayment()
    {
        var noStart = Issued(); noStart["Term"] = new JsonObject { ["NotAfter"] = "2027-10-18T00:00:00Z" };
        Assert.Equal("not valid", Check(noStart));
        var notFinal = Issued(); notFinal["Payment"]!["SPMStatus"] = "Model";
        Assert.Equal("not valid", Check(notFinal));                                                      // what was paid is Final
        var noSettlement = Issued(); ((JsonObject)noSettlement["Payment"]!).Remove("Settlement");
        Assert.Equal("not valid", Check(noSettlement));                                                  // and has its proof
        var noChoice = Issued(); ((JsonObject)noChoice["Payment"]!).Remove("Chosen");
        Assert.Equal("not valid", Check(noChoice));
        Assert.Equal("valid", Check(Offer()));                                                           // an offer has a price model, no settlement
    }

    [Theory]
    [InlineData("P1Y", true)] [InlineData("P6M", true)] [InlineData("P30D", true)] [InlineData("PT12H", true)] [InlineData("P1Y6M", true)]
    [InlineData("1 year", false)] [InlineData("P", false)] [InlineData("PT", false)] [InlineData("1Y", false)]
    public void TheDurationOfAnOfferIsAnIso8601Duration(string duration, bool valid)
    {
        var copy = Offer(); copy["Term"] = new JsonObject { ["Duration"] = duration };
        Assert.Equal(valid ? "valid" : "not valid", Check(copy));
    }

    [Fact]
    public void TheLicenseeIsTheHolderOfATrustAnchor()
    {
        var none = Issued(); none["Licensee"] = new JsonObject { ["ControllerID"] = "c-1" };
        Assert.Equal("not valid", Check(none));                                                          // a Controller alone is not a licensee
        var one = Issued(); one["Licensee"] = new JsonObject { ["TrustAnchorID"] = "owner-anchor", ["ControllerID"] = "c-1" };
        Assert.Equal("valid", Check(one));                                                               // unless the licence says that only it is covered
    }

    [Theory]
    [InlineData("P7D", true)] [InlineData("PT12H", true)] [InlineData("P1M", true)] [InlineData("a week", false)] [InlineData("P", false)]
    public void TheStatusMayGoUnconfirmedForAnIso8601Duration(string maxAge, bool valid)
    {
        var copy = Issued(); copy["Status"]!["MaxAge"] = maxAge;
        Assert.Equal(valid ? "valid" : "not valid", Check(copy));
    }

    [Fact]
    public void ALicenceThatDoesNotGrantTheRightToRunIsNotValid()
    {
        var copy = Offer(); copy["Grant"]!["Rights"] = new JsonArray("Keep");
        Assert.Equal("not valid", Check(copy));
        copy["Grant"]!["Rights"] = new JsonArray("Run", "Sell");
        Assert.Equal("not valid", Check(copy));
    }

    [Fact]
    public void WhatAnIssuedAimLicenceCoversIsNamedByFingerprint()
    {
        var copy = Issued(); ((JsonArray)copy["AIM"]!["Implementations"]!).Clear();
        Assert.Equal("not valid", Check(copy));                                                          // at least one file
        var bad = Issued(); bad["AIM"]!["Implementations"]![0]!["Hash"] = "not hexadecimal";
        Assert.Equal("not valid", Check(bad));
    }

    [Fact]
    public void ThePriceIsAnMmmServicePricingModel()
    {
        var noCurrency = Offer(); ((JsonObject)noCurrency["Payment"]!).Remove("CurrencyObject");
        Assert.Equal("not valid", Check(noCurrency));                                                    // a price has a currency
        var noModel = Offer(); ((JsonObject)noModel["Payment"]!).Remove("OneTime");
        Assert.Equal("not valid", Check(noModel));                                                       // the model type says what it needs
        var unknown = Offer(); unknown["Payment"]!["ModelType"] = "Barter";
        Assert.Equal("not valid", Check(unknown));
        var wrongPrice = Offer(); wrongPrice["Payment"]!["OneTime"]!["Price"] = "ten";
        Assert.Equal("not valid", Check(wrongPrice));
        // Another pricing model of the MMM: pay per use, a rate for each request.
        var perUse = Offer(); ((JsonObject)perUse["Payment"]!).Remove("OneTime"); perUse["Payment"]!["ModelType"] = "PayPerUse";
        perUse["Payment"]!["PayPerUse"] = new JsonObject { ["Meter"] = "requests", ["Unit"] = "request", ["Rate"] = 0.002 };
        Assert.Equal("valid", Check(perUse));
    }

    [Fact]
    public void UsageIsReportedToSomewhere()
    {
        var copy = Offer(); ((JsonObject)copy["Obligations"]!).Remove("ReportTo");
        Assert.Equal("not valid", Check(copy));
    }

    [Fact]
    public void ALimitIsAtLeastOne()
    {
        var copy = Offer(); copy["Grant"]!["MaxControllers"] = 0;
        Assert.Equal("not valid", Check(copy));
    }

    [Fact]
    public void NothingElseIsAllowed()
    {
        var copy = Offer(); copy["Price"] = 10;
        Assert.Equal("not valid", Check(copy));
    }
}
