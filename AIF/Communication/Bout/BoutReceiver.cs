using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIF.Trust;

namespace AIF.Bout;

// THE RECEIVER OF A BOUT on one Port (MPAI-AIF V3.0, Communication 9.1; MPAI-PTF V1.0, Verification Pipeline 11).
//   Accept   AcceptBout: the eight checks of Verification Pipeline 11.1, in order; at the first that fails the Bout is refused with its PTF Error Code.
//   Receive  each Bout Instance: checks (a) to (e) of Communication 9.1, in order; refused, and counted, at the first that fails (COM-18). In a Chain the
//            instances of a segment are held until the signed instance that ends it verifies (VerifyChain), or CloseBout gives the last chain value.
//   Close    CloseBout: the signed TrustRequest of the giver; later instances of the Bout are refused.
// Delivered instances are the decompressed instances, with their Data Exchange Metadata: what is stored (COM-20).
public sealed class BoutReceiver
{
    public const string SchemaViolation = "schemaViolation";
    public const string SignatureFailure = "signatureFailure";
    public const string PolicyViolation = "policyViolation";
    public const string InvalidValue = "invalidValue";
    public const string UnsupportedAlgorithm = "unsupportedAlgorithm";
    public const string HashMismatch = "hashMismatch";

    public sealed record Options
    {
        public required string DataType { get; init; }
        public required int PortNumber { get; init; }
        // The public key a KeyID names, or null where the receiver knows none.
        public required Func<string, BoutKey?> KeyFor { get; init; }
        // Check 3: null where the signer may give this flow - the Controller at the other end of the link, or the holder of an Instance Credential this
        // Controller issued and has not revoked - otherwise the reason.
        public Func<JsonObject, string?> MayGive { get; init; } = _ => null;
        // Check 8: EvaluatePolicy. null where the policy allows the flow.
        public Func<JsonObject, string?> Policy { get; init; } = _ => null;
        // Check (c): the instance against the JSON Schema of its Data Type. Absent: not checked here.
        public Func<JsonObject, bool>? Validate { get; init; }
        public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;
        // How far a stamp may be from the receiver's time (COM-13).
        public TimeSpan Freshness { get; init; } = TimeSpan.FromMinutes(5);
        // The Trace: (Trust Operation, target type, target, failure or null).
        public Action<string, string, string, string?>? Record { get; init; }
    }

    public sealed record Outcome(string? ErrorCode, string? Reason)
    {
        public bool Ok => ErrorCode is null;
        public static readonly Outcome Accepted = new(null, null);
    }

    public sealed record Received(string? ErrorCode, string? Reason, IReadOnlyList<JsonObject> Delivered, bool Held = false)
    {
        public bool Ok => ErrorCode is null;
    }

