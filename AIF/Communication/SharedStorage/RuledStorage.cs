using System.Text.Json;

namespace AIF.SharedStorage;

// STORAGE UNDER RULES (M3219 3.1, 3.2). The Private Storage of a Module - and,
// from Step 3, a Shared Storage of several Modules - holds each datum with the
// rules under which it may be read: who may read it and how long it is kept.
// The writer sets them, unless the store has a central control; with one, the
// general rules of each category are the central control's, and a writer may
// restrict them for its datum, never extend them. Nobody reaches the store but
// through a handle the Controller bound to its holder's identity.

// What a call to a store under rules comes to.
public enum StorageOutcome
{
    OK,
    NotFound,
    // Not allowed by the rules: a write the rules do not let this writer make, a
    // reader or a time beyond them, a read by one who is not a reader.
    NotAuthorised
}

// How long a datum is kept: until the Module instance stops, until the session
// of the User Agent ends, or as long as the scope is kept - and, where given, no
// longer than a duration from its writing.
public enum StorageLifetime { Module, Session, Scope }

public sealed record StorageTime(StorageLifetime Lifetime = StorageLifetime.Scope, TimeSpan? Within = null)
{
    public static StorageTime Scope { get; } = new();

    // Not longer than other: a shorter lifetime, and a duration within other's.
    public bool NotLongerThan(StorageTime other) =>
        Lifetime <= other.Lifetime &&
        (other.Within is null || (Within is { } mine && mine <= other.Within.Value));

    // "Module", "Session", "Scope", "200ms", "5s", "Module 5s".
    public static StorageTime Parse(string text)
    {
        var lifetime = StorageLifetime.Scope;
        TimeSpan? within = null;
        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Enum.TryParse<StorageLifetime>(part, true, out var l)) lifetime = l;
            else if (part.EndsWith("ms") && double.TryParse(part[..^2], System.Globalization.CultureInfo.InvariantCulture, out var ms)) within = TimeSpan.FromMilliseconds(ms);
            else if (part.EndsWith('s') && double.TryParse(part[..^1], System.Globalization.CultureInfo.InvariantCulture, out var s)) within = TimeSpan.FromSeconds(s);
            else throw new FormatException($"'{part}' is not a lifetime or a duration.");
        }
        return new StorageTime(lifetime, within);
    }

    public override string ToString() => Lifetime + (Within is { } w ? $" {w.TotalMilliseconds:0.#}ms" : "");
}

// The general rules of a category, set by a central control: who may write it,
// who may read it, and the longest time.
public sealed record StorageRule(IReadOnlyCollection<string> Writers, IReadOnlyCollection<string> Readers, StorageTime Time);

// What is known of a datum: who wrote it and when, its category, its readers as
// the writer named them (none named: as the rules give), its time, and the rule
// under which the write was allowed.
public sealed record StorageTrace(string Writer, DateTimeOffset Stamp, string Category,
                                  IReadOnlyList<string>? Readers, StorageTime Time, string Rule);

// A handle: the store, as its holder may reach it.
public interface IRuledStorage
{
    string Holder { get; }
    StorageOutcome MPAI_AIFM_RuledStorage_Put(string key, byte[] data, string category,
                                              IReadOnlyCollection<string>? readers = null, StorageTime? time = null);
    StorageOutcome MPAI_AIFM_RuledStorage_Get(string key, out byte[] data);
    StorageOutcome MPAI_AIFM_RuledStorage_Delete(string key);
    IReadOnlyList<string> MPAI_AIFM_RuledStorage_List(string? category = null, string prefix = "");
    bool MPAI_AIFM_RuledStorage_Exists(string key);
    StorageOutcome MPAI_AIFM_RuledStorage_Trace(string key, out StorageTrace? trace);

    // The central control's: the general rules of a category.
    StorageOutcome MPAI_AIFM_RuledStorage_SetRule(string category, StorageRule rule);
}

// One store: the files at a location, the rules, and the identities of the
// Module instance and the session its data of those lifetimes belong to.
public sealed class RuledStore
{
    public const string UserAgent = "UserAgent";
    public const string Controller = "Controller";

    private readonly Func<string?> location;
    private readonly Func<DateTimeOffset> now;
    private readonly string moduleInstance;
    private readonly string session;
    private readonly Dictionary<string, StorageRule> rules = new(StringComparer.Ordinal);
    private readonly object one = new();

