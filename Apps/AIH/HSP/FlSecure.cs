using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using AIF.Controller;
using AIF.RootOfTrust;
using AIF.Security;
using AIF.SharedStorage;
using AIF.Trust;

namespace Mpai.Aih.Hsp;

// FEDERATED LEARNING IN THE SECURE PROFILE: the pieces that the federated-learning demonstration (FlDemo --secure) and its test use. FlDemo calls the
// federated-learning components directly (no Controller, no Module); here each party - the Back End and each End User's Front End - has a root of trust of its own
// (a simulated TPM 2.0) and a Security API (AIF V3.0, Security API), and uses it for:
//   - the End User's vault (the cases and every version of the NN Model) kept sealed: SealedSharedStorage, AES-256-GCM under a persistent key of the vault's Owner;
//   - the Back End's watermark key, a persistent key of the Back End in its Security API (FlWatermarkKey);
//   - each Front End signing the model it returns (MPAI_AIFSIGN_Sign_Message, ECDSA P-256) and giving its Attestation token over a challenge of the Back End
//     (MPAI_AIFAT_Get_Token); the Back End accepts an update only where the token proves approved code and the signature verifies (FlUpdateGate).
// None of this is standard text: the Secure Profile of AIF V3.0 gives the Trusted Services; how a federated-learning round uses them is a choice of this demonstration.

// THE CODE OF A PARTY OF THE DEMONSTRATION: the AIF assemblies (PartyCode) and the training code of the Front End (Mpai.Aih.Hfl.dll), each by its SHA-256, into the
// code register. A party with ALTERED code measures another SHA-256 for one of those files.
public static class FlCode
{
    public const string Trainer = "Mpai.Aih.Hfl.dll";

    public static IReadOnlyList<(string File, string Sha256)> Of(string directory)
    {
        var list = PartyCode.Of(directory).ToList();
        var trainer = Path.Combine(directory, Trainer);
        if (File.Exists(trainer)) list.Add((Trainer, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(trainer)))));
        return list;
    }

    public static Dictionary<string, string> Reference(string directory) => Of(directory).ToDictionary(c => c.File, c => c.Sha256, StringComparer.Ordinal);

    // The file whose measurement an altered party changes: the trainer where it is there, otherwise the first AIF assembly.
    public static string AlteredFile(string directory) => Of(directory).Any(c => c.File == Trainer) ? Trainer : Of(directory)[0].File;

    public static void MeasureInto(IRootOfTrust root, string directory, string? alteredFile = null)
    {
        foreach (var (file, sha256) in Of(directory))
            root.Measure(Attestation.CodeRegister, PartyCode.Prefix + file, file == alteredFile ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("altered " + sha256))) : sha256);
    }

    public static Attestation.Policy Policy(SimulatedManufacturer manufacturer, IReadOnlyDictionary<string, string> reference) =>
        new(manufacturer.Root, m => PartyCode.Approve(reference, m));
}

// A PARTY OF THE DEMONSTRATION: its TPM (provisioned once, enrolled by the manufacturer), its code measured, then its Security API opened on its folder. Restart
// boots the TPM again (its registers from zero) and measures the code again - approved, or altered.
public sealed class FlParty : IDisposable
{
    private readonly string partyFile, securityFolder, codeDirectory;
    private SimulatedTpm? tpm;

    public string Name { get; }
    public int TpmPort { get; }
    public ECDsa IdentityKey { get; private set; } = null!;
    public SecurityApi Security { get; private set; } = null!;
    public bool Altered { get; private set; }

    public FlParty(string name, string folder, int tpmPort, SimulatedManufacturer manufacturer, string codeDirectory, bool altered = false)
    {
        Name = name; TpmPort = tpmPort; this.codeDirectory = codeDirectory;
        Directory.CreateDirectory(folder);
        partyFile = Path.Combine(folder, "party.json");
        securityFolder = Path.Combine(folder, "security");
        var (host, port) = TpmSimulators.At(tpmPort);
        if (!File.Exists(partyFile))
            TpmParty.Provision(partyFile, name, host, port, manufacturer, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1));
        Start(altered);
    }

    private void Start(bool altered)
    {
        var (anchor, booted) = TpmParty.Load(partyFile);
        tpm = booted;
        IdentityKey = anchor.PublicKey;
        FlCode.MeasureInto(booted, codeDirectory, altered ? FlCode.AlteredFile(codeDirectory) : null);
        Security = new SecurityApi(booted, securityFolder);
        Altered = altered;
    }

    public void Restart(bool altered)
    {
        Security.Dispose();
        tpm?.Dispose();
        Start(altered);
    }

    public SecurityCaller For(string owner) => Security.For(owner);

    public void Dispose()
    {
        Security?.Dispose();
        tpm?.Dispose();
    }
}