    private static readonly Regex DataTypePattern = new(@"^[A-Z0-9]{3,4}-[A-Z0-9]{3}-V[0-9]{1,2}\.[0-9]{1,2}$", RegexOptions.CultureInvariant);
    private static readonly Regex HexPattern = new(@"^([0-9A-F]{2})+$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> BoutMembers =
        ["Header", "MInstanceID", "UEnvironmentID", "BoutID", "BoutTime", "DataType", "PortNumber", "Method", "Dictionary",
         "SigningMode", "ChainInterval", "RepeatInterval", "MaxInstanceLength", "DataXMData", "DescrMetadata"];

    private readonly Options o;
    private readonly HashSet<(string KeyId, int BoutId)> used = [];
    private readonly HashSet<int> closed = [];
    private readonly List<(long Sequence, JsonObject Instance, byte[] Chain)> pending = [];
    private byte[]? dictionary;
    private BoutKey? key;
    private string keyId = "";
    private string mode = BoutSecurity.EachInstance;
    private int maxLength;
    private int boutId;
    private DateTimeOffset boutStamp;
    private long lastSequence;
    private byte[] chainBase = [];
    private long baseSequence;
    private bool broken;
    private long refused;

    public BoutReceiver(Options options) => o = options;

    public JsonObject? Bout { get; private set; }
    public long Refused => Interlocked.Read(ref refused);

    // ------------------------------------------------------------------ AcceptBout
    public Outcome Accept(JsonObject bout) => Now(AcceptAsync(bout));
    public Received Receive(byte[] frame) => Now(ReceiveAsync(frame));
    public Received Close(JsonObject message) => Now(CloseAsync(message));

    // The synchronous form, for a key that answers at once (the platform's ECDSA). A key that answers only asynchronously - the browser's Web Crypto -
    // is used with the Async methods: blocking on it in a browser would never end.
    private static T Now<T>(ValueTask<T> pending) =>
        pending.IsCompleted ? pending.GetAwaiter().GetResult() : throw new InvalidOperationException("This key signs asynchronously: use the Async methods.");

    public async ValueTask<Outcome> AcceptAsync(JsonObject bout)
    {
        var id = IsInteger(bout["BoutID"], out var given) && given is >= 0 and <= int.MaxValue ? (int)given : 0;
        var target = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Outcome Refuse(string code, string reason)
        {
            o.Record?.Invoke("AcceptBout", BoutSecurity.BoutHeader, target, code + ": " + reason);
            return new Outcome(code, reason);
        }

        // 1 the Bout schema of MPAI-AIF
        if (SchemaProblem(bout) is { } problem) return Refuse(SchemaViolation, problem);

        // 2 the Signature, with the key its KeyID names
        var kid = BoutSecurity.KeyIdOf(bout) ?? "";
        var k = o.KeyFor(kid);
        if (k is null || !await BoutSecurity.VerifyAsync(bout, k)) return Refuse(SignatureFailure, "the Signature of the Bout does not verify");

        // 3 who signed it may give the flow
        if (o.MayGive(bout) is { } notAllowed) return Refuse(PolicyViolation, notAllowed);

        // 4 the Binding names this Port; its stamp is fresh
        var binding = BoutSecurity.ParseBinding(BoutSecurity.BindingOf(bout));
        if (binding is not { } b || b.DataType != o.DataType || b.PortNumber != o.PortNumber || b.BoutId != id)
            return Refuse(InvalidValue, "the Binding does not name this Port and BoutID");
        if (!Fresh(b.Stamp)) return Refuse(InvalidValue, "the stamp of the Bout is not fresh");
        if ((string?)bout["DataType"] != o.DataType || BoutSecurity.AsLong(bout["PortNumber"]) != o.PortNumber) return Refuse(InvalidValue, "the Bout is for another Port");

        // 5 the BoutID in range, not used before; the Dictionary a Zstandard dictionary whose Dictionary_ID is the BoutID
        if (id < BoutZstd.MinBoutId) return Refuse(InvalidValue, "the BoutID is out of range");
        if (used.Contains((kid, id))) return Refuse(InvalidValue, "the BoutID has been used before");
        var dict = Convert.FromHexString((string)bout["Dictionary"]!);
        if (BoutZstd.DictionaryId(dict) != (uint)id) return Refuse(InvalidValue, "the Dictionary_ID is not the BoutID");

        // 6 the Method
        if ((string?)bout["Method"] != "Zstandard") return Refuse(UnsupportedAlgorithm, "the Method is not Zstandard");

        // 7 newer than the Bout in force
        if (Bout is not null && b.Stamp <= boutStamp) return Refuse(InvalidValue, "the Bout is not newer than the one in force");

        // 8 the policy (EvaluatePolicy)
        if (o.Policy(bout) is { } refusedByPolicy) return Refuse(PolicyViolation, refusedByPolicy);

        // In force from now: the one before it is superseded.
        if (Bout is not null) closed.Add(boutId);
        used.Add((kid, id));
        Bout = bout; dictionary = dict; key = k; keyId = kid; boutId = id; boutStamp = b.Stamp;
        mode = (string)bout["SigningMode"]!;
        maxLength = (int)BoutSecurity.AsLong(bout["MaxInstanceLength"])!.Value;
        lastSequence = 0; baseSequence = 0; broken = false; pending.Clear();
        chainBase = SHA256.HashData(BoutSecurity.SignedBytes(bout));
        o.Record?.Invoke("AcceptBout", BoutSecurity.BoutHeader, target, null);
        return Outcome.Accepted;
    }

    // ------------------------------------------------------------------ a Bout Instance
    public async ValueTask<Received> ReceiveAsync(byte[] frame)
    {
        // (a) the Dictionary_ID names a Bout accepted and in force on this Port
        var named = BoutZstd.FrameDictionaryId(frame);
        if (Bout is null || dictionary is null || key is null || named != (uint)boutId || closed.Contains(boutId))
            return Refuse(InvalidValue, "the frame names no Bout in force on this Port");

        // (b) the Frame_Content_Size, before decompressing
        if (BoutZstd.FrameContentSize(frame) is not { } size || size > maxLength)
            return Refuse(InvalidValue, "the frame states no content size, or one larger than MaxInstanceLength");

        byte[] content;
        try { content = BoutZstd.Decompress(dictionary, frame, maxLength); }
        catch (Exception e) when (e is ZstdSharp.ZstdException or InvalidOperationException or ArgumentException) { return Refuse(InvalidValue, "the frame does not decompress"); }

        // (c) canonical JSON, valid against the schema of the Data Type
        JsonObject x;
        try { x = JsonNode.Parse(content) as JsonObject ?? throw new FormatException(); }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException) { return Refuse(SchemaViolation, "the instance is not JSON"); }
        if (!PtfCanonical.Bytes(x).AsSpan().SequenceEqual(content)) return Refuse(SchemaViolation, "the instance is not in canonical form");
        if (o.Validate is { } validate && !validate(x)) return Refuse(SchemaViolation, "the instance does not validate against its Data Type");

        // (d) the Binding names this Port and the BoutID; the stamp is fresh; the Sequence larger than any accepted
        var binding = BoutSecurity.ParseBinding(BoutSecurity.BindingOf(x));
        if (binding is not { } b || b.DataType != o.DataType || b.PortNumber != o.PortNumber || b.BoutId != boutId)
            return Refuse(InvalidValue, "the Binding does not name this Port and BoutID");
        if (!Fresh(b.Stamp)) return Refuse(InvalidValue, "the stamp of the instance is not fresh");
        if (BoutSecurity.SequenceOf(x) is not { } sequence || sequence <= lastSequence) return Refuse(InvalidValue, "the Sequence is not larger than any accepted");
        lastSequence = sequence;

        // (e) the Signature, or the chain and the signature that ends its segment
        if (BoutSecurity.KeyIdOf(x) != keyId) return Refuse(SignatureFailure, "the instance names another key");
        var signed = BoutSecurity.Integrity(x)?["Signature"] is not null;
        var h = BoutSecurity.H(x);
        if ((string?)BoutSecurity.Integrity(x)?["Hash"]?["Value"] is { } stated && !string.Equals(stated, Convert.ToHexString(h), StringComparison.OrdinalIgnoreCase))
            return RefuseSegment(HashMismatch, "the Hash of the instance does not match");

        if (mode == BoutSecurity.EachInstance)
        {
            if (!signed || !await BoutSecurity.VerifyAsync(x, key)) return Refuse(SignatureFailure, "the Signature of the instance does not verify");
            return new Received(null, null, [x]);
        }

        // Chain
        var expected = (pending.Count > 0 ? pending[^1].Sequence : baseSequence) + 1;
        if (broken || sequence != expected)
        {
            // An instance of the segment is missing: the segment is refused. A signed instance that verifies starts the chain again from its value.
            var reason = broken ? "the segment is broken" : "an instance of the segment is missing";
            if (signed && await BoutSecurity.VerifyAsync(x, key) && (string?)BoutSecurity.Security(x)?["AuditChainHash"] is { } restart)
            {
                chainBase = Convert.FromHexString(restart); baseSequence = sequence; broken = false;
                var dropped = pending.Count + 1; pending.Clear();
                Interlocked.Add(ref refused, dropped);
                Trace("VerifyChain", reason);
                return new Received(InvalidValue, reason, []);
            }
            broken = true;
            return RefuseSegment(InvalidValue, reason);
        }
        if (!signed && BoutSecurity.Integrity(x)?["Hash"] is null) return RefuseSegment(HashMismatch, "an unsigned instance of a Chain carries no Hash");
        var a = SHA256.HashData([.. (pending.Count > 0 ? pending[^1].Chain : chainBase), .. h]);
        if (!string.Equals((string?)BoutSecurity.Security(x)?["AuditChainHash"], Convert.ToHexString(a), StringComparison.OrdinalIgnoreCase))
        {
            broken = true;
            return RefuseSegment(HashMismatch, "the chain value does not match");
        }
        if (!signed)
        {
            pending.Add((sequence, x, a));
            return new Received(null, null, [], Held: true);
        }
        if (!await BoutSecurity.VerifyAsync(x, key))
        {
            broken = true;
            return RefuseSegment(SignatureFailure, "the signature that ends the segment does not verify");
        }
        var delivered = pending.Select(p => p.Instance).Append(x).ToList();
        var first = pending.Count > 0 ? pending[0].Sequence : sequence;
        pending.Clear(); chainBase = a; baseSequence = sequence;
        Trace("VerifyChain", null, $"{first}-{sequence}");
        return new Received(null, null, delivered);
    }

