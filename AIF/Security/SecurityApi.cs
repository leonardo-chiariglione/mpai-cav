using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AIF.Controller;

namespace AIF.Security;

// THE SECURITY API OF AIF V3.0 (https://mpai.community/standards/mpai-aif/v3-0/security-api/): the Trusted Services of the Secure Profile, on the root of
// trust of a party (a Controller, an AIM host). The Secure Controller holds one SecurityApi and gives each caller a handle (For) bound to its identity: the
// AIM Instance, the Controller or the User Agent. That identity is the Owner of the keys it generates, imports or derives and of the Secure Storage areas it
// initialises (Table 4, section 4): a call on a key or an area of another Owner returns NotAuthorised.
//
// WHERE THE SECRETS ARE. At its first opening the SecurityApi draws a master key from the root of trust and has it SEALED by the root of trust to its
// registers - the measurements of the party's code and of the Implementations it loaded (Zero Trust and Profiles 5). Persistent keys and Secure Storage areas
// are kept in its folder encrypted and authenticated (AES-256-GCM) under the master key, each bound to its place. With other code measured, the root of trust
// does not unseal the master key: the SecurityApi does not open (Opened false) and every call that needs a key or an area returns NotTrusted. The SecurityApi
// is therefore opened after the party's code and its Implementations are measured, in the same order at each start.
//
// LIMITS of this reference implementation, stated where they apply: the key material is generated in the memory of the Secure Controller and kept under the
// sealed master key (a TPM could generate and hold each key); replay protection of Secure Storage keeps the version of each area in a sealed index, which an
// attacker able to replace the whole folder could roll back (a monotonic counter of the root of trust closes this); not offered, with NotSupported: DES and
// DH keys (FFDH), PURE_EDDSA, the salt (label) of RSA_OAEP, a GCM nonce other than 12 bytes, XTS on data that is not a whole number of blocks, SHA-3 and
// ChaCha20-Poly1305 where the platform has not them.
public sealed class SecurityApi : IDisposable
{
    private readonly ISecureRoot root;
    private readonly string folder;
    private readonly byte[]? master;
    private readonly Func<string, string, bool> authoriseCopy;
    private readonly object gate = new();
    private readonly Dictionary<uint, Entry> keys = [];
    private Dictionary<string, long> versions = new(StringComparer.Ordinal);

    private sealed class Entry
    {
        public required KeyAttributes Attributes;
        public required byte[] Material;
        public bool PublicOnly;
    }

    // authoriseCopy(from, to): whether the Controller authorises a copy of a key of the Owner from for the Owner to (MPAI_AIFKM_Copy_Key); none by default.
    public SecurityApi(ISecureRoot root, string folder, Func<string, string, bool>? authoriseCopy = null)
    {
        this.root = root;
        this.folder = folder;
        this.authoriseCopy = authoriseCopy ?? ((from, to) => from == to);
        Directory.CreateDirectory(Path.Combine(folder, "keys"));
        Directory.CreateDirectory(Path.Combine(folder, "storage"));
        var sealedPath = Path.Combine(folder, "master.sealed");
        if (File.Exists(sealedPath)) master = root.Unseal(File.ReadAllBytes(sealedPath));
        else
        {
            master = root.Random(32);
            File.WriteAllBytes(sealedPath, root.Seal(master));
        }
        if (master is null) return;
        foreach (var file in Directory.GetFiles(Path.Combine(folder, "keys"), "*.key"))
        {
            var id = Convert.ToUInt32(Path.GetFileNameWithoutExtension(file), 16);
            var json = Open(File.ReadAllBytes(file), "key:" + id);
            if (json is not null) keys[id] = FromJson(JsonNode.Parse(json)!.AsObject());
        }
        var index = Path.Combine(folder, "storage", "index.bin");
        if (File.Exists(index) && Open(File.ReadAllBytes(index), "index") is { } list)
            versions = JsonSerializer.Deserialize<Dictionary<string, long>>(list) ?? versions;
    }

    // Whether the root of trust unsealed the master key: the party's code and Implementations are those measured when it was sealed.
    public bool Opened => master is not null;

    public ISecureRoot Root => root;

    // The handle of a caller: everything it does is done as its Owner.
    public SecurityCaller For(string owner) => new(this, owner);

