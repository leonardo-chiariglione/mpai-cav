using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AIF.Store;
using AIF.Trust;

namespace Mpai.StoreService;

// THE STORE AS A TRUST AUTHORITY FOR IMPLEMENTERS. Its key is made the first time and kept (store-key.hex); with it the Store issues the credential of an approved Implementer and
// checks it again when that Implementer submits. The admin key is the Store operator's (MPAI): it approves registrations, and MPAI's own Implementer ID 1 submits with it.
public sealed class StoreIdentity
{
    public const string KeyId = "mpai-store-1";
    public ECDsa Key { get; }
    public PtfIssuer Issuer { get; }

    private StoreIdentity(ECDsa key) { Key = key; Issuer = new PtfIssuer("MPAI Store", key, KeyId); }

    public static StoreIdentity Load(string root)
    {
        var file = Path.Combine(root, "store-key.hex");
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(file)) key.ImportPkcs8PrivateKey(Convert.FromHexString(File.ReadAllText(file).Trim()), out _);
        else File.WriteAllText(file, Convert.ToHexString(key.ExportPkcs8PrivateKey()));
        return new StoreIdentity(key);
    }

    public static string AdminKey(string root)
    {
        var file = Path.Combine(root, "admin-key.txt");
        if (!File.Exists(file)) File.WriteAllText(file, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
        return File.ReadAllText(file).Trim();
    }

    public static bool IsAdmin(string given, string adminKey) =>
        given.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(adminKey));

    // What is wrong with a submission by this Implementer, none when it is good: the Implementer approved, its credential issued by this Store and current, for the identity it gave
    // and for this Implementer ID, and the L3 signed by the key of that identity.
    public static string? CheckSubmitter(ImplementerRegistry.Implementer who, string signature, JsonObject l3, StoreIdentity store)
    {
        if (who.Status != ImplementerRegistry.Approved || who.Cii is null || who.Credential is null)
            return $"Not approved: Implementer ID {who.Id} is {who.Status}; MPAI has not yet approved the registration.";
        if (signature.Length == 0) return "The submission is not signed (header MPAI-Signature: the signature of the L3 with your key).";
        var credential = PtfCredential.CheckCredential(who.Credential, who.Cii, id => id == KeyId ? store.Key : null, DateTimeOffset.UtcNow);
        if (credential != PtfCredential.Outcome.Valid) return $"The credential of Implementer {who.Id} is not good ({credential}).";
        if ((string?)who.Credential["Subject"]?["InstanceID"] != who.Id) return "The credential is not for this Implementer ID.";
        var key = PtfIdentity.PublicKey(who.Cii);
        if (key is null || !ImplementerKey.Verify(l3, signature, key)) return "The signature is not that of the key of this Implementer.";
        return null;
    }
}
