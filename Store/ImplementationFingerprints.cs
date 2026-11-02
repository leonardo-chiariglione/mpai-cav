using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIF.Store;

// THE FINGERPRINTS OF APPROVED IMPLEMENTATIONS (Phase 13, M3223 3.2; OI-9). When
// the Store approves an L3 it records, for each Implementation the L3 names by its
// BinaryName, the SHA-256 of that binary as the Implementer submitted it. A
// Controller that verifies what it runs compares the binary it is about to run with
// this record: the Store is the guarantor that an Implementation is what its
// Implementer built (M3205 3.7).
//
// Kept beside the L3s, in approved/<AIMName>.json - the Store's record, not the
// Implementer's Metadata.
// WHAT THE STORE SAYS OF A FINGERPRINT IT IS ASKED ABOUT (MPAI_AIFS_VerifyImplementation). The real fingerprint of an approved Implementation is the Store's alone: a Controller measures
// what it has obtained, asks, and is told. It is never given the fingerprint to compare for itself.
public enum VerifyResult { Same, Different, NotRecorded }

public interface IImplementationVerifier
{
    VerifyResult Verify(string aimName, string binaryName, string fingerprint);

    // For the version of the AIM Instance Metadata the Controller holds (the version the Store published it as): what the Store approved for that version. A verifier that keeps no versions answers as for the latest.
    VerifyResult Verify(string aimName, string binaryName, string fingerprint, string? version) => Verify(aimName, binaryName, fingerprint);
}

public sealed class ImplementationFingerprints : IImplementationVerifier
{
    public const string Folder = "approved";
    private readonly string folder;

    public ImplementationFingerprints(string storeFolder) => folder = Path.Combine(storeFolder, Folder);

    // The fingerprint of a file: SHA-256, hexadecimal, upper case.
    public static string Of(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    // The fingerprint of a binary inside a package that is a .zip: the entry named binaryName + ".dll" at the top of the package, or null if it is not there.
    public static string? OfPackage(Stream zip, string binaryName)
    {
        using var archive = new System.IO.Compression.ZipArchive(zip, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);
        var entry = archive.GetEntry(binaryName + ".dll");
        if (entry is null) return null;
        using var stream = entry.Open();
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    // Records the fingerprint of the binary that implements an AIM, as approved.
    public void Approve(string aimName, string binaryName, string fingerprint, DateTimeOffset at, string? version = null)
    {
        Directory.CreateDirectory(folder);
        var path = PathOf(aimName);
        var record = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject()
                                       : new JsonObject { ["AIMName"] = aimName, ["Implementations"] = new JsonObject() };
        var approved = new JsonObject { ["Algorithm"] = "SHA-256", ["Fingerprint"] = fingerprint, ["Approved"] = at.ToString("O") };
        record["Implementations"]![binaryName] = approved.DeepClone();              // the latest
        if (version is not null)
        {
            // Every version keeps what was approved for it: a new version does not take away the approval of the one a Controller still holds (STR-3; Verification, 2).
            var versions = record["Versions"] as JsonObject ?? new JsonObject();
            var of = versions[version] as JsonObject ?? new JsonObject();
            of[binaryName] = approved.DeepClone();
            versions[version] = of;
            record["Versions"] = versions;
        }
        File.WriteAllText(path, record.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    // The Store's answer to a fingerprint calculated elsewhere: the one it recorded for that binary of that AIM, or not.
    public VerifyResult Verify(string aimName, string binaryName, string fingerprint) =>
        Approved(aimName, binaryName) is not { } recorded ? VerifyResult.NotRecorded
        : string.Equals(recorded, fingerprint, StringComparison.OrdinalIgnoreCase) ? VerifyResult.Same : VerifyResult.Different;

    public VerifyResult Verify(string aimName, string binaryName, string fingerprint, string? version)
    {
        var path = PathOf(aimName);
        // A record made before versions were kept has none: it answers as for the latest.
        if (version is null || !File.Exists(path) || JsonNode.Parse(File.ReadAllText(path))?["Versions"] is not JsonObject versions) return Verify(aimName, binaryName, fingerprint);
        return (string?)versions[version]?[binaryName]?["Fingerprint"] is not { } recorded ? VerifyResult.NotRecorded
             : string.Equals(recorded, fingerprint, StringComparison.OrdinalIgnoreCase) ? VerifyResult.Same : VerifyResult.Different;
    }

    // The binaries of an AIM the Store has a fingerprint of (their names; the fingerprints stay with the Store).
    public IReadOnlyList<string> Binaries(string aimName)
    {
        var path = PathOf(aimName);
        return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path))?["Implementations"] is JsonObject list ? list.Select(p => p.Key).ToList() : [];
    }

    // The approved fingerprint of the binary an AIM's L3 names; null where the Store
    // approved none. Only the Store reads it: a Controller asks Verify.
    public string? Approved(string aimName, string binaryName)
    {
        var path = PathOf(aimName);
        if (!File.Exists(path)) return null;
        return (string?)JsonNode.Parse(File.ReadAllText(path))?["Implementations"]?[binaryName]?["Fingerprint"];
    }

    private string PathOf(string aimName) => Path.Combine(folder, aimName + ".json");
}
