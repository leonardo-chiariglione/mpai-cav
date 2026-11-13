using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using AIF.Controller;
using AIF.Security;
using AIF.Trust;
using Mpai.Aif.Api;

namespace AIF.SecureProfile;

// THE CONTROLLER API OF THE USER AGENT, in the Secure Profile. The User Agent holds an IControllerApi as always; this one reaches the Controller through the
// Non-Secure Controller (a frame link) and seals every request and reply end to end with the Secure Controller.
//
// MPAI_AIFU_Controller_Initialize_Secure (Security API 3.1) switches the Secure Profile on: the User Agent sends a nonce and an ephemeral key, receives the
// Secure Controller's key and its Entity Attestation Token over SHA-256(nonce || its key || the Secure Controller's key), and verifies the token - signed by the
// identity key of the party it expects (its Trust Anchor), from a root of trust of a manufacturer it accepts, with code it approves, over that challenge. Only
// then does it derive the key of the session (ECDH, HKDF-SHA-256), which the Secure Controller derives too and the Non-Secure Controller never has. Until then
// every request returns NotInitialized; a token that does not verify returns NotTrusted.
public sealed class SecureControllerClient : IControllerApi
{
    private readonly IFrameLink link;
    private readonly ECDsa secureControllerIdentity;
    private readonly ECDsa identity;
    private readonly Attestation.Policy policy;
    private readonly object gate = new();
    private string? session;
    private byte[]? key;
    private long nextOut;
    private long lastIn = -1;

    // identity: the User Agent's own identity key, which signs its Hello; the Secure Controller serves only the User Agents it was given.
    public SecureControllerClient(IFrameLink nonSecureController, ECDsa identity, ECDsa secureControllerIdentity, Attestation.Policy policy)
    {
        link = nonSecureController;
        this.identity = identity;
        this.secureControllerIdentity = secureControllerIdentity;
        this.policy = policy;
    }

    // Why the last Initialize_Secure, or the last request, was refused.
    public string? Refusal { get; private set; }

    public bool Initialized { get { lock (gate) return key is not null; } }

    public AifError MPAI_AIFU_Controller_Initialize_Secure()
    {
        using var mine = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var nonce = RandomNumberGenerator.GetBytes(32);
        var uaKey = mine.ExportSubjectPublicKeyInfo();
        JsonObject? welcome;
        var hello = new JsonObject
        {
            ["Type"] = "Hello", ["Nonce"] = Convert.ToBase64String(nonce), ["UaKey"] = Convert.ToBase64String(uaKey),
            ["UaId"] = Convert.ToBase64String(identity.ExportSubjectPublicKeyInfo()), ["UaSig"] = Convert.ToBase64String(identity.SignData(Frames.Signed(nonce, uaKey), HashAlgorithmName.SHA256))
        };
        try { welcome = Frames.Parse(link.Send(Frames.Bytes(hello))); }
        catch (Exception e) { Refusal = "the Non-Secure Controller does not answer: " + e.Message; return AifError.Failed; }
        if ((string?)welcome?["Type"] != "Welcome") { Refusal = (string?)welcome?["Reason"] ?? "no Welcome"; return AifError.Failed; }

        byte[] scKey, token;
        try { scKey = Convert.FromBase64String((string)welcome["ScKey"]!); token = Convert.FromBase64String((string)welcome["Token"]!); }
        catch (Exception) { Refusal = "the Welcome is not well formed"; return AifError.Failed; }
        var challenge = Frames.Challenge(nonce, uaKey, scKey);
        if (AttestationToken.Verify(token, secureControllerIdentity, challenge, policy, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1)) is { } why)
        {
            Refusal = why;
            return AifError.NotTrusted;
        }

