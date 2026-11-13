using System.Security.Cryptography;

using AIF.Controller;

namespace AIF.Security;

// THE HANDLE OF ONE CALLER - an AIM Instance, the Controller, the User Agent - on the Security API of its party (SecurityApi.For). Its functions are those of
// the Security API page, with their names; a byte array or a span takes the place of a pointer and its size, an out parameter that of a size_t* or key_id_t*.
// Every key and Secure Storage area it makes is its own: it is their Owner.
public sealed class SecurityCaller
{
    private readonly SecurityApi api;

    internal SecurityCaller(SecurityApi api, string owner) { this.api = api; Owner = owner; }

    public string Owner { get; }

    // ---- 4 Secure Storage
    public AifError MPAI_AIFSS_Storage_Init(string name, ReadOnlySpan<byte> data, StorageFlags flags) => api.StorageInit(Owner, name, data, flags);
    public AifError MPAI_AIFSS_Storage_Write(string name, ReadOnlySpan<byte> data) => api.StorageWrite(Owner, name, data);
    public AifError MPAI_AIFSS_Storage_Read(string name, Span<byte> data, out int length) => api.StorageRead(Owner, name, data, out length);
    public AifError MPAI_AIFSS_Storage_GetInfo(string name, out StorageInfo? info) => api.StorageInfoOf(Owner, name, out info);
    public AifError MPAI_AIFSS_Storage_Delete(string name) => api.StorageDelete(Owner, name);

    // ---- 5 Attestation
    public AifError MPAI_AIFAT_Get_Token(ReadOnlySpan<byte> challenge, Span<byte> token, out int tokenSize)
    {
        tokenSize = 0;
        if (challenge.Length is not (32 or 48 or 64)) return AifError.Failed;
        byte[] made;
        try { made = AttestationToken.Make(api.Root, challenge.ToArray()); }
        catch (Exception) { return AifError.Failed; }
        return SecurityApi.Out(made, token, out tokenSize);
    }

    public AifError MPAI_AIFAT_Get_Token_Size(int challengeSize, out int tokenSize)
    {
        tokenSize = 0;
        if (challengeSize is not (32 or 48 or 64)) return AifError.Failed;
        try { tokenSize = AttestationToken.Make(api.Root, new byte[challengeSize]).Length + AttestationToken.Margin; }
        catch (Exception) { return AifError.Failed; }
        return AifError.OK;
    }

    // ---- 6.1 Hashing
    public AifError MPAI_AIFCR_Hash_Compute(Algorithm alg, ReadOnlySpan<byte> input, Span<byte> hash, out int hashLength)
    {
        hashLength = 0;
        if (SecurityApi.HashName(alg) is not { } name) return SecurityApi.IsHash(alg) ? AifError.NotSupported : AifError.Failed;
        using var h = IncrementalHash.CreateHash(name);
        h.AppendData(input);
        return SecurityApi.Out(h.GetHashAndReset(), hash, out hashLength);
    }

    public AifError MPAI_AIFCR_Hash_Setup(HashState state, Algorithm alg)
    {
        if (state.Hash is not null) return AifError.Failed;
        if (SecurityApi.HashName(alg) is not { } name) return SecurityApi.IsHash(alg) ? AifError.NotSupported : AifError.Failed;
        state.Hash = IncrementalHash.CreateHash(name);
        return AifError.OK;
    }

    public AifError MPAI_AIFCR_Hash_Update(HashState state, ReadOnlySpan<byte> input)
    {
        if (state.Hash is null) return AifError.NotInitialized;
        state.Hash.AppendData(input);
        return AifError.OK;
    }

    public AifError MPAI_AIFCR_Hash_Finish(HashState state, Span<byte> hash, out int hashLength)
    {
        hashLength = 0;
        if (state.Hash is null) return AifError.NotInitialized;
        if (hash.Length < state.Hash.HashLengthInBytes) { hashLength = state.Hash.HashLengthInBytes; return AifError.BufferTooSmall; }
        var value = state.Hash.GetHashAndReset();
        state.Release();
        return SecurityApi.Out(value, hash, out hashLength);
    }

    public AifError MPAI_AIFCR_Hash_Verify(HashState state, ReadOnlySpan<byte> hash)
    {
        if (state.Hash is null) return AifError.NotInitialized;
        var value = state.Hash.GetHashAndReset();
        state.Release();
        return CryptographicOperations.FixedTimeEquals(value, hash) ? AifError.OK : AifError.InvalidSignature;
    }

    public AifError MPAI_AIFCR_Hash_Abort(HashState state) { state.Release(); return AifError.OK; }

