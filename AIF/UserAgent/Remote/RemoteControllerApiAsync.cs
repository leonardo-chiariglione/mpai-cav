using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Aif.PortData;

namespace Mpai.Mas.Client;

// MPAI-MAS FROM A BROWSER. The same requests as RemoteControllerApi,
// awaited instead of blocked on. The HttpClient is the page's own: its base
// address is the origin that served the client, which forwards /MPAI/AIFU to
// the Service, so the browser never makes a cross-origin call.
public sealed class RemoteControllerApiAsync : IAsyncControllerApi
{
    private readonly HttpClient     http;
    private readonly PortDataCodecs codecs = PortDataCodecs.Default();
    private string  prefix = "/MPAI/AIFU";
    private string? controllerId;
    private readonly Dictionary<string, string> modules = new(StringComparer.Ordinal);

    // BOUTS (MPAI-MAS V1.1, API 3.3), as RemoteControllerApi has them. The Trust Anchor the SCI returned verifies its Bouts; this client's key, with the
    // Instance Credential the SCI issued for its Cryptographic Instance Identity, signs the Bouts it gives. In a browser .NET has no ECDSA: the keys are
    // the browser's (Web Crypto), which the page gives through NewKey and KeyFromSpki, and every signature is made and checked asynchronously.
    private AIF.Bout.BoutKey? sciKey;
    private AIF.Bout.BoutKey? clientKey;

    // How a key pair is made, and a public key read from its SubjectPublicKeyInfo: .NET's ECDSA unless the page gives the browser's.
    public Func<Task<AIF.Bout.BoutKey>> NewKey { get; init; } = () => Task.FromResult(AIF.Bout.BoutKey.Create());
    public Func<byte[], Task<AIF.Bout.BoutKey?>> KeyFromSpki { get; init; } = spki => Task.FromResult(AIF.Bout.BoutKey.FromSpki(spki));
    private readonly string clientInstance = "urn:mpai:mas:rca:" + Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, AIF.Bout.BoutReceiver> outputBouts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AIF.Bout.BoutGiver> inputBouts = new(StringComparer.Ordinal);

    // True before the first request: the client asks for an Instance Credential when it creates the SCI.
    public bool GivesBouts { get; set; }
    public JsonObject? Credential { get; private set; }
    public int BoutInstancesSent { get; private set; }
    public int BoutInstancesReceived { get; private set; }

    public RemoteControllerApiAsync(HttpClient http, string? bearerToken = null)
    {
        this.http = http;
        if (!string.IsNullOrWhiteSpace(bearerToken))
            this.http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
    }

    private async Task<string> ControllerAsync()
    {
        if (controllerId is not null) return controllerId;
        HttpContent? proof = null;
        if (GivesBouts)
        {
            clientKey ??= await NewKey();
            proof = new StringContent(new JsonObject { ["Proof"] = await AIF.Bout.BoutCredentials.ProofAsync(clientKey, clientInstance, DateTimeOffset.UtcNow) }.ToJsonString(), Encoding.UTF8, "application/json");
        }
        var response = await http.PostAsync($"{prefix.TrimStart('/')}/Controller", proof);
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject
                   ?? throw new FormatException("Controller response is not JSON.");
        var alternative = (string?)json["prefix"];
        if (!string.IsNullOrWhiteSpace(alternative)) prefix = alternative!.TrimEnd('/');
        controllerId = (string?)json["id"] ?? throw new FormatException("Controller response has no id.");
        // The Trust Anchor of the SCI (PTF-TRA), from this Response only (MPAI-MAS V1.1, API 2.1).
        if (json["anchor"] is JsonObject anchor && (string?)anchor["Header"] == "PTF-TRA-V1.0" && (string?)anchor["PublicKey"]?["KeyValue"] is { } spki)
            sciKey = await KeyFromSpki(Convert.FromHexString(spki));
        Credential = json["credential"] as JsonObject;
        return controllerId;
    }

    private async Task<string> RootAsync() => $"{prefix.TrimStart('/')}/{await ControllerAsync()}/MODULE";

