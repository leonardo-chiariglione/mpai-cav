using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIF.Store;

// THE MPAI STORE REACHED OVER HTTPS (the Store Service): the source of AIMs of a Controller that does not share a disk with the Store. The Metadata of an AIM and the names of the binaries
// the Store has a fingerprint of are asked of it; what the Controller measured it puts to the Store (MPAI_AIFS_VerifyImplementation), which says whether it is the fingerprint it
// approved. The AIM Licence is asked of it too (MPAI_AIFS_GetLicence, POST /MPAI/Store/Licence) for the holder of the Trust Anchor this source is made for - and, where only one
// Controller is licensed, that Controller - and its status is asked (MPAI_AIFS_VerifyLicence); the public key of the Store can be fetched to check the signature with.
public sealed class HttpAimSource : IAimSource, IDisposable
{
    private readonly HttpClient http;
    private readonly string? trustAnchorId;
    private readonly string? controllerId;
    private readonly Dictionary<string, System.Security.Cryptography.ECDsa> keys = [];
    private readonly AimCaller? caller;

    // WHO ASKS THE STORE FOR AN AIM LICENCE, and proves it: its Cryptographic Instance Identity and the key of it, its Instance Credential and the links of the chain to the Trust Anchor
    // it asks the licence for. The Store checks them; it issues nothing to a caller it cannot authenticate.
    public sealed record AimCaller(JsonObject Cii, JsonObject Credential, IReadOnlyList<AIF.Trust.PtfCredential.Link> Chain, System.Security.Cryptography.ECDsa Key);

