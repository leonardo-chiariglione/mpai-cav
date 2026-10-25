// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using System.Security.Cryptography;
using System.Text.Json;

namespace AIF.Controller;

// THE TERMS OF DATA IN ACCESS: the licence it was published under, where that is stated, and whether it describes persons.
// Informational: the Controller does not enforce them; they travel with the Metadata so that the one who deploys a Module
// can see what the Module will hold.
public sealed record AccessTerms(string Licence, string Uri, bool PersonalData);

// WHAT AN AIM DECLARES IT READS (AIM Metadata, AccessSources): data identified by the URI it is obtained from - a file, or a
// set of files under a URI that ends in "/" - with the SHA-256 that pins it (of the file, or of the manifest of the set), the
// Keys it reads where it says so, and a Name by which the AIM asks for it. The SHA-256 is the identity of the data and the URI
// only where it is found: Mirrors are other places holding the same bytes. ValidWith names, by SHA-256, the other data of the
// same AIM that this data is only meaningful with (a database of features and the model that computed them). MPAI does not
// classify the data: what is inside is the AIM's own business.
public sealed record AccessEntry(
    string Name, string Uri, string Sha256, IReadOnlyList<string> KeyPrefixes, bool IsOptional, string Description = "",
    IReadOnlyList<string>? Mirrors = null, IReadOnlyList<string>? ValidWith = null, AccessTerms? Terms = null, string Setting = "", string AsFile = "", string Member = "", bool Unpack = false, string Inside = "")
{
    public const string ManifestKey = "manifest.json";

    // A set of files: its URI ends in "/", and a manifest of it (manifest.json) lists the Keys and the SHA-256 of each.
    public bool IsSet => Uri.EndsWith('/');

    // A single file's Key is its name.
    public string FileName => Member.Length > 0 ? Member[(Member.LastIndexOf('/') + 1)..] : Uri.TrimEnd('/')[(Uri.TrimEnd('/').LastIndexOf('/') + 1)..];

    // Not yet bound to data: a template value, /* ... */.
    public bool IsPlaceholder => Uri.TrimStart().StartsWith("/*", StringComparison.Ordinal);

    // Where the data is looked for, in order: its Uri, then its Mirrors.
    public IReadOnlyList<string> Locations => [Uri, .. Mirrors ?? []];

    // The entries of an AIM's Metadata (a JSON element holding AccessSources); none where it declares none.
    public static List<AccessEntry> ReadFrom(JsonElement aim)
    {
        var entries = new List<AccessEntry>();
        if (!aim.TryGetProperty("AccessSources", out var list) || list.ValueKind != JsonValueKind.Array) return entries;
        foreach (var e in list.EnumerateArray())
        {
            string Text(JsonElement of, string p) => of.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            List<string> Texts(string p) => e.TryGetProperty(p, out var k) && k.ValueKind == JsonValueKind.Array
                ? k.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                : [];
            var optional = e.TryGetProperty("IsOptional", out var o) && o.ValueKind == JsonValueKind.True;
            AccessTerms? terms = null;
            if (e.TryGetProperty("Terms", out var t) && t.ValueKind == JsonValueKind.Object)
                terms = new AccessTerms(Text(t, "Licence"), Text(t, "Uri"), t.TryGetProperty("PersonalData", out var p) && p.ValueKind == JsonValueKind.True);
            entries.Add(new AccessEntry(Text(e, "Name"), Text(e, "Uri"), Text(e, "Sha256"), Texts("KeyPrefixes"), optional,
                Text(e, "Description"), Texts("Mirrors"), Texts("ValidWith"), terms, Text(e, "Setting"), Text(e, "AsFile"), Text(e, "Member"),
                e.TryGetProperty("Unpack", out var u) && u.ValueKind == JsonValueKind.True, Text(e, "Inside")));
        }
        return entries;
    }

    // WHETHER THE ENTRIES OF ONE AIM ARE CONSISTENT: a name used once; one SHA-256 for one Uri; Mirrors of the same kind as the
    // Uri (a set's end in a slash); and every SHA-256 in ValidWith that of another entry. Empty when they are.
    public static string Check(IReadOnlyList<AccessEntry> entries)
    {
        var bound = entries.Where(e => !e.IsPlaceholder).ToList();
        foreach (var e in bound)
        {
            if (e.Name.Length == 0) return "an Access entry has no Name";
            if (e.Terms is null || e.Terms.Licence.Length == 0 || e.Terms.Uri.Length == 0) return $"'{e.Name}' states no Terms: the licence of the data and the URI where the terms are stated";
            if (bound.Count(x => x.Name == e.Name) > 1) return $"the Name '{e.Name}' is used by more than one entry";
            if (bound.Any(x => x.Uri == e.Uri && x.Member == e.Member && !Same(x.Sha256, e.Sha256))) return $"'{e.Name}': the Uri {e.Uri} is given two different SHA-256";
            if (e.Member.Length > 0 && e.IsSet) return $"'{e.Name}': a set of files cannot be a member of an archive";
            if (e.Unpack && (e.IsSet || e.Member.Length > 0)) return $"'{e.Name}': an archive to unpack is a file, not a set nor a member of an archive";
            if (e.Inside.Length > 0 && (!e.Unpack || e.Inside.Split('/', '\\').Contains(".."))) return $"'{e.Name}': Inside is a path inside an unpacked archive";
            if (e.AsFile.Length > 0 && bound.Count(x => x.AsFile == e.AsFile) > 1) return $"the file name '{e.AsFile}' is given to more than one entry";
            if (e.Mirrors?.FirstOrDefault(m => m.EndsWith('/') != e.IsSet) is { } odd) return $"'{e.Name}': the mirror {odd} is not of the kind of its Uri";
            foreach (var h in e.ValidWith ?? [])
                if (!bound.Any(x => x != e && Same(x.Sha256, h))) return $"'{e.Name}' is valid only with data {h}, which this AIM does not declare";
        }
        return "";
    }

    // FOR AN IMPLEMENTATION THAT RUNS A PROGRAM which reads files: the settings of the AIM with, for each entry that names a
    // Setting, the file where Access keeps the data - read through the Access of the AIM, so that its rights apply. The
    // program is the Implementation's; the data it reads is Access's.
    // An entry that names AsFile is given under that name, in a folder of the AIM Instance (group) where the files of its
    // entries are together: a model whose parts refer to one another by file name - an ONNX model and its external data -
    // finds them. The file is the one Access keeps, linked and not copied where the system allows it.
    public static IReadOnlyDictionary<string, string> Overlay(IReadOnlyDictionary<string, string> settings, IReadOnlyList<AccessEntry> entries, IAccessReader? access, string group = "")
    {
        var wanted = entries.Where(e => (e.Setting.Length > 0 || e.AsFile.Length > 0) && !e.IsPlaceholder).ToList();
        if (access is null || wanted.Count == 0) return settings;
        var changed = new Dictionary<string, string>(settings, StringComparer.Ordinal);
        foreach (var e in wanted)
        {
            if (access.LocalPath(e.Uri, e.IsSet ? "" : e.FileName, out var path) != AccessOutcome.OK) continue;
            if (e.Unpack) path = Unpacked(path, e.Sha256, e.Inside);
            else if (e.AsFile.Length > 0) path = Named(path, group, e.AsFile);
            if (e.Setting.Length > 0) changed[e.Setting] = path;
        }
        return changed;
    }

    // A PROGRAM THAT COMES IN AN ARCHIVE (a zip of an executable and the libraries and data beside it) is unpacked, once, into
    // a folder named by the SHA-256 of the archive - the same archive for several AIMs is unpacked once - and Inside says which
    // file of it the setting is given. A file of the archive that would be written outside the folder refuses the archive.
    private static string Unpacked(string archive, string sha256, string inside)
    {
        var root = Directory.GetParent(Directory.GetParent(archive)!.FullName)!.FullName;
        var folder = Path.Combine(root, "unpacked", AccessStore.DiskName(sha256.ToUpperInvariant()));
        var done = folder + ".done";
        if (!File.Exists(done) || !Directory.Exists(folder))
        {
            var temp = folder + "." + Guid.NewGuid().ToString("N") + ".tmp";
            Directory.CreateDirectory(temp);
            try
            {
                var top = Path.GetFullPath(temp) + Path.DirectorySeparatorChar;
                using (var zip = System.IO.Compression.ZipFile.OpenRead(archive))
                    foreach (var entry in zip.Entries)
                    {
                        var target = Path.GetFullPath(Path.Combine(temp, entry.FullName));
                        if (!target.StartsWith(top, StringComparison.Ordinal)) throw new InvalidDataException($"{entry.FullName} is outside the folder the archive is unpacked into");
                        if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        System.IO.Compression.ZipFileExtensions.ExtractToFile(entry, target, true);
                    }
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
                Directory.Move(temp, folder);
                File.WriteAllText(done, sha256);
            }
            catch { try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { } if (!Directory.Exists(folder)) throw; }
        }
        return inside.Length == 0 ? folder : Path.Combine(folder, inside.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string Named(string kept, string group, string name)
    {
        var root = Directory.GetParent(Directory.GetParent(kept)!.FullName)!.FullName;
        var folder = Path.Combine(root, "view", AccessStore.DiskName(group));
        Directory.CreateDirectory(folder);
        var view = Path.Combine(folder, Path.GetFileName(name));
        var source = new FileInfo(kept);
        if (File.Exists(view) && new FileInfo(view) is var have && have.Length == source.Length && have.LastWriteTimeUtc == source.LastWriteTimeUtc) return view;
        File.Delete(view);
        if (!OperatingSystem.IsWindows() || !CreateHardLink(view, kept, IntPtr.Zero))
        {
            File.Copy(kept, view, true);
            File.SetLastWriteTimeUtc(view, source.LastWriteTimeUtc);
        }
        return view;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newFile, string existingFile, IntPtr security);

    internal static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

// THE CONTROLLER OBTAINS WHAT AN AIM DECLARES: a file from its URI (or, failing that, a mirror), a set through its manifest,
// each checked against the SHA-256 the Metadata gives, and keeps it in Access under the URI. Data whose hash is not the
// declared one is refused wherever it came from, so what the AIM reads is exactly what its Metadata names. The items of a set
// are obtained when first read, each checked against the hash its manifest gives. http, https and file URIs. Data is
// received into a file as it arrives and hashed on the way, so its size is not held in memory.
public sealed class AccessFetcher(AccessStore store, HttpClient? http = null)
{
    public const string Writer = "Controller";
    private readonly HttpClient http = http ?? Client();

    // Servers refuse a request that says nothing of who makes it.
    private static HttpClient Client()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromHours(1) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MPAI-AIF-Controller/3.0");
        return client;
    }

    // The data at a location, in a temporary file, and its SHA-256. With a member, the location holds an archive (a zip) and
    // the data is that file of it: the SHA-256 is that of the file, as the Metadata gives it, since that is what the AIM reads.
    private string Download(string location, string member, out string hash)
    {
        if (member.Length == 0) return Download(location, out hash);
        var archive = Download(location, out _);
        var temp = Path.Combine(Path.GetTempPath(), "mpai-access-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(archive);
            var found = zip.GetEntry(member) ?? throw new FileNotFoundException($"{member} is not in the archive");
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var from = found.Open())
            using (var to = File.Create(temp))
            {
                var buffer = new byte[1 << 20];
                int n;
                while ((n = from.Read(buffer, 0, buffer.Length)) > 0) { sha.AppendData(buffer, 0, n); to.Write(buffer, 0, n); }
            }
            hash = Convert.ToHexString(sha.GetHashAndReset());
            return temp;
        }
        catch { try { File.Delete(temp); } catch { } throw; }
        finally { try { File.Delete(archive); } catch { } }
    }

    private string Download(string location, out string hash)
    {
        var temp = Path.Combine(Path.GetTempPath(), "mpai-access-" + Guid.NewGuid().ToString("N"));
        var u = new Uri(location);
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var from = u.IsFile ? File.OpenRead(u.LocalPath) : http.GetStreamAsync(u).GetAwaiter().GetResult())
            using (var to = File.Create(temp))
            {
                var buffer = new byte[1 << 20];
                int n;
                while ((n = from.Read(buffer, 0, buffer.Length)) > 0) { sha.AppendData(buffer, 0, n); to.Write(buffer, 0, n); }
            }
            hash = Convert.ToHexString(sha.GetHashAndReset());
            return temp;
        }
        catch { try { File.Delete(temp); } catch { } throw; }
    }

    // Whether the item kept is the one with that hash. A file already checked and unchanged is not read again: checking a
    // model of gigabytes at every start of a Module would cost more than starting it.
    private bool Holds(string source, string key, string hash)
    {
        if (store.IsChecked(source, key, hash)) return true;
        if (store.Open(source, key, out var stream) != AccessOutcome.OK) return false;
        using (stream)
            if (!AccessEntry.Same(Convert.ToHexString(SHA256.HashData(stream)), hash)) return false;
        store.MarkChecked(source, key, hash);
        return true;
    }

    // WHAT IS ALREADY IN ACCESS IS NOT DOWNLOADED AGAIN: data is identified by its SHA-256, so bytes kept under another Uri
    // (another AIM, another Module, a mirror) are the data wanted. Found, they are checked and kept under this Uri as well.
    private bool Reuse(string source, string key, string hash, string reader)
    {
        if (!store.Recall(hash, out var from, out var fromKey) || (from == source && fromKey == key) || !Holds(from, fromKey, hash)) return false;
        if (store.Version(source) < 0) store.Create(source, Writer);
        if (store.Trace(source, key, out _) == AccessOutcome.OK) return store.Allow(source, Writer, key, [reader]) == AccessOutcome.OK;
        return store.PutFrom(source, Writer, key, from, fromKey, readers: [reader], sha256: hash) == AccessOutcome.OK;
    }

    // Looks at each location in turn for data with that hash; keeps the first found. Why not, in why.
    private AccessOutcome Obtain(string source, string key, IEnumerable<string> locations, string hash, string reader, out string why, string member = "")
    {
        var tried = new List<string>();
        var wrong = false;
        if (Reuse(source, key, hash, reader)) { why = ""; store.Remember(hash, source, key); return AccessOutcome.OK; }
        foreach (var location in locations)
        {
            string? temp = null;
            try
            {
                temp = Download(location, member, out var got);
                if (!AccessEntry.Same(got, hash)) { wrong = true; tried.Add($"{location} is not the data its Metadata names (SHA-256 {got})"); continue; }
                why = "";
                var kept = Keep(source, key, temp, reader, hash);
                if (kept == AccessOutcome.OK) store.Remember(hash, source, key);
                return kept;
            }
            catch (Exception e) { tried.Add($"{location} cannot be obtained: {e.Message}"); }
            finally { if (temp is not null) try { File.Delete(temp); } catch { } }
        }
        why = string.Join("; ", tried);
        return wrong ? AccessOutcome.Refused : AccessOutcome.NotFound;
    }

    // Makes sure what the entry names is in Access, for the AIM Instance that declared it (the reader the Controller, as the
    // Writer, names in the rules of the datum): its file, or its manifest.
    public AccessOutcome Ensure(AccessEntry entry, string reader, out string why)
    {
        why = "";
        if (entry.IsPlaceholder) { why = "its Uri is not set"; return AccessOutcome.NotFound; }
        var key = entry.IsSet ? AccessEntry.ManifestKey : entry.FileName;
        if (Holds(entry.Uri, key, entry.Sha256))
        {
            store.Remember(entry.Sha256, entry.Uri, key);
            return store.Allow(entry.Uri, Writer, key, [reader]);
        }
        return Obtain(entry.Uri, key, entry.Locations.Select(l => entry.IsSet ? l + AccessEntry.ManifestKey : l), entry.Sha256, reader, out why, entry.Member);
    }

    // One item of a set, obtained when first read.
    public AccessOutcome FetchItem(AccessEntry entry, string key, string reader)
    {
        if (!entry.IsSet || store.Get(entry.Uri, AccessEntry.ManifestKey, out var manifest) != AccessOutcome.OK) return AccessOutcome.NotFound;
        try
        {
            using var doc = JsonDocument.Parse(manifest);
            if (!doc.RootElement.TryGetProperty("Items", out var items) || !items.TryGetProperty(key, out var hash) || hash.GetString() is not { } expected)
                return AccessOutcome.NotFound;
            if (store.Trace(entry.Uri, key, out _) == AccessOutcome.OK && Holds(entry.Uri, key, expected))
                return store.Allow(entry.Uri, Writer, key, [reader]);
            return Obtain(entry.Uri, key, entry.Locations.Select(l => new Uri(new Uri(l), key).ToString()), expected, reader, out _);
        }
        catch { return AccessOutcome.NotFound; }
    }

    private AccessOutcome Keep(string source, string key, string file, string reader, string hash)
    {
        if (store.Version(source) < 0) store.Create(source, Writer);
        // what was read before by others stays readable by them
        var readers = store.Trace(source, key, out var before) == AccessOutcome.OK ? before!.Readers?.Union([reader]).ToList() : [reader];
        return store.PutFile(source, Writer, key, file, readers: readers, sha256: hash);
    }
}

