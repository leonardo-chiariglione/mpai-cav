using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIF.Store;

// WHAT A SOURCE OF AIMs (the MPAI Store) GIVES A CONTROLLER FOR ONE AIM: its approved L3 - which has the AIMLicence, the Implementer's offer and its price, where there is one - and
// the names of the binaries and models the Store has a fingerprint of (the fingerprints stay with the Store: a Controller measures and asks, Verify). The Store does not hold the AIM: it validates it. The Implementations and models are where the L3 says
// they are (ImplementationURI), and the Controller obtains them from there. The AIM Licence is obtained apart (IAimSource.GetLicence), after the Controller has seen the offer.
public sealed record AimPackage(string AimName, string L3Json, string Version, IReadOnlyList<string> Binaries);

// THE MPAI STORE, as a Controller reaches it to obtain AIMs (the Store API: MPAI_AIFS_GetAIMMetadata, MPAI_AIFS_VerifyImplementation, MPAI_AIFS_GetAndParseArchive).
public interface IAimSource : IImplementationVerifier
{
    // The Metadata and the approved fingerprints of an AIM (MPAI_AIFS_GetAIMMetadata, MPAI_AIFS_VerifyImplementation); null where the Store does not have it. Nothing is paid for.
    AimPackage? Get(string aimName);

    // THE AIM LICENCE for the AIM, issued to the holder of the Trust Anchor that asks and signed by the Store (MPAI_AIFS_GetLicence): this is what is paid for, once, and the
    // Controller asks it after it has seen the offer and price in the Metadata. Null where the AIM's Metadata has no AIMLicence.
    string? GetLicence(string aimName);

    // The version the Store now has of an AIM, without obtaining it; null where it does not have it.
    string? CurrentVersion(string aimName);

    // Whether the Store has revoked an AIM Licence, asked by its identifier; null where it cannot be asked.
    bool? IsRevoked(string aimLicenceId);
}

// A CONTROLLER'S REPOSITORY OF AIMs - what it has obtained from the Store and keeps. It has the right to use what it obtained, so it is not obtained again; it may be
// very large and take long to obtain; a service Controller keeps a large set of AIMs for the services it provides. The folder is the Controller's AIM Metadata folder
// (the L3s are in it, as AmdStore reads them); beside them are the files of each AIM (implementations/<AIM>/) and the Controller's record of it (repository/<AIM>.json:
// version, the fingerprints approved by the Store, when it was obtained) and, where the AIM has one, its AIM Licence (repository/<AIM>.licence.json).
//
// Before an AIM is built its files are measured against the fingerprints kept - one that is not what the Store approved is refused (NOT_TRUSTED) - and its AIM Licence is
// checked: signed by the Store, naming this Controller, covering these files, in its Term, not revoked when the Store can be asked (NOT_LICENSED). Whether the Store has a newer
// version is asked for; the Controller decides whether to follow it (Update). What it holds it keeps until it does.
public sealed class AimRepository
{
    private readonly string _folder;

    public AimRepository(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(RecordFolder);
    }

    // What this Controller proves it is (its Instance Credential and chain, the Trust Anchors it trusts): an AIM Licence covers it if it is under the Trust Anchor the licence names.
    // Required to hold an AIM that has an AIM Licence.
    public AimLicences.ControllerIdentity? Identity { get; set; }

    // The public key a KeyID of the MPAI Store names, or null where this Controller knows none: what it checks the signature of an AIM Licence with.
    public Func<string, ECDsa?>? StoreKeys { get; set; }

    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    // True in the repository of what keeps AIMs that execute apart from the Controller: the identity is that of what controls their execution, and the AIM Licence has to allow it
    // - under the licensee's Trust Anchor, or, if not, under the control of another party.
    public bool ExecutesApart { get; set; }

    // How the Controller obtains an Implementation or a model from the location the L3 gives (its ImplementationURI): file: and https:. What it gets is measured against the
    // fingerprint the Store approved before it is kept.
    public Func<string, byte[]> Fetch { get; set; } = FetchUri;

    private static byte[] FetchUri(string uri)
    {
        var parsed = new Uri(uri, UriKind.Absolute);
        if (parsed.IsFile) return File.ReadAllBytes(parsed.LocalPath);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        return http.GetByteArrayAsync(parsed).GetAwaiter().GetResult();
    }

    public string Folder => _folder;
    private string RecordFolder => Path.Combine(_folder, "repository");
    private string RecordOf(string aim) => Path.Combine(RecordFolder, aim + ".json");
    private string LicenceFileOf(string aim) => Path.Combine(RecordFolder, aim + ".licence.json");
    private string StatusFileOf(string aim) => Path.Combine(RecordFolder, aim + ".status.json");

