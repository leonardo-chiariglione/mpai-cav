using AIF.Channels;
using AIF.Trust;

namespace AIF.Controller;

// A CAV'S TRUST IN THE CITY IT IS IN (M3241 3.1, 3.3): what its External hub needs -
// its links admitted by the Trust Protocol, its anchor presented with the credential
// of the city's Trust Authority; what its Module sends signed with its key; what it
// receives verified against the CAV the link admitted.
public sealed class CityTrust
{
    private readonly TrustProtocol protocol;
    private readonly TrustAuthority.Admission admission;
    private readonly string city;
    private readonly Func<DateTimeOffset> now;

    public CityTrust(TrustAuthority authority, TrustAuthority.Admission admission, Func<DateTimeOffset>? now = null)
    {
        this.admission = admission;
        city = authority.City;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        protocol = authority.ProtocolFor(admission);
    }

    public string CavId => admission.Anchor.AnchorId;
    public ILinkAdmission Admission => new TrustedLink(protocol);

    public string Sign(string json, string? binding = null) => ExternalSigning.Sign(json, admission.Anchor, admission.Key, city, now(), binding: binding);

    public string? StillTrusted(string from) => protocol.StillTrusted(from);

    public string? Verify(string from, string json, string? binding = null)
    {
        if (protocol.StillTrusted(from) is { } expired) return expired;
        var outcome = ExternalSigning.Verify(json, from, protocol.AnchorOf, binding);
        return outcome == ExternalSigning.Outcome.Valid ? null : outcome.ToString();
    }
}