// ACCESS AS ONE AIM READS IT (AIF V3.0, Metadata AccessSources and Storage 3): who may read a datum of Access is decided by
// the rules of the datum, as in Shared Storage - its Readers - which the reader given here (the Access of one holder: an AIM
// Instance of a Module) applies. What the AIM declares is what it asks the Controller, the Writer of what it obtains, to
// obtain and to let it read: the Controller names the AIM among the Readers of the datum, and an item of a set is obtained
// when the AIM first reads it, if the Key prefixes of its entry allow it. A file's Key is its name (an empty Key gives it).
public sealed class BoundAccess(IAccessReader inner, IReadOnlyList<AccessEntry> entries, AccessFetcher? fetcher = null, string reader = "") : IAccessReader
{
    // The entry for a Source and Key: several entries may name the same Source when it is an archive (each a member of it,
    // whose Key is the name of its file); an empty Key is the one entry, if there is one.
    private AccessEntry? EntryOf(string source, string key = "")
    {
        var of = entries.Where(e => e.Uri == source && !e.IsPlaceholder).ToList();
        if (of.Count <= 1) return of.FirstOrDefault();
        return of.FirstOrDefault(e => e.FileName == key) ?? (key.Length == 0 && of.Select(e => e.Member).Distinct().Count() == 1 ? of[0] : null);
    }