// THE END USER'S VAULT, SEALED: every value is kept AES-256-GCM-encrypted under a persistent key of the vault's Owner (the pattern of SealedRuledStorage), the
// key of the value as associated data, so a value moved to another key does not open. The keys (the names of the values) are not encrypted; the values are. Where the
// Security API does not open (other code measured), the vault cannot be made: its key is not found and cannot be generated.
public sealed class SealedSharedStorage : ISharedStorage
{
    private const string KeyArea = "sealed-vault-key";
    private readonly ISharedStorage inner;
    private readonly SecurityCaller security;
    private readonly uint key;

    public SealedSharedStorage(ISharedStorage inner, SecurityCaller security)
    {
        this.inner = inner;
        this.security = security;
        key = SealedRuledStorage.KeyOf(security, KeyArea, new KeyAttributes(KeyType.Aes, 256, KeyLifetime.Persistent, Algorithm.Gcm, KeyUsage.Encrypt | KeyUsage.Decrypt));
    }

    private static byte[] Associated(string k) => Encoding.UTF8.GetBytes("vault\n" + k);

    public void MPAI_AIFM_SharedStorage_Put(string k, byte[] data)
    {
        data ??= [];
        var nonce = new byte[12];
        Check(security.MPAI_AIFRNG_Generate_Random(nonce), "give random bytes");
        var sealedData = new byte[data.Length + 16];
        Check(security.MPAI_AIFAEAD_Encrypt(new AeadState(), key, Algorithm.Gcm, nonce, Associated(k), data, sealedData, out _), "seal a value");
        inner.MPAI_AIFM_SharedStorage_Put(k, [.. nonce, .. sealedData]);
    }

    public byte[] MPAI_AIFM_SharedStorage_Get(string k)
    {
        var stored = inner.MPAI_AIFM_SharedStorage_Get(k);
        if (stored.Length < 28) throw new InvalidDataException($"The value at '{k}' is not sealed.");
        var plain = new byte[stored.Length - 28];
        var e = security.MPAI_AIFAEAD_Decrypt(new AeadState(), key, Algorithm.Gcm, stored.AsSpan(0, 12), Associated(k), stored.AsSpan(12), plain, out var n);
        if (e != AifError.OK) throw new InvalidDataException($"The value at '{k}' does not open ({e}): altered, moved, or not sealed by this vault.");
        return plain[..n];
    }

    public void MPAI_AIFM_SharedStorage_Delete(string k) => inner.MPAI_AIFM_SharedStorage_Delete(k);
    public IReadOnlyList<string> MPAI_AIFM_SharedStorage_List(string prefix) => inner.MPAI_AIFM_SharedStorage_List(prefix);
    public bool MPAI_AIFM_SharedStorage_Exists(string k) => inner.MPAI_AIFM_SharedStorage_Exists(k);

    // The provenance of the sealed value: its Length is that of the sealed value (28 bytes more than the value: the nonce and the tag).
    public KeyInfo MPAI_AIFM_SharedStorage_GetKeyInfo(string k) => inner.MPAI_AIFM_SharedStorage_GetKeyInfo(k);

    private static void Check(AifError e, string what)
    {
        if (e != AifError.OK) throw new InvalidOperationException($"The Security API did not {what}: {e}.");
    }
}

// THE BACK END'S WATERMARK KEY: a persistent key of the Back End in its Security API (RawData, 512 bits), generated once, its identifier kept in a Secure Storage
// area of the Back End. Its value is exported only into memory, to make the Key that is given to Watermark Insertion and Watermark Extraction at their Input Port.
public static class FlWatermarkKey
{
    public const string Area = "watermark-key";

    public static byte[] Material(SecurityCaller backEnd)
    {
        var key = SealedRuledStorage.KeyOf(backEnd, Area, new KeyAttributes(KeyType.RawData, 512, KeyLifetime.Persistent, Algorithm.None, KeyUsage.Export));
        var buffer = new byte[64];
        var e = backEnd.MPAI_AIFKM_Export_Key(key, buffer, out var n);
        if (e != AifError.OK) throw new InvalidOperationException($"The Security API did not give the watermark key: {e}.");
        return buffer[..n];
    }
}

// WHAT A FRONT END DOES WITH ITS SECURITY API WHEN IT RETURNS A MODEL: signs it with a persistent key of its own and answers the Back End's challenge with its token.
public static class FlFrontEndSecurity
{
    public const string SigningArea = "fl-signing-key";

