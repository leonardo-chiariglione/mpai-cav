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

    // The fingerprint of a file - of a model, of a data file: SHA-256, hexadecimal, upper case.
    public static string Of(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    // THE FINGERPRINT OF AN IMPLEMENTATION IS THAT OF ITS PACKAGE (AIF V3.0, MPAI Store 4; Zero Trust and Profiles 5; 2026-11-13): the SHA-256 of the list of the
    // files of the package that the Implementation loads - for each, in the order of their paths, its path and its SHA-256 - so that no file it loads is changed
    // unseen. The files it loads: the binary its BinaryName names and the assemblies it refers to, at any depth, that the package has; not the assemblies of
    // MPAI-AIF itself (AIF.*), which the Controller gives every package so that none replaces the framework types the Controller relies on. A binary that is not
    // a .NET assembly is a package of one file.
    public const string FrameworkPrefix = "AIF.";

    public static string OfImplementation(string binaryFile)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(binaryFile))!;
        // The binary is listed as BinaryName.dll, whatever the name of the file that holds it (an AIM repository keeps it under its BinaryName).
        var file = Path.GetFileName(binaryFile);
        var listed = Path.HasExtension(file) ? file : file + ".dll";
        string? Where(string name) => name == listed ? Path.Combine(folder, file) : File.Exists(Path.Combine(folder, name)) ? Path.Combine(folder, name) : null;
        return OfFiles(PackageFiles(listed, name => Where(name) is { } path ? File.ReadAllBytes(path) : null, Cached(folder, listed, file)));
    }

    // The SHA-256 of a file of a package on disk, kept while the file is unchanged (a package is measured at every Start).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, long Length, DateTime Written), (string Sha256, string[] References)> known = new();

    private static Func<string, byte[], (string, string[])?> Cached(string folder, string listed, string file) => (name, bytes) =>
    {
        var info = new FileInfo(Path.Combine(folder, name == listed ? file : name));
        return known.GetOrAdd((info.FullName, info.Length, info.LastWriteTimeUtc), _ => (Convert.ToHexString(SHA256.HashData(bytes)), References(bytes).ToArray()));
    };

    // The fingerprint of the Implementation binaryName inside a package that is a .zip (its files at the top of the package), or null if its binary is not there.
    public static string? OfPackage(Stream zip, string binaryName)
    {
        using var archive = new System.IO.Compression.ZipArchive(zip, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);
        byte[]? Read(string name)
        {
            if (archive.GetEntry(name) is not { } entry) return null;
            using var stream = entry.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }
        var files = PackageFiles(binaryName + ".dll", Read);
        return files.Count == 0 ? null : OfFiles(files);
    }

    // The files of a package the Implementation loads, with their SHA-256, in the order of their paths (read: the bytes of a file of the package, or null).
    public static IReadOnlyList<(string Path, string Sha256)> PackageFiles(string binary, Func<string, byte[]?> read, Func<string, byte[], (string, string[])?>? known = null)
    {
        var found = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var todo = new Stack<string>([binary]);
        while (todo.TryPop(out var name))
        {
            if (found.ContainsKey(name) || read(name) is not { } bytes) continue;
            var (sha, references) = known?.Invoke(name, bytes) ?? (Convert.ToHexString(SHA256.HashData(bytes)), References(bytes).ToArray());
            found[name] = sha;
            foreach (var reference in references)
                if (!reference.StartsWith(FrameworkPrefix, StringComparison.Ordinal)) todo.Push(reference + ".dll");
        }
        return found.Select(f => (f.Key, f.Value)).ToList();
    }

    private static IEnumerable<string> References(byte[] assembly)
    {
        try
        {
            using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(assembly));
            if (!pe.HasMetadata) return [];
            var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            return metadata.AssemblyReferences.Select(h => metadata.GetString(metadata.GetAssemblyReference(h).Name)).ToList();
        }
        catch (BadImageFormatException) { return []; }                           // not a .NET assembly: a package of one file
    }

    public static string OfFiles(IReadOnlyList<(string Path, string Sha256)> files) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Concat(files.Select(f => f.Path + " " + f.Sha256 + "\n")))));

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