    private static bool Allows(AccessEntry e, string key) =>
        e.KeyPrefixes.Count == 0 || e.KeyPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal));

    // What the AIM asks for at that Source and Key, where it declared the Source: a file has the name of the file for its Key; a
    // set, the Keys its entry allows. A Source it did not declare is read under the rules of its data alone.
    private AccessOutcome Authorise(string source, ref string key, out AccessEntry? entry)
    {
        entry = EntryOf(source, key);
        if (entry is null) return entries.Any(e => e.Uri == source && !e.IsPlaceholder) ? AccessOutcome.NotAuthorised : AccessOutcome.OK;
        if (!entry.IsSet) key = key.Length == 0 ? entry.FileName : key;
        if (!entry.IsSet && key != entry.FileName) return AccessOutcome.NotAuthorised;
        return Allows(entry, key) ? AccessOutcome.OK : AccessOutcome.NotAuthorised;
    }

    public AccessOutcome Get(string source, string key, out byte[] data)
    {
        data = [];
        if (Authorise(source, ref key, out var e) is var denied && denied != AccessOutcome.OK) return denied;
        var outcome = inner.Get(source, key, out data);
        if (outcome != AccessOutcome.OK && e is { IsSet: true } && fetcher is not null && fetcher.FetchItem(e, key, reader) == AccessOutcome.OK)
            outcome = inner.Get(source, key, out data);
        return outcome;
    }

    public AccessOutcome LocalPath(string source, string key, out string path)
    {
        path = "";
        if (Authorise(source, ref key, out _) is var denied && denied != AccessOutcome.OK) return denied;
        return inner.LocalPath(source, key, out path);
    }

    // The same, as a stream: what is too large to be held in memory is read from where it is kept.
    public AccessOutcome Open(string source, string key, out Stream stream)
    {
        stream = Stream.Null;
        if (Authorise(source, ref key, out var e) is var denied && denied != AccessOutcome.OK) return denied;
        var outcome = inner.Open(source, key, out stream);
        if (outcome != AccessOutcome.OK && e is { IsSet: true } && fetcher is not null && fetcher.FetchItem(e, key, reader) == AccessOutcome.OK)
            outcome = inner.Open(source, key, out stream);
        return outcome;
    }

    public IReadOnlyList<string> List(string source, string prefix = "")
    {
        var declared = entries.Where(x => x.Uri == source && !x.IsPlaceholder).ToList();
        if (declared.Count == 0) return inner.List(source, prefix);
        if (!declared[0].IsSet) { var kept = inner.List(source); return declared.Select(x => x.FileName).Where(kept.Contains).Order(StringComparer.Ordinal).ToList(); }
        var e = declared[0];
        // A set without a manifest (one the User wrote into Access) is listed as it is kept.
        if (inner.Get(source, AccessEntry.ManifestKey, out var manifest) != AccessOutcome.OK)
            return inner.List(source, prefix).Where(k => Allows(e, k)).ToList();
        try
        {
            using var doc = JsonDocument.Parse(manifest);
            return doc.RootElement.TryGetProperty("Items", out var items)
                ? items.EnumerateObject().Select(p => p.Name)
                       .Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && Allows(e, k)).Order(StringComparer.Ordinal).ToList()
                : [];
        }
        catch { return []; }
    }

    public long Version(string source) => inner.Version(source);

    // The Uris of the AIM's entries of that Name.
    public IReadOnlyList<string> SourcesOf(string name) =>
        entries.Where(e => !e.IsPlaceholder && (name.Length == 0 || e.Name == name)).Select(e => e.Uri).ToList();
}
