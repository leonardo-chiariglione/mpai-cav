using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AIF.Trust;

namespace AIF.Store;

// WHO SUBMITS (STR-4). An Implementer is a member of the trusted MPAI ecosystem: it holds a key pair of its own; the MPAI Store, the Trust Authority for Implementers, issues it a
// credential (MPAI-PTF Instance Credential) once MPAI has approved its registration; and each L3 it submits is signed by its key. The Store checks the credential, that it is for the
// Implementer ID in the L3, and the signature. The L3 itself is not changed: the signature is over its canonical form (PTF-JSON-CANON-V1) and travels beside it.
public sealed class ImplementerKey
{
    private readonly ECDsa key;

    private ImplementerKey(ECDsa key) => this.key = key;

    // The key kept in a file of the Implementer's, made the first time.
    public static ImplementerKey LoadOrCreate(string file)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(file)) key.ImportPkcs8PrivateKey(Convert.FromHexString(File.ReadAllText(file).Trim()), out _);
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            File.WriteAllText(file, Convert.ToHexString(key.ExportPkcs8PrivateKey()));
        }
        return new ImplementerKey(key);
    }

    public static ImplementerKey Create() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    // The Cryptographic Instance Identity the Implementer gives the Store with its registration: its public key, proving it holds the private one.
    public JsonObject Cii(string implementerId, DateTimeOffset now) => PtfIdentity.Make(key, "implementer:" + implementerId, null, now);

    // The signature of an L3, hexadecimal: over its canonical form, so that whitespace and the order of members do not matter.
    public string Sign(JsonObject l3) => Convert.ToHexString(key.SignData(PtfCanonical.Bytes(l3), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    public static bool Verify(JsonObject l3, string signatureHex, ECDsa publicKey)
    {
        try
        {
            return publicKey.VerifyData(PtfCanonical.Bytes(l3), Convert.FromHexString(signatureHex), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception e) when (e is FormatException or CryptographicException) { return false; }
    }
}
