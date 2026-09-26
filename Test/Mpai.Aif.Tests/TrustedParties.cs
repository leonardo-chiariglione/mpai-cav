using System.Security.Cryptography;
using System.Text.Json.Nodes;

using AIF.Trust;

namespace Mpai.Aif.Tests;

// THE PARTIES OF THE TRUST PROTOCOL IN THE TESTS (M3223 3.4): the test Controller,
// controller-1, and AIM hosts, each a Trust Anchor kept in a file as a deployment
// keeps it; a host started to serve the Controllers whose anchors it is given.
public static class TrustedParties
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "mpai-phase13-anchors-" + Guid.NewGuid().ToString("N"));
    private static readonly Lazy<string> controller = new(() => Save("controller-1"));

    // An anchor saved: its file, and its Trust Anchor object - what the others are
    // given. Valid from an hour ago for a day, unless stated.
    public static string Save(string id, ECDsa? key = null, DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, $"{id}-{Guid.NewGuid():N}.json");
        var from = notBefore ?? DateTimeOffset.UtcNow.AddHours(-1);
        TrustAnchorKey.Save(path, id, key ?? ECDsa.Create(ECCurve.NamedCurves.nistP256), from, notAfter ?? from.AddDays(1));
        return path;
    }

    public static JsonObject AnchorOf(string saved) => TrustAnchorKey.Load(saved).Anchor.Object();

    public static string TrustFile(params JsonObject[] anchors)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, $"trust-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, new JsonArray(anchors.Select(a => (JsonNode)a.DeepClone()).ToArray()).ToJsonString());
        return path;
    }

    // The test Controller, the same anchor in every test of a run.
    public static string ControllerFile => controller.Value;
    public static TrustDomain Controller()
    {
        var (anchor, key) = TrustAnchorKey.Load(ControllerFile);
        return new TrustDomain(anchor, key);
    }

    // A host serving the test Controller - or the Controllers given - with its anchor.
    public static HostProcess Host(string? amds = null, string? hostAnchor = null, params JsonObject[] controllers)
    {
        var saved = hostAnchor ?? Save("aimhost-1");
        return new HostProcess(amds, saved, TrustFile(controllers.Length > 0 ? controllers : [AnchorOf(ControllerFile)])) { Anchor = AnchorOf(saved) };
    }
}