    // ---- 6.2 Key management
    public AifError MPAI_AIFKM_Import_Key(KeyAttributes attributes, ReadOnlySpan<byte> data, out uint key) => api.Import(Owner, attributes, data, out key);
    public AifError MPAI_AIFKM_Generate_Key(KeyAttributes attributes, out uint key) => api.Generate(Owner, attributes, out key);
    public AifError MPAI_AIFKM_Copy_Key(uint sourceKey, KeyAttributes attributes, out uint targetKey) => api.Copy(Owner, sourceKey, attributes, out targetKey);
    public AifError MPAI_AIFKM_Destroy_Key(uint key) => api.Destroy(Owner, key);
    public AifError MPAI_AIFKM_Export_Key(uint key, Span<byte> data, out int dataLength) => api.Export(Owner, key, data, out dataLength);
    public AifError MPAI_AIFKM_Export_Public_Key(uint key, Span<byte> data, out int dataLength) => api.ExportPublic(Owner, key, data, out dataLength);

    // ---- 6.3 Key exchange and key derivation
    public AifError MPAI_AIFKX_Raw_Key_Agreement(Algorithm alg, uint privateKey, ReadOnlySpan<byte> peerKey, Span<byte> output, out int outputLength)
    {
        outputLength = 0;
        var error = api.Agree(Owner, alg, privateKey, peerKey, out var secret);
        if (error != AifError.OK) return error;
        error = SecurityApi.Out(secret, output, out outputLength);
        CryptographicOperations.ZeroMemory(secret);
        return error;
    }

    public AifError MPAI_AIFKX_Key_Derivation_Key_Agreement(KeyDerivationOperation operation, DerivationStep step, uint privateKey, ReadOnlySpan<byte> peerKey)
    {
        if (!operation.Active) return AifError.NotInitialized;
        var error = api.Agree(Owner, Algorithm.Ecdh, privateKey, peerKey, out var secret);
        if (error != AifError.OK) return error;
        error = MPAI_AIFKD_Input_Bytes(operation, step, secret);
        CryptographicOperations.ZeroMemory(secret);
        return error;
    }

    public AifError MPAI_AIFKD_Setup(KeyDerivationOperation operation, Algorithm alg)
    {
        if (operation.Active || alg is not (Algorithm.HkdfSha256 or Algorithm.HkdfSha384 or Algorithm.HkdfSha512)) return AifError.Failed;
        operation.Algorithm = alg;
        operation.Active = true;
        return AifError.OK;
    }

    public AifError MPAI_AIFKD_Input_Bytes(KeyDerivationOperation operation, DerivationStep step, ReadOnlySpan<byte> data)
    {
        if (!operation.Active) return AifError.NotInitialized;
        if (operation.Used > 0) return AifError.Failed;                                // inputs come before the first output
        switch (step)
        {
            case DerivationStep.Salt: operation.Salt = data.ToArray(); break;
            case DerivationStep.Secret: operation.Secret = data.ToArray(); break;
            case DerivationStep.Info: operation.Info = data.ToArray(); break;
        }
        return AifError.OK;
    }

    public AifError MPAI_AIFKD_Output_Key(KeyAttributes attributes, KeyDerivationOperation operation, out uint key) => api.Derive(Owner, attributes, operation, out key);

    public AifError MPAI_AIFKD_Abort(KeyDerivationOperation operation) { operation.Release(); return AifError.OK; }

    // ---- 6.4 Message authentication code
    public AifError MPAI_AIFMAC_Sign_Setup(MacState state, uint key, Algorithm alg) => api.MacSetup(Owner, state, key, alg, false);
    public AifError MPAI_AIFMAC_Verify_Setup(MacState state, uint key, Algorithm alg) => api.MacSetup(Owner, state, key, alg, true);

    public AifError MPAI_AIFMAC_Update(MacState state, ReadOnlySpan<byte> input)
    {
        if (state.Mac is null) return AifError.NotInitialized;
        state.Mac.AppendData(input);
        return AifError.OK;
    }

    public AifError MPAI_AIFMAC_Sign_Finish(MacState state, Span<byte> mac, out int macLength)
    {
        macLength = 0;
        if (state.Mac is null || state.Verifying) return AifError.NotInitialized;
        if (mac.Length < state.Mac.HashLengthInBytes) { macLength = state.Mac.HashLengthInBytes; return AifError.BufferTooSmall; }
        var value = state.Mac.GetHashAndReset();
        state.Release();
        return SecurityApi.Out(value, mac, out macLength);
    }