        using var theirs = ECDiffieHellman.Create();
        theirs.ImportSubjectPublicKeyInfo(scKey, out _);
        var secret = mine.DeriveRawSecretAgreement(theirs.PublicKey);
        var derived = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, nonce, Encoding.UTF8.GetBytes(Frames.Info));
        CryptographicOperations.ZeroMemory(secret);
        lock (gate)
        {
            session = (string)welcome["Session"]!;
            key = derived;
            nextOut = 0;
            lastIn = -1;
        }
        Refusal = null;
        return AifError.OK;
    }

    // ONE REQUEST, sealed; its reply, opened. null where the session is not made, or the reply is refused or does not open (Refusal says why).
    private JsonObject? Call(JsonObject request)
    {
        string id;
        byte[] k;
        long seq;
        lock (gate)
        {
            if (key is null || session is null) { Refusal = "the Secure Profile is not initialised"; return null; }
            id = session; k = key; seq = nextOut++;
        }
        var body = Encoding.UTF8.GetBytes(request.ToJsonString());
        var cipher = new byte[body.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(k, 16)) gcm.Encrypt(Frames.Nonce(Frames.ToSecure, seq), body, cipher, tag, Frames.Associated(id, Frames.ToSecure));
        JsonObject? reply;
        try { reply = Frames.Parse(link.Send(Frames.Bytes(new JsonObject { ["Type"] = "Sealed", ["Session"] = id, ["Seq"] = seq, ["Data"] = Convert.ToBase64String([.. cipher, .. tag]) }))); }
        catch (Exception e) { Refusal = "the Non-Secure Controller does not answer: " + e.Message; return null; }
        if ((string?)reply?["Type"] != "Sealed" || (string?)reply["Session"] != id) { Refusal = (string?)reply?["Reason"] ?? "no sealed reply"; return null; }

        long inSeq;
        byte[] data;
        try { inSeq = (long)reply["Seq"]!; data = Convert.FromBase64String((string)reply["Data"]!); }
        catch (Exception) { Refusal = "the reply is not well formed"; return null; }
        if (data.Length < 16) { Refusal = "the reply is not well formed"; return null; }
        var plain = new byte[data.Length - 16];
        lock (gate)
        {
            if (inSeq <= lastIn) { Refusal = "a reply already received (replay)"; return null; }
            try
            {
                using var gcm = new AesGcm(k, 16);
                gcm.Decrypt(Frames.Nonce(Frames.ToUserAgent, inSeq), data.AsSpan(0, data.Length - 16), data.AsSpan(data.Length - 16), plain, Frames.Associated(id, Frames.ToUserAgent));
            }
            catch (CryptographicException) { Refusal = "the reply is not of this session, or was altered"; return null; }
            lastIn = inSeq;
        }
        return JsonNode.Parse(plain)!.AsObject();
    }

    private AifError ErrorOf(JsonObject request)
    {
        if (Call(request) is not { } reply) return Initialized ? AifError.NotTrusted : AifError.NotInitialized;
        return Enum.TryParse<AifError>((string?)reply["Error"], out var e) ? e : AifError.Failed;
    }

    private static JsonObject R(string call, string module) => new() { ["Call"] = call, ["Module"] = module };

    public AifError StartFlow(string moduleName) => ErrorOf(R("StartFlow", moduleName));
    public void StopFlow(string moduleName) => ErrorOf(R("StopFlow", moduleName));
    public AifError Pause(string moduleName) => ErrorOf(R("Pause", moduleName));
    public AifError Resume(string moduleName) => ErrorOf(R("Resume", moduleName));
    public AifError StopAim(string moduleName, string aimName) { var r = R("StopAim", moduleName); r["Aim"] = aimName; return ErrorOf(r); }
    public AifError SharedStorageInit(string moduleName, string location) { var r = R("SharedStorageInit", moduleName); r["Location"] = location; return ErrorOf(r); }

    public AifError InputWrite(string moduleName, string dataType, int portNumber, string json, int timeoutMs = -1)
    {
        var r = R("InputWrite", moduleName);
        r["DataType"] = dataType; r["PortNumber"] = portNumber; r["Json"] = json; r["TimeoutMs"] = timeoutMs;
        return ErrorOf(r);
    }

    public ControllerApi.Read OutputRead(string moduleName, string dataType, int portNumber, int timeoutMs = -1)
    {
        var r = R("OutputRead", moduleName);
        r["DataType"] = dataType; r["PortNumber"] = portNumber; r["TimeoutMs"] = timeoutMs;
        if (Call(r) is not { } reply) return new ControllerApi.Read(Initialized ? AifError.NotTrusted : AifError.NotInitialized, null);
        return new ControllerApi.Read(Enum.Parse<AifError>((string)reply["Error"]!), (string?)reply["Json"]);
    }

    public ControllerApi.Result Advance(string moduleName, IEnumerable<ControllerApi.Datum> inputs)
    {
        var r = R("Advance", moduleName);
        r["Inputs"] = SecureController.Data(inputs);
        if (Call(r) is not { } reply) return new ControllerApi.Result(Initialized ? AifError.NotTrusted : AifError.NotInitialized, []);
        var outputs = (reply["Outputs"] as JsonArray ?? []).Select(o => new ControllerApi.Datum((string)o!["DataType"]!, (int)o["PortNumber"]!, (string)o["Json"]!)).ToList();
        return new ControllerApi.Result(Enum.Parse<AifError>((string)reply["Error"]!), outputs);
    }

    public ControllerApi.ModuleStatus Status(string moduleName)
    {
        if (Call(R("Status", moduleName)) is not { } reply) return new ControllerApi.ModuleStatus(Initialized ? AifError.NotTrusted : AifError.NotInitialized, []);
        var aims = (reply["Aims"] as JsonArray ?? []).Select(a => new AimReport((string)a!["Aim"]!, Enum.Parse<AimStatus>((string)a["Status"]!), (string?)a["Reason"] ?? "",
                                                                                (a["Reports"] as JsonArray ?? []).Select(x => (string)x!).ToList())).ToList();
        return new ControllerApi.ModuleStatus(Enum.Parse<AifError>((string)reply["Error"]!), aims)
        {
            Stopped = (bool?)reply["Stopped"] ?? false,
            State = Enum.TryParse<ModuleState>((string?)reply["State"], out var st) ? st : ModuleState.Running
        };
    }

    public AifError SharedStorageStore(string moduleName, string key, byte[] data, string category = "Data")
    {
        var r = R("SharedStorageStore", moduleName);
        r["Key"] = key; r["Data"] = Convert.ToBase64String(data); r["Category"] = category;
        return ErrorOf(r);
    }

    public AifError SharedStorageRead(string moduleName, string key, out byte[] data)
    {
        data = [];
        var r = R("SharedStorageRead", moduleName);
        r["Key"] = key;
        if (Call(r) is not { } reply) return Initialized ? AifError.NotTrusted : AifError.NotInitialized;
        data = Convert.FromBase64String((string?)reply["Data"] ?? "");
        return Enum.Parse<AifError>((string)reply["Error"]!);
    }

    public AifError SharedStorageDelete(string moduleName, string key) { var r = R("SharedStorageDelete", moduleName); r["Key"] = key; return ErrorOf(r); }

    public IReadOnlyList<string> SharedStorageList(string moduleName, string prefix = "")
    {
        var r = R("SharedStorageList", moduleName);
        r["Prefix"] = prefix;
        return Call(r) is { } reply ? (reply["Keys"] as JsonArray ?? []).Select(k => (string)k!).ToList() : [];
    }

    public AifError ExternalAuthority(string moduleName, string policy, string host = "127.0.0.1", int port = 0)
    {
        var r = R("ExternalAuthority", moduleName);
        r["Policy"] = policy; r["Host"] = host; r["Port"] = port;
        return ErrorOf(r);
    }

    public AifError ExternalJoin(string moduleName, string authority, string? rendezvous, string? role = null, string? controllerId = null)
    {
        var r = R("ExternalJoin", moduleName);
        r["Authority"] = authority; r["Rendezvous"] = rendezvous; r["Role"] = role; r["ControllerId"] = controllerId;
        return ErrorOf(r);
    }

    public AifError ExternalLeave(string moduleName) => ErrorOf(R("ExternalLeave", moduleName));
    public string? ExternalAnchor(string moduleName) => (string?)Call(R("ExternalAnchor", moduleName))?["Value"];
    public string? ExternalRendezvous(string moduleName) => (string?)Call(R("ExternalRendezvous", moduleName))?["Value"];
}
