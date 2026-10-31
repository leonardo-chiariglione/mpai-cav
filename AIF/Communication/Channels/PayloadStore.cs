using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AIF.Channels;

// PAYLOADS BY REFERENCE, WITHIN A CONTROLLER (M3205 3.6.4, M3215 3.6). Whether an
// Object carries its data inline or by reference is the writer's choice, among the
// forms its Data Type's schema allows: an entry { Data } inline, or
// { DataLength, DataURI }. The store of one Module instance holds the bytes of the
// Objects written by reference; the reference is the Implementation's form -
// aif:payload/<Channel>/<sequence> here. A payload is freed when every reader of
// its Channel has released it, or its MaxAge has passed. A reference means
// nothing outside the Controller that issued it: where a Message leaves the
// Controller it is inlined.

// WHAT A HUB NEEDS OF THE PAYLOADS OF ITS MODULE, to send them apart on the payload
// channel (Communication 4, 7): the references a Message carries, their data, and
// the Message with its references replaced by the data where it travels without a
// payload channel.
public interface IPayloadSource
{
    IReadOnlyList<string> ReferencesOf(string json);
    byte[]? Get(string reference);
    void Release(string reference);
    string Inline(string json);
}

public sealed class PayloadStore : IPayloadSource
{
    public const string Scheme = "aif:payload/";

    private static readonly Regex References = new("\"(aif:payload/[^\"]+)\"", RegexOptions.Compiled);

    private readonly IClock clock;
    private readonly ConcurrentDictionary<string, Entry> held = new();
    private long sequence;

    private sealed class Entry
    {
        public ReadOnlyMemory<byte> Data { get; set; }

        // A payload that is on the other Controller until a reader asks for it: fetched
        // then, over the payload channel (null: it could not be obtained).
        public Func<byte[]?>? Fetch { get; set; }
        public required long Placed { get; init; }
        public required TimeSpan? MaxAge { get; init; }
        public int Unreleased;
    }

    public PayloadStore(IClock? clock = null) => this.clock = clock ?? SystemClock.Instance;

    public int Held => Sweep();

    public static bool IsReference(string? uri) => uri is not null && uri.StartsWith(Scheme, StringComparison.Ordinal);

    // The references a JSON instance carries.
    public static IReadOnlyList<string> ReferencesIn(string json) =>
        References.Matches(json).Select(m => m.Groups[1].Value).Distinct().ToList();

    // Placed for the Channel it will travel on: each of its readers is to release it.
    public string Put(ChannelSpec channel, ReadOnlyMemory<byte> data)
    {
        Sweep();
        var reference = $"{Scheme}{Uri.EscapeDataString(channel.Id)}/{Interlocked.Increment(ref sequence)}";
        held[reference] = new Entry
        {
            Data = data,
            Placed = clock.Monotonic,
            MaxAge = channel.Readers.Select(r => r.Behaviour.MaxAge).Where(a => a is not null).DefaultIfEmpty(null).Max(),
            Unreleased = Math.Max(1, channel.Readers.Count)
        };
        return reference;
    }

    // A payload that stays on the Controller that wrote it and is fetched when its
    // reader asks (Communication 7): placed for the Channel as any other.
    public string PutRemote(ChannelSpec channel, Func<byte[]?> fetch)
    {
        var reference = Put(channel, ReadOnlyMemory<byte>.Empty);
        held[reference].Fetch = fetch;
        return reference;
    }

    public bool TryGet(string reference, out ReadOnlyMemory<byte> data)
    {
        Sweep();
        if (held.TryGetValue(reference, out var h))
        {
            if (h.Fetch is { } fetch)
                lock (h)
                    if (h.Fetch is { } again)
                    {
                        var got = again();
                        if (got is null) { data = default; return false; }
                        h.Data = got;
                        h.Fetch = null;
                    }
            data = h.Data;
            return true;
        }
        data = default;
        return false;
    }

    public IReadOnlyList<string> ReferencesOf(string json) => ReferencesIn(json);
    public byte[]? Get(string reference) => TryGet(reference, out var data) ? data.ToArray() : null;

    // One reader is done with it; the last frees it.
    public void Release(string reference)
    {
        if (held.TryGetValue(reference, out var h) && Interlocked.Decrement(ref h.Unreleased) <= 0)
            held.TryRemove(reference, out _);
    }

    // A JSON instance with every reference this store holds replaced by the data
    // itself - { DataLength, DataURI } becoming { Data } - and released for the
    // reader that asked. References the store does not hold are left as they are.
    public string Inline(string json)
    {
        var references = ReferencesIn(json);
        if (references.Count == 0) return json;
        var root = JsonNode.Parse(json);
        if (root is null) return json;
        Walk(root);
        foreach (var reference in references) Release(reference);
        return root.ToJsonString();

        void Walk(JsonNode node)
        {
            switch (node)
            {
                case JsonObject o:
                    if (o["DataURI"] is JsonValue uri && uri.TryGetValue<string>(out var u) && TryGet(u, out var data))
                    {
                        o.Remove("DataURI");
                        o.Remove("DataLength");
                        o["Data"] = Convert.ToBase64String(data.Span);
                    }
                    foreach (var (_, child) in o.ToList()) if (child is not null) Walk(child);
                    break;
                case JsonArray a:
                    foreach (var child in a) if (child is not null) Walk(child);
                    break;
            }
        }
    }

    // Payloads older than their Channel's MaxAge are freed.
    private int Sweep()
    {
        var now = clock.Monotonic;
        foreach (var (reference, h) in held)
            if (h.MaxAge is { } age && now - h.Placed > (long)(age.TotalSeconds * System.Diagnostics.Stopwatch.Frequency))
                held.TryRemove(reference, out _);
        return held.Count;
    }
}