    public async Task<AifError> StartFlowAsync(string moduleName)
    {
        if (modules.ContainsKey(moduleName)) return AifError.OK;
        var body = new StringContent(new JsonObject { ["module"] = moduleName }.ToJsonString(),
                                     Encoding.UTF8, "application/json");
        var response = await http.PostAsync($"{await RootAsync()}/Start", body);
        if (!response.IsSuccessStatusCode) return AifError.Failed;
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject;
        var id = (string?)json?["id"];
        if (string.IsNullOrEmpty(id)) return AifError.Failed;
        modules[moduleName] = id!;
        return AifError.OK;
    }

    public async Task StopFlowAsync(string moduleName)
    {
        if (!modules.TryGetValue(moduleName, out var mid)) return;
        try { await http.GetAsync($"{await RootAsync()}/{mid}/Stop"); }
        catch { /* a Module already gone is stopped */ }
        modules.Remove(moduleName);
    }

    // ONE EXCHANGE. The inputs are written, then every Output Port the client can
    // read is asked for; the first read closes the exchange on the Service and
    // runs the Module on what was written.
    public Task<ControllerApi.Result> AdvanceAsync(string moduleName, IEnumerable<ControllerApi.Datum> inputs) =>
        AdvanceAsync(moduleName, inputs, Array.Empty<(string, int)>());

    // ONLY WHAT IS WANTED, when the caller says: asking for every Data Type the
    // client knows on four Ports each is thirty requests a step, nearly all of them
    // "no such output". The Service discards a run's outputs at the next input, so
    // one not asked for is never served to a later step. Nothing said, all asked.
    public async Task<ControllerApi.Result> AdvanceAsync(string moduleName, IEnumerable<ControllerApi.Datum> inputs,
                                                         IReadOnlyCollection<(string DataType, int PortNumber)> wanted)
    {
        if (!modules.ContainsKey(moduleName))
        {
            var started = await StartFlowAsync(moduleName);
            if (started != AifError.OK)
                return new ControllerApi.Result(started, Array.Empty<ControllerApi.Datum>());
        }
        var mid  = modules[moduleName];
        var root = await RootAsync();

        foreach (var datum in inputs)
        {
            if (!codecs.Knows(datum.DataType))
                return new ControllerApi.Result(AifError.Failed, Array.Empty<ControllerApi.Datum>());
            var content = await InputContentAsync(mid, datum.DataType, datum.PortNumber, codecs.ToWire(datum.DataType, datum.Json));
            var posted = await http.PostAsync($"{root}/{mid}/Input/{Segment(datum.DataType, datum.PortNumber)}", content);
            if (!posted.IsSuccessStatusCode)
                return new ControllerApi.Result(AifError.Failed, Array.Empty<ControllerApi.Datum>());
        }

        var outputs = new List<ControllerApi.Datum>();
        var asked = wanted.Count > 0
            ? wanted.Where(w => codecs.Knows(w.DataType)).Distinct().ToList()
            : codecs.KnownDataTypes.SelectMany(t => Enumerable.Range(1, 4).Select(n => (t, n))).ToList();
        foreach (var (dataType, portNumber) in asked)
        {
            {
                var response = await GetAsync($"{root}/{mid}/Output/{Segment(dataType, portNumber)}");
                if (!response.IsSuccessStatusCode) continue;
                if (await OutputBytesAsync(response, root, mid, dataType, portNumber) is not { } wire) continue;
                outputs.Add(new ControllerApi.Datum(dataType, portNumber, codecs.ToInternal(dataType, wire)));
            }
        }
        return new ControllerApi.Result(AifError.OK, outputs);
    }

    // The data path over MPAI-MAS's routes: see RemoteControllerApi.
    public async Task<AifError> InputWriteAsync(string moduleName, string dataType, int portNumber, string json, int timeoutMs = -1)
    {
        if (!modules.TryGetValue(moduleName, out var mid)) return AifError.NotStarted;
        if (!codecs.Knows(dataType)) return AifError.Failed;
        var content = await InputContentAsync(mid, dataType, portNumber, codecs.ToWire(dataType, json));
        try
        {
            using var limit = Limit(timeoutMs);
            var posted = await http.PostAsync($"{await RootAsync()}/{mid}/Input/{Segment(dataType, portNumber)}", content, limit.Token);
            return posted.IsSuccessStatusCode ? AifError.OK : AifError.Failed;
        }
        catch (OperationCanceledException) { return AifError.Timeout; }
    }

