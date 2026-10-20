using System.Text.Json.Nodes;

namespace Mpai.Aif.Tests;

// THE AIMLicence FIELD OF AIM METADATA (L1, AIMMetadata.json): optional, at the top level, and the AIM Licence Data Type (AIF-LIC) as the Implementer's OFFER - what the
// MPAI Store publishes once, for everybody: it has no Licensee and no Signature.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class AimLicenceFieldTests
{
    private static readonly AIF.Metadata.AimMetadataSchema Schema = AIF.Metadata.AimMetadataSchema.Load(Repository.Schemas);

    private static IReadOnlyList<string> Check(JsonNode? licence, bool present = true)
    {
        var l3 = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Amds, "1AIH-ARA-V1.1-I01.json")))!.AsObject();
        if (present) l3["AIMLicence"] = licence;
        return Schema.Violations(l3.ToJsonString());
    }

    [Fact] public void ItIsOptionalAndOmittedMeansTheAimNeedsNoLicence() => Assert.Empty(Check(null, present: false));

    [Fact] public void AnOfferIsAccepted() => Assert.Empty(Check(AimLicenceSchemaTests.Offer()));

    [Fact]
    public void AFreeOfferIsAccepted()
    {
        var free = AimLicenceSchemaTests.Offer(); free.Remove("Payment");
        Assert.Empty(Check(free));
    }

    [Fact]
    public void AnOfferSaysWhereItsTermsAre()
    {
        var offer = AimLicenceSchemaTests.Offer(); offer.Remove("TermsURI");
        Assert.NotEmpty(Check(offer));
    }

    // The Metadata is published once, for everybody, and never overwritten: it holds no licence of anyone's.
    [Fact] public void AnAimLicenceIssuedToAControllerIsNotMetadata() => Assert.NotEmpty(Check(AimLicenceSchemaTests.Issued()));

    [Fact]
    public void AnOfferThatNamesALicenseeOrIsSignedIsRefused()
    {
        var named = AimLicenceSchemaTests.Offer(); named["Licensee"] = new JsonObject { ["ControllerID"] = "c" };
        Assert.NotEmpty(Check(named));
        var signed = AimLicenceSchemaTests.Offer(); signed["Signature"] = new string('B', 128);
        Assert.NotEmpty(Check(signed));
    }

    [Fact] public void ItIsTheAimLicenceDataTypeNotAnotherShape() => Assert.NotEmpty(Check(new JsonObject { ["Type"] = "Free", ["TermsURI"] = "https://store.example/t", ["LicensorID"] = "x" }));
}
