using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Mpai.StoreService;

// THE TRUST ANCHORS THE STORE RECOGNISES (by AnchorID): the keys with which it checks that the caller of its licence service is the holder of the Trust Anchor it asks the AIM Licence for.
// The Store's operator registers them (the admin key); they are kept in <Root>/trust-anchors.json. An AIM Licence is never issued to a Trust Anchor the Store does not know.
public sealed class TrustAnchorRegistry
{
    private readonly string file;
    private readonly Dictionary<string, string> anchors = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public TrustAnchorRegistry(string root)
    {
        file = Path.Combine(root, "trust-anchors.json");
        if (File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonObject saved)
            foreach (var (id, key) in saved) if ((string?)key is { } hex) anchors[id] = hex;
    }

    // Records the public key (SubjectPublicKeyInfo, hexadecimal) of a Trust Anchor; null when it is good, else what is wrong.
    public string? Register(string anchorId, string publicKeyHex)
    {
        if (anchorId.Length == 0) return "An AnchorID is needed.";
        try { using var key = ECDsa.Create(); key.ImportSubjectPublicKeyInfo(Convert.FromHexString(publicKeyHex), out _); }
        catch (Exception e) when (e is FormatException or CryptographicException) { return "The PublicKey is not a hexadecimal SubjectPublicKeyInfo of an ECDSA key."; }
        lock (gate)
        {
            anchors[anchorId] = publicKeyHex.ToUpperInvariant();
            var saved = new JsonObject();
            foreach (var (id, key) in anchors) saved[id] = key;
            File.WriteAllText(file, saved.ToJsonString());
        }
        return null;
    }

    // The public key of a Trust Anchor, or null where the Store does not recognise it.
    public ECDsa? Key(string anchorId)
    {
        string? hex;
        lock (gate) hex = anchors.TryGetValue(anchorId, out var h) ? h : null;
        if (hex is null) return null;
        var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromHexString(hex), out _);
        return key;
    }
}