    // ------------------------------------------------------------------ CloseBout
    public async ValueTask<Received> CloseAsync(JsonObject message)
    {
        if (Bout is null || key is null) return new Received(InvalidValue, "no Bout is in force", []);
        var target = boutId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if ((string?)message["Header"] != "PTF-MSG-V1.0" || (string?)message["MessageType"] != "TrustRequest" ||
            (string?)message["Request"]?["Operation"] != "CloseBout" || (string?)message["Request"]?["TargetType"] != BoutSecurity.BoutHeader ||
            (string?)message["Request"]?["TargetID"] != target)
            return new Received(InvalidValue, "not the CloseBout of the Bout in force", []);
        if ((string?)message["KeyID"] != keyId || !await BoutSecurity.VerifyTopAsync(message, key))
            return new Received(SignatureFailure, "the CloseBout is not signed by the giver of the Bout", []);

        // The last chain value verifies the instances after the last signed one; a last Sequence beyond what arrived is a truncation.
        var delivered = new List<JsonObject>();
        string? problem = null;
        var last = BoutSecurity.AsLong(message["DataXMData"]?["Security"]?["Freshness"]?["Sequence"]) ?? -1;
        if (pending.Count > 0)
        {
            var closing = (string?)message["DataXMData"]?["Security"]?["AuditChainHash"];
            if (pending[^1].Sequence == last && string.Equals(closing, Convert.ToHexString(pending[^1].Chain), StringComparison.OrdinalIgnoreCase))
            {
                delivered.AddRange(pending.Select(p => p.Instance));
                Trace("VerifyChain", null, $"{pending[0].Sequence}-{last}");
            }
            else { problem = "the last instances do not verify against CloseBout"; Interlocked.Add(ref refused, pending.Count); }
            pending.Clear();
        }
        else if (last > lastSequence) problem = "instances given before CloseBout did not arrive";

        closed.Add(boutId);
        Bout = null; dictionary = null;
        o.Record?.Invoke("CloseBout", BoutSecurity.BoutHeader, target, problem);
        return new Received(problem is null ? null : InvalidValue, problem, delivered);
    }

