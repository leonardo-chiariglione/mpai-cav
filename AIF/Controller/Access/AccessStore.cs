using System.Text;
using System.Text.Json;

using AIF.SharedStorage;

namespace AIF.Controller;

// ACCESS (AIF V3.0, Storage 6): the static or slowly changing data a Module needs -
// domain knowledge, data models, knowledge bases - which the AIMs of a Module READ and
// never write. Its content is organised in Sources: a set of items, each at a Key, with
// a Version that changes whenever its content changes. ONLY THE CONTROLLER WRITES ACCESS:
// it obtains the data an AIM's Metadata names from the URI the Metadata gives (AccessFetcher),
// a Provider being a third party that publishes data at such a URI. There is no other
// writer: no AIM, no User Agent, no Provider.
//
// What the store adds is the Version: it rises by one at every write, so an AIM that reads
// MPAI_AIFM_Access_Version can tell that content has changed; and a Source is never brought
// back to an earlier state - an item records the SHA-256 of what it held before, and data
// with one of those is refused. A Source leaves Access only through Remove, an operation
// of the Controller (Prune): the Sources that no Module's Metadata declares any more.
//
// On disk, per Source a folder (its name in hexadecimal, any text being a valid Source):
// source.json - the Source, its Writer and Version - and per item a .data and a .info
// (the Key, who wrote it, when, at which Version).
public enum AccessOutcome { OK, NotFound, NotAuthorised, Exists, Refused }

// From: where the Controller obtained the item (the location it was fetched from); empty where it was not fetched.
public sealed record AccessTrace(string Writer, DateTimeOffset Stamp, long Version, string Category = "Data", IReadOnlyList<string>? Readers = null, string From = "");

public sealed class AccessStore : IAccessReader
{
    private readonly string root;
    private readonly Func<DateTimeOffset> now;

    private readonly object one = new();

    public AccessStore(string root, Func<DateTimeOffset>? now = null)
    {
        this.root = Path.GetFullPath(root);
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        Directory.CreateDirectory(this.root);
        Migrate();
    }

    private sealed record SourceInfo(string Source, string Writer, long Version);
    // Sha256, Length and FileTime: what the file was when its SHA-256 was last checked - the same length and time are the same file.
private sealed record ItemInfo(string Key, string Writer, DateTimeOffset Stamp, long Version, string Category = "Data", List<string>? Readers = null,
                                   string? Sha256 = null, long Length = 0, DateTime FileTime = default, List<string>? Earlier = null, string From = "");

    // THE ONE WRITER.
    public const string Controller = "Controller";

