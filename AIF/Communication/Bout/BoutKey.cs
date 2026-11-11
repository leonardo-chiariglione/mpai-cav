using System.Security.Cryptography;

namespace AIF.Bout;

// THE KEY THAT SIGNS OR VERIFIES A BOUT: ECDSA P-256 with SHA-256 (PTF-ALGO-SIG-ECDSA-P256-SHA256), signatures in IEEE P1363 form (r and s, 32 bytes
// each). Where .NET has ECDSA the key is .NET's (an ECDsa converts to a BoutKey), and answers at once. In a browser, where .NET in WebAssembly has no
// ECDSA, the key is the browser's own (Web Crypto), which answers only asynchronously: the application gives it, as a BoutKey that implements
// SignAsync and VerifyAsync.
public abstract class BoutKey
{
    public abstract byte[] Sign(byte[] data);
    public abstract bool Verify(byte[] data, byte[] signature);
    public abstract byte[] Spki();

    public virtual ValueTask<byte[]> SignAsync(byte[] data) => ValueTask.FromResult(Sign(data));
    public virtual ValueTask<bool> VerifyAsync(byte[] data, byte[] signature) => ValueTask.FromResult(Verify(data, signature));

    public static bool PlatformHasEcdsa { get; } = !OperatingSystem.IsBrowser();

    // A new key pair of .NET's ECDSA; in a browser the key is the browser's, given by the application.
    public static BoutKey Create() =>
        PlatformHasEcdsa ? new Platform(ECDsa.Create(ECCurve.NamedCurves.nistP256)) : throw new PlatformNotSupportedException("In a browser, the key is the browser's (Web Crypto).");

    // The public key of a SubjectPublicKeyInfo, or null.
    public static BoutKey? FromSpki(byte[] spki)
    {
        if (!PlatformHasEcdsa) throw new PlatformNotSupportedException("In a browser, the key is the browser's (Web Crypto).");
        try { var k = ECDsa.Create(); k.ImportSubjectPublicKeyInfo(spki, out _); return new Platform(k); }
        catch (CryptographicException) { return null; }
    }

    public static implicit operator BoutKey(ECDsa key) => key is null ? null! : new Platform(key);

    private sealed class Platform(ECDsa key) : BoutKey
    {
        public override byte[] Sign(byte[] data) => key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        public override bool Verify(byte[] data, byte[] signature) =>
            signature.Length == 64 && key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        public override byte[] Spki() => key.ExportSubjectPublicKeyInfo();
    }

}