    public AifError MPAI_AIFMAC_Verify_Finish(MacState state, ReadOnlySpan<byte> mac)
    {
        if (state.Mac is null || !state.Verifying) return AifError.NotInitialized;
        var value = state.Mac.GetHashAndReset();
        state.Release();
        return CryptographicOperations.FixedTimeEquals(value, mac) ? AifError.OK : AifError.InvalidSignature;
    }

    public AifError MPAI_AIFMAC_Abort(MacState state) { state.Release(); return AifError.OK; }

    // ---- 6.5 Ciphers. Where an algorithm needs an IV, the caller gives it in iv[..ivLength]; or, encrypting, passes ivLength 0 and a buffer of 16 bytes, in which
    // the IV generated by the Implementation is written, ivLength its size; the caller sends it with the data. Decrypt_Setup always takes the IV.
    public AifError MPAI_AIFCIP_Encrypt_Setup(CipherState state, uint key, Algorithm alg, Span<byte> iv, ref int ivLength) => api.CipherSetup(Owner, state, key, alg, iv, true, ref ivLength);
    public AifError MPAI_AIFCIP_Decrypt_Setup(CipherState state, uint key, Algorithm alg, Span<byte> iv, ref int ivLength) => api.CipherSetup(Owner, state, key, alg, iv, false, ref ivLength);

    // The output needs at most the input and one block (16 bytes) more.
    public AifError MPAI_AIFCIP_Update(CipherState state, ReadOnlySpan<byte> input, Span<byte> output, out int outputLength)
    {
        outputLength = 0;
        if (!state.Active) return AifError.NotInitialized;
        if (output.Length < input.Length + 16) return AifError.BufferTooSmall;
        switch (state.Algorithm)
        {
            case Algorithm.Ctr:
                for (var i = 0; i < input.Length; i++) output[i] = (byte)(input[i] ^ NextKeyStreamByte(state));
                outputLength = input.Length;
                return AifError.OK;
            case Algorithm.Xts:
                state.Pending = [.. state.Pending, .. input];                             // a data unit is processed whole, at Finish
                return AifError.OK;
            default:
                var all = (byte[])[.. state.Pending, .. input];
                var whole = all.Length / 16 * 16;
                // decrypting with PKCS7, the last whole block is kept for Finish, which removes the padding
                if (!state.Encrypting && state.Algorithm == Algorithm.CbcPkcs7 && whole == all.Length && whole > 0) whole -= 16;
                if (whole > 0)
                {
                    var buffer = new byte[whole];
                    outputLength = state.Transform!.TransformBlock(all, 0, whole, buffer, 0);
                    buffer.AsSpan(0, outputLength).CopyTo(output);
                }
                state.Pending = all[whole..];
                return AifError.OK;
        }
    }

    public AifError MPAI_AIFCIP_Finish(CipherState state, Span<byte> output, out int outputLength)
    {
        outputLength = 0;
        if (!state.Active) return AifError.NotInitialized;
        var needed = state.Algorithm switch
        {
            Algorithm.Xts => state.Pending.Length,
            Algorithm.CbcPkcs7 when state.Encrypting => (state.Pending.Length / 16 + 1) * 16,
            Algorithm.CbcPkcs7 => 16,
            _ => 0
        };
        if (output.Length < needed) { outputLength = needed; return AifError.BufferTooSmall; }
        byte[] last;
        try
        {
            switch (state.Algorithm)
            {
                case Algorithm.Ctr:
                    last = [];
                    break;
                case Algorithm.Xts:
                    if (state.Pending.Length < 16 || state.Pending.Length % 16 != 0) { state.Release(); return AifError.NotSupported; }   // no ciphertext stealing
                    last = Xts(state);
                    break;
                case Algorithm.CbcPkcs7 when state.Encrypting:
                    var pad = 16 - state.Pending.Length % 16;
                    var padded = (byte[])[.. state.Pending, .. Enumerable.Repeat((byte)pad, pad)];
                    last = new byte[padded.Length];
                    state.Transform!.TransformBlock(padded, 0, padded.Length, last, 0);
                    break;
                case Algorithm.CbcPkcs7:
                    if (state.Pending.Length != 16) { state.Release(); return AifError.Failed; }
                    var block = new byte[16];
                    state.Transform!.TransformBlock(state.Pending, 0, 16, block, 0);
                    var p = block[15];
                    if (p is < 1 or > 16 || block[(16 - p)..].Any(b => b != p)) { state.Release(); return AifError.Failed; }
                    last = block[..(16 - p)];
                    break;
                default:                                                                 // no padding: whole blocks only
                    if (state.Pending.Length != 0) { state.Release(); return AifError.Failed; }
                    last = [];
                    break;
            }
        }
        catch (CryptographicException) { state.Release(); return AifError.Failed; }
        state.Release();
        return SecurityApi.Out(last, output, out outputLength);
    }