    public void Dispose()
    {
        lock (gate)
        {
            foreach (var e in keys.Values) CryptographicOperations.ZeroMemory(e.Material);
            keys.Clear();
            if (master is not null) CryptographicOperations.ZeroMemory(master);
        }
    }

    // ---- protection of what is kept in the folder: AES-256-GCM under the master key, bound to its place
    private byte[] Protect(byte[] plain, string place)
    {
        var nonce = root.Random(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(master!, 16)) gcm.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(place));
        return [.. nonce, .. tag, .. cipher];
    }

    private byte[]? Open(byte[] blob, string place)
    {
        if (master is null || blob.Length < 28) return null;
        var plain = new byte[blob.Length - 28];
        try
        {
            using var gcm = new AesGcm(master, 16);
            gcm.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), plain, Encoding.UTF8.GetBytes(place));
            return plain;
        }
        catch (CryptographicException) { return null; }
    }

    private static JsonObject ToJson(Entry e) => new()
    {
        ["Identifier"] = e.Attributes.Identifier, ["Owner"] = e.Attributes.Owner, ["Type"] = e.Attributes.Type.ToString(), ["Bits"] = e.Attributes.Bits,
        ["Lifetime"] = e.Attributes.Lifetime.ToString(), ["Algorithm"] = e.Attributes.Algorithm.ToString(), ["Usage"] = (int)e.Attributes.Usage,
        ["PublicOnly"] = e.PublicOnly, ["Material"] = Convert.ToBase64String(e.Material)
    };

    private static Entry FromJson(JsonObject o) => new()
    {
        Attributes = new KeyAttributes(Enum.Parse<KeyType>((string)o["Type"]!), (int)o["Bits"]!, Enum.Parse<KeyLifetime>((string)o["Lifetime"]!),
                                       Enum.Parse<Algorithm>((string)o["Algorithm"]!), (KeyUsage)(int)o["Usage"]!)
        { Identifier = (uint)o["Identifier"]!, Owner = (string?)o["Owner"] },
        Material = Convert.FromBase64String((string)o["Material"]!),
        PublicOnly = (bool)o["PublicOnly"]!
    };

    // ---- keys
    private uint NewIdentifier()
    {
        while (true)
        {
            var id = BitConverter.ToUInt32(root.Random(4));
            if (id != 0 && !keys.ContainsKey(id)) return id;
        }
    }

    private AifError Add(KeyAttributes attributes, string owner, byte[] material, bool publicOnly, out uint key)
    {
        lock (gate)
        {
            key = NewIdentifier();
            var entry = new Entry { Attributes = attributes with { Identifier = key, Owner = owner }, Material = material, PublicOnly = publicOnly };
            keys[key] = entry;
            if (attributes.Lifetime == KeyLifetime.Persistent)
                File.WriteAllBytes(Path.Combine(folder, "keys", key.ToString("X8") + ".key"), Protect(Encoding.UTF8.GetBytes(ToJson(entry).ToJsonString()), "key:" + key));
            return AifError.OK;
        }
    }

    // The key of the caller, where it is the Owner and the Policy permits the usage with the algorithm.
    private AifError Use(string owner, uint key, KeyUsage usage, Algorithm algorithm, out Entry entry)
    {
        entry = null!;
        if (!Opened) return AifError.NotTrusted;
        lock (gate)
        {
            if (!keys.TryGetValue(key, out var found)) return AifError.NotFound;
            if (found.Attributes.Owner != owner) return AifError.NotAuthorised;
            if ((found.Attributes.Usage & usage) == 0) return AifError.NotAuthorised;
            if (algorithm != Algorithm.None && found.Attributes.Algorithm != algorithm) return AifError.NotAuthorised;
            entry = found;
            return AifError.OK;
        }
    }

    private static readonly int[] AesBits = [128, 192, 256];

    internal AifError Generate(string owner, KeyAttributes a, out uint key)
    {
        key = 0;
        if (!Opened) return AifError.NotTrusted;
        byte[] material;
        switch (a.Type)
        {
            case KeyType.Aes when AesBits.Contains(a.Bits) || (a.Algorithm == Algorithm.Xts && a.Bits is 256 or 512):
            case KeyType.Hmac or KeyType.RawData or KeyType.Derive or KeyType.Password when a.Bits > 0 && a.Bits % 8 == 0:
            case KeyType.ChaCha20 when a.Bits == 256:
                material = root.Random(a.Bits / 8);
                break;
            case KeyType.Des or KeyType.Dh:
                return AifError.NotSupported;
            case KeyType.Ecc when Curve(a.Bits) is { } curve:
                using (var ec = ECDsa.Create(curve)) material = ec.ExportPkcs8PrivateKey();
                break;
            case KeyType.Rsa when a.Bits >= 2048 && a.Bits % 8 == 0:
                using (var rsa = RSA.Create(a.Bits)) material = rsa.ExportPkcs8PrivateKey();
                break;
            default:
                return AifError.Failed;
        }
        return Add(a, owner, material, false, out key);
    }

    internal AifError Import(string owner, KeyAttributes a, ReadOnlySpan<byte> data, out uint key)
    {
        key = 0;
        if (!Opened) return AifError.NotTrusted;
        var bytes = data.ToArray();
        var publicOnly = false;
        var bits = a.Bits;
        switch (a.Type)
        {
            case KeyType.Aes when AesBits.Contains(bytes.Length * 8) || (a.Algorithm == Algorithm.Xts && bytes.Length is 32 or 64):
            case KeyType.Hmac or KeyType.RawData or KeyType.Derive or KeyType.Password when bytes.Length > 0:
            case KeyType.ChaCha20 when bytes.Length == 32:
                bits = bytes.Length * 8;
                break;
            case KeyType.Des or KeyType.Dh:
                return AifError.NotSupported;
            case KeyType.Ecc:
                using (var ec = ECDsa.Create())
                {
                    if (!TryImportEcc(ec, bytes, a.Bits, out publicOnly)) return AifError.Failed;
                    bits = ec.KeySize;
                    bytes = publicOnly ? ec.ExportSubjectPublicKeyInfo() : ec.ExportPkcs8PrivateKey();
                }
                break;
            case KeyType.Rsa:
                using (var rsa = RSA.Create())
                {
                    try { rsa.ImportPkcs8PrivateKey(bytes, out _); }
                    catch (CryptographicException)
                    {
                        try { rsa.ImportSubjectPublicKeyInfo(bytes, out _); publicOnly = true; }
                        catch (CryptographicException) { return AifError.Failed; }
                    }
                    bits = rsa.KeySize;
                    bytes = publicOnly ? rsa.ExportSubjectPublicKeyInfo() : rsa.ExportPkcs8PrivateKey();
                }
                break;
            default:
                return AifError.Failed;
        }
        if (a.Bits != 0 && a.Bits != bits) return AifError.Failed;
        return Add(a with { Bits = bits }, owner, bytes, publicOnly, out key);
    }

    private static bool TryImportEcc(ECDsa ec, byte[] bytes, int bits, out bool publicOnly)
    {
        publicOnly = false;
        try { ec.ImportPkcs8PrivateKey(bytes, out _); return true; } catch (CryptographicException) { }
        publicOnly = true;
        try { ec.ImportSubjectPublicKeyInfo(bytes, out _); return true; } catch (CryptographicException) { }
        if (Point(bytes, bits) is { } p) { ec.ImportParameters(p); return true; }
        return false;
    }

    // An uncompressed point 04 || X || Y on the NIST curve of bits.
    private static ECParameters? Point(byte[] bytes, int bits)
    {
        var size = (bits + 7) / 8;
        if (Curve(bits) is not { } curve || bytes.Length != 1 + 2 * size || bytes[0] != 4) return null;
        return new ECParameters { Curve = curve, Q = new ECPoint { X = bytes[1..(1 + size)], Y = bytes[(1 + size)..] } };
    }

    private static ECCurve? Curve(int bits) => bits switch { 256 => ECCurve.NamedCurves.nistP256, 384 => ECCurve.NamedCurves.nistP384, 521 => ECCurve.NamedCurves.nistP521, _ => null };

    internal AifError Copy(string owner, uint source, KeyAttributes a, out uint target)
    {
        target = 0;
        var error = Use(owner, source, KeyUsage.Copy, Algorithm.None, out var e);
        if (error != AifError.OK) return error;
        var to = a.Owner ?? owner;
        if (to != owner && !authoriseCopy(owner, to)) return AifError.NotAuthorised;
        var copy = a with
        {
            Type = e.Attributes.Type, Bits = e.Attributes.Bits, Usage = a.Usage & e.Attributes.Usage,
            Algorithm = a.Algorithm == Algorithm.None ? e.Attributes.Algorithm : a.Algorithm
        };
        if (copy.Algorithm != e.Attributes.Algorithm) return AifError.NotAuthorised;
        return Add(copy, to, (byte[])e.Material.Clone(), e.PublicOnly, out target);
    }

    internal AifError Destroy(string owner, uint key)
    {
        if (!Opened) return AifError.NotTrusted;
        lock (gate)
        {
            if (!keys.TryGetValue(key, out var e)) return AifError.NotFound;
            if (e.Attributes.Owner != owner) return AifError.NotAuthorised;
            keys.Remove(key);
            CryptographicOperations.ZeroMemory(e.Material);
            File.Delete(Path.Combine(folder, "keys", key.ToString("X8") + ".key"));
            return AifError.OK;
        }
    }

    internal AifError Export(string owner, uint key, Span<byte> data, out int length)
    {
        length = 0;
        var error = Use(owner, key, KeyUsage.Export, Algorithm.None, out var e);
        return error != AifError.OK ? error : Out(e.Material, data, out length);
    }

    internal AifError ExportPublic(string owner, uint key, Span<byte> data, out int length)
    {
        length = 0;
        if (!Opened) return AifError.NotTrusted;
        Entry e;
        lock (gate)
        {
            if (!keys.TryGetValue(key, out e!)) return AifError.NotFound;
            if (e.Attributes.Owner != owner) return AifError.NotAuthorised;
        }
        byte[] pub;
        switch (e.Attributes.Type)
        {
            case KeyType.Ecc: using (var ec = Ecdsa(e)) pub = ec.ExportSubjectPublicKeyInfo(); break;
            case KeyType.Rsa: using (var rsa = Rsa(e)) pub = rsa.ExportSubjectPublicKeyInfo(); break;
            default: return AifError.Failed;
        }
        return Out(pub, data, out length);
    }

    internal static AifError Out(ReadOnlySpan<byte> value, Span<byte> buffer, out int length)
    {
        length = value.Length;
        if (buffer.Length < value.Length) return AifError.BufferTooSmall;
        value.CopyTo(buffer);
        return AifError.OK;
    }

    internal static ECDsa Ecdsa(object entry)
    {
        var e = (Entry)entry;
        var ec = ECDsa.Create();
        if (e.PublicOnly) ec.ImportSubjectPublicKeyInfo(e.Material, out _); else ec.ImportPkcs8PrivateKey(e.Material, out _);
        return ec;
    }

    private static ECDiffieHellman Ecdh(Entry e)
    {
        var ec = ECDiffieHellman.Create();
        ec.ImportPkcs8PrivateKey(e.Material, out _);
        return ec;
    }

    private static RSA Rsa(Entry e)
    {
        var rsa = RSA.Create();
        if (e.PublicOnly) rsa.ImportSubjectPublicKeyInfo(e.Material, out _); else rsa.ImportPkcs8PrivateKey(e.Material, out _);
        return rsa;
    }

    // ---- 6.1 hashing (non-secure area: no key)
    internal static bool IsHash(Algorithm a) => a is Algorithm.Md5 or Algorithm.Sha1 or Algorithm.Sha256 or Algorithm.Sha384 or Algorithm.Sha512
                                                    or Algorithm.Sha3_256 or Algorithm.Sha3_384 or Algorithm.Sha3_512;

    internal static HashAlgorithmName? HashName(Algorithm a) => a switch
    {
        Algorithm.Md5 => HashAlgorithmName.MD5, Algorithm.Sha1 => HashAlgorithmName.SHA1, Algorithm.Sha256 => HashAlgorithmName.SHA256,
        Algorithm.Sha384 => HashAlgorithmName.SHA384, Algorithm.Sha512 => HashAlgorithmName.SHA512,
        Algorithm.Sha3_256 when SHA3_256.IsSupported => HashAlgorithmName.SHA3_256, Algorithm.Sha3_384 when SHA3_384.IsSupported => HashAlgorithmName.SHA3_384,
        Algorithm.Sha3_512 when SHA3_512.IsSupported => HashAlgorithmName.SHA3_512,
        _ => null
    };

    // ---- 6.4 message authentication code
    internal AifError MacSetup(string owner, MacState state, uint key, Algorithm algorithm, bool verifying)
    {
        var name = algorithm switch { Algorithm.HmacSha256 => HashAlgorithmName.SHA256, Algorithm.HmacSha384 => HashAlgorithmName.SHA384, Algorithm.HmacSha512 => HashAlgorithmName.SHA512, _ => (HashAlgorithmName?)null };
        if (name is null || state.Mac is not null) return AifError.Failed;
        var error = Use(owner, key, verifying ? KeyUsage.VerifyMessage : KeyUsage.SignMessage, algorithm, out var e);
        if (error != AifError.OK) return error;
        if (e.Attributes.Type != KeyType.Hmac) return AifError.Failed;
        state.Mac = IncrementalHash.CreateHMAC(name.Value, e.Material);
        state.Verifying = verifying;
        return AifError.OK;
    }

    // ---- 6.5 ciphers
    // 6.5: where the algorithm needs an IV, the caller gives it in iv[..ivLength]; or, encrypting, passes ivLength 0 and a buffer of 16 bytes or more, in which
    // the IV generated by the root of trust is written, ivLength its size. Decrypting always takes the IV.
    internal AifError CipherSetup(string owner, CipherState state, uint key, Algorithm algorithm, Span<byte> iv, bool encrypting, ref int ivLength)
    {
        if (state.Active) return AifError.Failed;
        if (algorithm is not (Algorithm.Xts or Algorithm.Ctr or Algorithm.EcbNoPadding or Algorithm.CbcNoPadding or Algorithm.CbcPkcs7)) return AifError.Failed;
        var error = Use(owner, key, encrypting ? KeyUsage.Encrypt : KeyUsage.Decrypt, algorithm, out var e);
        if (error != AifError.OK) return error;
        if (e.Attributes.Type != KeyType.Aes) return AifError.Failed;
        var needsIv = algorithm != Algorithm.EcbNoPadding;
        byte[]? theIv = null;
        if (needsIv)
        {
            if (ivLength == 0)
            {
                if (!encrypting) return AifError.Failed;
                if (iv.Length < 16) { ivLength = 16; return AifError.BufferTooSmall; }
                root.Random(16).CopyTo(iv);
                ivLength = 16;
            }
            if (ivLength != 16 || iv.Length < 16) return AifError.Failed;
            theIv = iv[..16].ToArray();
        }
        else ivLength = 0;
        var aes = Aes.Create();
        var material = e.Material;
        if (algorithm == Algorithm.Xts)
        {
            if (material.Length is not (32 or 64)) { aes.Dispose(); return AifError.Failed; }
            var half = material.Length / 2;
            aes.Key = material[..half];
            state.XtsKey2 = material[half..];
            using var k2 = Aes.Create();
            k2.Key = state.XtsKey2;
            state.Counter = k2.EncryptEcb(theIv!, PaddingMode.None);                       // T = E_K2(tweak)
        }
        else
        {
            aes.Key = material;
            if (algorithm == Algorithm.Ctr) state.Counter = theIv!;
            else
            {
                aes.Mode = algorithm == Algorithm.EcbNoPadding ? CipherMode.ECB : CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                state.Transform = encrypting ? aes.CreateEncryptor(material, theIv) : aes.CreateDecryptor(material, theIv);
            }
        }
        state.Aes = aes;
        state.Algorithm = algorithm;
        state.Encrypting = encrypting;
        return AifError.OK;
    }

    // ---- 6.6 authenticated encryption
    internal AifError Aead(string owner, uint key, Algorithm algorithm, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ad, ReadOnlySpan<byte> input,
                           Span<byte> output, out int length, bool encrypting)
    {
        length = 0;
        var error = Use(owner, key, encrypting ? KeyUsage.Encrypt : KeyUsage.Decrypt, algorithm, out var e);
        if (error != AifError.OK) return error;
        if (nonce.Length != 12) return AifError.NotSupported;
        var plainLength = encrypting ? input.Length : input.Length - 16;
        if (plainLength < 0) return AifError.InvalidSignature;
        length = encrypting ? input.Length + 16 : plainLength;
        if (output.Length < length) return AifError.BufferTooSmall;
        try
        {
            if (algorithm == Algorithm.Gcm && e.Attributes.Type == KeyType.Aes)
            {
                using var gcm = new AesGcm(e.Material, 16);
                if (encrypting) gcm.Encrypt(nonce, input, output[..input.Length], output.Slice(input.Length, 16), ad);
                else gcm.Decrypt(nonce, input[..plainLength], input[plainLength..], output[..plainLength], ad);
            }
            else if (algorithm == Algorithm.ChaCha20Poly1305 && e.Attributes.Type == KeyType.ChaCha20)
            {
                if (!ChaCha20Poly1305.IsSupported) return AifError.NotSupported;
                using var cc = new ChaCha20Poly1305(e.Material);
                if (encrypting) cc.Encrypt(nonce, input, output[..input.Length], output.Slice(input.Length, 16), ad);
                else cc.Decrypt(nonce, input[..plainLength], input[plainLength..], output[..plainLength], ad);
            }
            else return AifError.Failed;
        }
        catch (AuthenticationTagMismatchException) { output[..plainLength].Clear(); return AifError.InvalidSignature; }
        return AifError.OK;
    }

    // ---- 6.7 signature
    internal AifError Sign(string owner, uint key, Algorithm algorithm, ReadOnlySpan<byte> input, Span<byte> signature, out int length, bool isHash)
    {
        length = 0;
        var error = Use(owner, key, isHash ? KeyUsage.SignHash : KeyUsage.SignMessage, algorithm, out var e);
        if (error != AifError.OK) return error;
        if (e.PublicOnly) return AifError.NotAuthorised;
        byte[] sig;
        switch (algorithm)
        {
            case Algorithm.Ecdsa when e.Attributes.Type == KeyType.Ecc:
                using (var ec = Ecdsa(e))
                    sig = isHash ? ec.SignHash(input.ToArray(), DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
                                 : ec.SignData(input.ToArray(), EcdsaHash(e.Attributes.Bits), DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                break;
            case Algorithm.RsaPkcs1v15Sign or Algorithm.RsaPss when e.Attributes.Type == KeyType.Rsa:
                using (var rsa = Rsa(e))
                {
                    var padding = algorithm == Algorithm.RsaPss ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1;
                    sig = isHash ? rsa.SignHash(input.ToArray(), HashAlgorithmName.SHA256, padding) : rsa.SignData(input.ToArray(), HashAlgorithmName.SHA256, padding);
                }
                break;
            case Algorithm.PureEdDsa:
                return AifError.NotSupported;
            default:
                return AifError.Failed;
        }
        return Out(sig, signature, out length);
    }

    internal AifError Verify(string owner, uint key, Algorithm algorithm, ReadOnlySpan<byte> input, ReadOnlySpan<byte> signature, bool isHash)
    {
        var error = Use(owner, key, isHash ? KeyUsage.VerifyHash : KeyUsage.VerifyMessage, algorithm, out var e);
        if (error != AifError.OK) return error;
        bool ok;
        switch (algorithm)
        {
            case Algorithm.Ecdsa when e.Attributes.Type == KeyType.Ecc:
                using (var ec = Ecdsa(e))
                    ok = isHash ? ec.VerifyHash(input, signature, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
                                : ec.VerifyData(input, signature, EcdsaHash(e.Attributes.Bits), DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                break;
            case Algorithm.RsaPkcs1v15Sign or Algorithm.RsaPss when e.Attributes.Type == KeyType.Rsa:
                using (var rsa = Rsa(e))
                {
                    var padding = algorithm == Algorithm.RsaPss ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1;
                    ok = isHash ? rsa.VerifyHash(input, signature, HashAlgorithmName.SHA256, padding) : rsa.VerifyData(input, signature, HashAlgorithmName.SHA256, padding);
                }
                break;
            case Algorithm.PureEdDsa:
                return AifError.NotSupported;
            default:
                return AifError.Failed;
        }
        return ok ? AifError.OK : AifError.InvalidSignature;
    }

    private static HashAlgorithmName EcdsaHash(int bits) => bits switch { > 384 => HashAlgorithmName.SHA512, > 256 => HashAlgorithmName.SHA384, _ => HashAlgorithmName.SHA256 };

    // ---- 6.8 asymmetric encryption
    internal AifError Asymmetric(string owner, uint key, Algorithm algorithm, ReadOnlySpan<byte> input, ReadOnlySpan<byte> salt, Span<byte> output, out int length, bool encrypting)
    {
        length = 0;
        var error = Use(owner, key, encrypting ? KeyUsage.Encrypt : KeyUsage.Decrypt, algorithm, out var e);
        if (error != AifError.OK) return error;
        if (e.Attributes.Type != KeyType.Rsa) return AifError.Failed;
        if (!salt.IsEmpty) return AifError.NotSupported;
        if (!encrypting && e.PublicOnly) return AifError.NotAuthorised;
        var padding = algorithm switch { Algorithm.RsaOaep => RSAEncryptionPadding.OaepSHA256, Algorithm.RsaPkcs1v15Crypt => RSAEncryptionPadding.Pkcs1, _ => null };
        if (padding is null) return AifError.Failed;
        using var rsa = Rsa(e);
        try
        {
            var result = encrypting ? rsa.Encrypt(input.ToArray(), padding) : rsa.Decrypt(input.ToArray(), padding);
            return Out(result, output, out length);
        }
        catch (CryptographicException) { return AifError.Failed; }
    }

    // ---- 6.3 key exchange and derivation
    internal AifError Agree(string owner, Algorithm algorithm, uint privateKey, ReadOnlySpan<byte> peerKey, out byte[] secret)
    {
        secret = [];
        if (algorithm == Algorithm.Ffdh) return AifError.NotSupported;
        if (algorithm != Algorithm.Ecdh) return AifError.Failed;
        var error = Use(owner, privateKey, KeyUsage.Derive, algorithm, out var e);
        if (error != AifError.OK) return error;
        if (e.Attributes.Type != KeyType.Ecc || e.PublicOnly) return AifError.Failed;
        using var mine = Ecdh(e);
        using var peer = ECDiffieHellman.Create();
        try
        {
            var bytes = peerKey.ToArray();
            if (Point(bytes, e.Attributes.Bits) is { } p) peer.ImportParameters(p); else peer.ImportSubjectPublicKeyInfo(bytes, out _);
            secret = mine.DeriveRawSecretAgreement(peer.PublicKey);
            return AifError.OK;
        }
        catch (CryptographicException) { return AifError.Failed; }
    }

    internal AifError Derive(string owner, KeyAttributes a, KeyDerivationOperation op, out uint key)
    {
        key = 0;
        if (!Opened) return AifError.NotTrusted;
        if (!op.Active || op.Secret is null) return AifError.Failed;
        if (a.Type is not (KeyType.Aes or KeyType.ChaCha20 or KeyType.Hmac or KeyType.RawData or KeyType.Derive or KeyType.Password) || a.Bits <= 0 || a.Bits % 8 != 0) return AifError.Failed;
        if (a.Type == KeyType.ChaCha20 && a.Bits != 256) return AifError.Failed;
        if (a.Type == KeyType.Aes && !AesBits.Contains(a.Bits)) return AifError.Failed;
        var name = op.Algorithm switch { Algorithm.HkdfSha384 => HashAlgorithmName.SHA384, Algorithm.HkdfSha512 => HashAlgorithmName.SHA512, _ => HashAlgorithmName.SHA256 };
        var take = a.Bits / 8;
        var all = HKDF.DeriveKey(name, op.Secret, op.Used + take, op.Salt ?? [], op.Info ?? []);
        var material = all[op.Used..];
        op.Used += take;
        CryptographicOperations.ZeroMemory(all);
        return Add(a, owner, material, false, out key);
    }

    // ---- 9 random generation
    internal AifError Random(Span<byte> output)
    {
        try
        {
            var bytes = root.Random(output.Length);
            if (bytes.Length != output.Length) return AifError.InsufficientEntropy;
            bytes.CopyTo(output);
            return AifError.OK;
        }
        catch (Exception) { return AifError.InsufficientEntropy; }
    }

    // ---- 4 secure storage: one file per area of an Owner, its version in the sealed index
    private string AreaFile(string owner, string name) =>
        Path.Combine(folder, "storage", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner)))[..32],
                     Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..32] + ".area");

    private static string Place(string owner, string name) => "area:" + owner + "\n" + name;

    private sealed record Area(StorageFlags Flags, long Version, long Capacity, byte[] Data);

    private AifError ReadArea(string owner, string name, out Area? area)
    {
        area = null;
        var file = AreaFile(owner, name);
        if (!File.Exists(file)) return AifError.NotFound;
        var json = Open(File.ReadAllBytes(file), Place(owner, name));
        if (json is null) return AifError.NotTrusted;                                   // altered, or moved from another area or another party
        var o = JsonNode.Parse(json)!.AsObject();
        area = new Area((StorageFlags)(int)o["Flags"]!, (long)o["Version"]!, (long)o["Capacity"]!, Convert.FromBase64String((string)o["Data"]!));
        if ((area.Flags & StorageFlags.NoReplayProtection) == 0 && (!versions.TryGetValue(Place(owner, name), out var v) || v != area.Version))
            return AifError.NotTrusted;                                                  // an earlier content put back
        return AifError.OK;
    }

    private void WriteArea(string owner, string name, Area area)
    {
        var file = AreaFile(owner, name);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var json = new JsonObject { ["Flags"] = (int)area.Flags, ["Version"] = area.Version, ["Capacity"] = area.Capacity, ["Data"] = Convert.ToBase64String(area.Data) };
        File.WriteAllBytes(file, Protect(Encoding.UTF8.GetBytes(json.ToJsonString()), Place(owner, name)));
        versions[Place(owner, name)] = area.Version;
        SaveIndex();
    }

    private void SaveIndex() =>
        File.WriteAllBytes(Path.Combine(folder, "storage", "index.bin"), Protect(JsonSerializer.SerializeToUtf8Bytes(versions), "index"));

    internal AifError StorageInit(string owner, string name, ReadOnlySpan<byte> data, StorageFlags flags)
    {
        if (!Opened) return AifError.NotTrusted;
        lock (gate)
        {
            var found = ReadArea(owner, name, out var area);
            if (found == AifError.NotTrusted) return found;
            if (area is not null && (area.Flags & StorageFlags.WriteOnce) != 0) return AifError.NotAuthorised;
            WriteArea(owner, name, new Area(flags, (area?.Version ?? 0) + 1, data.Length, data.ToArray()));
            return AifError.OK;
        }
    }

    internal AifError StorageWrite(string owner, string name, ReadOnlySpan<byte> data)
    {
        if (!Opened) return AifError.NotTrusted;
        lock (gate)
        {
            var found = ReadArea(owner, name, out var area);
            if (found != AifError.OK) return found;
            if ((area!.Flags & StorageFlags.WriteOnce) != 0) return AifError.NotAuthorised;
            WriteArea(owner, name, area with { Version = area.Version + 1, Capacity = Math.Max(area.Capacity, data.Length), Data = data.ToArray() });
            return AifError.OK;
        }
    }

    internal AifError StorageRead(string owner, string name, Span<byte> data, out int length)
    {
        length = 0;
        if (!Opened) return AifError.NotTrusted;
        lock (gate)
        {
            var found = ReadArea(owner, name, out var area);
            return found != AifError.OK ? found : Out(area!.Data, data, out length);
        }
    }

    internal AifError StorageInfoOf(string owner, string name, out StorageInfo? info)
    {
        info = null;
        if (!Opened) return AifError.NotTrusted;
        lock (gate)
        {
            var found = ReadArea(owner, name, out var area);
            if (found != AifError.OK) return found;
            info = new StorageInfo(area!.Capacity, area.Data.Length, area.Flags);
            return AifError.OK;
        }
    }

    internal AifError StorageDelete(string owner, string name)
    {
        if (!Opened) return AifError.NotTrusted;
        lock (gate)
        {
            var found = ReadArea(owner, name, out var area);
            if (found != AifError.OK) return found;
            if ((area!.Flags & StorageFlags.WriteOnce) != 0) return AifError.NotAuthorised;
            File.Delete(AreaFile(owner, name));
            versions.Remove(Place(owner, name));
            SaveIndex();
            return AifError.OK;
        }
    }
}