    // centralControl: the AIM Instance that holds the central control; null for
    // none. members: who may hold a handle - the AIMs of the Module and the User
    // Agent - checked when a writer names readers.
    public RuledStore(Func<string?> location, Func<DateTimeOffset> now, string moduleInstance, string session, string? centralControl)
    {
        this.location = location;
        this.now = now;
        this.moduleInstance = moduleInstance;
        this.session = session;
        CentralControl = centralControl;
    }

    public string? CentralControl { get; }

    public IRuledStorage For(string holder) => new Handle(this, holder);

    // ---- the files --------------------------------------------------------------

    private sealed class Meta
    {
        public string Writer { get; set; } = "";
        public DateTimeOffset Stamp { get; set; }
        public string Category { get; set; } = "";
        public List<string>? Readers { get; set; }
        public StorageLifetime Lifetime { get; set; }
        public double? WithinMs { get; set; }
        public string Rule { get; set; } = "";
        public string ModuleInstance { get; set; } = "";
        public string Session { get; set; } = "";
    }

    private string Root()
    {
        var root = location() ?? throw new InvalidOperationException(
            "No storage is initialised for this Module (MPAI_AIFU_SharedStorage_Init).");
        Directory.CreateDirectory(root);
        return root;
    }

    private (string Data, string Meta) PathsFor(string key)
    {
        var name = Uri.EscapeDataString(key);
        var root = Root();
        return (Path.Combine(root, name + ".data"), Path.Combine(root, name + ".meta"));
    }

    // A datum, unless there is none, or its time has passed - then it is removed.
    private Meta? Load(string key)
    {
        var (dataPath, metaPath) = PathsFor(key);
        if (!File.Exists(metaPath) || !File.Exists(dataPath)) return null;
        var meta = JsonSerializer.Deserialize<Meta>(File.ReadAllBytes(metaPath))!;
        if (Expired(meta)) { File.Delete(dataPath); File.Delete(metaPath); return null; }
        return meta;
    }

    private bool Expired(Meta meta) =>
        (meta.WithinMs is { } ms && now() - meta.Stamp > TimeSpan.FromMilliseconds(ms)) ||
        (meta.Lifetime == StorageLifetime.Module && meta.ModuleInstance != moduleInstance) ||
        (meta.Lifetime == StorageLifetime.Session && meta.Session != session);

    // The Module instance has stopped: its data of that lifetime go.
    public void EndOfModule()
    {
        if (location() is not { } root || !Directory.Exists(root)) return;
        lock (one)
            foreach (var metaPath in Directory.EnumerateFiles(root, "*.meta"))
            {
                var meta = JsonSerializer.Deserialize<Meta>(File.ReadAllBytes(metaPath))!;
                if (meta.Lifetime != StorageLifetime.Module || meta.ModuleInstance != moduleInstance) continue;
                File.Delete(metaPath);
                File.Delete(Path.ChangeExtension(metaPath, ".data"));
            }
    }

    // ---- the rules ----------------------------------------------------------------

    private StorageRule? RuleOf(string category)
    {
        lock (one) return rules.GetValueOrDefault(category);
    }

    private bool MayRead(string holder, Meta meta)
    {
        if (holder == meta.Writer || holder == CentralControl) return true;
        if (CentralControl is null) return meta.Readers?.Contains(holder) == true;

        // With a central control: a reader the general rules give now, and the
        // writer did not leave out.
        var rule = RuleOf(meta.Category);
        return rule is not null && rule.Readers.Contains(holder) && (meta.Readers is null || meta.Readers.Contains(holder));
    }

