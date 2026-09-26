using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using AIF.Channels;
using AIF.Controller;
using AIF.Metadata;
using AIF.Trust;
using Json.Schema;
using Mpai.Aif.Api;
using Xunit.Abstractions;

namespace Mpai.Aif.Tests;

// PHASE 13, STEP 5 (M3223 3.4): trust between a Controller and an AIM host. Each is
// a Trust Anchor, given the anchors of the others it trusts; the link opens with
// PTF's Trust Protocol - the Controller's TrustRequest, the host's TrustResponse,
// each signed by its anchor and naming the TLS certificate of the other end - in
// place of the key shared in configuration. A party foreign, expired or forged is
// refused, at either end; a message is good only on the link it was sent on.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class TrustLinkTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static TrustProtocol Party(string id, ECDsa key, params JsonObject[] trusted) => Party(id, key, Now.AddHours(-1), Now.AddDays(1), trusted);
    private static TrustProtocol Party(string id, ECDsa key, DateTimeOffset notBefore, DateTimeOffset notAfter, params JsonObject[] trusted) =>
        new(new TrustAnchorKey(id, key, notBefore, notAfter), key, trusted);
    private static JsonObject Anchor(string id, ECDsa key, DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null) =>
        new TrustAnchorKey(id, key, notBefore ?? Now.AddHours(-1), notAfter ?? Now.AddDays(1)).Object();

    // A LINK, in this process: a Controller's end to a host's; admitted and used, or
    // refused, and what each end said.
    private static async Task<string> Open(ILinkAdmission controller, ILinkAdmission host)
    {
        string? hostSaw = null;
        if (host is TrustedLink trusted) trusted.Answered = (c, refused) => hostSaw = refused is null ? $"admitted {c}" : $"refused {c}: {refused}";
        await using var listener = new RemoteLink.Listener(0, host, link =>
            link.OnRequest = f => Task.FromResult<JsonObject?>(new JsonObject { ["Ok"] = true, ["Echo"] = f["Text"]?.DeepClone() }));
        try
        {
            await using var link = await RemoteLink.ConnectAsync("localhost", listener.Port, controller);
            var echo = await link.RequestAsync(new JsonObject { ["Text"] = "over the link" });
            return $"admitted; the host: {hostSaw}; a request answered: {echo["Echo"]}";
        }
        catch (UnauthorizedAccessException refused)
        {
            return $"refused: {refused.Message.Replace($"localhost:{listener.Port}", "<host>")} The host: {hostSaw ?? "-"}";
        }
    }

    [Fact]
    public async Task Protocol()
    {
        var result = new Dictionary<string, string>();
        using var controllerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var hostKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var controllerAnchor = Anchor("controller-1", controllerKey);
        var hostAnchor = Anchor("aimhost-1", hostKey);
        var controller = Party("controller-1", controllerKey, hostAnchor);
        var host = Party("aimhost-1", hostKey, controllerAnchor);
        TrustedLink C(TrustProtocol p) => new(p);

        result["mutual trust"] = await Open(C(controller), C(host));

        // The host, as the Controller judges it.
        result["a host the Controller does not trust"] = await Open(C(controller), C(Party("aimhost-2", otherKey, controllerAnchor)));
        var past = (Now.AddDays(-3), Now.AddDays(-2));
        result["a host whose anchor has expired"] = await Open(
            C(Party("controller-1", controllerKey, Anchor("aimhost-1", hostKey, past.Item1, past.Item2))),
            C(Party("aimhost-1", hostKey, past.Item1, past.Item2, controllerAnchor)));
        result["a host claiming the anchor of another"] = await Open(C(controller), C(Party("aimhost-1", otherKey, controllerAnchor)));

        // The Controller, as the host judges it.
        result["a Controller the host does not trust"] = await Open(C(Party("controller-2", otherKey, hostAnchor)), C(host));
        result["a Controller whose anchor has expired"] = await Open(
            C(Party("controller-1", controllerKey, past.Item1, past.Item2, hostAnchor)),
            C(Party("aimhost-1", hostKey, Anchor("controller-1", controllerKey, past.Item1, past.Item2))));
        result["a Controller claiming the anchor of another"] = await Open(C(Party("controller-1", otherKey, hostAnchor)), C(host));

        // Trust and the key do not mix: the key is withdrawn where trust is configured.
        result["a Controller with the key, to a host of the Trust Protocol"] = await Open(new KeyAdmission("a key"), C(host));
        result["a Controller of the Trust Protocol, to a host with the key"] = await Open(C(controller), new KeyAdmission("a key"));

        // THE MESSAGES: each good only on its link, only fresh, only as signed.
        var controllerCertificate = new string('C', 64);
        var hostCertificate = new string('H', 64);
        var request = controller.Request(hostCertificate);
        result["a request, answered on its link"] = Shown(host.Answer(request, hostCertificate, controllerCertificate));
        result["a request relayed to another host"] = Shown(host.Answer(request, new string('E', 64), controllerCertificate));
        var altered = request.DeepClone().AsObject();
        altered["Request"]!["TargetID"] = new string('E', 64);
        result["a request altered after it was signed"] = Shown(host.Answer(altered, new string('E', 64), controllerCertificate));
        var late = new TrustProtocol(new TrustAnchorKey("controller-1", controllerKey, Now.AddHours(-1), Now.AddDays(1)), controllerKey, [hostAnchor], () => Now.AddMinutes(-6));
        result["a request sent six minutes ago"] = Shown(host.Answer(late.Request(hostCertificate), hostCertificate, controllerCertificate));
        var (response, _, _) = host.Answer(request, hostCertificate, controllerCertificate);
        result["a response, checked on its link"] = controller.Check(response, controllerCertificate) ?? "trusted";
        result["a response relayed to another Controller"] = controller.Check(response, new string('E', 64)) ?? "trusted";
        var refusal = host.Answer(altered, new string('E', 64), controllerCertificate).Response;
        result["a refusal, checked"] = controller.Check(refusal, controllerCertificate) ?? "trusted";

        result["the request against PTF-MSG"] = Violations("TrustMessage", request);
        result["the response against PTF-MSG"] = Violations("TrustMessage", response);
        result["a refusal against PTF-MSG"] = Violations("TrustMessage", refusal);
        result["an anchor against PTF-TRA"] = Violations("TrustAnchor", hostAnchor);
        Expected.Match("trust-link.json", result);
    }

    private static string Shown((JsonObject Response, string? Admitted, string? Refused) answer) =>
        answer.Admitted is not null ? $"admitted {answer.Admitted}" : $"refused: {answer.Refused}";

    // MODULES WITH AIMs ON A HOST, under trust: as before; a host that does not trust
    // the Controller, or one the Controller does not trust, not used.
    [Fact]
    public void Modules()
    {
        var result = new Dictionary<string, string>();
        var before = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Repository.Root, "Test", "Expected", "remote-placed.json")))!;
        var amds = TrustEvidenceTests.ApprovedCopy();
        using (var host = TrustedParties.Host())
        {
            foreach (var (module, continuous) in RemoteTests.Modules)
            {
                var aims = new RemoteAims();
                using var api = Api(amds, aims, host);
                var now = RemoteTests.Run(api, module, continuous, aims);
                result[$"{module} under trust"] = now == before[module] ? "as before" : now;
                if (RemoteTests.Hosted.ContainsKey(module))
                    output.WriteLine($"{module}: the link opened in {api.Controller.LastLinkOpened.TotalMilliseconds:0} ms, the Trust Protocol included");
            }
        }

        using (var host = TrustedParties.Host(controllers: TrustedParties.AnchorOf(TrustedParties.Save("controller-2"))))
        {
            using var api = Api(amds, new RemoteAims(), host);
            result["a host that does not trust this Controller"] = Start(api, "TST-RXC", host);
        }
        using (var host = TrustedParties.Host())
        {
            using var api = Api(amds, new RemoteAims(), host);
            api.Controller.HostAnchors.Clear();
            result["a host this Controller does not trust"] = Start(api, "TST-RXC", host);
            api.Controller.HostAnchors.Add(TrustedParties.AnchorOf(TrustedParties.Save("aimhost-1")));
            result["a host claiming an anchor this Controller trusts, without its key"] = Start(api, "TST-RXC", host);
        }
        using (var host = new HostProcess())
        {
            using var api = Api(amds, new RemoteAims(), null);
            api.Controller.AimHostKey = HostProcess.Key;
            foreach (var aim in RemoteTests.Hosted["TST-RXC"]) api.Controller.AimHosts[aim] = host.Address;
            result["a host with the key, to a Controller under trust"] = Start(api, "TST-RXC", host);
        }
        Expected.Match("trust-link-modules.json", result);
    }

    private static ControllerApi Api(string amds, RemoteAims aims, HostProcess? host)
    {
        var api = new ControllerApi(amds, Path.Combine(amds, "no-settings.json"), aims);
        api.Controller.Trust = TrustedParties.Controller();
        if (host is not null)
        {
            api.Controller.HostAnchors.Add(host.Anchor!);
            foreach (var aims_ in RemoteTests.Hosted.Values) foreach (var aim in aims_) api.Controller.AimHosts[aim] = host.Address;
        }
        return api;
    }

    private static string Start(ControllerApi api, string module, HostProcess host)
    {
        var name = $"1{module}-V1.0-I01";
        AifError outcome;
        try { outcome = api.StartFlow(name); }
        catch (InvalidOperationException refused) { return "refused: " + refused.Message.Replace(host.Address, "<host>"); }
        if (outcome == AifError.NotTrusted) return "NOT_TRUSTED: " + api.Controller.LastRefusal!.Replace(host.Address, "<host>");
        api.StopFlow(name);
        return outcome.ToString();
    }

    private static string Violations(string schemaName, JsonObject instance)
    {
        var schemas = Path.Combine(Repository.Root, "schemas");
        var schema = PublishedSchemas.At(schemas)[Path.GetFullPath(Path.Combine(schemas, "PTF", "V1.0", "data", schemaName + ".json"))];
        EvaluationResults evaluation;
        using var document = JsonDocument.Parse(instance.ToJsonString());
        lock (PublishedSchemas.Lock)
            evaluation = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (evaluation.IsValid) return "valid";
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var d in evaluation.Details ?? [])
            if (d.Errors is { Count: > 0 })
                foreach (var (k, m) in d.Errors)
                    found.Add($"{(d.InstanceLocation.ToString() is { Length: > 0 } l ? l : "/")} {k}: {Regex.Replace(m.Length > 90 ? m[..90] + "..." : m, @"\s+", " ")}");
        return string.Join("; ", found);
    }
}
