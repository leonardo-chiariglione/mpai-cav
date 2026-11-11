using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AIF.Trust;

namespace AIF.Bout;

// THE GIVER OF A BOUT (MPAI-AIF V3.0, Communication 9.1): a Controller on an External Port or a boundary Output Port, or a User Agent or a Remote
// Client Application on a boundary Input Port with the Instance Credential its Controller issued to it. It makes the Bout from the first instance of
// the flow, signs it, and then makes each instance a Bout Instance: its Data Exchange Metadata completed - Binding, Sequence, KeyID, Signature or Hash,
// and in a Chain the AuditChainHash - and its canonical form compressed as one Zstandard frame with the Dictionary.
public sealed class BoutGiver
{
    public sealed record Options
    {
        public required string DataType { get; init; }
        public required int PortNumber { get; init; }
        public required int BoutId { get; init; }
        public string SigningMode { get; init; } = BoutSecurity.EachInstance;
        public int ChainInterval { get; init; } = 1;
        public int MaxInstanceLength { get; init; } = 65536;
        public int RepeatInterval { get; init; }
        // How hard each instance is compressed (Zstandard level 1 to 22): smaller frames, or less time to make them.
        public int CompressionLevel { get; init; } = BoutZstd.Level;
        public required BoutKey Key { get; init; }
        public required string KeyId { get; init; }
        // Identity.Issuer: who gives the Bout.
        public required string Issuer { get; init; }
        // The AIM whose flow it is, as the Source of the Data Exchange Metadata.
        public required string SourceAimId { get; init; }
        public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;
    }

    private readonly Options o;
    private byte[]? dictionary;
    private byte[] chain = [];
    private long sequence;
    private bool lastSigned = true;

    public BoutGiver(Options options)
    {
        if (options.BoutId < BoutZstd.MinBoutId) throw new ArgumentOutOfRangeException(nameof(options), "A BoutID is from 32768 to 2147483647.");
        if (options.SigningMode is not (BoutSecurity.EachInstance or BoutSecurity.Chain)) throw new ArgumentException("SigningMode is EachInstance or Chain.", nameof(options));
        if (options.ChainInterval < 1) throw new ArgumentException("ChainInterval is at least 1.", nameof(options));
        o = options;
    }

    public JsonObject? Bout { get; private set; }
    public int BoutId => o.BoutId;
    public bool Closed { get; private set; }
    public long Sequence => sequence;

    // The Bout, made from the first instance of the flow and signed; given before the first Bout Instance.
    public JsonObject Open(JsonObject firstInstance) => Now(OpenAsync(firstInstance));
    public byte[] Instance(JsonObject instance, bool last = false) => Now(InstanceAsync(instance, last));
    public JsonObject Close() => Now(CloseAsync());

    // The synchronous form, for a key that answers at once (the platform's ECDSA). A key that answers only asynchronously - the browser's Web Crypto -
    // is used with the Async methods: blocking on it in a browser would never end.
    private static T Now<T>(ValueTask<T> pending) =>
        pending.IsCompleted ? pending.GetAwaiter().GetResult() : throw new InvalidOperationException("This key signs asynchronously: use the Async methods.");

    public async ValueTask<JsonObject> OpenAsync(JsonObject firstInstance)
    {
        if (Bout is not null) throw new InvalidOperationException("The Bout is open.");
        // The content of the Dictionary is the canonical form of an instance of the flow as it travels: the first instance with its Data Exchange
        // Metadata, so that what the metadata of every instance repeats is in the Dictionary too.
        var now = o.Clock();
        var sample = firstInstance.DeepClone().AsObject();
        sample["DataXMData"] = Metadata((string?)firstInstance["DataXMData"]?["DataID"] ?? $"bout:{o.BoutId}:0", now, 0);
        BoutSecurity.Integrity(sample)!["Hash"] = new JsonObject { ["Algorithm"] = BoutSecurity.HashAlgorithm, ["Value"] = Convert.ToHexString(BoutSecurity.H(sample)) };
        var content = PtfCanonical.Bytes(sample);
        dictionary = BoutZstd.MakeDictionary(content, o.BoutId);
        var ms = now.ToUnixTimeMilliseconds();
        var bout = new JsonObject
        {
            ["Header"] = BoutSecurity.BoutHeader,
            ["BoutID"] = o.BoutId,
            ["BoutTime"] = new JsonObject
            {
                ["Header"] = "OSD-STM-V1.5", ["SimpleTimeID"] = $"bout-{o.BoutId}-T",
                ["SimpleTimeData"] = new JsonArray(new JsonObject { ["FlagsByte"] = 3, ["StartTime"] = ms, ["EndTime"] = ms, ["AccuracyMode"] = "single", ["AccuracyPlusMinus"] = 1 })
            },
            ["DataType"] = o.DataType,
            ["PortNumber"] = o.PortNumber,
            ["Method"] = "Zstandard",
            ["Dictionary"] = Convert.ToHexString(dictionary),
            ["SigningMode"] = o.SigningMode
        };
        if (o.SigningMode == BoutSecurity.Chain) bout["ChainInterval"] = o.ChainInterval;
        if (o.RepeatInterval > 0) bout["RepeatInterval"] = o.RepeatInterval;
        bout["MaxInstanceLength"] = o.MaxInstanceLength;
        bout["DataXMData"] = Metadata($"bout:{o.BoutId}", now, sequence: null);
        await BoutSecurity.SignAsync(bout, o.Key);
        chain = SHA256.HashData(BoutSecurity.SignedBytes(bout));
        return Bout = bout;
    }

