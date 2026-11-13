using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.RootOfTrust;
using AIF.Security;
using AIF.Trust;

namespace Mpai.Aif.Tests;

// THE SECURITY API OF AIF V3.0 (https://mpai.community/standards/mpai-aif/v3-0/security-api/), in AIF\Security. Each caller has a handle bound to its
// identity, the Owner of its keys and Secure Storage areas (Table 4, section 4). Secure Storage and persistent keys are kept under a master key the root of
// trust seals to its measurements. The functions give what the platform's cryptography gives for the same inputs, and the published test vector for XTS.
//
// The fast tests run on a SOFTWARE stand-in of a root of trust (TestRoot below: it "seals" under a key of the process, bound to its one register). The
// attestation token, and the sealing to real registers, are tested on the simulated TPM 2.0 (SecurityApiTpmTests).
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class SecurityApiTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "mpai-security-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(folder, true); } catch { } }

    // A stand-in of a root of trust, for the tests only: one register, which Measure extends; Seal binds a secret to the register's value.
    internal sealed class TestRoot : ISecureRoot
    {
        private static readonly byte[] processKey = RandomNumberGenerator.GetBytes(32);
        private byte[] register = new byte[32];
        public string Name => "test-party";
        public ECDsa IdentityKey { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public void Measure(int r, string what, string sha256) => register = SHA256.HashData([.. register, .. Convert.FromHexString(sha256)]);
        public JsonObject Evidence(string partyId, byte[] nonce) => throw new NotSupportedException("the test root gives no quote");
        public byte[] Random(int count) => RandomNumberGenerator.GetBytes(count);
        public byte[] Seal(byte[] secret)
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[secret.Length];
            var tag = new byte[16];
            using var gcm = new AesGcm(processKey, 16);
            gcm.Encrypt(nonce, secret, cipher, tag, register);
            return [.. nonce, .. tag, .. cipher];
        }
        public byte[]? Unseal(byte[] blob)
        {
            var plain = new byte[blob.Length - 28];
            try { using var gcm = new AesGcm(processKey, 16); gcm.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), plain, register); return plain; }
            catch (CryptographicException) { return null; }
        }
    }

    private static KeyAttributes Key(KeyType type, int bits, Algorithm alg, KeyUsage usage, KeyLifetime life = KeyLifetime.Volatile) => new(type, bits, life, alg, usage);

    private static byte[] Take(Span<byte> buffer, int length) => buffer[..length].ToArray();

    [Fact]
    public void AKeyIsUsedOnlyByItsOwnerAndCopiedOnlyWhereTheControllerAuthorises()
    {
        using var api = new SecurityApi(new TestRoot(), folder, (from, to) => from == "aim-a" && to == "aim-c");
        var a = api.For("aim-a");
        var b = api.For("aim-b");
        Assert.Equal(AifError.OK, a.MPAI_AIFKM_Generate_Key(Key(KeyType.Hmac, 256, Algorithm.HmacSha256, KeyUsage.SignMessage | KeyUsage.Copy), out var key));

        // Another AIM that knows the identifier cannot use, export, copy or destroy the key.
        Assert.Equal(AifError.NotAuthorised, b.MPAI_AIFMAC_Sign_Setup(new MacState(), key, Algorithm.HmacSha256));
        Assert.Equal(AifError.NotAuthorised, b.MPAI_AIFKM_Copy_Key(key, Key(KeyType.Hmac, 256, Algorithm.None, KeyUsage.SignMessage), out _));
        Assert.Equal(AifError.NotAuthorised, b.MPAI_AIFKM_Destroy_Key(key));
        // Its Owner cannot export it: the Policy has no EXPORT; nor use it for what the Policy does not permit.
        Assert.Equal(AifError.NotAuthorised, a.MPAI_AIFKM_Export_Key(key, new byte[64], out _));
        Assert.Equal(AifError.NotAuthorised, a.MPAI_AIFMAC_Verify_Setup(new MacState(), key, Algorithm.HmacSha256));
        Assert.Equal(AifError.NotAuthorised, a.MPAI_AIFMAC_Sign_Setup(new MacState(), key, Algorithm.HmacSha512));

        // A copy for another Owner: refused for aim-b, given to aim-c, which the Controller authorises; the copy has at most the usage of the source.
        Assert.Equal(AifError.NotAuthorised, a.MPAI_AIFKM_Copy_Key(key, Key(KeyType.Hmac, 256, Algorithm.None, KeyUsage.SignMessage) with { Owner = "aim-b" }, out _));
        Assert.Equal(AifError.OK, a.MPAI_AIFKM_Copy_Key(key, Key(KeyType.Hmac, 256, Algorithm.None, KeyUsage.SignMessage | KeyUsage.Export) with { Owner = "aim-c" }, out var copy));
        var c = api.For("aim-c");
        var s1 = new MacState();
        Assert.Equal(AifError.OK, c.MPAI_AIFMAC_Sign_Setup(s1, copy, Algorithm.HmacSha256));
        Assert.Equal(AifError.NotAuthorised, c.MPAI_AIFKM_Export_Key(copy, new byte[64], out _));
        Assert.Equal(AifError.NotFound, a.MPAI_AIFKM_Destroy_Key(12345));
    }

    [Fact]
    public void PersistentKeysAndAreasSurviveARestartAndOpenOnlyWithTheSameMeasurements()
    {
        var root = new TestRoot();
        root.Measure(10, "code", new string('A', 64));
        uint persistent, volatileKey;
        var mac = new byte[32];
        using (var api = new SecurityApi(root, folder))
        {
            var a = api.For("aim-a");
            Assert.Equal(AifError.OK, a.MPAI_AIFKM_Generate_Key(Key(KeyType.Hmac, 256, Algorithm.HmacSha256, KeyUsage.SignMessage | KeyUsage.VerifyMessage, KeyLifetime.Persistent), out persistent));
            Assert.Equal(AifError.OK, a.MPAI_AIFKM_Generate_Key(Key(KeyType.Hmac, 256, Algorithm.HmacSha256, KeyUsage.SignMessage), out volatileKey));
            var s = new MacState();
            a.MPAI_AIFMAC_Sign_Setup(s, persistent, Algorithm.HmacSha256);
            a.MPAI_AIFMAC_Update(s, "kept"u8);
            Assert.Equal(AifError.OK, a.MPAI_AIFMAC_Sign_Finish(s, mac, out _));
            Assert.Equal(AifError.OK, a.MPAI_AIFSS_Storage_Init("profile", "the End User's profile"u8, StorageFlags.None));
        }
        // Not a byte of the key or of the area is in the folder in clear.
        foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain("End User", Encoding.UTF8.GetString(File.ReadAllBytes(file)));

        using (var api = new SecurityApi(root, folder))
        {
            Assert.True(api.Opened);
            var a = api.For("aim-a");
            var v = new MacState();
            Assert.Equal(AifError.OK, a.MPAI_AIFMAC_Verify_Setup(v, persistent, Algorithm.HmacSha256));
            a.MPAI_AIFMAC_Update(v, "kept"u8);
            Assert.Equal(AifError.OK, a.MPAI_AIFMAC_Verify_Finish(v, mac));
            Assert.Equal(AifError.NotFound, a.MPAI_AIFMAC_Sign_Setup(new MacState(), volatileKey, Algorithm.HmacSha256));
            var buffer = new byte[100];
            Assert.Equal(AifError.OK, a.MPAI_AIFSS_Storage_Read("profile", buffer, out var n));
            Assert.Equal("the End User's profile", Encoding.UTF8.GetString(buffer, 0, n));
        }

        // Other code measured: the root of trust does not unseal the master key; nothing opens.
        root.Measure(10, "other code", new string('B', 64));
        using (var api = new SecurityApi(root, folder))
        {
            Assert.False(api.Opened);
            var a = api.For("aim-a");
            Assert.Equal(AifError.NotTrusted, a.MPAI_AIFMAC_Sign_Setup(new MacState(), persistent, Algorithm.HmacSha256));
            Assert.Equal(AifError.NotTrusted, a.MPAI_AIFSS_Storage_Read("profile", new byte[100], out _));
        }
    }

    [Fact]
    public void SecureStorageAreasBelongToTheirOwnerAndRefuseAlterationAndReplay()
    {
        using var api = new SecurityApi(new TestRoot(), folder);
        var a = api.For("aim-a");
        var b = api.For("aim-b");
        var buffer = new byte[64];
        Assert.Equal(AifError.OK, a.MPAI_AIFSS_Storage_Init("notes", "one"u8, StorageFlags.None));
        Assert.Equal(AifError.NotFound, b.MPAI_AIFSS_Storage_Read("notes", buffer, out _));          // the same name is another area for another Owner
        Assert.Equal(AifError.OK, b.MPAI_AIFSS_Storage_Init("notes", "of b"u8, StorageFlags.None));
        Assert.Equal(AifError.OK, a.MPAI_AIFSS_Storage_Read("notes", buffer, out var n));
        Assert.Equal("one", Encoding.UTF8.GetString(buffer, 0, n));
        Assert.Equal(AifError.BufferTooSmall, a.MPAI_AIFSS_Storage_Read("notes", new byte[2], out n));
        Assert.Equal(3, n);

        var file = Directory.GetFiles(Path.Combine(folder, "storage"), "*.area", SearchOption.AllDirectories)
            .Single(f => api.For("aim-a").MPAI_AIFSS_Storage_GetInfo("notes", out _) == AifError.OK && File.ReadAllBytes(f).Length > 0 && f.Contains(Convert.ToHexString(SHA256.HashData("aim-a"u8))[..32]));
        var first = File.ReadAllBytes(file);
        Assert.Equal(AifError.OK, a.MPAI_AIFSS_Storage_Write("notes", "two, longer"u8));
        Assert.Equal(AifError.OK, a.MPAI_AIFSS_Storage_GetInfo("notes", out var info));
        Assert.Equal(new StorageInfo(11, 11, StorageFlags.None), info);

        // An earlier content put back: refused (replay). The content altered: refused.
        var second = File.ReadAllBytes(file);
        File.WriteAllBytes(file, first);
        Assert.Equal(AifError.NotTrusted, a.MPAI_AIFSS_Storage_Read("notes", buffer, out _));
        second[^1] ^= 1;
        File.WriteAllBytes(file, second);
        Assert.Equal(AifError.NotTrusted, a.MPAI_AIFSS_Storage_Read("notes", buffer, out _));

        // Write once: neither written again nor deleted.
        Assert.Equal(AifError.OK, a.MPAI_AIFSS_Storage_Init("consent", "given"u8, StorageFlags.WriteOnce));
        Assert.Equal(AifError.NotAuthorised, a.MPAI_AIFSS_Storage_Write("consent", "withdrawn"u8));
        Assert.Equal(AifError.NotAuthorised, a.MPAI_AIFSS_Storage_Init("consent", "again"u8, StorageFlags.None));
        Assert.Equal(AifError.NotAuthorised, a.MPAI_AIFSS_Storage_Delete("consent"));
        Assert.Equal(AifError.OK, b.MPAI_AIFSS_Storage_Delete("notes"));
        Assert.Equal(AifError.NotFound, b.MPAI_AIFSS_Storage_Read("notes", buffer, out _));
    }

    [Fact]
    public void HashingInOneCallOrInChunksGivesTheSameHash()
    {
        using var api = new SecurityApi(new TestRoot(), folder);
        var x = api.For("aim-a");
        var data = RandomNumberGenerator.GetBytes(1000);
        var one = new byte[32];
        Assert.Equal(AifError.OK, x.MPAI_AIFCR_Hash_Compute(Algorithm.Sha256, data, one, out var n));
        Assert.Equal(SHA256.HashData(data), Take(one, n));
        var s = new HashState();
        Assert.Equal(AifError.OK, x.MPAI_AIFCR_Hash_Setup(s, Algorithm.Sha256));
        x.MPAI_AIFCR_Hash_Update(s, data.AsSpan(0, 333));
        x.MPAI_AIFCR_Hash_Update(s, data.AsSpan(333));
        Assert.Equal(AifError.BufferTooSmall, x.MPAI_AIFCR_Hash_Finish(s, new byte[16], out _));
        var chunks = new byte[32];
        Assert.Equal(AifError.OK, x.MPAI_AIFCR_Hash_Finish(s, chunks, out _));
        Assert.Equal(one, chunks);
        var v = new HashState();
        x.MPAI_AIFCR_Hash_Setup(v, Algorithm.Sha256);
        x.MPAI_AIFCR_Hash_Update(v, data);
        Assert.Equal(AifError.InvalidSignature, x.MPAI_AIFCR_Hash_Verify(v, new byte[32]));
        Assert.Equal(AifError.NotInitialized, x.MPAI_AIFCR_Hash_Update(v, data));
        Assert.Equal(AifError.Failed, x.MPAI_AIFCR_Hash_Compute(Algorithm.Gcm, data, one, out _));
    }

    [Fact]
    public void TheMacIsTheHmacAndAChangedMessageDoesNotVerify()
    {
        using var api = new SecurityApi(new TestRoot(), folder);
        var x = api.For("aim-a");
        var raw = RandomNumberGenerator.GetBytes(32);
        Assert.Equal(AifError.OK, x.MPAI_AIFKM_Import_Key(Key(KeyType.Hmac, 0, Algorithm.HmacSha256, KeyUsage.SignMessage | KeyUsage.VerifyMessage), raw, out var key));
        var s = new MacState();
        x.MPAI_AIFMAC_Sign_Setup(s, key, Algorithm.HmacSha256);
        x.MPAI_AIFMAC_Update(s, "a Message"u8);
        var mac = new byte[32];
        Assert.Equal(AifError.OK, x.MPAI_AIFMAC_Sign_Finish(s, mac, out _));
        Assert.Equal(HMACSHA256.HashData(raw, "a Message"u8), mac);
        var v = new MacState();
        x.MPAI_AIFMAC_Verify_Setup(v, key, Algorithm.HmacSha256);
        x.MPAI_AIFMAC_Update(v, "a Messagf"u8);
        Assert.Equal(AifError.InvalidSignature, x.MPAI_AIFMAC_Verify_Finish(v, mac));
    }

    [Fact]
    public void TheCiphersGiveWhatThePlatformGivesAndXtsTheIeee1619Vector()
    {
        using var api = new SecurityApi(new TestRoot(), folder);
        var x = api.For("aim-a");
        var raw = RandomNumberGenerator.GetBytes(32);
        var iv = RandomNumberGenerator.GetBytes(16);
        var plain = RandomNumberGenerator.GetBytes(100);
        using var reference = Aes.Create();
        reference.Key = raw;

        foreach (var alg in new[] { Algorithm.CbcPkcs7, Algorithm.Ctr })
        {
            Assert.Equal(AifError.OK, x.MPAI_AIFKM_Import_Key(Key(KeyType.Aes, 0, alg, KeyUsage.Encrypt | KeyUsage.Decrypt), raw, out var key));
            var cipher = Run(x, key, alg, iv, plain, true);
            if (alg == Algorithm.CbcPkcs7) Assert.Equal(reference.EncryptCbc(plain, iv, PaddingMode.PKCS7), cipher);
            else Assert.Equal(reference.EncryptEcb(iv, PaddingMode.None).Zip(plain.Take(16), (k, p) => (byte)(k ^ p)), cipher.Take(16));
            Assert.Equal(plain, Run(x, key, alg, iv, cipher, false));
        }

        // XTS-AES-128, IEEE 1619 vector 1: both keys zero, data unit 0, 32 zero bytes.
        Assert.Equal(AifError.OK, x.MPAI_AIFKM_Import_Key(Key(KeyType.Aes, 0, Algorithm.Xts, KeyUsage.Encrypt | KeyUsage.Decrypt), new byte[32], out var xts));
        var xtsCipher = Run(x, xts, Algorithm.Xts, new byte[16], new byte[32], true);
        Assert.Equal("917CF69EBD68B2EC9B9FE9A3EADDA692CD43D2F59598ED858C02C2652FBF922E", Convert.ToHexString(xtsCipher));
        Assert.Equal(new byte[32], Run(x, xts, Algorithm.Xts, new byte[16], xtsCipher, false));

        // Where the caller passes a buffer with ivLength 0, Encrypt_Setup writes there the IV it generates (6.5, G14); decrypting takes it.
        Assert.Equal(AifError.OK, x.MPAI_AIFKM_Import_Key(Key(KeyType.Aes, 0, Algorithm.CbcPkcs7, KeyUsage.Encrypt | KeyUsage.Decrypt), raw, out var cbc));
        var generated = new byte[16];
        var ivLength = 0;
        Assert.Equal(AifError.OK, x.MPAI_AIFCIP_Encrypt_Setup(new CipherState(), cbc, Algorithm.CbcPkcs7, generated, ref ivLength));
        Assert.Equal(16, ivLength);
        Assert.NotEqual(new byte[16], generated);
        ivLength = 0;
        Assert.Equal(AifError.BufferTooSmall, x.MPAI_AIFCIP_Encrypt_Setup(new CipherState(), cbc, Algorithm.CbcPkcs7, new byte[8], ref ivLength));
        ivLength = 0;
        Assert.Equal(AifError.Failed, x.MPAI_AIFCIP_Decrypt_Setup(new CipherState(), cbc, Algorithm.CbcPkcs7, generated, ref ivLength));
        var c = Run(x, cbc, Algorithm.CbcPkcs7, generated, plain, true);
        Assert.Equal(plain, Run(x, cbc, Algorithm.CbcPkcs7, generated, c, false));
    }

    private static byte[] Run(SecurityCaller x, uint key, Algorithm alg, byte[] iv, byte[] input, bool encrypt)
    {
        var st = new CipherState();
        var ivLength = iv.Length;
        var error = encrypt ? x.MPAI_AIFCIP_Encrypt_Setup(st, key, alg, iv, ref ivLength) : x.MPAI_AIFCIP_Decrypt_Setup(st, key, alg, iv, ref ivLength);
        Assert.Equal(AifError.OK, error);
        var result = new List<byte>();
        foreach (var chunk in input.Chunk(37))
        {
            var o = new byte[chunk.Length + 16];
            Assert.Equal(AifError.OK, x.MPAI_AIFCIP_Update(st, chunk, o, out var n));
            result.AddRange(o.Take(n));
        }
        var last = new byte[input.Length + 32];
        Assert.Equal(AifError.OK, x.MPAI_AIFCIP_Finish(st, last, out var m));
        result.AddRange(last.Take(m));
        return [.. result];
    }

    [Fact]
    public void AuthenticatedEncryptionRefusesAnAlteredCiphertext()
    {
        using var api = new SecurityApi(new TestRoot(), folder);
        var x = api.For("aim-a");
        Assert.Equal(AifError.OK, x.MPAI_AIFKM_Generate_Key(Key(KeyType.Aes, 256, Algorithm.Gcm, KeyUsage.Encrypt | KeyUsage.Decrypt), out var key));
        var nonce = new byte[12];
        Assert.Equal(AifError.OK, x.MPAI_AIFRNG_Generate_Random(nonce));
        var plain = "AIH Data of an End User"u8.ToArray();
        var cipher = new byte[plain.Length + 16];
        Assert.Equal(AifError.OK, x.MPAI_AIFAEAD_Encrypt(new AeadState(), key, Algorithm.Gcm, nonce, "header"u8, plain, cipher, out var cl));
        var back = new byte[plain.Length];
        Assert.Equal(AifError.OK, x.MPAI_AIFAEAD_Decrypt(new AeadState(), key, Algorithm.Gcm, nonce, "header"u8, cipher.AsSpan(0, cl), back, out _));
        Assert.Equal(plain, back);
        Assert.Equal(AifError.InvalidSignature, x.MPAI_AIFAEAD_Decrypt(new AeadState(), key, Algorithm.Gcm, nonce, "another header"u8, cipher, back, out _));
        cipher[0] ^= 1;
        Assert.Equal(AifError.InvalidSignature, x.MPAI_AIFAEAD_Decrypt(new AeadState(), key, Algorithm.Gcm, nonce, "header"u8, cipher, back, out _));
        Assert.Equal(AifError.NotSupported, x.MPAI_AIFAEAD_Encrypt(new AeadState(), key, Algorithm.Gcm, new byte[8], [], plain, new byte[100], out _));
        if (ChaCha20Poly1305.IsSupported)
        {
            Assert.Equal(AifError.OK, x.MPAI_AIFKM_Generate_Key(Key(KeyType.ChaCha20, 256, Algorithm.ChaCha20Poly1305, KeyUsage.Encrypt | KeyUsage.Decrypt), out var cc));
            var c2 = new byte[plain.Length + 16];
            Assert.Equal(AifError.OK, x.MPAI_AIFAEAD_Encrypt(new AeadState(), cc, Algorithm.ChaCha20Poly1305, nonce, [], plain, c2, out _));
            Assert.Equal(AifError.OK, x.MPAI_AIFAEAD_Decrypt(new AeadState(), cc, Algorithm.ChaCha20Poly1305, nonce, [], c2, back, out _));
            Assert.Equal(plain, back);
        }
        Assert.Equal(AifError.Failed, x.MPAI_AIFKM_Generate_Key(Key(KeyType.ChaCha20, 128, Algorithm.ChaCha20Poly1305, KeyUsage.Encrypt), out _));
        Assert.Equal(AifError.NotSupported, x.MPAI_AIFKM_Generate_Key(Key(KeyType.Des, 64, Algorithm.CbcNoPadding, KeyUsage.Encrypt), out _));
    }

    [Fact]
    public void SignaturesVerifyWithThePublicKeyAndPureEdDsaIsNotOffered()
    {
        using var api = new SecurityApi(new TestRoot(), folder);
        var x = api.For("aim-a");
        foreach (var (type, bits, alg) in new[] { (KeyType.Ecc, 256, Algorithm.Ecdsa), (KeyType.Rsa, 2048, Algorithm.RsaPss), (KeyType.Rsa, 2048, Algorithm.RsaPkcs1v15Sign) })
        {
            Assert.Equal(AifError.OK, x.MPAI_AIFKM_Generate_Key(Key(type, bits, alg, KeyUsage.SignMessage | KeyUsage.VerifyMessage | KeyUsage.SignHash), out var key));
            var sig = new byte[512];
            Assert.Equal(AifError.OK, x.MPAI_AIFSIGN_Sign_Message(key, alg, "a Model"u8, sig, out var n));
            Assert.Equal(AifError.OK, x.MPAI_AIFSIGN_Verify_Message(key, alg, "a Model"u8, sig.AsSpan(0, n)));
            Assert.Equal(AifError.InvalidSignature, x.MPAI_AIFSIGN_Verify_Message(key, alg, "another Model"u8, sig.AsSpan(0, n)));

            // Anyone verifies with the exported public key.
            var pub = new byte[1024];
            Assert.Equal(AifError.OK, x.MPAI_AIFKM_Export_Public_Key(key, pub, out var pl));
            if (type == KeyType.Ecc)
            {
                using var ec = ECDsa.Create();
                ec.ImportSubjectPublicKeyInfo(pub.AsSpan(0, pl), out _);
                Assert.True(ec.VerifyData("a Model"u8, sig.AsSpan(0, n), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            }
            else
            {
                using var rsa = RSA.Create();
                rsa.ImportSubjectPublicKeyInfo(pub.AsSpan(0, pl), out _);
                Assert.True(rsa.VerifyData("a Model"u8, sig.AsSpan(0, n), HashAlgorithmName.SHA256, alg == Algorithm.RsaPss ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1));
            }
            var hash = SHA256.HashData("a Model"u8);
            Assert.Equal(AifError.OK, x.MPAI_AIFSIGN_Sign_Hash(key, alg, hash, sig, out n));
            Assert.Equal(AifError.NotAuthorised, x.MPAI_AIFSIGN_Verify_Hash(key, alg, hash, sig.AsSpan(0, n)));     // VERIFY_HASH not in its Policy
        }
        Assert.Equal(AifError.OK, x.MPAI_AIFKM_Generate_Key(Key(KeyType.Ecc, 256, Algorithm.PureEdDsa, KeyUsage.SignMessage), out var ed));
        Assert.Equal(AifError.NotSupported, x.MPAI_AIFSIGN_Sign_Message(ed, Algorithm.PureEdDsa, "x"u8, new byte[128], out _));
    }

    [Fact]
    public void RsaOaepEncryptsForTheOwnerOfThePrivateKey()
    {
        using var api = new SecurityApi(new TestRoot(), folder);
        var x = api.For("aim-a");
        Assert.Equal(AifError.OK, x.MPAI_AIFKM_Generate_Key(Key(KeyType.Rsa, 2048, Algorithm.RsaOaep, KeyUsage.Encrypt | KeyUsage.Decrypt), out var key));
        var cipher = new byte[256];
        Assert.Equal(AifError.OK, x.MPAI_AIFASYM_Encrypt(key, Algorithm.RsaOaep, "a short secret"u8, [], cipher, out var n));
        var plain = new byte[256];
        Assert.Equal(AifError.OK, x.MPAI_AIFASYM_Decrypt(key, Algorithm.RsaOaep, cipher.AsSpan(0, n), [], plain, out var m));
        Assert.Equal("a short secret", Encoding.UTF8.GetString(plain, 0, m));
        Assert.Equal(AifError.NotSupported, x.MPAI_AIFASYM_Encrypt(key, Algorithm.RsaOaep, "x"u8, "a label"u8, cipher, out _));
    }

    // Two AIMs agree a key (ECDH), each derives the same AES key from it with HKDF (6.3.3 to 6.3.6), and one decrypts what the other encrypted.
    [Fact]
    public void TwoOwnersAgreeAKeyAndDeriveTheSameEncryptionKey()
    {
        using var api = new SecurityApi(new TestRoot(), folder);
        var a = api.For("aim-a");
        var b = api.For("aim-b");
        var agree = Key(KeyType.Ecc, 256, Algorithm.Ecdh, KeyUsage.Derive);
        Assert.Equal(AifError.OK, a.MPAI_AIFKM_Generate_Key(agree, out var ka));
        Assert.Equal(AifError.OK, b.MPAI_AIFKM_Generate_Key(agree, out var kb));
        var pa = new byte[200]; var pb = new byte[200];
        a.MPAI_AIFKM_Export_Public_Key(ka, pa, out var la);
        b.MPAI_AIFKM_Export_Public_Key(kb, pb, out var lb);

        var sa = new byte[32]; var sb = new byte[32];
        Assert.Equal(AifError.OK, a.MPAI_AIFKX_Raw_Key_Agreement(Algorithm.Ecdh, ka, pb.AsSpan(0, lb), sa, out _));
        Assert.Equal(AifError.OK, b.MPAI_AIFKX_Raw_Key_Agreement(Algorithm.Ecdh, kb, pa.AsSpan(0, la), sb, out _));
        Assert.Equal(sa, sb);
        Assert.Equal(AifError.NotSupported, a.MPAI_AIFKX_Raw_Key_Agreement(Algorithm.Ffdh, ka, pb.AsSpan(0, lb), sa, out _));

        uint Derived(SecurityCaller who, uint mine, byte[] theirs)
        {
            var op = new KeyDerivationOperation();
            Assert.Equal(AifError.OK, who.MPAI_AIFKD_Setup(op, Algorithm.HkdfSha256));
            Assert.Equal(AifError.OK, who.MPAI_AIFKD_Input_Bytes(op, DerivationStep.Salt, "MPAI"u8));
            Assert.Equal(AifError.OK, who.MPAI_AIFKX_Key_Derivation_Key_Agreement(op, DerivationStep.Secret, mine, theirs));
            Assert.Equal(AifError.OK, who.MPAI_AIFKD_Input_Bytes(op, DerivationStep.Info, "channel 1"u8));
            Assert.Equal(AifError.OK, who.MPAI_AIFKD_Output_Key(Key(KeyType.Aes, 256, Algorithm.Gcm, KeyUsage.Encrypt | KeyUsage.Decrypt), op, out var key));
            who.MPAI_AIFKD_Abort(op);
            return key;
        }
        var ea = Derived(a, ka, pb[..lb]);
        var eb = Derived(b, kb, pa[..la]);
        var nonce = new byte[12];
        var cipher = new byte[40];
        Assert.Equal(AifError.OK, a.MPAI_AIFAEAD_Encrypt(new AeadState(), ea, Algorithm.Gcm, nonce, [], "from a to b"u8, cipher, out var cl));
        var plain = new byte[40];
        Assert.Equal(AifError.OK, b.MPAI_AIFAEAD_Decrypt(new AeadState(), eb, Algorithm.Gcm, nonce, [], cipher.AsSpan(0, cl), plain, out var pl));
        Assert.Equal("from a to b", Encoding.UTF8.GetString(plain, 0, pl));
        Assert.Equal(AifError.NotAuthorised, b.MPAI_AIFAEAD_Decrypt(new AeadState(), ea, Algorithm.Gcm, nonce, [], cipher.AsSpan(0, cl), plain, out _));
    }

    [Fact]
    public void RandomBytesComeFromTheRootOfTrust()
    {
        using var api = new SecurityApi(new TestRoot(), folder);
        var x = api.For("aim-a");
        var r1 = new byte[48]; var r2 = new byte[48];
        Assert.Equal(AifError.OK, x.MPAI_AIFRNG_Generate_Random(r1));
        Assert.Equal(AifError.OK, x.MPAI_AIFRNG_Generate_Random(r2));
        Assert.NotEqual(r1, r2);
        Assert.Equal(AifError.Failed, x.MPAI_AIFAT_Get_Token(new byte[20], new byte[4000], out _));       // a challenge of 32, 48 or 64 bytes only
    }
}

// THE SECURITY API ON THE SIMULATED TPM 2.0: the master key sealed to the registers of the party, and the attestation token over the challenge of a Verifier.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
[Collection(Timing.Name)]
public class SecurityApiTpmTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "mpai-security-tpm-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(folder, true); } catch { } }

    private static readonly Lazy<SimulatedManufacturer> manufacturer = new(() => SimulatedManufacturer.Make());
    private static string Bin => AppContext.BaseDirectory;
    private static Dictionary<string, string> Reference() => PartyCode.Of(Bin).ToDictionary(c => c.File, c => c.Sha256, StringComparer.Ordinal);

    [Fact]
    public void TheTokenProvesThePartyAndItsCodeOverTheChallengeOfTheVerifier()
    {
        var (host, at) = TpmSimulators.At(TpmSimulators.Alone);
        var file = Path.Combine(Path.GetTempPath(), $"mpai-security-tpm-party-{Guid.NewGuid():N}.json");
        TpmParty.Provision(file, "health-back-end", host, at, manufacturer.Value, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1));
        try
        {
            var (anchor, tpm) = TpmParty.Load(file);
            using (tpm)
            {
                PartyCode.MeasureInto(tpm, Bin);
                using var api = new SecurityApi(tpm, folder);
                Assert.True(api.Opened);
                var controller = api.For("Controller");
                var policy = new Attestation.Policy(manufacturer.Value.Root, m => PartyCode.Approve(Reference(), m));

                var challenge = RandomNumberGenerator.GetBytes(32);
                Assert.Equal(AifError.OK, controller.MPAI_AIFAT_Get_Token_Size(32, out var size));
                Assert.Equal(AifError.BufferTooSmall, controller.MPAI_AIFAT_Get_Token(challenge, new byte[10], out _));
                var buffer = new byte[size];
                Assert.Equal(AifError.OK, controller.MPAI_AIFAT_Get_Token(challenge, buffer, out var n));
                var token = buffer[..n];
                string Checked(byte[] t, byte[] c, Attestation.Policy p, ECDsa key) => AttestationToken.Verify(t, key, c, p, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1)) ?? "proven";

                Assert.Equal("proven", Checked(token, challenge, policy, anchor.PublicKey));
                Assert.Equal("the token is not over the challenge of the Verifier", Checked(token, RandomNumberGenerator.GetBytes(32), policy, anchor.PublicKey));
                using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                Assert.Equal("the token is not signed by the identity key of the party", Checked(token, challenge, policy, other));
                var altered = Reference();
                altered[altered.Keys.First(k => k.StartsWith("AIF.Security", StringComparison.Ordinal))] = new string('0', 64);
                Assert.NotEqual("proven", Checked(token, challenge, new Attestation.Policy(manufacturer.Value.Root, m => PartyCode.Approve(altered, m)), anchor.PublicKey));
                var changed = (byte[])token.Clone();
                changed[^70] ^= 1;
                Assert.NotEqual("proven", Checked(changed, challenge, policy, anchor.PublicKey));

                // A key kept by the party opens again while its code is the same; not after other code is measured.
                Assert.Equal(AifError.OK, controller.MPAI_AIFSS_Storage_Init("grants", "the Grants of the Module"u8, StorageFlags.None));
            }
            var (_, rebooted) = TpmParty.Load(file);
            using (rebooted)
            {
                PartyCode.MeasureInto(rebooted, Bin);
                using (var api = new SecurityApi(rebooted, folder))
                {
                    Assert.True(api.Opened);
                    var buffer = new byte[64];
                    Assert.Equal(AifError.OK, api.For("Controller").MPAI_AIFSS_Storage_Read("grants", buffer, out var n));
                    Assert.Equal("the Grants of the Module", Encoding.UTF8.GetString(buffer, 0, n));
                }
                rebooted.Measure(Attestation.ImplementationRegister, "implementation not approved", new string('C', 64));
                using (var api = new SecurityApi(rebooted, folder))
                    Assert.False(api.Opened);
            }
        }
        finally { File.Delete(file); }
    }
}
