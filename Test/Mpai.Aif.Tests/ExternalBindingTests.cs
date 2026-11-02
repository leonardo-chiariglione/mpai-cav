using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AIF.Channels;
using AIF.Trust;

namespace Mpai.Aif.Tests;

// Communication 9 and 10: a Message from another Controller carries that Controller's identifier and stamp and is signed by it - the Port it is for and the stamp are in what is signed, so
// that neither is changed on the way (a Message cannot be given to another Port, or made to look fresh).
public class ExternalBindingTests
{
    private const string Message = """{"Header":"TST-TXT-V1.0","ObjectID":"o1","Entries":[{"Data":"AQID"}]}""";

    private static (TrustAnchorKey Anchor, ECDsa Key) Anchor(string id)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var t = DateTimeOffset.UtcNow;
        return (new TrustAnchorKey(id, key, t.AddMinutes(-5), t.AddDays(30)), key);
    }

    [Fact]
    public void ASignedMessageIsBoundToItsPortAndItsStamp()
    {
        var (anchor, key) = Anchor("CAV-A");
        var stamp = "2026-06-01T12:00:00.0000000+00:00";
        var binding = ExternalHub.BindingOf("TST-TXT-V1.0", 1, stamp);
        var signed = ExternalSigning.Sign(Message, anchor, key, "City", DateTimeOffset.UtcNow, binding: binding);
        JsonObject? Anchors(string id) => id == "CAV-A" ? anchor.Object() : null;

        Assert.Equal(ExternalSigning.Outcome.Valid, ExternalSigning.Verify(signed, "CAV-A", Anchors, binding));
        Assert.Equal(ExternalSigning.Outcome.NotBound, ExternalSigning.Verify(signed, "CAV-A", Anchors, ExternalHub.BindingOf("TST-TXT-V1.0", 2, stamp)));                       // another Port of the same type
        Assert.Equal(ExternalSigning.Outcome.NotBound, ExternalSigning.Verify(signed, "CAV-A", Anchors, ExternalHub.BindingOf("TST-OTH-V1.0", 1, stamp)));                       // another Data Type
        Assert.Equal(ExternalSigning.Outcome.NotBound, ExternalSigning.Verify(signed, "CAV-A", Anchors, ExternalHub.BindingOf("TST-TXT-V1.0", 1, "2026-06-01T12:04:00.0000000+00:00")));   // a stamp made later
    }

    [Fact]
    public void TheBindingCannotBeStrippedOrChangedInsideTheMessage()
    {
        var (anchor, key) = Anchor("CAV-A");
        var binding = ExternalHub.BindingOf("TST-TXT-V1.0", 1, "2026-06-01T12:00:00.0000000+00:00");
        var signed = ExternalSigning.Sign(Message, anchor, key, "City", DateTimeOffset.UtcNow, binding: binding);
        JsonObject? Anchors(string id) => id == "CAV-A" ? anchor.Object() : null;

        var other = ExternalHub.BindingOf("TST-TXT-V1.0", 2, "2026-06-01T12:00:00.0000000+00:00");
        var tampered = JsonNode.Parse(signed)!.AsObject();
        tampered["DataXMData"]!["Security"]!["Transmission"]!["Binding"] = other;                                                      // the binding rewritten to match another Port
        var changed = tampered.ToJsonString();
        Assert.Equal(ExternalSigning.Outcome.Invalid, ExternalSigning.Verify(changed, "CAV-A", Anchors, other));                       // the signature no longer holds
        var unbound = ExternalSigning.Sign(Message, anchor, key, "City", DateTimeOffset.UtcNow);                                       // signed without a binding
        Assert.Equal(ExternalSigning.Outcome.NotBound, ExternalSigning.Verify(unbound, "CAV-A", Anchors, binding));
    }

    private static bool Valid(string schema, JsonNode node)
    {
        var all = AIF.Metadata.PublishedSchemas.At(Repository.Schemas);
        var compiled = all[Path.GetFullPath(Path.Combine(Repository.Schemas, schema))];
        using var doc = System.Text.Json.JsonDocument.Parse(node.ToJsonString());
        lock (AIF.Metadata.PublishedSchemas.Lock) return compiled.Evaluate(doc.RootElement).IsValid;
    }

    // MPAI-PTF: what the Controller signs and records is valid against the PTF Data Types - the Security of a Message (Transmission.Binding, Integrity.Payloads) and the Trust Operations of its
    // decisions (the operation and target types of the External links).
    [Fact]
    public void WhatIsSignedAndRecordedIsValidAgainstThePtfSchemas()
    {
        var (anchor, key) = Anchor("CAV-A");
        var hash = new string('A', 64);
        var signed = JsonNode.Parse(ExternalSigning.Sign(Message, anchor, key, "City", DateTimeOffset.UtcNow, [("aif:payload/x", 3, hash)], ExternalHub.BindingOf("TST-TXT-V1.0", 1, "2026-06-01T12:00:00.0000000+00:00")))!;
        Assert.True(Valid("PTF/V1.0/data/DataExchangeMetadata.json", signed["DataXMData"]!.DeepClone()), "the signed Message's DataXMData");

        var operations = new TrustOperations(anchor, key);
        foreach (var (type, target) in new[] { ("VerifyCredential", "InstanceCredential"), ("VerifyMessage", "ExternalMessage"), ("ServePayload", "Payload"), ("VerifyPayload", "Payload"),
                                               ("AcceptLink", "ExternalLink"), ("CloseLink", "ExternalLink"), ("AdmitController", "Controller"), ("RefuseController", "Controller") })
            Assert.True(Valid("PTF/V1.0/data/TrustOperation.json", operations.Operation(type, target, "id-1", type == "ServePayload" ? "not for it" : null)), $"{type} on {target}");
    }
}
