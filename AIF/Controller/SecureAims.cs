using System.Text;

using AIF.Security;
using AIF.SharedStorage;

namespace AIF.Controller;

// THE SECURITY API FOR THE AIMs OF A PARTY IN THE SECURE PROFILE. The party where AIMs execute - the Secure Controller, or an AIM host with a root of trust -
// gives its providers its SecurityApi; a provider gives each AIM a handle whose Owner is that AIM (Security API, Table 4 and 4), so that one AIM never uses
// the keys or the Secure Storage areas of another. An L3 that declares APIProfile Secure is built only where a provider has the Security API, opened.
public interface ISecurityAware
{
    SecurityApi? Security { get; set; }
}

// STORAGE SEALED WITH THE SECURITY API. Wraps a Ruled Storage (the Private Storage of a Module, the Shared Storage): every datum is written encrypted and
// authenticated (AES-256-GCM, a fresh 12-byte nonce from the root of trust, the holder and the key of the datum as associated data) under a persistent key of
// the AIM, generated once and never exportable, whose identifier it keeps in a Secure Storage area of the AIM. The storage, and whoever reaches its files,
// sees only ciphertext; a datum altered, or moved to another key, does not open (NotFound, and Failures counts it). The rules of the storage - category,
// readers, time - are kept as they are: they are given to the storage beneath.
public sealed class SealedRuledStorage : IRuledStorage
{
    private const string KeyArea = "sealed-storage-key";
    private readonly IRuledStorage inner;
    private readonly SecurityCaller security;
    private readonly uint key;

    public SealedRuledStorage(IRuledStorage inner, SecurityCaller security)
    {
        this.inner = inner;
        this.security = security;
        key = KeyOf(security, KeyArea, new KeyAttributes(KeyType.Aes, 256, KeyLifetime.Persistent, Algorithm.Gcm, KeyUsage.Encrypt | KeyUsage.Decrypt));
    }

    // How many data did not open (altered, or not sealed by this AIM).
    public int Failures { get; private set; }

    public string Holder => inner.Holder;

    // A persistent key of the caller: the one whose identifier is in its area, else a new one, its identifier kept there (write once).
    public static uint KeyOf(SecurityCaller security, string area, KeyAttributes attributes)
    {
        var buffer = new byte[4];
        if (security.MPAI_AIFSS_Storage_Read(area, buffer, out var n) == AifError.OK && n == 4) return BitConverter.ToUInt32(buffer);
        Check(security.MPAI_AIFKM_Generate_Key(attributes, out var made), "generate its key");
        Check(security.MPAI_AIFSS_Storage_Init(area, BitConverter.GetBytes(made), StorageFlags.WriteOnce), "keep the identifier of its key");
        return made;
    }

    private static void Check(AifError e, string what)
    {
        if (e != AifError.OK) throw new InvalidOperationException($"The Security API did not {what}: {e}.");
    }

    private byte[] Associated(string k) => Encoding.UTF8.GetBytes(inner.Holder + "\n" + k);

    public StorageOutcome MPAI_AIFM_RuledStorage_Put(string k, byte[] data, string category, IReadOnlyCollection<string>? readers = null, StorageTime? time = null)
    {
        var nonce = new byte[12];
        Check(security.MPAI_AIFRNG_Generate_Random(nonce), "give random bytes");
        var sealedData = new byte[data.Length + 16];
        Check(security.MPAI_AIFAEAD_Encrypt(new AeadState(), key, Algorithm.Gcm, nonce, Associated(k), data, sealedData, out _), "seal a datum");
        return inner.MPAI_AIFM_RuledStorage_Put(k, [.. nonce, .. sealedData], category, readers, time);
    }

    public StorageOutcome MPAI_AIFM_RuledStorage_Get(string k, out byte[] data)
    {
        data = [];
        var outcome = inner.MPAI_AIFM_RuledStorage_Get(k, out var stored);
        if (outcome != StorageOutcome.OK) return outcome;
        if (stored.Length < 28) { Failures++; return StorageOutcome.NotFound; }
        var plain = new byte[stored.Length - 28];
        if (security.MPAI_AIFAEAD_Decrypt(new AeadState(), key, Algorithm.Gcm, stored.AsSpan(0, 12), Associated(k), stored.AsSpan(12), plain, out var n) != AifError.OK)
        {
            Failures++;
            return StorageOutcome.NotFound;
        }
        data = plain[..n];
        return StorageOutcome.OK;
    }

    public StorageOutcome MPAI_AIFM_RuledStorage_Delete(string k) => inner.MPAI_AIFM_RuledStorage_Delete(k);
    public IReadOnlyList<string> MPAI_AIFM_RuledStorage_List(string? category = null, string prefix = "") => inner.MPAI_AIFM_RuledStorage_List(category, prefix);
    public bool MPAI_AIFM_RuledStorage_Exists(string k) => inner.MPAI_AIFM_RuledStorage_Exists(k);
    public StorageOutcome MPAI_AIFM_RuledStorage_Trace(string k, out StorageTrace? trace) => inner.MPAI_AIFM_RuledStorage_Trace(k, out trace);
    public StorageOutcome MPAI_AIFM_RuledStorage_SetRule(string category, StorageRule rule) => inner.MPAI_AIFM_RuledStorage_SetRule(category, rule);
}