    // ------------------------------------------------------------------
    private bool Fresh(DateTimeOffset stamp) => (o.Clock() - stamp).Duration() <= o.Freshness;

    private Received Refuse(string code, string reason)
    {
        Interlocked.Increment(ref refused);
        return new Received(code, reason, []);
    }

    private Received RefuseSegment(string code, string reason)
    {
        Interlocked.Add(ref refused, pending.Count + 1);
        pending.Clear();
        if (mode == BoutSecurity.Chain) Trace("VerifyChain", reason);
        return new Received(code, reason, []);
    }

    private void Trace(string operation, string? failure, string? range = null) =>
        o.Record?.Invoke(operation, BoutSecurity.BoutHeader, boutId.ToString(System.Globalization.CultureInfo.InvariantCulture) + (range is null ? "" : ":" + range), failure);

    // What the Bout schema (AIF/V3.0/data/Bout.json) requires, or null.
    private static string? SchemaProblem(JsonObject bout)
    {
        foreach (var member in bout) if (!BoutMembers.Contains(member.Key)) return $"{member.Key} is not a member of a Bout";
        if ((string?)(bout["Header"] as JsonValue) != BoutSecurity.BoutHeader) return "Header is not AIF-BOU-V3.0";
        if (!IsInteger(bout["BoutID"], out var id) || id < BoutZstd.MinBoutId || id > BoutZstd.MaxBoutId) return "BoutID is not an integer from 32768 to 2147483647";
        if (bout["DataType"] is not JsonValue dt || !dt.TryGetValue<string>(out var t) || !DataTypePattern.IsMatch(t)) return "DataType is not a Data Type in canonical form";
        if (!IsInteger(bout["PortNumber"], out var pn) || pn < 1) return "PortNumber is not an integer from 1";
        if (bout["Method"] is not JsonValue m || !m.TryGetValue<string>(out _)) return "Method is missing";
        if (bout["Dictionary"] is not JsonValue d || !d.TryGetValue<string>(out var hex) || !HexPattern.IsMatch(hex)) return "Dictionary is not Hexadecimal ASCII";
        if ((string?)(bout["SigningMode"] as JsonValue) is not (BoutSecurity.EachInstance or BoutSecurity.Chain)) return "SigningMode is not EachInstance or Chain";
        if ((string?)bout["SigningMode"] == BoutSecurity.Chain && (!IsInteger(bout["ChainInterval"], out var ci) || ci < 1)) return "a Chain Bout has no ChainInterval";
        if (bout["ChainInterval"] is not null && (!IsInteger(bout["ChainInterval"], out var c2) || c2 < 1)) return "ChainInterval is not an integer from 1";
        if (bout["RepeatInterval"] is not null && (!IsInteger(bout["RepeatInterval"], out var ri) || ri < 0)) return "RepeatInterval is not an integer from 0";
        if (!IsInteger(bout["MaxInstanceLength"], out var ml) || ml < 1) return "MaxInstanceLength is not an integer from 1";
        if (bout["DataXMData"] is not JsonObject dem || (string?)(dem["Header"] as JsonValue) != BoutSecurity.DemHeader) return "DataXMData is not Data Exchange Metadata";
        if (BoutSecurity.Security(bout) is not { } sec || (string?)(sec["Header"] as JsonValue) != BoutSecurity.SecurityHeader || sec["Identity"] is not JsonObject || sec["Transmission"] is not JsonObject || sec["Integrity"] is not JsonObject)
            return "DataXMData has no Security with Header, Identity, Transmission and Integrity";
        return null;
    }

    private static bool IsInteger(JsonNode? node, out long value)
    {
        var n = BoutSecurity.AsLong(node);
        value = n ?? 0;
        return n is not null;
    }
}