    // THE NAMES ON DISK ARE SHORT: a Source is a URI, and a Key may be a long path, so a name made of the text itself would
    // pass the length a program that reads the file is able to open (260 characters on Windows); the name is the first 32
    // hexadecimal digits of the SHA-256 of the text, and the text itself is in the files beside the data.
    public static string DiskName(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    private string Folder(string source) => Path.Combine(root, DiskName(source));
    private string Item(string source, string key) => Path.Combine(Folder(source), DiskName(key));

    // What an earlier version kept under the hexadecimal of the text is moved to the short names, once.
    private void Migrate()
    {
        foreach (var folder in Directory.EnumerateDirectories(root).ToList())
        {
            var file = Path.Combine(folder, "source.json");
            if (!File.Exists(file)) continue;
            SourceInfo? info;
            try { info = JsonSerializer.Deserialize<SourceInfo>(File.ReadAllText(file)); } catch { continue; }
            if (info is null || Path.GetFileName(folder) == DiskName(info.Source)) continue;
            foreach (var itemInfo in Directory.EnumerateFiles(folder, "*.info").ToList())
            {
                var item = JsonSerializer.Deserialize<ItemInfo>(File.ReadAllText(itemInfo))!;
                var from = Path.Combine(folder, Path.GetFileNameWithoutExtension(itemInfo));
                var to = Path.Combine(folder, DiskName(item.Key));
                if (from == to) continue;
                File.Move(from + ".data", to + ".data", true);
                File.Move(itemInfo, to + ".info", true);
            }
            var target = Folder(info.Source);
            if (!Directory.Exists(target)) Directory.Move(folder, target);
        }
    }

    private SourceInfo? Info(string source)
    {
        var file = Path.Combine(Folder(source), "source.json");
        return File.Exists(file) ? JsonSerializer.Deserialize<SourceInfo>(File.ReadAllText(file)) : null;
    }

    private void Save(SourceInfo info) =>
        File.WriteAllText(Path.Combine(Folder(info.Source), "source.json"), JsonSerializer.Serialize(info));

    // WHAT LEFT ACCESS IS NOT FORGOTTEN: a Source is never brought back to an earlier state, also when an item was deleted or the Source removed (and made again). The ledger keeps, per Source, the last
    // Version it had and, per item, the SHA-256 of what it held; data with one of those is refused, and a Source made again goes on from its Version.
    private sealed record Ledger(Dictionary<string, long> Versions, Dictionary<string, List<string>> Hashes);
    private string LedgerFile => Path.Combine(root, "retired.json");
    private Ledger LoadLedger() => File.Exists(LedgerFile) ? JsonSerializer.Deserialize<Ledger>(File.ReadAllText(LedgerFile)) ?? new(new(), new()) : new(new(), new());
    private void SaveLedger(Ledger ledger) => File.WriteAllText(LedgerFile, JsonSerializer.Serialize(ledger));
    private static string LedgerKey(string source, string key) => source + (char)9 + key;
    private static void Retire(Ledger ledger, string source, string key, ItemInfo item)
    {
        var k = LedgerKey(source, key);
        ledger.Hashes[k] = (ledger.Hashes.GetValueOrDefault(k) ?? []).Concat(item.Earlier ?? []).Append(item.Sha256 ?? "").Where(h => h.Length > 0)
            .Select(h => h.ToUpperInvariant()).Distinct().ToList();
    }

    // The Controller wrote the file, and it is read, not written, by whoever reads it (Access_Path): a file is made writable only to be replaced or deleted.
    private static void Writable(string file) { if (File.Exists(file)) File.SetAttributes(file, FileAttributes.Normal); }
    private static string HashFile(string path) { using var s = File.OpenRead(path); return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s)); }

    // ---- the Controller's: creating a Source and writing it -----------------------------

    public AccessOutcome Create(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return AccessOutcome.Refused;
        lock (one)
        {
            if (Info(source) is not null) return AccessOutcome.Exists;
            Directory.CreateDirectory(Folder(source));
            Save(new SourceInfo(source, Controller, LoadLedger().Versions.GetValueOrDefault(source, 0)));      // a Source made again goes on from the Version it had
            return AccessOutcome.OK;
        }
    }

    // The rules of an item are those of a datum of Shared Storage (AIF V3.0, Storage 3): its Category, and its Readers - the
    // AIM Instances, Modules or User Agent that may read it; none named: every holder. The Version of the Source rises by one.
    public AccessOutcome Put(string source, string key, byte[] data, string category = "Data", IReadOnlyCollection<string>? readers = null) =>
        Write(source, key, Hash(data), (info, earlier) =>
        {
            Writable(Item(source, key) + ".data");
            File.WriteAllBytes(Item(source, key) + ".data", data);
            File.WriteAllText(Item(source, key) + ".info", JsonSerializer.Serialize(new ItemInfo(key, Controller, now(), info.Version, category, readers?.ToList(), Sha256: Hash(data), Earlier: earlier)));
            return AccessOutcome.OK;
        });

    // The same, the data being a file (a large item is never held in memory): it is copied into the Source. sha256: the hash
    // the file was checked to have.
    public AccessOutcome PutFile(string source, string key, string path, string category = "Data", IReadOnlyCollection<string>? readers = null, string? sha256 = null, string from = "") =>
        Write(source, key, sha256 ??= HashFile(path), (info, earlier) =>      // a hash not given is the file's own: what was held before is never put back
        {
            Writable(Item(source, key) + ".data");
            File.Copy(path, Item(source, key) + ".data", true);
            File.WriteAllText(Item(source, key) + ".info", JsonSerializer.Serialize(Checked(new ItemInfo(key, Controller, now(), info.Version, category, readers?.ToList(), Earlier: earlier, From: from), Item(source, key) + ".data", sha256)));
            return AccessOutcome.OK;
        });

    // The same, the data being an item already kept in another Source (the same bytes needed under another Uri).
    public AccessOutcome PutFrom(string source, string key, string fromSource, string fromKey,
                                 string category = "Data", IReadOnlyCollection<string>? readers = null, string? sha256 = null) =>
        Write(source, key, sha256, (info, earlier) =>
        {
            var from = Item(fromSource, fromKey) + ".data";
            if (!File.Exists(from)) return AccessOutcome.NotFound;
            Writable(Item(source, key) + ".data");
            File.Copy(from, Item(source, key) + ".data", true);
            // where the Controller obtained it: where the item it is copied from was obtained
            var origin = File.Exists(Item(fromSource, fromKey) + ".info") ? JsonSerializer.Deserialize<ItemInfo>(File.ReadAllText(Item(fromSource, fromKey) + ".info"))!.From : "";
            File.WriteAllText(Item(source, key) + ".info", JsonSerializer.Serialize(Checked(new ItemInfo(key, Controller, now(), info.Version, category, readers?.ToList(), Earlier: earlier, From: origin), Item(source, key) + ".data", sha256)));
            return AccessOutcome.OK;
        });

    private static string Hash(byte[] data) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));

    // The item with the SHA-256 that was checked of the file as it is now.
    private static ItemInfo Checked(ItemInfo info, string file, string? sha256)
    {
        if (sha256 is null) return info;
        var f = new FileInfo(file);
        return info with { Sha256 = sha256.ToUpperInvariant(), Length = f.Length, FileTime = f.LastWriteTimeUtc };
    }

    // Whether the item is the file with that SHA-256, as it was checked: the same SHA-256 was recorded for a file of the same
    // length and time. A file that was not checked, or that changed, is hashed again by whoever asks (MarkChecked).
    public bool IsChecked(string source, string key, string sha256)
    {
        var file = Item(source, key);
        if (!File.Exists(file + ".info") || !File.Exists(file + ".data")) return false;
        var info = JsonSerializer.Deserialize<ItemInfo>(File.ReadAllText(file + ".info"))!;
        var f = new FileInfo(file + ".data");
        return string.Equals(info.Sha256, sha256, StringComparison.OrdinalIgnoreCase) && info.Length == f.Length && info.FileTime == f.LastWriteTimeUtc;
    }

    public void MarkChecked(string source, string key, string sha256)
    {
        lock (one)
        {
            var file = Item(source, key);
            if (!File.Exists(file + ".info") || !File.Exists(file + ".data")) return;
            var info = JsonSerializer.Deserialize<ItemInfo>(File.ReadAllText(file + ".info"))!;
            File.WriteAllText(file + ".info", JsonSerializer.Serialize(Checked(info, file + ".data", sha256)));
        }
    }

    // WHERE DATA WITH A GIVEN SHA-256 IS KEPT: data is the same data whatever Uri it was obtained from, so before anything is
    // downloaded the Controller asks whether the bytes are already in Access. An entry here is a hint, never trusted: whoever
    // uses it checks the hash of what it finds.
    public void Remember(string hash, string source, string key)
    {
        // Several Modules, and AIM hosts in other processes, write this index at once: each writes its own file and puts it in
        // place; a hint that cannot be written is only a hint lost.
        try
        {
            var dir = Path.Combine(root, "by-hash");
            Directory.CreateDirectory(dir);
            var temp = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(temp, source + (char)9 + key);
            try { File.Move(temp, Path.Combine(dir, hash.ToUpperInvariant()), true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public bool Recall(string hash, out string source, out string key)
    {
        source = key = "";
        var file = Path.Combine(root, "by-hash", hash.ToUpperInvariant());
        string text;
        try { text = File.ReadAllText(file); } catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
        var lines = text.Split((char)9, 2);
        if (lines.Length != 2) return false;
        (source, key) = (lines[0], lines[1]);
        return true;
    }

    // Removes one item: the Version of the Source rises. An operation of the Controller; no AIM has it.
    public AccessOutcome Delete(string source, string key) =>
        Write(source, key, null, (_, _) =>
        {
            if (!File.Exists(Item(source, key) + ".data")) return AccessOutcome.NotFound;
            var ledger = LoadLedger();
            if (File.Exists(Item(source, key) + ".info")) Retire(ledger, source, key, JsonSerializer.Deserialize<ItemInfo>(File.ReadAllText(Item(source, key) + ".info"))!);
            SaveLedger(ledger);
            Writable(Item(source, key) + ".data");
            File.Delete(Item(source, key) + ".data");
            File.Delete(Item(source, key) + ".info");
            return AccessOutcome.OK;
        });

    // THE SOURCE LEAVES ACCESS, with what it holds: an operation of the Controller (Prune), for a Source that no Module declares
    // any more. What was removed is recorded - the Source, its Version and items, and when - in access-removed.log beside the
    // store, and the places remembered for its data are forgotten.
    public AccessOutcome Remove(string source)
    {
        lock (one)
        {
            if (Info(source) is not { } info) return AccessOutcome.NotFound;
            var items = List(source);
            var ledger = LoadLedger();
            ledger.Versions[source] = info.Version;
            foreach (var infoFile in Directory.EnumerateFiles(Folder(source), "*.info"))
                if (JsonSerializer.Deserialize<ItemInfo>(File.ReadAllText(infoFile)) is { } gone) Retire(ledger, source, gone.Key, gone);
            SaveLedger(ledger);
            foreach (var file in Directory.EnumerateFiles(Folder(source), "*", SearchOption.AllDirectories)) Writable(file);
            Directory.Delete(Folder(source), true);
            var by = Path.Combine(root, "by-hash");
            if (Directory.Exists(by))
                foreach (var hint in Directory.EnumerateFiles(by).ToList())
                {
                    try { if (File.ReadAllText(hint).StartsWith(source + (char)9, StringComparison.Ordinal)) File.Delete(hint); } catch (IOException) { }
                }
            File.AppendAllText(Path.Combine(root, "access-removed.log"), JsonSerializer.Serialize(new { Source = source, info.Version, Items = items, When = now() }) + Environment.NewLine);
            return AccessOutcome.OK;
        }
    }

    // The Sources in Access.
    public IReadOnlyList<string> Sources() =>
        Directory.EnumerateDirectories(root)
            .Select(d => Path.Combine(d, "source.json")).Where(File.Exists)
            .Select(f => JsonSerializer.Deserialize<SourceInfo>(File.ReadAllText(f))!.Source).Order(StringComparer.Ordinal).ToList();

    // hash: the SHA-256 of what is written, where it is known. THE VERSION RISES BY ONE with every write; what an item held before is
    // kept as the SHA-256 of it, and data that has one of those is refused: a Source is never brought back to an earlier state.
    private AccessOutcome Write(string source, string key, string? hash, Func<SourceInfo, List<string>?, AccessOutcome> act)
    {
        if (string.IsNullOrEmpty(key)) return AccessOutcome.Refused;
        lock (one)
        {
            if (Info(source) is not { } info) return AccessOutcome.NotFound;
            List<string>? earlier = null;
            if (hash is not null && LoadLedger().Hashes.GetValueOrDefault(LedgerKey(source, key)) is { } retired && retired.Contains(hash.ToUpperInvariant())) return AccessOutcome.Refused;   // a step back, past a deletion
            var file = Item(source, key) + ".info";
            if (File.Exists(file))
            {
                var before = JsonSerializer.Deserialize<ItemInfo>(File.ReadAllText(file))!;
                earlier = [.. before.Earlier ?? []];
                if (before.Sha256 is { } held && !string.Equals(held, hash, StringComparison.OrdinalIgnoreCase)) earlier.Add(held.ToUpperInvariant());
                if (hash is not null && earlier.Contains(hash.ToUpperInvariant(), StringComparer.OrdinalIgnoreCase)) return AccessOutcome.Refused;   // a step back
            }
            var next = info.Version + 1;
            var outcome = act(info with { Version = next }, earlier);
            if (outcome == AccessOutcome.OK) Save(info with { Version = next });
            return outcome;
        }
    }

    // ---- the AIMs': reading -------------------------------------------------------------

    public AccessOutcome Get(string source, string key, out byte[] data)
    {
        data = [];
        var file = Item(source, key) + ".data";
        if (!File.Exists(file)) return AccessOutcome.NotFound;
        data = File.ReadAllBytes(file);
        return AccessOutcome.OK;
    }

    // The item as a stream, read from where it is kept.
    public AccessOutcome Open(string source, string key, out Stream stream)
    {
        stream = Stream.Null;
        var file = Item(source, key) + ".data";
        if (!File.Exists(file)) return AccessOutcome.NotFound;
        stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        return AccessOutcome.OK;
    }

    // The file where the item is kept.
    public AccessOutcome LocalPath(string source, string key, out string path)
    {
        path = Item(source, key) + ".data";
        if (!File.Exists(path)) return AccessOutcome.NotFound;
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);     // the file is read, not written (Storage 6.2)
        return AccessOutcome.OK;
    }

    public IReadOnlyList<string> List(string source, string prefix = "")
    {
        if (!Directory.Exists(Folder(source))) return [];
        return Directory.EnumerateFiles(Folder(source), "*.info")
            .Select(f => JsonSerializer.Deserialize<ItemInfo>(File.ReadAllText(f))!.Key)
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToList();
    }

    // The Version of a Source, -1 when there is no such Source.
    public long Version(string source) => Info(source)?.Version ?? -1;

    public string? WriterOf(string source) => Info(source)?.Writer;

    // Who wrote an item, when, and at which Version of its Source.
    public AccessOutcome Trace(string source, string key, out AccessTrace? trace)
    {
        trace = null;
        var file = Item(source, key) + ".info";
        if (!File.Exists(file)) return AccessOutcome.NotFound;
        var info = JsonSerializer.Deserialize<ItemInfo>(File.ReadAllText(file))!;
        trace = new AccessTrace(info.Writer, info.Stamp, info.Version, info.Category, info.Readers, info.From);
        return AccessOutcome.OK;
    }

    // ---- who may read: the rules of Shared Storage ----------------------------------------

    // The Controller adds readers to an item: the rule of its datum, widened. An item readable by every holder stays so.
    public AccessOutcome Allow(string source, string key, IEnumerable<string> readers)
    {
        lock (one)
        {
            if (Info(source) is null) return AccessOutcome.NotFound;
            var file = Item(source, key) + ".info";
            if (!File.Exists(file)) return AccessOutcome.NotFound;
            var info = JsonSerializer.Deserialize<ItemInfo>(File.ReadAllText(file))!;
            if (info.Readers is null) return AccessOutcome.OK;
            var more = info.Readers.Union(readers).ToList();
            if (more.Count != info.Readers.Count) File.WriteAllText(file, JsonSerializer.Serialize(info with { Readers = more }));
            return AccessOutcome.OK;
        }
    }

    // The Controller takes a reader away from every item of a Source: data an AIM was not to be given (its partner is missing) is not read by it.
    public void Withdraw(string source, string reader)
    {
        lock (one)
        {
            if (Info(source) is null) return;
            foreach (var file in Directory.EnumerateFiles(Folder(source), "*.info", SearchOption.AllDirectories))
            {
                if (JsonSerializer.Deserialize<ItemInfo>(File.ReadAllText(file)) is not { Readers: { } named } info || !named.Contains(reader)) continue;
                File.WriteAllText(file, JsonSerializer.Serialize(info with { Readers = named.Where(r => r != reader).ToList() }));
            }
        }
    }

    // Whether a holder may read an item: the Controller and the central control always; otherwise those the Controller named
    // as readers, or every holder where it named none.
    private bool MayRead(StorageHolder holder, StorageHolder? central, ItemInfo info) =>
        holder.Is(info.Writer) || (central is not null && central == holder) ||
        (info.Readers is { } named ? named.Any(holder.Is) : true);

    private bool MayRead(StorageHolder holder, StorageHolder? central, string source, string key)
    {
        var file = Item(source, key) + ".info";
        return File.Exists(file) && MayRead(holder, central, JsonSerializer.Deserialize<ItemInfo>(File.ReadAllText(file))!);
    }

    // Access as one holder - an AIM Instance of a Module - may read it: the rules of each item decide, as they decide in
    // Shared Storage. A holder not allowed to read an item is told NOT_AUTHORISED.
    public IAccessReader As(StorageHolder holder, StorageHolder? central = null) => new HolderAccess(this, holder, central);

    private sealed class HolderAccess(AccessStore store, StorageHolder holder, StorageHolder? central) : IAccessReader
    {
        private AccessOutcome Check(string source, string key) =>
            store.Trace(source, key, out _) != AccessOutcome.OK ? AccessOutcome.NotFound
            : store.MayRead(holder, central, source, key) ? AccessOutcome.OK : AccessOutcome.NotAuthorised;

        public AccessOutcome Get(string source, string key, out byte[] data)
        {
            data = [];
            return Check(source, key) is var c && c != AccessOutcome.OK ? c : store.Get(source, key, out data);
        }

        public AccessOutcome Open(string source, string key, out Stream stream)
        {
            stream = Stream.Null;
            return Check(source, key) is var c && c != AccessOutcome.OK ? c : store.Open(source, key, out stream);
        }

        public AccessOutcome LocalPath(string source, string key, out string path)
        {
            path = "";
            return Check(source, key) is var c && c != AccessOutcome.OK ? c : store.LocalPath(source, key, out path);
        }

        public IReadOnlyList<string> List(string source, string prefix = "") =>
            store.List(source, prefix).Where(k => store.MayRead(holder, central, source, k)).ToList();

        public long Version(string source) => store.Version(source);
    }
}
