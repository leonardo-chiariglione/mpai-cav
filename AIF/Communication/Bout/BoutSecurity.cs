using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AIF.Trust;

namespace AIF.Bout;

// HOW A BOUT AND ITS INSTANCES ARE PROTECTED (MPAI-PTF V1.0, Verification Pipeline 11). Each carries its Data Exchange Metadata, whose Security holds the
// Binding <Data Type>#<Port Number>@<stamp of its writer>/<BoutID>, the KeyID, and the Signature where it is signed or the Hash where it is not.
//   H(x)       the SHA-256 of the canonical form (PTF-JSON-CANON-V1) of x without Integrity.Signature, Integrity.Hash and AuditChainHash;
//   Signature  ECDSA P-256 over the canonical form of x without its Signature (the KeyID included), IEEE P1363, Hexadecimal ASCII;
//   chain      A(0) = SHA-256 of the canonical form of the Bout without its Signature; A(k) = SHA-256(A(k-1) || H(x(k))).
public static class BoutSecurity
{
    public const string BoutHeader = "AIF-BOU-V3.0";
    public const string DemHeader = "PTF-DEM-V1.0";
    public const string SecurityHeader = "PTF-SEC-V1.0";
    public const string HashAlgorithm = "PTF-ALGO-HASH-SHA256";
    public const string EachInstance = "EachInstance";
    public const string Chain = "Chain";

    public static JsonObject? Security(JsonObject x) => x["DataXMData"]?["Security"] as JsonObject;
    public static JsonObject? Integrity(JsonObject x) => Security(x)?["Integrity"] as JsonObject;

    public static byte[] H(JsonObject x)
    {
        var c = x.DeepClone().AsObject();
        if (Integrity(c) is { } i) { i.Remove("Signature"); i.Remove("Hash"); }
        Security(c)?.Remove("AuditChainHash");
        return SHA256.HashData(PtfCanonical.Bytes(c));
    }

    public static byte[] SignedBytes(JsonObject x)
    {
        var c = x.DeepClone().AsObject();
        Integrity(c)?.Remove("Signature");
        return PtfCanonical.Bytes(c);
    }

    public static void Sign(JsonObject x, BoutKey key) => Integrity(x)!["Signature"] = Convert.ToHexString(key.Sign(SignedBytes(x)));

    public static bool Verify(JsonObject x, BoutKey key)
    {
        if ((string?)Integrity(x)?["Signature"] is not { } hex) return false;
        byte[] value;
        try { value = Convert.FromHexString(hex); } catch (FormatException) { return false; }
        return key.Verify(SignedBytes(x), value);
    }

    // The same, for a key that answers asynchronously (the browser's Web Crypto).
    public static async ValueTask SignAsync(JsonObject x, BoutKey key) => Integrity(x)!["Signature"] = Convert.ToHexString(await key.SignAsync(SignedBytes(x)));

    public static async ValueTask<bool> VerifyAsync(JsonObject x, BoutKey key)
    {
        if ((string?)Integrity(x)?["Signature"] is not { } hex) return false;
        byte[] value;
        try { value = Convert.FromHexString(hex); } catch (FormatException) { return false; }
        return await key.VerifyAsync(SignedBytes(x), value);
    }

    public static async ValueTask<JsonObject> SignTopAsync(JsonObject obj, BoutKey key, string keyId)
    {
        obj.Remove("Signature");
        obj["KeyID"] = keyId;
        obj["Signature"] = Convert.ToHexString(await key.SignAsync(PtfCanonical.Bytes(obj)));
        return obj;
    }

    public static async ValueTask<bool> VerifyTopAsync(JsonObject obj, BoutKey key)
    {
        if ((string?)obj["Signature"] is not { } hex) return false;
        byte[] value;
        try { value = Convert.FromHexString(hex); } catch (FormatException) { return false; }
        var unsigned = obj.DeepClone().AsObject();
        unsigned.Remove("Signature");
        return await key.VerifyAsync(PtfCanonical.Bytes(unsigned), value);
    }

    // A PTF object signed at its top: the Signature over its canonical form without the Signature, the KeyID included (MPAI-PTF Data Conventions 3).
    public static JsonObject SignTop(JsonObject obj, BoutKey key, string keyId)
    {
        obj.Remove("Signature");
        obj["KeyID"] = keyId;
        obj["Signature"] = Convert.ToHexString(key.Sign(PtfCanonical.Bytes(obj)));
        return obj;
    }

    public static bool VerifyTop(JsonObject obj, BoutKey key)
    {
        if ((string?)obj["Signature"] is not { } hex) return false;
        byte[] value;
        try { value = Convert.FromHexString(hex); } catch (FormatException) { return false; }
        var unsigned = obj.DeepClone().AsObject();
        unsigned.Remove("Signature");
        return key.Verify(PtfCanonical.Bytes(unsigned), value);
    }

    public static string Stamp(DateTimeOffset t) => t.ToString("o", CultureInfo.InvariantCulture);

    public static string Binding(string dataType, int portNumber, DateTimeOffset stamp, int boutId) =>
        $"{dataType}#{portNumber.ToString(CultureInfo.InvariantCulture)}@{Stamp(stamp)}/{boutId.ToString(CultureInfo.InvariantCulture)}";

    // <Data Type>#<Port Number>@<stamp>/<BoutID>, or null where it is not of that form.
    public static (string DataType, int PortNumber, DateTimeOffset Stamp, int BoutId)? ParseBinding(string? binding)
    {
        if (binding is null) return null;
        var hash = binding.IndexOf('#'); var at = binding.IndexOf('@'); var slash = binding.LastIndexOf('/');
        if (hash <= 0 || at < hash || slash < at) return null;
        if (!int.TryParse(binding.AsSpan(hash + 1, at - hash - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port < 1) return null;
        if (!DateTimeOffset.TryParse(binding.AsSpan(at + 1, slash - at - 1), CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp)) return null;
        if (!int.TryParse(binding.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var bout)) return null;
        return (binding[..hash], port, stamp, bout);
    }

    public static string? BindingOf(JsonObject x) => (string?)Security(x)?["Transmission"]?["Binding"];
    public static string? KeyIdOf(JsonObject x) => (string?)Integrity(x)?["KeyID"];
    public static long? SequenceOf(JsonObject x) => AsLong(Security(x)?["Freshness"]?["Sequence"]);

    // An integer, whether the value was parsed from text or made in code (an int, a long).
    public static long? AsLong(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number &&
        long.TryParse(v.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : null;
}
