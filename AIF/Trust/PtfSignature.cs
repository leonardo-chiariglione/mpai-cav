using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace AIF.Trust;

// THE SIGNATURE OF A PTF OBJECT (MPAI-PTF V1.0, Data Conventions 2 and 3). A signed
// object carries a member Signature { Algorithm, Value }, as the PTF schemas define
// it; the signature is computed over the canonical form of the object without that
// member, and verified the same way, so that a change to anything else in the object,
// at any depth, is detected, and a change of member order or whitespace is not.
//
// Choices where PTF does not say (reported, M3223 3.7):
//   - the algorithm: PTF-ALGO-SIG-ECDSA-P256-SHA256, from the Security Algorithm
//     Taxonomy;
//   - the encoding of an ECDSA signature: r and s, 32 bytes each, concatenated
//     (IEEE P1363), not DER;
//   - the text of Value: hexadecimal, upper case, as the Signature Conventions say;
//     the Binary Data rule of the same chapter says Base64URL.
public static class PtfSignature
{
    public const string Member = "Signature";
    public const string EcdsaP256 = "PTF-ALGO-SIG-ECDSA-P256-SHA256";

    public enum Outcome { Valid, Missing, UnknownAlgorithm, Malformed, Invalid }

    // Signs the object in place: any Signature it had is replaced.
    public static JsonObject Sign(JsonObject obj, ECDsa key)
    {
        obj.Remove(Member);
        var value = key.SignData(PtfCanonical.Bytes(obj), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        obj[Member] = new JsonObject { ["Algorithm"] = EcdsaP256, ["Value"] = Convert.ToHexString(value) };
        return obj;
    }

    public static Outcome Verify(JsonObject obj, ECDsa publicKey)
    {
        if (obj[Member] is not JsonObject signature) return Outcome.Missing;
        if ((string?)signature["Algorithm"] != EcdsaP256) return Outcome.UnknownAlgorithm;
        byte[] value;
        try { value = Convert.FromHexString((string?)signature["Value"] ?? ""); }
        catch (FormatException) { return Outcome.Malformed; }
        if (value.Length != 64) return Outcome.Malformed;

        var unsigned = obj.DeepClone().AsObject();
        unsigned.Remove(Member);
        return publicKey.VerifyData(PtfCanonical.Bytes(unsigned), value, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
            ? Outcome.Valid
            : Outcome.Invalid;
    }
}