    public async Task<ControllerApi.Read> OutputReadAsync(string moduleName, string dataType, int portNumber, int timeoutMs = -1)
    {
        if (!modules.TryGetValue(moduleName, out var mid)) return new ControllerApi.Read(AifError.NotStarted, null);
        if (!codecs.Knows(dataType)) return new ControllerApi.Read(AifError.Failed, null);
        try
        {
            using var limit = Limit(timeoutMs);
            var root = await RootAsync();
            var response = await GetAsync($"{root}/{mid}/Output/{Segment(dataType, portNumber)}", limit.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return new ControllerApi.Read(AifError.NotProduced, null);
            if (!response.IsSuccessStatusCode) return new ControllerApi.Read(AifError.Failed, null);
            if (await OutputBytesAsync(response, root, mid, dataType, portNumber) is not { } wire) return new ControllerApi.Read(AifError.NotProduced, null);
            return new ControllerApi.Read(AifError.OK, codecs.ToInternal(dataType, wire));
        }
        catch (OperationCanceledException) { return new ControllerApi.Read(AifError.Timeout, null); }
    }

    // ------------------------------------------------------------------ Bouts (MPAI-MAS V1.1, API 3.3), as RemoteControllerApi has them
    public async Task<AifError> OutputBoutOpenAsync(string moduleName, string dataType, int portNumber, string signingMode = AIF.Bout.BoutSecurity.EachInstance, int chainInterval = 1)
    {
        if (!modules.TryGetValue(moduleName, out var mid)) return AifError.NotStarted;
        var root = await RootAsync();
        if (sciKey is null) return AifError.NotTrusted;
        var body = new JsonObject { ["SigningMode"] = signingMode };
        if (signingMode == AIF.Bout.BoutSecurity.Chain) body["ChainInterval"] = chainInterval;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{root}/{mid}/Output/{Segment(dataType, portNumber)}/Bout")
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/zstd");
        var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return AifError.NotAuthorised;
        var receiver = NewReceiver(dataType, portNumber);
        outputBouts[BoutKeyOf(mid, dataType, portNumber)] = receiver;
        if ((int)response.StatusCode == 201 && JsonNode.Parse(await response.Content.ReadAsStringAsync()) is JsonObject bout && !(await receiver.AcceptAsync(bout)).Ok)
            return AifError.NotTrusted;
        return AifError.OK;
    }

    public async Task<AifError> OutputBoutCloseAsync(string moduleName, string dataType, int portNumber)
    {
        if (!modules.TryGetValue(moduleName, out var mid)) return AifError.NotStarted;
        var response = await http.DeleteAsync($"{await RootAsync()}/{mid}/Output/{Segment(dataType, portNumber)}/Bout");
        if (outputBouts.Remove(BoutKeyOf(mid, dataType, portNumber), out var receiver) && response.IsSuccessStatusCode &&
            JsonNode.Parse(await response.Content.ReadAsStringAsync()) is JsonObject closing)
            await receiver.CloseAsync(closing);
        return response.IsSuccessStatusCode ? AifError.OK : AifError.NotFound;
    }

    public async Task<AifError> InputBoutGiveAsync(string moduleName, string dataType, int portNumber, string firstJson, string signingMode = AIF.Bout.BoutSecurity.EachInstance, int chainInterval = 1)
    {
        if (!modules.TryGetValue(moduleName, out var mid)) return AifError.NotStarted;
        var root = await RootAsync();
        if (Credential is null || clientKey is null) return AifError.NotTrusted;
        var giver = new AIF.Bout.BoutGiver(new AIF.Bout.BoutGiver.Options
        {
            DataType = dataType, PortNumber = portNumber, BoutId = 32768 + System.Security.Cryptography.RandomNumberGenerator.GetInt32(1 << 30),
            SigningMode = signingMode, ChainInterval = chainInterval, Key = clientKey, KeyId = clientInstance, Issuer = clientInstance, SourceAimId = clientInstance
        });
        var bout = await giver.OpenAsync(JsonNode.Parse(codecs.ToWire(dataType, firstJson))!.AsObject());
        var response = await http.PostAsync($"{root}/{mid}/Input/{Segment(dataType, portNumber)}/Bout", new StringContent(bout.ToJsonString(), Encoding.UTF8, "application/json"));
        if (!response.IsSuccessStatusCode) return response.StatusCode == System.Net.HttpStatusCode.Unauthorized ? AifError.NotTrusted : AifError.NotAuthorised;
        inputBouts[BoutKeyOf(mid, dataType, portNumber)] = giver;
        return AifError.OK;
    }

    public async Task<AifError> InputBoutEndAsync(string moduleName, string dataType, int portNumber)
    {
        if (!modules.TryGetValue(moduleName, out var mid)) return AifError.NotStarted;
        if (!inputBouts.Remove(BoutKeyOf(mid, dataType, portNumber), out var giver)) return AifError.NotFound;
        var response = await http.PostAsync($"{await RootAsync()}/{mid}/Input/{Segment(dataType, portNumber)}/Bout/Close",
                                            new StringContent((await giver.CloseAsync()).ToJsonString(), Encoding.UTF8, "application/json"));
        return response.IsSuccessStatusCode ? AifError.OK : AifError.Failed;
    }

    private static string BoutKeyOf(string mid, string dataType, int portNumber) => $"{mid}/{dataType}#{portNumber}";

    private AIF.Bout.BoutReceiver NewReceiver(string dataType, int portNumber)
    {
        var sci = controllerId!;
        var key = sciKey!;
        return new AIF.Bout.BoutReceiver(new AIF.Bout.BoutReceiver.Options { DataType = dataType, PortNumber = portNumber, KeyFor = id => id == sci ? key : null });
    }

    // A GET that accepts a JSON object or a Bout Instance: the page's HttpClient is shared, so the Accept header is set per request.
    private Task<HttpResponseMessage> GetAsync(string uri, System.Threading.CancellationToken cancel = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("application/zstd");
        return http.SendAsync(request, cancel);
    }

    private async Task<HttpContent> InputContentAsync(string mid, string dataType, int portNumber, byte[] wire)
    {
        if (inputBouts.TryGetValue(BoutKeyOf(mid, dataType, portNumber), out var giver))
        {
            var frame = new ByteArrayContent(await giver.InstanceAsync(JsonNode.Parse(wire)!.AsObject()));
            frame.Headers.ContentType = new MediaTypeHeaderValue("application/zstd");
            BoutInstancesSent++;
            return frame;
        }
        var content = new ByteArrayContent(wire);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private async Task<byte[]?> OutputBytesAsync(HttpResponseMessage response, string root, string mid, string dataType, int portNumber)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (response.Content.Headers.ContentType?.MediaType != "application/zstd") return bytes;
        if (!outputBouts.TryGetValue(BoutKeyOf(mid, dataType, portNumber), out var receiver)) return null;
        if (receiver.Bout is null)
        {
            var got = await GetAsync($"{root}/{mid}/Output/{Segment(dataType, portNumber)}/Bout");
            if (!got.IsSuccessStatusCode || JsonNode.Parse(await got.Content.ReadAsStringAsync()) is not JsonObject bout || !(await receiver.AcceptAsync(bout)).Ok) return null;
        }
        var received = await receiver.ReceiveAsync(bytes);
        if (!received.Ok || received.Delivered.Count == 0) return null;
        BoutInstancesReceived += received.Delivered.Count;
        return Encoding.UTF8.GetBytes(received.Delivered[^1].ToJsonString());
    }

    // Refused over MPAI-MAS until Phase 15: see RemoteControllerApi.
    public Task<AifError> PauseAsync(string moduleName) => Task.FromResult(AifError.Failed);

    public Task<AifError> ResumeAsync(string moduleName) => Task.FromResult(AifError.Failed);

    public Task<ControllerApi.ModuleStatus> StatusAsync(string moduleName) =>
        Task.FromResult(new ControllerApi.ModuleStatus(AifError.Failed, Array.Empty<AimReport>()));

    public Task<AifError> StopAimAsync(string moduleName, string aimName) => Task.FromResult(AifError.Failed);

    public Task<AifError> SharedStorageInitAsync(string moduleName, string location) => Task.FromResult(AifError.Failed);

    private static System.Threading.CancellationTokenSource Limit(int timeoutMs) =>
        timeoutMs < 0 ? new System.Threading.CancellationTokenSource() : new System.Threading.CancellationTokenSource(Math.Max(timeoutMs, 1));

    private static string Segment(string dataType, int portNumber) =>
        dataType + "/" + portNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);   // two path segments (MPAI-MAS V1.1, API 1)
}