    // A Bout Instance: one Zstandard frame. last: the instance is the last before CloseBout, and is signed in a Chain.
    public async ValueTask<byte[]> InstanceAsync(JsonObject instance, bool last = false)
    {
        if (Bout is null || dictionary is null) throw new InvalidOperationException("The Bout is not open.");
        if (Closed) throw new InvalidOperationException("The Bout is closed.");
        var x = instance.DeepClone().AsObject();
        var k = ++sequence;
        var dataId = (string?)x["DataXMData"]?["DataID"] ?? $"bout:{o.BoutId}:{k}";
        x["DataXMData"] = Metadata(dataId, o.Clock(), k);
        var h = BoutSecurity.H(x);
        if (o.SigningMode == BoutSecurity.Chain)
        {
            chain = SHA256.HashData([.. chain, .. h]);
            BoutSecurity.Security(x)!["AuditChainHash"] = Convert.ToHexString(chain);
        }
        var signed = o.SigningMode == BoutSecurity.EachInstance || k % o.ChainInterval == 0 || last;
        if (signed) await BoutSecurity.SignAsync(x, o.Key);
        else BoutSecurity.Integrity(x)!["Hash"] = new JsonObject { ["Algorithm"] = BoutSecurity.HashAlgorithm, ["Value"] = Convert.ToHexString(h) };
        lastSigned = signed;
        var canonical = PtfCanonical.Bytes(x);
        if (canonical.Length > o.MaxInstanceLength) throw new InvalidOperationException($"The instance is {canonical.Length} bytes, more than MaxInstanceLength {o.MaxInstanceLength}.");
        return BoutZstd.Compress(dictionary, canonical, o.CompressionLevel);
    }

    // CloseBout (MPAI-PTF V1.0, Verification Pipeline 11.3): a Trust Message of type TrustRequest, signed by the giver, giving the last Sequence and, in a
    // Chain, the last chain value - which also verifies the instances after the last signed one.
    public async ValueTask<JsonObject> CloseAsync()
    {
        if (Bout is null) throw new InvalidOperationException("The Bout is not open.");
        Closed = true;
        var now = o.Clock();
        var dem = Metadata($"bout:{o.BoutId}:close", now, sequence);
        var request = new JsonObject { ["Operation"] = "CloseBout", ["TargetType"] = BoutSecurity.BoutHeader, ["TargetID"] = o.BoutId.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        var security = dem["Security"]!.AsObject();
        if (o.SigningMode == BoutSecurity.Chain) security["AuditChainHash"] = Convert.ToHexString(chain);
        security["Integrity"]!["Hash"] = new JsonObject { ["Algorithm"] = BoutSecurity.HashAlgorithm, ["Value"] = Convert.ToHexString(SHA256.HashData(PtfCanonical.Bytes(request))) };
        var message = new JsonObject
        {
            ["Header"] = "PTF-MSG-V1.0",
            ["MessageID"] = $"{o.KeyId}#MSG-{Guid.NewGuid():N}",
            ["MessageTime"] = new JsonObject { ["Header"] = "OSD-TIM-V1.5", ["TimeID"] = $"{o.KeyId}#TIM-{Guid.NewGuid():N}", ["Data"] = PtfIdentity.Time(now) },
            ["MessageType"] = "TrustRequest",
            ["RequesterID"] = o.KeyId,
            ["Request"] = request,
            ["DataXMData"] = dem
        };
        return await BoutSecurity.SignTopAsync(message, o.Key, o.KeyId);
    }

    public bool LastInstanceSigned => lastSigned;

    private JsonObject Metadata(string dataId, DateTimeOffset stamp, long? sequence)
    {
        var security = new JsonObject
        {
            ["Header"] = BoutSecurity.SecurityHeader,
            ["Identity"] = new JsonObject { ["Issuer"] = o.Issuer },
            ["Transmission"] = new JsonObject { ["Protocol"] = "Custom", ["Binding"] = BoutSecurity.Binding(o.DataType, o.PortNumber, stamp, o.BoutId) },
            ["Integrity"] = new JsonObject { ["KeyID"] = o.KeyId }
        };
        if (sequence is { } s) security["Freshness"] = new JsonObject { ["Sequence"] = s };
        return new JsonObject
        {
            ["Header"] = BoutSecurity.DemHeader,
            ["DataID"] = dataId,
            ["DataType"] = o.DataType,
            ["Source"] = new JsonArray(new JsonObject { ["AIMID"] = o.SourceAimId }),
            ["Security"] = security
        };
    }
}