    public AifError MPAI_AIFCIP_Abort(CipherState state) { state.Release(); return AifError.OK; }

    private static byte NextKeyStreamByte(CipherState state)
    {
        if (state.KeyStream.Length == 0)
        {
            state.KeyStream = state.Aes!.EncryptEcb(state.Counter, PaddingMode.None);
            for (var i = 15; i >= 0; i--) if (++state.Counter[i] != 0) break;          // the counter block, big endian
        }
        var b = state.KeyStream[0];
        state.KeyStream = state.KeyStream[1..];
        return b;
    }

    // XTS-AES (IEEE 1619) on one data unit, a whole number of blocks: C = E_K1(P xor T) xor T, T multiplied by alpha in GF(2^128) after each block.
    private static byte[] Xts(CipherState state)
    {
        var data = state.Pending;
        var result = new byte[data.Length];
        var t = (byte[])state.Counter.Clone();
        var block = new byte[16];
        for (var at = 0; at < data.Length; at += 16)
        {
            for (var i = 0; i < 16; i++) block[i] = (byte)(data[at + i] ^ t[i]);
            var done = state.Encrypting ? state.Aes!.EncryptEcb(block, PaddingMode.None) : state.Aes!.DecryptEcb(block, PaddingMode.None);
            for (var i = 0; i < 16; i++) result[at + i] = (byte)(done[i] ^ t[i]);
            var carry = 0;
            for (var i = 0; i < 16; i++)
            {
                var next = t[i] >> 7;
                t[i] = (byte)((t[i] << 1) | carry);
                carry = next;
            }
            if (carry != 0) t[0] ^= 0x87;
        }
        return result;
    }

    // ---- 6.6 Authenticated encryption with associated data: the ciphertext is followed by its 16-byte tag
    public AifError MPAI_AIFAEAD_Encrypt(AeadState state, uint key, Algorithm alg, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> additionalData,
                                         ReadOnlySpan<byte> plaintext, Span<byte> ciphertext, out int ciphertextLength) =>
        api.Aead(Owner, key, alg, nonce, additionalData, plaintext, ciphertext, out ciphertextLength, true);

    public AifError MPAI_AIFAEAD_Decrypt(AeadState state, uint key, Algorithm alg, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> additionalData,
                                         ReadOnlySpan<byte> ciphertext, Span<byte> plaintext, out int plaintextLength) =>
        api.Aead(Owner, key, alg, nonce, additionalData, ciphertext, plaintext, out plaintextLength, false);

    public AifError MPAI_AIFAEAD_Abort(AeadState state) { state.Release(); return AifError.OK; }

    // ---- 6.7 Signature
    public AifError MPAI_AIFSIGN_Sign_Message(uint key, Algorithm alg, ReadOnlySpan<byte> input, Span<byte> signature, out int signatureLength) =>
        api.Sign(Owner, key, alg, input, signature, out signatureLength, false);
    public AifError MPAI_AIFSIGN_Verify_Message(uint key, Algorithm alg, ReadOnlySpan<byte> input, ReadOnlySpan<byte> signature) =>
        api.Verify(Owner, key, alg, input, signature, false);
    public AifError MPAI_AIFSIGN_Sign_Hash(uint key, Algorithm alg, ReadOnlySpan<byte> hash, Span<byte> signature, out int signatureLength) =>
        api.Sign(Owner, key, alg, hash, signature, out signatureLength, true);
    public AifError MPAI_AIFSIGN_Verify_Hash(uint key, Algorithm alg, ReadOnlySpan<byte> hash, ReadOnlySpan<byte> signature) =>
        api.Verify(Owner, key, alg, hash, signature, true);

    // ---- 6.8 Asymmetric encryption
    public AifError MPAI_AIFASYM_Encrypt(uint key, Algorithm alg, ReadOnlySpan<byte> input, ReadOnlySpan<byte> salt, Span<byte> output, out int outputLength) =>
        api.Asymmetric(Owner, key, alg, input, salt, output, out outputLength, true);
    public AifError MPAI_AIFASYM_Decrypt(uint key, Algorithm alg, ReadOnlySpan<byte> input, ReadOnlySpan<byte> salt, Span<byte> output, out int outputLength) =>
        api.Asymmetric(Owner, key, alg, input, salt, output, out outputLength, false);

    // ---- 6.9 Random generation
    public AifError MPAI_AIFRNG_Generate_Random(Span<byte> output) => api.Random(output);
}
