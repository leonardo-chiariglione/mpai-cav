using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace AIF.Trust;

// THE SIGNATURE OF A PTF OBJECT (MPAI-PTF V1.0, Data Conventions 2 and 3). As the
// Signature Conventions say: a signed object carries its Signature - the signature
// value, hexadecimal ASCII - and the KeyID of the key that verifies it. The
// signature is computed over the canonical form of the object without its Signature
// (the KeyID included), and verified the same way, so that a change to anything
// else in the object, at any depth, is detected, and a change of member order or
// whitespace is not. The algorithm is the key's: the identity of a key states it.
//
// Choices where PTF does not say (M3223 3.7, agreed by the author):
//   - an ECDSA signature is r and s, 32 bytes each, concatenated (IEEE P1363);
//   - hexadecimal in upper case.
public static class PtfSignature
{
    public const string Member = "Signature";
    public const string KeyMember = "KeyID";

    public enum Outcome { Valid, Missing, UnknownKey, Malformed, Invalid }

    // Signs the object in place: any Signature it had is replaced; the KeyID is set.
    public static JsonObject Sign(JsonObject obj, ECDsa key, string keyId)
    {
        obj.Remove(Member);
        obj[KeyMember] = keyId;
        var value = key.SignData(PtfCanonical.Bytes(obj), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        obj[Member] = Convert.ToHexString(value);
        return obj;
    }

    // keyFor: the public key a KeyID names, or null where the verifier knows none.
    public static Outcome Verify(JsonObject obj, Func<string, ECDsa?> keyFor)
    {
        if ((string?)obj[Member] is not { } hex) return Outcome.Missing;
        if ((string?)obj[KeyMember] is not { } keyId || keyFor(keyId) is not { } key) return Outcome.UnknownKey;
        byte[] value;
        try { value = Convert.FromHexString(hex); }
        catch (FormatException) { return Outcome.Malformed; }
        if (value.Length != 64) return Outcome.Malformed;

        var unsigned = obj.DeepClone().AsObject();
        unsigned.Remove(Member);
        return key.VerifyData(PtfCanonical.Bytes(unsigned), value, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
            ? Outcome.Valid
            : Outcome.Invalid;
    }
}