    public HttpAimSource(string storeUrl, HttpMessageHandler? handler = null, string? trustAnchorId = null, string? controllerId = null, AimCaller? caller = null)
    {
        this.trustAnchorId = trustAnchorId; this.controllerId = controllerId; this.caller = caller;
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.BaseAddress = new Uri(storeUrl.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(30);
    }

    public bool Reachable { get; private set; } = true;

    private string? Text(string path)
    {
        try
        {
            using var answer = http.GetAsync(path).GetAwaiter().GetResult();
            Reachable = true;
            return answer.IsSuccessStatusCode ? answer.Content.ReadAsStringAsync().GetAwaiter().GetResult() : null;
        }
        catch (HttpRequestException) { Reachable = false; return null; }
        catch (TaskCanceledException) { Reachable = false; return null; }
    }

    public AimPackage? Get(string aimName)
    {
        var l3 = Text($"MPAI/Store/L3/{Uri.EscapeDataString(aimName)}");
        if (l3 is null) return null;
        var binaries = Text($"MPAI/Store/L3/{Uri.EscapeDataString(aimName)}/implementations") is { } list
            ? ((JsonArray?)JsonNode.Parse(list)?["binaries"] ?? []).Select(b => (string)b!).ToList() : [];
        using var document = JsonDocument.Parse(l3);
        return new AimPackage(aimName, l3, AimRepository.VersionIn(document.RootElement), binaries);
    }

    public VerifyResult Verify(string aimName, string binaryName, string fingerprint)
    {
        try
        {
            var nonce = Guid.NewGuid().ToString("N");
            using var content = new StringContent(new JsonObject { ["BinaryName"] = binaryName, ["Fingerprint"] = fingerprint, ["Nonce"] = nonce }.ToJsonString(), Encoding.UTF8, "application/json");
            using var answer = http.PostAsync($"MPAI/Store/L3/{Uri.EscapeDataString(aimName)}/verify", content).GetAwaiter().GetResult();
            Reachable = true;
            if (!answer.IsSuccessStatusCode) return VerifyResult.NotRecorded;
            // The answer is the Store's, to this question: signed with a key the Store gave, naming the AIM, the binary, the fingerprint and the Nonce that were asked. Anything else is not
            // an answer of the Store, and a Store that has not said it is the same has not.
            if (JsonNode.Parse(answer.Content.ReadAsStringAsync().GetAwaiter().GetResult()) is not JsonObject said) return VerifyResult.NotRecorded;
            if (AIF.Trust.PtfSignature.Verify(said, id => StoreKey(id)) != AIF.Trust.PtfSignature.Outcome.Valid) return VerifyResult.NotRecorded;
            if ((string?)said["AIMName"] != aimName || (string?)said["BinaryName"] != binaryName || (string?)said["Fingerprint"] != fingerprint || (string?)said["Nonce"] != nonce) return VerifyResult.NotRecorded;
            return Enum.TryParse<VerifyResult>((string?)said["Result"], out var result) ? result : VerifyResult.NotRecorded;
        }
        catch (HttpRequestException) { Reachable = false; return VerifyResult.NotRecorded; }       // a Store that cannot be asked has not said it is the same
        catch (TaskCanceledException) { Reachable = false; return VerifyResult.NotRecorded; }
    }

    // MPAI_AIFS_GetLicence over HTTPS: the AIM Licence the Store issues for the AIM to the holder of the Trust Anchor (the same one when asked again); null where the AIM has no
    // AIMLicence or the Store cannot be reached. A Store that refuses says why, and the refusal is raised.
    public string? GetLicence(string aimName)
    {
        var anchor = trustAnchorId ?? throw new InvalidOperationException($"This source names no Trust Anchor to license {aimName} to.");
        try
        {
            var request = new JsonObject { ["AIMName"] = aimName, ["TrustAnchorID"] = anchor };
            if (controllerId is not null) request["ControllerID"] = controllerId;
            if (caller is not null)
            {
                request["Caller"] = new JsonObject
                {
                    ["Cii"] = caller.Cii.DeepClone(), ["Credential"] = caller.Credential.DeepClone(),
                    ["Chain"] = new JsonArray(caller.Chain.Select(l => (JsonNode)new JsonObject { ["Cii"] = l.Cii.DeepClone(), ["Credential"] = l.Credential.DeepClone() }).ToArray())
                };
                request["Time"] = AIF.Trust.PtfIdentity.Time(DateTimeOffset.UtcNow);
                AIF.Trust.PtfSignature.Sign(request, caller.Key, (string?)caller.Cii["CryptographicInstanceID"] ?? (string?)caller.Cii["AnchorID"] ?? "caller");
            }
            using var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
            using var answer = http.PostAsync("MPAI/Store/Licence", content).GetAwaiter().GetResult();
            Reachable = true;
            var text = answer.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (answer.IsSuccessStatusCode) return text;
            if (answer.StatusCode == System.Net.HttpStatusCode.NotFound) return null;                    // the Metadata has no AIMLicence: it needs none
            throw new InvalidOperationException($"The Store refused the AIM Licence of {aimName}: {(string?)JsonNode.Parse(text)?["refused"] ?? answer.StatusCode.ToString()}");
        }
        catch (HttpRequestException) { Reachable = false; return null; }
        catch (TaskCanceledException) { Reachable = false; return null; }
    }

    // The public key of the Store a KeyID of an AIM Licence names, for a Controller to check the signature with; null where the Store does not have it or cannot be reached.
    public System.Security.Cryptography.ECDsa? StoreKey(string keyId)
    {
        if (keys.TryGetValue(keyId, out var known)) return known;
        if (Text($"MPAI/Store/Key/{Uri.EscapeDataString(keyId)}") is not { } text || (string?)JsonNode.Parse(text)?["publicKey"] is not { } hex) return null;
        var key = System.Security.Cryptography.ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromHexString(hex), out _);
        return keys[keyId] = key;
    }

    public string? CurrentVersion(string aimName)
    {
        if (Text($"MPAI/Store/L3/{Uri.EscapeDataString(aimName)}") is not { } l3) return null;
        using var document = JsonDocument.Parse(l3);
        return AimRepository.VersionIn(document.RootElement);
    }

    // MPAI_AIFS_VerifyLicence over HTTPS: whether the Store says the AIM Licence is revoked; null where it cannot be asked or does not know the AIM Licence.
    public bool? IsRevoked(string aimLicenceId)
    {
        if (Text($"MPAI/Store/Licence/{Uri.EscapeDataString(aimLicenceId)}/status") is not { } text) return null;
        return (string?)JsonNode.Parse(text)?["status"] switch { "revoked" => true, "valid" => false, _ => null };
    }

    public void Dispose() { http.Dispose(); foreach (var key in keys.Values) key.Dispose(); }
}