    // When the MPAI Store last confirmed that the AIM Licence is not revoked (the time it was obtained, if never asked since); null if unknown.
    private DateTimeOffset? LastStatusCheck(string aim) =>
        File.Exists(StatusFileOf(aim)) && DateTimeOffset.TryParse((string?)JsonNode.Parse(File.ReadAllText(StatusFileOf(aim)))?["Confirmed"], out var at) ? at : null;

    private void SaveStatusCheck(string aim, DateTimeOffset at) =>
        File.WriteAllText(StatusFileOf(aim), new JsonObject { ["Confirmed"] = at.ToString("O") }.ToJsonString());
    private string FilesOf(string aim) => Path.Combine(_folder, "implementations", aim);
    private string L3Of(string aim) => Path.Combine(_folder, aim + ".json");

    // Whether the Controller obtained the AIM (and holds it).
    public bool Holds(string aim) => File.Exists(RecordOf(aim));

    // The version of the L3 the Controller has of an AIM (held, or one of its own); null where it has none.
    public string? VersionOf(string aim)
    {
        if (!File.Exists(L3Of(aim))) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(L3Of(aim)));
        return VersionIn(document.RootElement);
    }

    // The AIM Licence the Store issued to this Controller for the AIM (AIF-LIC-V3.0); null where the AIM has none.
    public JsonObject? LicenceOf(string aim) => File.Exists(LicenceFileOf(aim)) ? JsonNode.Parse(File.ReadAllText(LicenceFileOf(aim)))!.AsObject() : null;

    // The identifier of that AIM Licence.
    public string? RightOf(string aim) => (string?)LicenceOf(aim)?["AIMLicenceID"];

    // THE FILE OF AN AIM, measured: it is what the Store approved, and the AIM Licence is good, or the AIM is refused.
    public string FileOf(string aim, string name)
    {
        CheckLicence(aim);
        var path = Path.Combine(FilesOf(aim), name);
        var record = Holds(aim) ? JsonNode.Parse(File.ReadAllText(RecordOf(aim))) : null;
        var held = (string?)(record?["Held"] ?? record?["Approved"])?[name] ?? throw new InvalidOperationException($"NOT_TRUSTED: the Store confirmed no {name} for {aim}.");
        if (!File.Exists(path) || Fingerprint(File.ReadAllBytes(path)) != held)
            throw new InvalidOperationException($"NOT_TRUSTED: {name} of {aim} is not the one the Store confirmed.");
        return path;
    }

    // THE AIM LICENCE, CHECKED before the AIM is built: an AIM whose Metadata offers a licence needs the AIM Licence the Store issued to this Controller, and it must be good - the
    // signature the Store's, this Controller named, the files it holds covered, in its Term, and - if the Store can be asked (source) - not revoked. An AIM that offers none needs none.
    public void CheckLicence(string aim, IAimSource? source = null)
    {
        if (!OffersLicence(aim)) return;
        var licence = LicenceOf(aim) ?? throw new InvalidOperationException($"NOT_LICENSED: {aim} has an AIMLicence in its Metadata, and this Controller holds no AIM Licence for it.");
        // Asking the Store, when it can be reached, confirms the status: a licence that is not revoked is confirmed now.
        var revoked = source is not null && (string?)licence["AIMLicenceID"] is { } id ? source.IsRevoked(id) : null;
        var confirmed = revoked == false ? Clock() : LastStatusCheck(aim);
        Verify(aim, licence, HeldFingerprints(aim), keeping: false, revoked, confirmed, File.ReadAllText(L3Of(aim)));
        if (revoked == false) SaveStatusCheck(aim, Clock());
    }

    private bool OffersLicence(string aim)
    {
        if (!File.Exists(L3Of(aim))) return false;
        using var document = JsonDocument.Parse(File.ReadAllText(L3Of(aim)));
        return document.RootElement.TryGetProperty("AIMLicence", out _);
    }

    private IReadOnlyDictionary<string, string> HeldFingerprints(string aim) =>
        Holds(aim) && JsonNode.Parse(File.ReadAllText(RecordOf(aim))) is { } record && (record["Held"] ?? record["Approved"]) is JsonObject held
            ? held.ToDictionary(p => p.Key, p => (string)p.Value!)
            : new Dictionary<string, string>();

    private void Verify(string aim, JsonObject licence, IReadOnlyDictionary<string, string> held, bool keeping, bool? revoked, DateTimeOffset? confirmed, string l3Json)
    {
        var metadata = JsonNode.Parse(l3Json)!.AsObject();
        var identity = Identity ?? throw new InvalidOperationException($"NOT_LICENSED: this Controller has no identity to hold an AIM Licence for {aim} with.");
        var keys = StoreKeys ?? throw new InvalidOperationException($"NOT_LICENSED: this Controller knows no key of the MPAI Store to check the AIM Licence of {aim} with.");
        var wrong = AimLicences.Check(licence, identity, aim, held, keys, Clock(), revoked, keeping, confirmed, ExecutesApart, metadata["AIMLicence"] as JsonObject, (string?)metadata["Identifier"]?["ImplementerID"]);
        if (wrong.Count > 0) throw new InvalidOperationException($"NOT_LICENSED: the AIM Licence of {aim}: {string.Join(" ", wrong)}");
    }

    // OBTAINS an AIM it does not hold; one it holds is used as it is. True when it was obtained from the source (and paid for), false when the repository had it.
    // fetchFiles false: the Controller of a Module whose AIMs run on AIM hosts keeps the Metadata, the fingerprints and the AIM Licence and leaves the Implementations to the hosts.
    public bool Obtain(string aim, IAimSource source, bool fetchFiles = true)
    {
        if (Holds(aim) && File.Exists(L3Of(aim))) return false;
        Keep(source, aim, fetchFiles);
        return true;
    }

    // WHAT A CONTROLLER HANDS AN AIM HOST that is to run an AIM (null where the repository holds none of it): the Metadata and the names of the binaries the Store has a fingerprint of, the AIM Licence.
    public JsonObject? PackageForHost(string aim)
    {
        if (!Holds(aim) || !File.Exists(L3Of(aim))) return null;
        var record = JsonNode.Parse(File.ReadAllText(RecordOf(aim)))!;
        var package = new JsonObject { ["L3"] = File.ReadAllText(L3Of(aim)), ["Version"] = (string?)record["Version"] ?? "", ["Binaries"] = record["Binaries"]?.DeepClone() ?? new JsonArray() };
        if (File.Exists(LicenceFileOf(aim))) package["Licence"] = File.ReadAllText(LicenceFileOf(aim));
        return package;
    }

    // AN AIM HOST TAKES IN an AIM its Controller hands it (the repository of the host): the Implementations and models are obtained from the places the Metadata gives and measured;
    // the host does not decide whether they are what the Store approved - it returns what it measured, and the Controller asks the Store (verify) and decides. The AIM Licence is
    // checked as the host's - a host of the licensee's or of another party's; then it is kept, and the AIM can be built. Returns the fingerprints of the files it holds.
    public IReadOnlyDictionary<string, string> Accept(JsonObject package, Func<string, string, VerifyResult>? verify = null)
    {
        var aim = (string)JsonNode.Parse((string)package["L3"]!)!["Identifier"]!["AIMName"]!;
        // What it holds already - the same version, the same AIM Licence - is not obtained again.
        if (Holds(aim) && File.Exists(L3Of(aim)) && VersionOf(aim) == (string?)package["Version"] && File.Exists(LicenceFileOf(aim)) == ((string?)package["Licence"] is not null) &&
            (package["Licence"] is null || File.ReadAllText(LicenceFileOf(aim)) == (string)package["Licence"]!))
        { CheckLicence(aim); return HeldFingerprints(aim); }
        var binaries = ((JsonArray?)package["Binaries"] ?? []).Select(b => (string)b!).ToList();
        return Keep(new AimPackage(aim, (string)package["L3"]!, (string?)package["Version"] ?? "", binaries), (string?)package["Licence"], fetch: true, verify);
    }

    // The version the source has when it is newer than the Controller's (held, or an L3 of its own); null when it is not newer, or the source does not have it.
    public string? NewerVersion(string aim, IAimSource source)
    {
        var there = source.CurrentVersion(aim);
        if (there is null) return null;
        var here = VersionOf(aim);
        return here is null || Compare(there, here) > 0 ? there : null;
    }

    // FOLLOWS the Store: obtains the AIM again, as the version the Store now has. The previous version's L3 is replaced.
    public void Update(string aim, IAimSource source, bool fetchFiles = true) => Keep(source, aim, fetchFiles);

    // The Metadata first; if it offers a licence, the AIM Licence (the price is paid here); then the Implementations from where the Metadata says.
    private void Keep(IAimSource source, string aim, bool fetch)
    {
        var package = source.Get(aim) ?? throw new FileNotFoundException($"{aim} is neither in the repository nor in the Store.");
        string? licence = null;
        using (var l3 = JsonDocument.Parse(package.L3Json))
            if (l3.RootElement.TryGetProperty("AIMLicence", out _)) licence = source.GetLicence(aim);
        Keep(package, licence, fetch, (binary, fingerprint) => source.Verify(aim, binary, fingerprint));
    }

    private IReadOnlyDictionary<string, string> Keep(AimPackage package, string? licence, bool fetch, Func<string, string, VerifyResult>? verify)
    {
        // The Implementations and models are where the L3 says: obtained from there, and measured against what the Store approved, before anything is kept.
        var files = new Dictionary<string, byte[]>();
        using (var l3 = JsonDocument.Parse(package.L3Json))
            if (l3.RootElement.TryGetProperty("Implementations", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var item in list.EnumerateArray())
                {
                    if (!fetch) break;                                                                       // the Implementations are the hosts', not this Controller's
                    var name = item.TryGetProperty("BinaryName", out var b) ? b.GetString() : null;
                    if (name is null || !package.Binaries.Contains(name)) continue;                           // what the Store has no fingerprint of is not obtained
                    var uri = item.TryGetProperty("ImplementationURI", out var u) ? u.GetString() : null;
                    byte[] bytes;
                    try { bytes = Fetch(FileUri(uri ?? throw new InvalidOperationException("the L3 gives no ImplementationURI"), name)); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or UriFormatException or HttpRequestException or InvalidOperationException)
                    { throw new InvalidOperationException($"{name} of {package.AimName} could not be obtained from {uri}: {e.Message}", e); }
                    // The Controller measured; the Store says whether that is what it approved. (A host measures and the Controller asks: verify is null there.)
                    if (verify is not null && verify(name, Fingerprint(bytes)) != VerifyResult.Same)
                        throw new InvalidOperationException($"NOT_TRUSTED: {name} of {package.AimName} is not what the Store approved (obtained from {uri}).");
                    files[name] = bytes;
                }
        if (fetch && package.Binaries.Except(files.Keys).Any())
            throw new InvalidOperationException($"{package.AimName}: the L3 does not say where {string.Join(", ", package.Binaries.Except(files.Keys))} can be obtained.");
        var held = files.ToDictionary(f => f.Key, f => Fingerprint(f.Value));

        // The AIM Licence, when the AIM has an offer: it must come, and be good, for the Controller to keep the AIM (the right to keep it is one it grants).
        using (var l3 = JsonDocument.Parse(package.L3Json))
        {
            var offers = l3.RootElement.TryGetProperty("AIMLicence", out _);
            if (offers && licence is null) throw new InvalidOperationException($"NOT_LICENSED: {package.AimName} has an AIMLicence in its Metadata and the Store gave no AIM Licence.");
            if (licence is not null)
                Verify(package.AimName, JsonNode.Parse(licence)!.AsObject(), held, keeping: true, revoked: null, confirmed: null, package.L3Json);
        }

        var into = FilesOf(package.AimName);
        if (Directory.Exists(into)) Directory.Delete(into, true);
        Directory.CreateDirectory(into);
        foreach (var (name, bytes) in files) File.WriteAllBytes(Path.Combine(into, name), bytes);
        File.WriteAllText(L3Of(package.AimName), package.L3Json);
        if (licence is not null) { File.WriteAllText(LicenceFileOf(package.AimName), licence); SaveStatusCheck(package.AimName, Clock()); }   // issued now: confirmed now
        else { foreach (var stale in new[] { LicenceFileOf(package.AimName), StatusFileOf(package.AimName) }) if (File.Exists(stale)) File.Delete(stale); }
        File.WriteAllText(RecordOf(package.AimName), new JsonObject
        {
            ["AIMName"] = package.AimName, ["Version"] = package.Version,
            ["Binaries"] = new JsonArray(package.Binaries.Select(b => (JsonNode)JsonValue.Create(b)!).ToArray()),
            ["Held"] = new JsonObject(held.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)JsonValue.Create(p.Value)))),       // what it measured; the Store said it is what it approved
            ["Obtained"] = DateTimeOffset.UtcNow.ToString("O")
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return held;
    }

    // The ImplementationURI is the Implementation itself (a file), or the folder of the package holding it: BinaryName.dll, else a file named BinaryName.
    private static string FileUri(string uri, string binaryName)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile || !Directory.Exists(parsed.LocalPath)) return uri;
        var dll = Path.Combine(parsed.LocalPath, binaryName + ".dll");
        return new Uri(File.Exists(dll) ? dll : Path.Combine(parsed.LocalPath, binaryName)).AbsoluteUri;
    }

    public static string Fingerprint(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    // The version an L3 states for its Implementation.
    public static string VersionIn(JsonElement l3) =>
        l3.TryGetProperty("Implementations", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0 &&
        list[0].TryGetProperty("Version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // Versions as numbers separated by dots (1.2 is newer than 1.1; 1.10 than 1.9); anything else by its text.
    public static int Compare(string a, string b)
    {
        var x = a.Split('.'); var y = b.Split('.');
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var p = i < x.Length ? x[i] : "0"; var q = i < y.Length ? y[i] : "0";
            var c = int.TryParse(p, out var m) && int.TryParse(q, out var n) ? m.CompareTo(n) : string.CompareOrdinal(p, q);
            if (c != 0) return c;
        }
        return 0;
    }
}

// THE LOCAL MPAI STORE as a source of AIMs (a folder of published L3s, with the fingerprints the Store approved; it holds no Implementation). An AIM whose Metadata offers a licence
// (AIMLicence) is licensed to the holder - the Controller that obtains it - by an AIM Licence the Store issues and signs with its key (issuer); the Store counts what it gave
// (Obtained), so that what is paid for once can be seen to be paid for once, and can revoke an AIM Licence.
public sealed class StoreAimSource(string folder, string trustAnchorId, AimLicences.Issuer? issuer = null, string? controllerId = null) : IAimSource
{
    // One AIM Licence per AIM for a holder of a Trust Anchor, however many Controllers it has and however often they are created: what is paid for once is not paid for again.
    private readonly Dictionary<string, string> _licences = new();
    private readonly List<string> _obtained = [];
    private readonly HashSet<string> _revoked = [];
    private readonly List<string> _issued = [];

    public IReadOnlyList<string> Obtained => _obtained;

    // The identifiers of the AIM Licences the Store issued.
    public IReadOnlyList<string> Issued => _issued;

    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    // Whether the Store can be reached: where it cannot, a Controller is not told of a revocation.
    public bool Reachable { get; set; } = true;

    public void Revoke(string aimLicenceId) => _revoked.Add(aimLicenceId);

    public bool? IsRevoked(string aimLicenceId) => Reachable ? _revoked.Contains(aimLicenceId) : null;

    public string? CurrentVersion(string aimName)
    {
        var path = Path.Combine(folder, aimName + ".json");
        if (!File.Exists(path)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return AimRepository.VersionIn(document.RootElement);
    }

    public AimPackage? Get(string aimName)
    {
        var path = Path.Combine(folder, aimName + ".json");
        if (!File.Exists(path)) return null;
        var l3 = File.ReadAllText(path);
        using var document = JsonDocument.Parse(l3);
        var fingerprints = new ImplementationFingerprints(folder);
        var binaries = new List<string>();
        if (document.RootElement.TryGetProperty("Implementations", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var item in list.EnumerateArray())
                if (item.TryGetProperty("BinaryName", out var b) && b.GetString() is { } name && fingerprints.Approved(aimName, name) is not null)
                    binaries.Add(name);                                                      // the Store has a fingerprint of it; the files themselves are where the L3 says

        _obtained.Add(aimName);
        return new AimPackage(aimName, l3, AimRepository.VersionIn(document.RootElement), binaries);
    }

    public VerifyResult Verify(string aimName, string binaryName, string fingerprint) => new ImplementationFingerprints(folder).Verify(aimName, binaryName, fingerprint);

    // MPAI_AIFS_GetLicence: the AIM Licence for the holder of the Trust Anchor, issued the first time and the same after - what is paid for once is not paid for again.
    public string? GetLicence(string aimName)
    {
        var path = Path.Combine(folder, aimName + ".json");
        if (!File.Exists(path)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("AIMLicence", out var offer)) return null;
        if (_licences.TryGetValue(aimName, out var kept)) return kept;
        var key = issuer ?? throw new InvalidOperationException($"{aimName} offers an AIM Licence and this Store has no key to issue one with.");
        var fingerprints = new ImplementationFingerprints(folder);
        var approved = new Dictionary<string, string>();
        if (document.RootElement.TryGetProperty("Implementations", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var item in list.EnumerateArray())
                if (item.TryGetProperty("BinaryName", out var b) && b.GetString() is { } name && fingerprints.Approved(aimName, name) is { } fingerprint) approved[name] = fingerprint;
        var issued = AimLicences.Issue(JsonNode.Parse(offer.GetRawText())!.AsObject(), aimName, AimRepository.VersionIn(document.RootElement), approved, trustAnchorId, controllerId, key, Clock(), document.RootElement.GetProperty("Identifier").GetProperty("ImplementerID").GetString() ?? "");
        _issued.Add((string)issued["AIMLicenceID"]!);
        return _licences[aimName] = issued.ToJsonString();
    }
}
