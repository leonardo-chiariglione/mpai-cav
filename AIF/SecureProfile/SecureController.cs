using System.Text;
using System.Text.Json.Nodes;

using AIF.Controller;
using AIF.Security;
using Mpai.Aif.Api;

namespace AIF.SecureProfile;

// THE SECURE CONTROLLER (Zero Trust and Profiles 6.2): in the area where secure code is executed. It holds the Controller - which interfaces with the Module,
// whose AIMs execute in the same area - and the Security API on the root of trust of the party, whose Secure Storage and keys it alone reaches.
//
// It answers two frames (Frames.cs). Hello: it makes an ephemeral key (MPAI_AIFKM_Generate_Key, ECDH), gives its Entity Attestation Token over the challenge
// that binds that key (MPAI_AIFAT_Get_Token), and derives with the key of the User Agent the key of the session (MPAI_AIFKX_Key_Derivation_Key_Agreement,
// HKDF-SHA-256, an AES-256-GCM key of its own). Sealed: it opens the request with the key of the session (MPAI_AIFAEAD_Decrypt), refuses a sequence number it
// has seen (replay), executes the request of the Controller API on the Controller, and seals the reply. The Non-Secure Controller, which relays the frames,
// never has the key.
public sealed class SecureController : IFrameLink, IDisposable
{
    private readonly IControllerApi controller;
    private readonly SecurityCaller security;
    private readonly Func<byte[], bool> serves;
    private readonly object gate = new();
    private readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);

    private sealed class Session
    {
        public uint Key;
        public readonly SeenSequences Seen = new();
        public long NextOut;
    }

    // serves(identity): whether the Secure Controller serves the User Agent whose identity key (SubjectPublicKeyInfo) this is - the User's decision.
    public SecureController(IControllerApi controller, SecurityApi security, Func<byte[], bool> serves)
    {
        this.controller = controller;
        this.serves = serves;
        this.security = security.For("Controller");
    }

    public int Sessions { get { lock (gate) return sessions.Count; } }

    public byte[] Send(byte[] frame)
    {
        var f = Frames.Parse(frame);
        var reply = (string?)f?["Type"] switch
        {
            "Hello" => Hello(f!),
            "Sealed" => Sealed(f!),
            _ => Frames.Refused("not a frame of the Secure Controller")
        };
        return Frames.Bytes(reply);
    }

    private JsonObject Hello(JsonObject f)
    {
        byte[] nonce, uaKey, uaId, uaSig;
        try
        {
            nonce = Convert.FromBase64String((string)f["Nonce"]!); uaKey = Convert.FromBase64String((string)f["UaKey"]!);
            uaId = Convert.FromBase64String((string)f["UaId"]!); uaSig = Convert.FromBase64String((string)f["UaSig"]!);
        }
        catch (Exception) { return Frames.Refused("a Hello needs a Nonce, a UaKey, a UaId and a UaSig"); }
        if (nonce.Length != 32) return Frames.Refused("the Nonce is 32 bytes");
        if (!serves(uaId)) return Frames.Refused("not a User Agent this Controller serves");
        using (var id = System.Security.Cryptography.ECDsa.Create())
        {
            try { id.ImportSubjectPublicKeyInfo(uaId, out _); }
            catch (System.Security.Cryptography.CryptographicException) { return Frames.Refused("the UaId is not a key"); }
            if (!id.VerifyData(Frames.Signed(nonce, uaKey), uaSig, System.Security.Cryptography.HashAlgorithmName.SHA256))
                return Frames.Refused("the Hello is not signed by the User Agent");
        }

        var ephemeral = new KeyAttributes(KeyType.Ecc, 256, KeyLifetime.Volatile, Algorithm.Ecdh, KeyUsage.Derive);
        if (security.MPAI_AIFKM_Generate_Key(ephemeral, out var mine) is var e1 && e1 != AifError.OK) return Frames.Refused("no key: " + e1);
        try
        {
            var pub = new byte[200];
            security.MPAI_AIFKM_Export_Public_Key(mine, pub, out var pubLength);
            var scKey = pub[..pubLength];

            var challenge = Frames.Challenge(nonce, uaKey, scKey);
            security.MPAI_AIFAT_Get_Token_Size(challenge.Length, out var size);
            var token = new byte[size];
            if (security.MPAI_AIFAT_Get_Token(challenge, token, out var tokenLength) is var e2 && e2 != AifError.OK) return Frames.Refused("no token: " + e2);

            var op = new KeyDerivationOperation();
            security.MPAI_AIFKD_Setup(op, Algorithm.HkdfSha256);
            security.MPAI_AIFKD_Input_Bytes(op, DerivationStep.Salt, nonce);
            if (security.MPAI_AIFKX_Key_Derivation_Key_Agreement(op, DerivationStep.Secret, mine, uaKey) is var e3 && e3 != AifError.OK) return Frames.Refused("no agreement: " + e3);
            security.MPAI_AIFKD_Input_Bytes(op, DerivationStep.Info, Encoding.UTF8.GetBytes(Frames.Info));
            var e4 = security.MPAI_AIFKD_Output_Key(new KeyAttributes(KeyType.Aes, 256, KeyLifetime.Volatile, Algorithm.Gcm, KeyUsage.Encrypt | KeyUsage.Decrypt), op, out var sessionKey);
            security.MPAI_AIFKD_Abort(op);
            if (e4 != AifError.OK) return Frames.Refused("no session key: " + e4);

            var id = Convert.ToHexString(RandomBytes(16));
            lock (gate) sessions[id] = new Session { Key = sessionKey };
            return new JsonObject
            {
                ["Type"] = "Welcome", ["Session"] = id, ["ScKey"] = Convert.ToBase64String(scKey),
                ["Token"] = Convert.ToBase64String(token, 0, tokenLength), ["Party"] = security.Owner
            };
        }
        finally { security.MPAI_AIFKM_Destroy_Key(mine); }
    }

    private byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        security.MPAI_AIFRNG_Generate_Random(b);
        return b;
    }

    private JsonObject Sealed(JsonObject f)
    {
        var id = (string?)f["Session"];
        Session? s;
        lock (gate) s = id is not null && sessions.TryGetValue(id, out var found) ? found : null;
        if (s is null) return Frames.Refused("no such session");
        long seq;
        byte[] data;
        try { seq = (long)f["Seq"]!; data = Convert.FromBase64String((string)f["Data"]!); }
        catch (Exception) { return Frames.Refused("a Sealed frame needs a Seq and Data"); }

        // Opened first, then its number counted: a frame that does not open uses up no number.
        var plain = new byte[Math.Max(0, data.Length - 16)];
        var e = security.MPAI_AIFAEAD_Decrypt(new AeadState(), s.Key, Algorithm.Gcm, Frames.Nonce(Frames.ToSecure, seq), Frames.Associated(id!, Frames.ToSecure), data, plain, out _);
        if (e != AifError.OK) return Frames.Refused("the request is not of this session, or was altered");
        if (!s.Seen.Accept(seq)) return Frames.Refused("a request already received (replay)");

        JsonObject reply;
        try { reply = Execute(JsonNode.Parse(plain)!.AsObject()); }
        catch (Exception ex) { reply = new JsonObject { ["Error"] = nameof(AifError.Failed), ["Reason"] = ex.Message }; }

        var body = Encoding.UTF8.GetBytes(reply.ToJsonString());
        var sealedReply = new byte[body.Length + 16];
        long outSeq;
        lock (s) outSeq = s.NextOut++;
        security.MPAI_AIFAEAD_Encrypt(new AeadState(), s.Key, Algorithm.Gcm, Frames.Nonce(Frames.ToUserAgent, outSeq), Frames.Associated(id!, Frames.ToUserAgent), body, sealedReply, out var n);
        return new JsonObject { ["Type"] = "Sealed", ["Session"] = id, ["Seq"] = outSeq, ["Data"] = Convert.ToBase64String(sealedReply, 0, n) };
    }

    // A REQUEST OF THE CONTROLLER API, executed on the Controller.
    private JsonObject Execute(JsonObject r)
    {
        // Only StartFlow names the Module; every other request addresses it by the MODULE_ID StartFlow returned (Basic API 2.2).
        var module = (int?)r["ModuleId"] ?? -1;
        static JsonObject E(AifError e) => new() { ["Error"] = e.ToString() };
        switch ((string?)r["Call"])
        {
            case "StartFlow":
            {
                var started = controller.StartFlow((string?)r["Module"] ?? "", out var moduleId);
                var reply = E(started);
                if (started == AifError.OK) reply["ModuleId"] = moduleId;
                return reply;
            }
            case "StopFlow": controller.StopFlow(module); return E(AifError.OK);
            case "Pause": return E(controller.Pause(module));
            case "Resume": return E(controller.Resume(module));
            case "StopAim": return E(controller.StopAim(module, (string)r["Aim"]!));
            case "SharedStorageInit": return E(controller.SharedStorageInit(module, (string)r["Location"]!));
            case "InputWrite": return E(controller.InputWrite(module, (string)r["DataType"]!, (int)r["PortNumber"]!, (string)r["Json"]!, (int)r["TimeoutMs"]!));
            case "OutputRead":
            {
                var read = controller.OutputRead(module, (string)r["DataType"]!, (int)r["PortNumber"]!, (int)r["TimeoutMs"]!);
                return new JsonObject { ["Error"] = read.Error.ToString(), ["Json"] = read.Json };
            }
            case "Advance":
            {
                var inputs = (r["Inputs"] as JsonArray ?? []).Select(i => new ControllerApi.Datum((string)i!["DataType"]!, (int)i["PortNumber"]!, (string)i["Json"]!));
                var result = controller.Advance(module, inputs.ToList());
                return new JsonObject { ["Error"] = result.Error.ToString(), ["Outputs"] = Data(result.Outputs) };
            }
            case "Status":
            {
                var status = controller.Status(module);
                return new JsonObject
                {
                    ["Error"] = status.Error.ToString(), ["Stopped"] = status.Stopped, ["State"] = status.State.ToString(),
                    ["Aims"] = new JsonArray(status.Aims.Select(a => (JsonNode)new JsonObject
                    {
                        ["Aim"] = a.Aim, ["Status"] = a.Status.ToString(), ["Reason"] = a.Reason, ["Reports"] = new JsonArray(a.Reports.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray())
                    }).ToArray())
                };
            }
            case "SharedStorageStore": return E(controller.SharedStorageStore(module, (string)r["Key"]!, Convert.FromBase64String((string)r["Data"]!), (string?)r["Category"] ?? "Data"));
            case "SharedStorageRead":
            {
                var e = controller.SharedStorageRead(module, (string)r["Key"]!, out var data);
                return new JsonObject { ["Error"] = e.ToString(), ["Data"] = Convert.ToBase64String(data) };
            }
            case "SharedStorageDelete": return E(controller.SharedStorageDelete(module, (string)r["Key"]!));
            case "SharedStorageList":
                return new JsonObject { ["Error"] = nameof(AifError.OK), ["Keys"] = new JsonArray(controller.SharedStorageList(module, (string?)r["Prefix"] ?? "").Select(k => (JsonNode)JsonValue.Create(k)!).ToArray()) };
            case "ExternalAuthority": return E(controller.ExternalAuthority(module, (string)r["Policy"]!, (string?)r["Host"] ?? "127.0.0.1", (int?)r["Port"] ?? 0));
            case "ExternalJoin": return E(controller.ExternalJoin(module, (string)r["Authority"]!, (string?)r["Rendezvous"], (string?)r["Role"], (string?)r["ControllerId"]));
            case "ExternalLeave": return E(controller.ExternalLeave(module));
            case "ExternalAnchor": return new JsonObject { ["Error"] = nameof(AifError.OK), ["Value"] = controller.ExternalAnchor(module) };
            case "ExternalRendezvous": return new JsonObject { ["Error"] = nameof(AifError.OK), ["Value"] = controller.ExternalRendezvous(module) };
            default: return new JsonObject { ["Error"] = nameof(AifError.Failed), ["Reason"] = "not a request of the Controller API" };
        }
    }

    internal static JsonArray Data(IEnumerable<ControllerApi.Datum> data) =>
        new(data.Select(d => (JsonNode)new JsonObject { ["DataType"] = d.DataType, ["PortNumber"] = d.PortNumber, ["Json"] = d.Json }).ToArray());

    public void Dispose()
    {
        lock (gate)
        {
            foreach (var s in sessions.Values) security.MPAI_AIFKM_Destroy_Key(s.Key);
            sessions.Clear();
        }
    }
}
