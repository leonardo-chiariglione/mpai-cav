using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

using AIF.Channels;
using AIF.Trust;

namespace AIF.Controller;

// A LINK ADMITTED BY THE TRUST PROTOCOL (M3223 3.4), at either end: the Controller
// opens with its TrustRequest, the host answers with its TrustResponse. Each end
// presents a TLS certificate made for the run, which the message it signs names;
// the certificate of the other end is the one the other's message names, or the
// link is refused.
public sealed class TrustedLink(TrustProtocol protocol) : ILinkAdmission
{
    public X509Certificate2? Certificate { get; } = RemoteLink.CertificateForTheRun(protocol.Id);

    // The host's end: each Controller that asked, and whether it was admitted or why
    // not.
    public Action<string, string?>? Answered { get; set; }

    private string Own => TrustProtocol.CertificateHash(Certificate!.RawData);
    private static string Of(X509Certificate? certificate) =>
        certificate is null ? "" : TrustProtocol.CertificateHash(certificate.GetRawCertData());

    public JsonObject Opening(X509Certificate? host) =>
        new() { ["Kind"] = "Trust", ["Message"] = protocol.Request(Of(host)) };

    public string? Admitted(JsonObject answer, X509Certificate? host) =>
        answer["Message"] is JsonObject response
            ? protocol.Check(response, Own)
            : $"it did not answer with a TrustResponse{(answer["Error"] is { } error ? $" ({error})" : "")}";

    public (JsonObject Answer, bool Admits) Answer(JsonObject opening, X509Certificate? controller)
    {
        if ((string?)opening["Kind"] != "Trust" || opening["Message"] is not JsonObject request)
        {
            Answered?.Invoke("a Controller", "it did not open with a TrustRequest");
            return (new JsonObject { ["Ok"] = false, ["Error"] = "this host admits a Controller by the Trust Protocol" }, false);
        }
        var (response, admitted, refused) = protocol.Answer(request, Own, Of(controller));
        Answered?.Invoke((string?)request["KeyID"] ?? "", refused);
        return (new JsonObject { ["Ok"] = admitted is not null, ["Message"] = response }, admitted is not null);
    }
}
