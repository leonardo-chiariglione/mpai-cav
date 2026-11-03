using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

using AIF.Channels;
using AIF.Trust;

namespace AIF.Controller;

// THE LINK BETWEEN THE CONTROLLER AND A PROVIDER (AIF V3.0, Storage 6.3): the Controller obtains the data of a Provider, a
// third party that publishes it at a URI, over a link the Trust Protocol (M3223 3.4) admitted. The Controller always opens
// the exchange; the Provider writes nothing. The binding is the one that a URI of https gives:
//
//   1. The Controller opens TLS to the host of the URI, presenting a certificate made for the link, and sees the certificate
//      the host presents (TLS is confidentiality here, as in RemoteLink; identity is the Trust Protocol's).
//   2. It POSTs the TrustRequest - signed by its anchor, naming the certificate of the host as it saw it - to TrustPath.
//   3. The Provider answers with a TrustResponse - signed by its anchor, naming the certificate of the Controller - and the
//      Controller checks it as any TrustResponse: from an anchor it trusts (given it, or credentialed by one it trusts),
//      current, fresh, for this link and admitting it.
//   4. Only then does it GET the data, on connections that present that same certificate; a connection that presents another
//      is refused.
// The Provider's end is Answer, which a Provider's server calls with the body of the POST.
public sealed class ProviderLink : IDisposable
{
    public const string TrustPath = "/.well-known/mpai-trust";

    private readonly TrustProtocol protocol;
    private readonly X509Certificate2 own;
    private readonly HttpClient http;
    private string? hostHash;

    public ProviderLink(TrustProtocol protocol)
    {
        this.protocol = protocol;
        own = RemoteLink.CertificateForALink(protocol.Id);
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        handler.SslOptions.ClientCertificates = [own];
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
        {
            if (certificate is null) return false;
            var hash = TrustProtocol.CertificateHash(certificate.GetRawCertData());
            if (hostHash is null) { hostHash = hash; return true; }          // the certificate this link is made over
            return string.Equals(hostHash, hash, StringComparison.OrdinalIgnoreCase);
        };
        http = new HttpClient(handler, true) { Timeout = TimeSpan.FromHours(1) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MPAI-AIF-Controller/3.0");
    }

    // Null where the Provider at the host of the URI is admitted; otherwise why not.
    public string? Admit(Uri uri)
    {
        var path = new Uri(uri, TrustPath);
        try
        {
            using (http.GetAsync(path).GetAwaiter().GetResult()) { }              // opens the link: the certificate of the host
            if (hostHash is null) return "no certificate was presented";
            var request = protocol.Request(hostHash).ToJsonString();
            using var posted = http.PostAsync(path, new StringContent(request, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            if (!posted.IsSuccessStatusCode) return $"it did not answer the TrustRequest ({(int)posted.StatusCode})";
            var answer = JsonNode.Parse(posted.Content.ReadAsStringAsync().GetAwaiter().GetResult()) as JsonObject;
            if (answer?["Message"] is not JsonObject response) return $"it did not answer with a TrustResponse{(answer?["Error"] is { } error ? $" ({error})" : "")}";
            return protocol.Check(response, TrustProtocol.CertificateHash(own.RawData));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or IOException)
        {
            return $"the link could not be made: {(e.InnerException ?? e).Message}";
        }
    }

    // The data, once the Provider is admitted.
    public Stream Get(Uri uri) => http.GetStreamAsync(uri).GetAwaiter().GetResult();

    public void Dispose() { http.Dispose(); own.Dispose(); }

    // THE PROVIDER'S END: its server gives the body of the POST to TrustPath, the certificate it presented to the Controller and the
    // one the Controller presented to it; the body of its answer. Whether it admits the Controller is its policy.
    public static string Answer(TrustProtocol provider, string body, X509Certificate own, X509Certificate? controller)
    {
        if (JsonNode.Parse(body) is not JsonObject request) return new JsonObject { ["Ok"] = false, ["Error"] = "not a TrustRequest" }.ToJsonString();
        static string Of(X509Certificate? c) => c is null ? "" : TrustProtocol.CertificateHash(c.GetRawCertData());
        var (response, admitted, _) = provider.Answer(request, Of(own), Of(controller));
        return new JsonObject { ["Ok"] = admitted is not null, ["Message"] = response }.ToJsonString();
    }
}
