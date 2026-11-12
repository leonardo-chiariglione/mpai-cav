using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AIF.Bout;
using AIF.Trust;
using Xunit;

namespace Mpai.Aif.Tests;

// BOUTS (MPAI-AIF V3.0, Communication 9.1; MPAI-PTF V1.0, Verification Pipeline 11): the published test vectors read as specified, a flow given and
// received through the library, and the refusals of COM-18 and of AcceptBout.
public sealed class BoutTests
{
    private static string Vectors => Path.Combine(Repository.Schemas, "AIF", "V3.0", "test-vectors", "Bout");
    private static readonly DateTimeOffset VectorTime = DateTimeOffset.Parse("2025-11-06T03:33:20+00:00");

    private static ECDsa TestKey(string set)
    {
        var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromHexString(File.ReadAllText(Path.Combine(Vectors, set, "test-key.public.spki.hex")).Trim()), out _);
        return key;
    }

    private static BoutReceiver VectorReceiver(string set) => new(new BoutReceiver.Options
    {
        DataType = "OSD-OSA-V1.5", PortNumber = 1, KeyFor = id => id == "CAV-1" ? TestKey(set) : null, Clock = () => VectorTime
    });

    private static byte[] Frame(string set, int k) => Convert.FromHexString(File.ReadAllText(Path.Combine(Vectors, set, $"instance-{k}.frame.hex")).Trim());
    private static byte[] Canon(string set, int k) => File.ReadAllBytes(Path.Combine(Vectors, set, $"instance-{k}.canon"));
    private static JsonObject BoutOf(string set) => JsonNode.Parse(File.ReadAllText(Path.Combine(Vectors, set, "bout.json")))!.AsObject();

    [Fact]
    public void PublishedVectors_EachInstance_AreAcceptedAndDecompressExactly()
    {
        var r = VectorReceiver("A-each-instance");
        var accepted = r.Accept(BoutOf("A-each-instance"));
        Assert.True(accepted.Ok, accepted.Reason);
        for (var k = 1; k <= 5; k++)
        {
            Assert.Equal(32769u, BoutZstd.FrameDictionaryId(Frame("A-each-instance", k)));
            var got = r.Receive(Frame("A-each-instance", k));
            Assert.True(got.Ok, got.Reason);
            Assert.Equal(Canon("A-each-instance", k), PtfCanonical.Bytes(Assert.Single(got.Delivered)));
        }
    }

    [Fact]
    public void PublishedVectors_Chain_DeliverEachSegmentWhenItsSignatureVerifies()
    {
        var r = VectorReceiver("B-chain");
        Assert.True(r.Accept(BoutOf("B-chain")).Ok);
        var delivered = new List<JsonObject>();
        var sizes = new List<int>();
        for (var k = 1; k <= 7; k++)
        {
            var got = r.Receive(Frame("B-chain", k));
            Assert.True(got.Ok, got.Reason);
            sizes.Add(got.Delivered.Count);
            delivered.AddRange(got.Delivered);
        }
        Assert.Equal(new[] { 0, 0, 3, 0, 0, 3, 1 }, sizes);       // instances 3, 6 and 7 are signed
        for (var k = 1; k <= 7; k++) Assert.Equal(Canon("B-chain", k), PtfCanonical.Bytes(delivered[k - 1]));
    }

    // The signed bytes and the bytes of H cut out of the received bytes are those the canonical form rebuilds, on every published instance.
    [Fact]
    public void PublishedVectors_CutBytes_AreTheRebuiltBytes()
    {
        foreach (var (set, count) in new[] { ("A-each-instance", 5), ("B-chain", 7) })
            for (var k = 1; k <= count; k++)
            {
                var content = Canon(set, k);
                var x = JsonNode.Parse(content)!.AsObject();
                var cut = BoutSecurity.Cut(content);
                Assert.NotNull(cut);
                Assert.Equal(BoutSecurity.SignedBytes(x), cut!.Signed);
                Assert.Equal(BoutSecurity.H(x), SHA256.HashData(cut.Hashed));
            }
    }

    // An instance not in canonical form is refused at (c), as before; a changed instance at (e).
    [Fact]
    public void PublishedVectors_NotCanonicalOrChanged_AreRefused()
    {
        byte[] Frame2(byte[] content) => BoutZstd.Compress(Convert.FromHexString((string)BoutOf("A-each-instance")["Dictionary"]!), content);
        var r = VectorReceiver("A-each-instance");
        Assert.True(r.Accept(BoutOf("A-each-instance")).Ok);
        var content = Canon("A-each-instance", 1);
        var spaced = System.Text.Encoding.UTF8.GetBytes(JsonNode.Parse(content)!.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Assert.Equal(BoutReceiver.SchemaViolation, r.Receive(Frame2(spaced)).ErrorCode);
        var sigSpaced = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(content).Replace("\"Signature\":", "\"Signature\" :"));
        Assert.Equal(BoutReceiver.SchemaViolation, r.Receive(Frame2(sigSpaced)).ErrorCode);
        Assert.True(r.Receive(Frame("A-each-instance", 1)).Ok);                  // the two refused at (c) did not move the Sequence on
        var x = JsonNode.Parse(Canon("A-each-instance", 2))!.AsObject();
        x["Position"]!["CartPosition"]![0] = 1.5;
        Assert.Equal(BoutReceiver.SignatureFailure, r.Receive(Frame2(PtfCanonical.Bytes(x))).ErrorCode);
    }

    [Fact]
    public void PublishedVectors_TamperedBoutSignature_IsRefused()
    {
        var bout = BoutOf("A-each-instance");
        bout["MaxInstanceLength"] = 4097;
        var refused = VectorReceiver("A-each-instance").Accept(bout);
        Assert.Equal(BoutReceiver.SignatureFailure, refused.ErrorCode);
    }

    [Fact]
    public void PublishedVectors_ReplayedInstance_IsRefused()
    {
        var r = VectorReceiver("A-each-instance");
        Assert.True(r.Accept(BoutOf("A-each-instance")).Ok);
        Assert.True(r.Receive(Frame("A-each-instance", 1)).Ok);
        Assert.Equal(BoutReceiver.InvalidValue, r.Receive(Frame("A-each-instance", 1)).ErrorCode);
        Assert.Equal(1, r.Refused);
    }

    [Fact]
    public void PublishedVectors_ChainWithAMissingInstance_RefusesTheSegment()
    {
        var r = VectorReceiver("B-chain");
        Assert.True(r.Accept(BoutOf("B-chain")).Ok);
        Assert.True(r.Receive(Frame("B-chain", 1)).Held);
        var third = r.Receive(Frame("B-chain", 3));                // instance 2 is missing
        Assert.False(third.Ok);
        Assert.Empty(third.Delivered);
        // The signed instance 3 starts the chain again: the next segment is delivered.
        Assert.True(r.Receive(Frame("B-chain", 4)).Held);
        Assert.True(r.Receive(Frame("B-chain", 5)).Held);
        Assert.Equal(3, r.Receive(Frame("B-chain", 6)).Delivered.Count);
    }

    // ------------------------------------------------------------------ a flow through the library
    private static JsonObject Instance(int k) => new()
    {
        ["Header"] = "OSD-OSA-V1.5", ["ObjectSpatialAttitudeID"] = $"EGO{k:D6}", ["ObjectID"] = "EGO",
        ["Position"] = new JsonObject { ["Header"] = "OSD-OPS-V1.5", ["PositionID"] = $"EGO{k:D6}-P", ["CartPosition"] = new JsonArray(10.0 + k, 20.0 + k * 0.5, 0.0) },
        ["Orientation"] = new JsonObject { ["Header"] = "OSD-OOR-V1.5", ["OrientationID"] = $"EGO{k:D6}-O", ["Orientation"] = new JsonArray(-18.0 + k * 0.1, 0.0, 0.0) }
    };

    private sealed class Flow
    {
        public readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-11T10:00:00+00:00");
        public readonly List<string> Trace = [];
        public BoutGiver Giver(int boutId, string mode = BoutSecurity.EachInstance, int interval = 1, int max = 65536) => new(new BoutGiver.Options
        {
            DataType = "OSD-OSA-V1.5", PortNumber = 1, BoutId = boutId, SigningMode = mode, ChainInterval = interval, MaxInstanceLength = max,
            Key = Key, KeyId = "CTRL-1", Issuer = "urn:mpai:controller:CTRL-1", SourceAimId = "CAV-SAG-V2.0", Clock = () => Now
        });
        public BoutReceiver Receiver(int port = 1) => new(new BoutReceiver.Options
        {
            DataType = "OSD-OSA-V1.5", PortNumber = port, KeyFor = id => id == "CTRL-1" ? Key : null, Clock = () => Now,
            Record = (op, _, target, failure) => Trace.Add($"{op} {target} {failure ?? "ok"}")
        });
    }

    [Fact]
    public void Flow_EachInstance_RoundTripsExactly()
    {
        var f = new Flow();
        var g = f.Giver(40001);
        var r = f.Receiver();
        Assert.True(r.Accept(g.Open(Instance(0))).Ok);
        for (var k = 1; k <= 10; k++)
        {
            f.Now = f.Now.AddMilliseconds(100);
            var frame = g.Instance(Instance(k));
            var got = r.Receive(frame);
            Assert.True(got.Ok, got.Reason);
            var x = Assert.Single(got.Delivered);
            Assert.Equal((string?)Instance(k)["ObjectSpatialAttitudeID"], (string?)x["ObjectSpatialAttitudeID"]);
            Assert.Equal(k, BoutSecurity.SequenceOf(x));
            Assert.True(frame.Length < PtfCanonical.Bytes(x).Length / 2, $"frame {frame.Length} bytes");
        }
        Assert.True(r.Close(g.Close()).Ok);
        Assert.Contains(f.Trace, t => t.StartsWith("AcceptBout 40001 ok"));
        Assert.Contains(f.Trace, t => t.StartsWith("CloseBout 40001 ok"));
    }

    [Fact]
    public void Flow_Chain_TheTailIsVerifiedByCloseBout()
    {
        var f = new Flow();
        var g = f.Giver(40002, BoutSecurity.Chain, 4);
        var r = f.Receiver();
        Assert.True(r.Accept(g.Open(Instance(0))).Ok);
        var delivered = 0;
        for (var k = 1; k <= 6; k++) delivered += r.Receive(g.Instance(Instance(k))).Delivered.Count;
        Assert.Equal(4, delivered);                                  // 5 and 6 wait for a signature
        var closed = r.Close(g.Close());
        Assert.True(closed.Ok, closed.Reason);
        Assert.Equal(2, closed.Delivered.Count);
    }

    [Fact]
    public void Flow_InstancesAfterCloseBout_AreRefused()
    {
        var f = new Flow();
        var g = f.Giver(40003);
        var r = f.Receiver();
        Assert.True(r.Accept(g.Open(Instance(0))).Ok);
        var late = g.Instance(Instance(1));
        var closed = r.Close(g.Close());                              // CloseBout states Sequence 1, which has not arrived: a truncation
        Assert.Equal(BoutReceiver.InvalidValue, closed.ErrorCode);
        Assert.Contains("did not arrive", closed.Reason);
        Assert.Equal(BoutReceiver.InvalidValue, r.Receive(late).ErrorCode);
    }

    [Fact]
    public void Flow_FrameLargerThanMaxInstanceLength_IsRefusedBeforeDecompressing()
    {
        var f = new Flow();
        var small = f.Giver(40004, max: 300);
        var big = f.Giver(40004, max: 65536);
        var r = f.Receiver();
        Assert.True(r.Accept(small.Open(Instance(0))).Ok);
        big.Open(Instance(0));
        var refused = r.Receive(big.Instance(Instance(1)));
        Assert.Equal(BoutReceiver.InvalidValue, refused.ErrorCode);
        Assert.Contains("MaxInstanceLength", refused.Reason);
    }

    [Fact]
    public void Accept_RefusesAWrongPortAStaleStampAndAReusedBoutID()
    {
        var f = new Flow();
        Assert.Equal(BoutReceiver.InvalidValue, f.Receiver(port: 2).Accept(f.Giver(40005).Open(Instance(0))).ErrorCode);

        var stale = f.Giver(40006).Open(Instance(0));
        f.Now = f.Now.AddHours(1);
        Assert.Equal(BoutReceiver.InvalidValue, f.Receiver().Accept(stale).ErrorCode);

        var r = f.Receiver();
        Assert.True(r.Accept(f.Giver(40007).Open(Instance(0))).Ok);
        f.Now = f.Now.AddSeconds(1);
        Assert.Equal(BoutReceiver.InvalidValue, r.Accept(f.Giver(40007).Open(Instance(0))).ErrorCode);
        f.Now = f.Now.AddSeconds(1);
        Assert.True(r.Accept(f.Giver(40008).Open(Instance(0))).Ok);   // a new Bout, with a new BoutID, takes over
    }

    [Fact]
    public void Accept_RefusesAnUnknownSignerAndASchemaViolation()
    {
        var f = new Flow();
        var other = new Flow { Now = f.Now };
        // signed by another key under the same KeyID
        Assert.Equal(BoutReceiver.SignatureFailure, f.Receiver().Accept(other.Giver(40009).Open(Instance(0))).ErrorCode);
        var bout = f.Giver(40010).Open(Instance(0));
        bout["Extra"] = 1;
        Assert.Equal(BoutReceiver.SchemaViolation, f.Receiver().Accept(bout).ErrorCode);
    }

    // ------------------------------------------------------------------ the credential of a User Agent or a client
    [Fact]
    public void Credential_IsIssuedOnAFreshIdentity_AndItsHolderMayGiveUntilRevoked()
    {
        var now = DateTimeOffset.Parse("2026-11-11T10:00:00+00:00");
        using var controllerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var credentials = new BoutCredentials(new PtfIssuer("CTRL-1", controllerKey, "CTRL-1"), () => now);
        using var uaKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var (credential, reason) = credentials.Issue(BoutCredentials.Proof(uaKey, "UA-1", now), BoutCredentials.UserAgent);
        Assert.NotNull(credential);
        Assert.Null(reason);
        Assert.Equal("ProcessInstance", (string?)credential!["Subject"]!["InstanceType"]);
        Assert.Equal(PtfSignature.Outcome.Valid, PtfSignature.Verify(credential, id => id == "CTRL-1" ? controllerKey : null));

        // The User Agent gives a Bout on a boundary Input Port; the Controller accepts it.
        var giver = new BoutGiver(new BoutGiver.Options
        {
            DataType = "OSD-OSA-V1.5", PortNumber = 1, BoutId = 50001, Key = uaKey, KeyId = "UA-1", Issuer = "urn:mpai:ua:UA-1", SourceAimId = "UA-1", Clock = () => now
        });
        var closedByRevocation = new List<string>();
        credentials.Revoked += id => closedByRevocation.Add(id);
        var receiver = new BoutReceiver(new BoutReceiver.Options
        {
            DataType = "OSD-OSA-V1.5", PortNumber = 1, KeyFor = credentials.KeyFor, MayGive = credentials.MayGive, Clock = () => now
        });
        Assert.True(receiver.Accept(giver.Open(Instance(0))).Ok);
        Assert.True(receiver.Receive(giver.Instance(Instance(1))).Ok);

        credentials.RevokeAll();
        Assert.Equal(["UA-1"], closedByRevocation);
        var after = new BoutGiver(new BoutGiver.Options
        {
            DataType = "OSD-OSA-V1.5", PortNumber = 1, BoutId = 50002, Key = uaKey, KeyId = "UA-1", Issuer = "urn:mpai:ua:UA-1", SourceAimId = "UA-1", Clock = () => now
        });
        Assert.Equal(BoutReceiver.SignatureFailure, receiver.Accept(after.Open(Instance(0))).ErrorCode);
    }

    [Fact]
    public void Credential_IsRefusedOnAStaleOrTamperedIdentity()
    {
        var now = DateTimeOffset.Parse("2026-11-11T10:00:00+00:00");
        using var controllerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var credentials = new BoutCredentials(new PtfIssuer("CTRL-1", controllerKey, "CTRL-1"), () => now);
        using var uaKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.NotNull(credentials.Issue(BoutCredentials.Proof(uaKey, "UA-1", now.AddMinutes(-30)), BoutCredentials.UserAgent).Reason);
        var tampered = BoutCredentials.Proof(uaKey, "UA-1", now);
        tampered["CryptographicInstanceID"] = "UA-2";
        Assert.NotNull(credentials.Issue(tampered, BoutCredentials.UserAgent).Reason);
        Assert.Null(credentials.KeyFor("UA-1"));
    }

    // ------------------------------------------------------------------ a key that answers only asynchronously, as the browser's Web Crypto does
    // .NET's ECDSA behind an await that does not complete at once: the Async methods of the library are used, and the synchronous ones refuse the key.
    public sealed class AsyncOnlyKey(ECDsa key) : BoutKey
    {
        public override byte[] Sign(byte[] data) => throw new InvalidOperationException("asynchronous only");
        public override bool Verify(byte[] data, byte[] signature) => throw new InvalidOperationException("asynchronous only");
        public override byte[] Spki() => key.ExportSubjectPublicKeyInfo();
        public override async ValueTask<byte[]> SignAsync(byte[] data) { await Task.Yield(); return key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation); }
        public override async ValueTask<bool> VerifyAsync(byte[] data, byte[] signature)
        {
            await Task.Yield();
            return signature.Length == 64 && key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
    }

    [Fact]
    public async Task AsyncKey_AWholeFlowSignedAndVerifiedAsynchronously()
    {
        var now = DateTimeOffset.Parse("2026-11-11T10:00:00+00:00");
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var key = new AsyncOnlyKey(ecdsa);
        var giver = new BoutGiver(new BoutGiver.Options
        {
            DataType = "OSD-OSA-V1.5", PortNumber = 1, BoutId = 60001, SigningMode = BoutSecurity.Chain, ChainInterval = 2,
            Key = key, KeyId = "RCA-1", Issuer = "urn:mpai:rca:RCA-1", SourceAimId = "RCA-1", Clock = () => now
        });
        var r = new BoutReceiver(new BoutReceiver.Options { DataType = "OSD-OSA-V1.5", PortNumber = 1, KeyFor = id => id == "RCA-1" ? key : null, Clock = () => now });

        // The synchronous methods refuse a key that answers only asynchronously.
        Assert.Throws<InvalidOperationException>(() => giver.Open(Instance(0)));

        Assert.True((await r.AcceptAsync(await giver.OpenAsync(Instance(0)))).Ok);
        var delivered = 0;
        for (var k = 1; k <= 5; k++) delivered += (await r.ReceiveAsync(await giver.InstanceAsync(Instance(k)))).Delivered.Count;
        delivered += (await r.CloseAsync(await giver.CloseAsync())).Delivered.Count;
        Assert.Equal(5, delivered);

        // The proof made asynchronously is a valid Cryptographic Instance Identity, which a Controller accepts.
        Assert.Equal(PtfIdentity.Outcome.Valid, PtfIdentity.Check(await BoutCredentials.ProofAsync(key, "RCA-1", now), out _));
    }
}
