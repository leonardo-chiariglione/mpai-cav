using System.Security.Cryptography;

namespace AIF.Security;

// THE TYPES OF THE SECURITY API (Security API 2, Table 3; key attributes, Table 4). In C# a byte array, or a span, takes the place of a pointer and its size;
// error_t is AifError (AIF.Trust\AifError.cs), the values of the Basic API with InvalidSignature and InsufficientEntropy.

// What the root of trust of a party gives the Security API: its identity key and its Attestation Evidence (IRootOfTrust), secrets sealed to its measurements,
// and random bytes. The simulated TPM 2.0 (AIF.RootOfTrust) is one.
public interface ISecureRoot : AIF.Trust.IRootOfTrust
{
    string Name { get; }

    // The secret, sealed so that only this root of trust, with its registers as they are now, opens it (at most 128 bytes: a key).
    byte[] Seal(byte[] secret);

    // The secret, or null where the registers no longer hold what it was sealed to.
    byte[]? Unseal(byte[] sealedBlob);

    // count random bytes from the generator of the root of trust.
    byte[] Random(int count);
}

// Table 4, Type.
public enum KeyType { RawData, Hmac, Derive, Password, Aes, ChaCha20, Des, Rsa, Ecc, Dh }

// Table 4, Lifetime.
public enum KeyLifetime { Volatile, Persistent }

// Table 4, Policy: the usage flags.
[Flags]
public enum KeyUsage
{
    None = 0, Export = 1, Copy = 2, Cache = 4, Encrypt = 8, Decrypt = 16, SignMessage = 32, VerifyMessage = 64, SignHash = 128, VerifyHash = 256,
    Derive = 512, VerifyDerivation = 1024
}

// algorithm_t: the algorithms each section of the Security API lists. The hash of a signature or of a MAC is in its name; ECDSA and the RSA signatures use
// SHA-256 (ECDSA with a P-384 key, SHA-384). HKDF is 6.3.3. The MAC algorithms (6.4 lists none) are HMAC.
public enum Algorithm
{
    None,
    Md5, Sha1, Sha256, Sha384, Sha512, Sha3_256, Sha3_384, Sha3_512,                         // 6.1
    Ffdh, Ecdh,                                                                              // 6.3
    HkdfSha256, HkdfSha384, HkdfSha512,                                                      // 6.3.3
    HmacSha256, HmacSha384, HmacSha512,                                                      // 6.4
    Xts, Ctr, EcbNoPadding, CbcNoPadding, CbcPkcs7,                                          // 6.5
    Gcm, ChaCha20Poly1305,                                                                   // 6.6
    RsaPkcs1v15Sign, RsaPss, Ecdsa, PureEdDsa,                                               // 6.7
    RsaPkcs1v15Crypt, RsaOaep                                                                // 6.8
}

// key_attributes_t (Table 4). Bits is the size of the key - of the AES key, of the curve, of the RSA modulus; 0 where MPAI_AIFKM_Import_Key takes it from
// the data. Identifier and Owner are set by the Security API, never by the caller; in MPAI_AIFKM_Copy_Key, Owner names
// the Owner the caller asks for the copy.
public sealed record KeyAttributes(KeyType Type, int Bits, KeyLifetime Lifetime, Algorithm Algorithm, KeyUsage Usage)
{
    public uint Identifier { get; init; }
    public string? Owner { get; init; }
}

// flags_t of Secure Storage (Table 3).
[Flags]
public enum StorageFlags { None = 0, WriteOnce = 1, NoConfidentiality = 2, NoReplayProtection = 4 }

// storage_info_t (Table 3).
public sealed record StorageInfo(long Capacity, long Size, StorageFlags Flags);

// key_derivation_step_t (Table 3).
public enum DerivationStep { Salt, Secret, Info }

// The state types (Table 3): opaque objects, set up by the first call of an operation, released by its Finish or Abort.
public sealed class HashState
{
    internal IncrementalHash? Hash;
    internal void Release() { Hash?.Dispose(); Hash = null; }
}

public sealed class MacState
{
    internal IncrementalHash? Mac;
    internal bool Verifying;
    internal void Release() { Mac?.Dispose(); Mac = null; }
}

public sealed class CipherState
{
    internal Algorithm Algorithm;
    internal bool Encrypting;
    internal Aes? Aes;
    internal ICryptoTransform? Transform;                                               // CBC and ECB
    internal byte[] Pending = [];
    internal byte[] Counter = [];                                                       // CTR: the next counter block; XTS: the tweak
    internal byte[] KeyStream = [];                                                     // CTR: the bytes of key stream not used yet
    internal byte[]? XtsKey2;
    internal bool Active => Aes is not null;
    internal void Release() { Transform?.Dispose(); Transform = null; Aes?.Dispose(); Aes = null; Pending = []; KeyStream = []; if (XtsKey2 is not null) CryptographicOperations.ZeroMemory(XtsKey2); XtsKey2 = null; }
}

public sealed class AeadState
{
    internal void Release() { }
}

public sealed class KeyDerivationOperation
{
    internal Algorithm Algorithm;
    internal byte[]? Salt, Secret, Info;
    internal int Used;                                                                  // the bytes of output already taken
    internal bool Active;
    internal void Release() { if (Secret is not null) CryptographicOperations.ZeroMemory(Secret); Salt = Secret = Info = null; Used = 0; Active = false; }
}