    public static uint SigningKey(SecurityCaller frontEnd) =>
        SealedRuledStorage.KeyOf(frontEnd, SigningArea, new KeyAttributes(KeyType.Ecc, 256, KeyLifetime.Persistent, Algorithm.Ecdsa, KeyUsage.SignMessage | KeyUsage.VerifyMessage));

    public static byte[] PublicKey(SecurityCaller frontEnd)
    {
        var buffer = new byte[256];
        var e = frontEnd.MPAI_AIFKM_Export_Public_Key(SigningKey(frontEnd), buffer, out var n);
        if (e != AifError.OK) throw new InvalidOperationException($"The Security API did not give the public signing key: {e}.");
        return buffer[..n];
    }

    // The signature of the message, or null where the Security API cannot sign (it did not open).
    public static byte[]? Sign(SecurityCaller frontEnd, byte[] message)
    {
        if (!TryKey(frontEnd, out var key)) return null;
        var buffer = new byte[128];
        return frontEnd.MPAI_AIFSIGN_Sign_Message(key, Algorithm.Ecdsa, message, buffer, out var n) == AifError.OK ? buffer[..n] : null;
    }

    private static bool TryKey(SecurityCaller frontEnd, out uint key)
    {
        try { key = SigningKey(frontEnd); return true; }
        catch (InvalidOperationException) { key = 0; return false; }
    }

    public static byte[] Token(SecurityCaller frontEnd, byte[] challenge)
    {
        if (frontEnd.MPAI_AIFAT_Get_Token_Size(challenge.Length, out var size) != AifError.OK) throw new InvalidOperationException("The Security API gave no token size.");
        var buffer = new byte[size];
        var e = frontEnd.MPAI_AIFAT_Get_Token(challenge, buffer, out var n);
        if (e != AifError.OK) throw new InvalidOperationException($"The Security API gave no token: {e}.");
        return buffer[..n];
    }
}

// THE BACK END'S GATE: the Front Ends it knows (enrolled: the identity key of each one's root of trust and its public signing key, imported into the Back End's
// Security API), a challenge for each update it asks for (32 random bytes of its Security API, used once), and the check of an update: the token over that challenge
// proves approved code (AttestationToken.Verify with the Back End's policy), and the signature of the update verifies (MPAI_AIFSIGN_Verify_Message).
public sealed class FlUpdateGate
{
    private readonly SecurityCaller backEnd;
    private readonly Attestation.Policy policy;
    private readonly Dictionary<string, (ECDsa Identity, uint VerifyKey)> enrolled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string FrontEnd, byte[] Challenge)> challenges = new(StringComparer.Ordinal);

    public FlUpdateGate(SecurityCaller backEnd, Attestation.Policy policy)
    {
        this.backEnd = backEnd;
        this.policy = policy;
    }

    public TimeSpan MaxAge { get; init; } = TimeSpan.FromMinutes(5);

    public void Enrol(string frontEnd, ECDsa identityKey, byte[] signingPublicKey)
    {
        var e = backEnd.MPAI_AIFKM_Import_Key(new KeyAttributes(KeyType.Ecc, 256, KeyLifetime.Volatile, Algorithm.Ecdsa, KeyUsage.VerifyMessage), signingPublicKey, out var key);
        if (e != AifError.OK) throw new InvalidOperationException($"The Back End's Security API did not import the signing key of {frontEnd}: {e}.");
        enrolled[frontEnd] = (identityKey, key);
    }

    // The challenge of the Back End for the update it asks a Front End for, under an identifier of that request.
    public byte[] Challenge(string frontEnd, string request)
    {
        var challenge = new byte[32];
        var e = backEnd.MPAI_AIFRNG_Generate_Random(challenge);
        if (e != AifError.OK) throw new InvalidOperationException($"The Back End's Security API gave no random bytes: {e}.");
        challenges[request] = (frontEnd, challenge);
        return challenge;
    }

    // null where the update that answers the request is accepted; otherwise why it is refused. A challenge is used once.
    public string? Admit(string frontEnd, string request, byte[] token, byte[] update, byte[]? signature)
    {
        if (!enrolled.TryGetValue(frontEnd, out var known)) return $"{frontEnd} is not a Front End the Back End knows";
        if (!challenges.Remove(request, out var open) || open.FrontEnd != frontEnd) return $"no challenge of the Back End is open for {frontEnd} under {request}";
        if (AttestationToken.Verify(token, known.Identity, open.Challenge, policy, DateTimeOffset.UtcNow, MaxAge) is { } why) return "the token: " + why;
        if (signature is null) return "the update is not signed";
        var e = backEnd.MPAI_AIFSIGN_Verify_Message(known.VerifyKey, Algorithm.Ecdsa, update, signature);
        return e == AifError.OK ? null : $"the signature of the update does not verify ({e})";
    }
}