    private StorageOutcome Put(string holder, string key, byte[] data, string category, IReadOnlyCollection<string>? readers, StorageTime? time)
    {
        string ruleText;
        var effective = time ?? StorageTime.Scope;
        if (CentralControl is null || holder == CentralControl || holder == Controller)
            ruleText = holder == CentralControl ? "the central control's own" : "the writer's";
        else
        {
            var rule = RuleOf(category);
            if (rule is null || !rule.Writers.Contains(holder)) return StorageOutcome.NotAuthorised;
            if (readers is not null && readers.Any(r => !rule.Readers.Contains(r))) return StorageOutcome.NotAuthorised;
            if (time is not null && !time.NotLongerThan(rule.Time)) return StorageOutcome.NotAuthorised;
            effective = time ?? rule.Time;
            ruleText = $"the general rule of {category}";
        }

        lock (one)
        {
            // A key another wrote is not overwritten, but by the central control.
            if (Load(key) is { } existing && existing.Writer != holder && holder != CentralControl)
                return StorageOutcome.NotAuthorised;

            var (dataPath, metaPath) = PathsFor(key);
            var meta = new Meta
            {
                Writer = holder, Stamp = now(), Category = category, Readers = readers?.ToList(),
                Lifetime = effective.Lifetime, WithinMs = effective.Within?.TotalMilliseconds, Rule = ruleText,
                ModuleInstance = moduleInstance, Session = session
            };
            File.WriteAllBytes(metaPath + ".tmp", JsonSerializer.SerializeToUtf8Bytes(meta));
            File.WriteAllBytes(dataPath + ".tmp", data);
            File.Move(metaPath + ".tmp", metaPath, true);
            File.Move(dataPath + ".tmp", dataPath, true);
        }
        return StorageOutcome.OK;
    }

    private StorageOutcome Get(string holder, string key, out byte[] data)
    {
        data = [];
        lock (one)
        {
            if (Load(key) is not { } meta) return StorageOutcome.NotFound;
            if (!MayRead(holder, meta)) return StorageOutcome.NotAuthorised;
            data = File.ReadAllBytes(PathsFor(key).Data);
            return StorageOutcome.OK;
        }
    }

    private StorageOutcome Delete(string holder, string key)
    {
        lock (one)
        {
            if (Load(key) is not { } meta) return StorageOutcome.NotFound;
            if (holder != meta.Writer && holder != CentralControl) return StorageOutcome.NotAuthorised;
            var (dataPath, metaPath) = PathsFor(key);
            File.Delete(metaPath);
            File.Delete(dataPath);
            return StorageOutcome.OK;
        }
    }

    private IReadOnlyList<string> List(string holder, string? category, string prefix)
    {
        if (location() is not { } root || !Directory.Exists(root)) return [];
        lock (one)
            return Directory.EnumerateFiles(root, "*.meta")
                .Select(p => Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(p)))
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .Where(k => Load(k) is { } m && (category is null || m.Category == category) && MayRead(holder, m))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();
    }

    private StorageOutcome Trace(string holder, string key, out StorageTrace? trace)
    {
        trace = null;
        lock (one)
        {
            if (Load(key) is not { } m) return StorageOutcome.NotFound;
            if (!MayRead(holder, m)) return StorageOutcome.NotAuthorised;
            trace = new StorageTrace(m.Writer, m.Stamp, m.Category, m.Readers,
                                     new StorageTime(m.Lifetime, m.WithinMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null), m.Rule);
            return StorageOutcome.OK;
        }
    }

    private StorageOutcome SetRule(string holder, string category, StorageRule rule)
    {
        if (holder != CentralControl) return StorageOutcome.NotAuthorised;
        lock (one) rules[category] = rule;
        return StorageOutcome.OK;
    }

    private sealed class Handle(RuledStore store, string holder) : IRuledStorage
    {
        public string Holder => holder;
        public StorageOutcome MPAI_AIFM_RuledStorage_Put(string key, byte[] data, string category, IReadOnlyCollection<string>? readers = null, StorageTime? time = null) =>
            store.Put(holder, key, data, category, readers, time);
        public StorageOutcome MPAI_AIFM_RuledStorage_Get(string key, out byte[] data) => store.Get(holder, key, out data);
        public StorageOutcome MPAI_AIFM_RuledStorage_Delete(string key) => store.Delete(holder, key);
        public IReadOnlyList<string> MPAI_AIFM_RuledStorage_List(string? category = null, string prefix = "") => store.List(holder, category, prefix);
        public bool MPAI_AIFM_RuledStorage_Exists(string key) => store.Get(holder, key, out _) == StorageOutcome.OK;
        public StorageOutcome MPAI_AIFM_RuledStorage_Trace(string key, out StorageTrace? trace) => store.Trace(holder, key, out trace);
        public StorageOutcome MPAI_AIFM_RuledStorage_SetRule(string category, StorageRule rule) => store.SetRule(holder, category, rule);
    }
}
