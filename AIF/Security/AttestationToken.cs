using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Security.Cryptography.Cose;
using System.Text;
using System.Text.Json.Nodes;

using AIF.Trust;

namespace AIF.Security;

// THE ENTITY ATTESTATION TOKEN of MPAI_AIFAT_Get_Token (Security API 5.1): CBOR claims (EAT) in a COSE_Sign1 signed by the identity key the root of trust holds
// (ES256 for the P-256 identity key of the simulated TPM). The claims:
//   10  eat_nonce   the challenge of the Verifier (32, 48 or 64 bytes)
//   256 ueid        0x01 (random type) and the SHA-256 of the identity key's SubjectPublicKeyInfo: the same entity, the same UEID
//   6   iat         when the token was made (seconds since 1970)
//   "mpai-party"    the party's name - its Trust Anchor ID - with which the Evidence is signed
//   "mpai-ptf-evidence"  the Attestation Evidence of MPAI-PTF (the quote of the measurement registers over the same challenge, the certificate of the attestation
//                   key, the log of what was measured), as the root of trust gives it, in its JSON form
// The token over the challenge of a Verifier is the Attestation Evidence of MPAI-PTF (5.1): Verify checks the COSE signature with the party's identity key, the
// challenge, the UEID, and the Evidence with Attestation.Check - the quote over the same challenge under a key its manufacturer certified, and every measurement.
public static class AttestationToken
{
    public const int NonceClaim = 10, UeidClaim = 256, IatClaim = 6;
    public const string PartyClaim = "mpai-party", EvidenceClaim = "mpai-ptf-evidence";

    // What a token may grow by between Get_Token_Size and Get_Token (the time claim and the encoding of the Evidence identifier).
    public const int Margin = 64;

    public static byte[] Ueid(ECDsa identity) => [0x01, .. SHA256.HashData(identity.ExportSubjectPublicKeyInfo())];

    public static byte[] Make(ISecureRoot root, byte[] challenge)
    {
        var evidence = root.Evidence(root.Name, challenge);
        var claims = new CborWriter(CborConformanceMode.Canonical);
        claims.WriteStartMap(5);
        claims.WriteInt32(IatClaim); claims.WriteInt64(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        claims.WriteInt32(NonceClaim); claims.WriteByteString(challenge);
        claims.WriteInt32(UeidClaim); claims.WriteByteString(Ueid(root.IdentityKey));
        claims.WriteTextString(PartyClaim); claims.WriteTextString(root.Name);
        claims.WriteTextString(EvidenceClaim); claims.WriteTextString(evidence.ToJsonString());
        claims.WriteEndMap();
        var signer = new CoseSigner(root.IdentityKey, HashAlgorithmName.SHA256);
        return CoseSign1Message.SignEmbedded(claims.Encode(), signer);
    }

    // null where the token proves what it says - made by the root of trust of the party whose identity key is given, over this challenge, with Evidence the
    // policy approves; otherwise why not.
    public static string? Verify(byte[] token, ECDsa identity, byte[] challenge, Attestation.Policy policy, DateTimeOffset now, TimeSpan maxAge)
    {
        CoseSign1Message message;
        try { message = CoseMessage.DecodeSign1(token); }
        catch (CryptographicException) { return "the token is not a COSE_Sign1"; }
        if (message.Content is not { } content) return "the token has no claims";
        try { if (!message.VerifyEmbedded(identity)) return "the token is not signed by the identity key of the party"; }
        catch (CryptographicException) { return "the token is not signed by the identity key of the party"; }

        byte[]? nonce = null, ueid = null;
        long? iat = null;
        string? party = null, evidence = null;
        try
        {
            var reader = new CborReader(content, CborConformanceMode.Canonical);
            var count = reader.ReadStartMap();
            for (var i = 0; count is null || i < count; i++)
            {
                if (count is null && reader.PeekState() == CborReaderState.EndMap) break;
                if (reader.PeekState() == CborReaderState.TextString)
                {
                    var name = reader.ReadTextString();
                    if (name == PartyClaim) party = reader.ReadTextString();
                    else if (name == EvidenceClaim) evidence = reader.ReadTextString();
                    else reader.SkipValue();
                }
                else
                {
                    var label = reader.ReadInt64();
                    if (label == NonceClaim) nonce = reader.ReadByteString();
                    else if (label == UeidClaim) ueid = reader.ReadByteString();
                    else if (label == IatClaim) iat = reader.ReadInt64();
                    else reader.SkipValue();
                }
            }
            reader.ReadEndMap();
        }
        catch (Exception e) when (e is CborContentException or InvalidOperationException) { return "the claims of the token are not well formed"; }

        if (nonce is null || !CryptographicOperations.FixedTimeEquals(nonce, challenge)) return "the token is not over the challenge of the Verifier";
        if (ueid is null || !ueid.AsSpan().SequenceEqual(Ueid(identity))) return "the token is not of the entity whose identity key is given";
        if (iat is null || now - DateTimeOffset.FromUnixTimeSeconds(iat.Value) > maxAge || DateTimeOffset.FromUnixTimeSeconds(iat.Value) - now > TimeSpan.FromMinutes(5))
            return "the token is not fresh";
        if (party is null || evidence is null) return "the token carries no Attestation Evidence";
        JsonObject parsed;
        try { parsed = JsonNode.Parse(evidence)!.AsObject(); }
        catch (Exception) { return "the Attestation Evidence of the token is not well formed"; }
        return Attestation.Check(parsed, identity, party, challenge, policy, now);
    }

    public static string Describe(byte[] token) => Convert.ToHexString(token.AsSpan(0, Math.Min(16, token.Length))) + "... (" + token.Length + " bytes)";

    internal static string Text(byte[] b) => Encoding.UTF8.GetString(b);
}
