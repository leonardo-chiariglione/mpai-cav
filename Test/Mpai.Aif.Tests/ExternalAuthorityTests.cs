using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

using AIF.Channels;
using AIF.Controller;
using AIF.Trust;
using Mpai.Aif.Api;

namespace Mpai.Aif.Tests;

// BETWEEN CONTROLLERS (Basic API 3.9; Communication 4, 7, 9). A Controller made the Trust
// Authority of a service admits Controllers under a policy; Controllers that were told to trust
// it join at its rendezvous; the hubs of their Modules link through it; and a payload written by
// reference travels apart on the payload channel of the link, checked against the length and the
// hash its signed Message states. Each Module stood in by a queue it reads (its External Output
// Port) and a list it writes (its External Input Port).
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
[Collection(Timing.Name)]
public class ExternalAuthorityTests
{
    private const string Service = "VIDEOCONFERENCE-V1.0", Data = "TST-OBJ-V1.0";

    private static (TrustAnchorKey Anchor, ECDsa Key) NewAnchor(string id)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var t = DateTimeOffset.UtcNow;
        return (new TrustAnchorKey(id, key, t.AddMinutes(-5), t.AddDays(30)), key);
    }

    private static ExternalAuthorityService Authority(string policy, out TrustAnchorKey anchor, string id = "Server-Authority")
    {
        var (a, key) = NewAnchor(id);
        anchor = a;
        return new ExternalAuthorityService(a, key, ExternalPolicy.Parse(policy));
    }

    private sealed class Store : IPayloadSource
    {
        public readonly ConcurrentDictionary<string, byte[]> Held = new();
        public IReadOnlyList<string> ReferencesOf(string json) => PayloadStore.ReferencesIn(json);
        public byte[]? Get(string reference) => Held.TryGetValue(reference, out var data) ? data : null;
        public void Release(string reference) { }
        public string Inline(string json)
        {
            foreach (var r in ReferencesOf(json))
                if (Get(r) is { } data) json = json.Replace($"\"DataURI\":\"{r}\"", $"\"Data\":\"{Convert.ToBase64String(data)}\"");
            return json;
        }
    }

    private sealed class Module : IAsyncDisposable
    {
        public readonly Channel<(string Json, DateTimeOffset Stamp)> Gives = Channel.CreateUnbounded<(string, DateTimeOffset)>();
        public readonly ConcurrentQueue<ExternalMessage> Got = new();
        public readonly Store Payloads = new();
        public readonly ExternalHub Hub;
        public AIF.Trust.TrustTrace? Trace { get; init; }

        public Module(string id, ILinkAdmission admission, IDiscoveryMedium discovery, Func<string, string, string> sign, Func<string, string, string, string?> verify,
                      Func<string, IReadOnlyList<PayloadDescriptor>, string, string> signPayloads, bool directed = false, IReadOnlyList<string>? correlate = null, IReadOnlyList<string>? address = null,
                      Func<string, string?>? stillTrusted = null, Action<string, string, string, string?>? decided = null)
        {
            Hub = new ExternalHub(new ExternalHub.Options
            {
                ControllerId = id, ModuleType = Service, Discovery = discovery, Admission = admission,
                AnnounceEvery = TimeSpan.FromMilliseconds(100), LeaveAfter = TimeSpan.FromSeconds(2), DirectedReplies = directed, CorrelateBy = correlate ?? [], AddressedBy = address ?? [],
                StillTrusted = stillTrusted, Decided = decided
            })
            {
                Outputs = [(Data, 1)],
                ReadOutput = async (_, _, timeout) => await Next(timeout) is { } m ? (Payloads.Inline(m.Json), m.Stamp) : null,
                ReadOutputApart = async (_, _, timeout) => await Next(timeout) is { } m ? (m.Json, m.Stamp) : null,
                PayloadSource = Payloads,
                SignPayloads = signPayloads,
                Deliver = m => { Got.Enqueue(m); return Task.CompletedTask; },
                Sign = sign, Verify = verify
            };
            Hub.Start();
        }

        private async Task<(string Json, DateTimeOffset Stamp)?> Next(int timeout)
        {
            using var cancel = new CancellationTokenSource(timeout);
            try { return await Gives.Reader.ReadAsync(cancel.Token); } catch (OperationCanceledException) { return null; }
        }

        public ValueTask DisposeAsync() => Hub.DisposeAsync();
    }

    private static async Task<bool> Until(Func<bool> condition, int ms = 8000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition()) { if (clock.ElapsedMilliseconds > ms) return false; await Task.Delay(20); }
        return true;
    }

    // The Authority's own Controller as a member of its service.
    private static Module Host(ExternalAuthorityService service, TrustAnchorKey anchor, ECDsa key, string id = "host")
    {
        var (credential, discovery) = service.AdmitSelf(id);
        var trust = new ExternalTrust(anchor, key, credential, anchor.Object(), service.Policy.AuthorityRole, service.Policy);
        return new Module(anchor.AnchorId, trust.Admission, discovery, trust.Sign, trust.Verify, trust.SignPayloads, service.Policy.Directed, service.Policy.Correlate, service.Policy.Address,
                          trust.StillTrusted, trust.Record) { Trace = trust.Trace };
    }

    private static Module Guest(ExternalMember member) =>
        new(member.Trust.ControllerId, member.Trust.Admission, member.Discovery, member.Trust.Sign, member.Trust.Verify, member.Trust.SignPayloads);

    private const string Open = """{ "Roles": ["Host", "Participant"], "DefaultRole": "Participant", "AuthorityRole": "Host", "Exchange": [["Host", "Participant"]] }""";

    private const string Directed = """{ "Roles": ["Host", "Participant"], "DefaultRole": "Participant", "AuthorityRole": "Host", "Exchange": [["Host", "Participant"]], "Directed": true, "Correlate": ["ObjectID"], "Address": ["HFEID"] }""";

    // A service that answers many clients (a Health Back End): a Message its Module gives goes only to the Controller
    // that sent the Message it answers - found by an identifier the two share - and to a Controller it names by its
    // controllerID; one that concerns no Controller linked is not sent, and counted.
    [Fact]
    public async Task ARepliesGoOnlyToTheControllerTheyAnswer()
    {
        var (anchor, key) = NewAnchor("Server-Authority");
        await using var service = new ExternalAuthorityService(anchor, key, ExternalPolicy.Parse(Directed));
        await using var host = Host(service, anchor, key);
        await using var memberA = await ExternalMember.JoinAsync("front-end-a", anchor.Object(), service.Rendezvous);
        await using var memberB = await ExternalMember.JoinAsync("front-end-b", anchor.Object(), service.Rendezvous);
        await using var a = Guest(memberA);
        await using var b = Guest(memberB);
        Assert.True(await Until(() => host.Hub.Linked.Count == 2 && a.Hub.Linked.Count == 1 && b.Hub.Linked.Count == 1));

        // A asks; the Host's Module answers with the identifier of the request: only A hears it.
        await a.Gives.Writer.WriteAsync((Obj("request-a"), DateTimeOffset.UtcNow));
        Assert.True(await Until(() => !host.Got.IsEmpty));
        await host.Gives.Writer.WriteAsync((Obj("request-a"), DateTimeOffset.UtcNow));
        Assert.True(await Until(() => !a.Got.IsEmpty), "A heard the answer");

        // The Host's Module names B by its controllerID: only B hears it.
        await host.Gives.Writer.WriteAsync((JsonSerializer.Serialize(new { Header = Data, ObjectID = "learn", HFEID = "front-end-b" }), DateTimeOffset.UtcNow));
        Assert.True(await Until(() => !b.Got.IsEmpty), "B heard what named it");

        // A Message that concerns no one is not sent.
        await host.Gives.Writer.WriteAsync((Obj("nobody"), DateTimeOffset.UtcNow));
        Assert.True(await Until(() => host.Hub.Undirected == 1), "counted, not sent");
        await Task.Delay(300);
        Assert.Single(a.Got);
        Assert.Single(b.Got);
        Assert.Contains("request-a", a.Got.First().Json);
        Assert.Contains("learn", b.Got.First().Json);
        Assert.Equal(0, a.Hub.Refused + b.Hub.Refused);
    }

    private static string Obj(string id) => JsonSerializer.Serialize(new { Header = Data, ObjectID = id });

    // The Authority proves that it holds the anchor; a Controller it admits is given a credential
    // and links, through the rendezvous, to the Authority's Controller and to no Participant, as
    // the policy says; what is exchanged is signed and verified.
    [Fact]
    public async Task AControllerJoinsAServiceAndExchanges()
    {
        var (anchor, key) = NewAnchor("Server-Authority");
        await using var service = new ExternalAuthorityService(anchor, key, ExternalPolicy.Parse(Open));
        await using var host = Host(service, anchor, key);
        await using var memberA = await ExternalMember.JoinAsync("guest-a", anchor.Object(), service.Rendezvous);
        await using var memberB = await ExternalMember.JoinAsync("guest-b", anchor.Object(), service.Rendezvous);
        await using var a = Guest(memberA);
        await using var b = Guest(memberB);

        Assert.Equal("Participant", memberA.Trust.Role);
        Assert.True(await Until(() => host.Hub.Linked.Count == 2 && a.Hub.Linked.Count == 1 && b.Hub.Linked.Count == 1), "the Host and each Participant linked");
        Assert.DoesNotContain("guest-b", a.Hub.Linked);                         // Participant with Participant: the policy lets them not exchange

        await a.Gives.Writer.WriteAsync((Obj("from-a"), DateTimeOffset.UtcNow));
        Assert.True(await Until(() => !host.Got.IsEmpty), "the Host received it");
        host.Got.TryPeek(out var got);
        Assert.Equal(memberA.Trust.ControllerId, got!.From);
        Assert.Contains("from-a", got.Json);
        Assert.Equal(0, host.Hub.Refused);
        Assert.True(b.Got.IsEmpty);
    }

    // The anchor the User decided to trust is not the one the rendezvous proves: NOT_TRUSTED.
    [Fact]
    public async Task AnAuthorityThatDoesNotHoldTheAnchorIsNotTrusted()
    {
        await using var real = Authority(Open, out var realAnchor);
        var (other, _) = NewAnchor("Another-Authority");
        var e = await Assert.ThrowsAsync<ExternalJoinException>(() => ExternalMember.JoinAsync("guest", other.Object(), real.Rendezvous));
        Assert.Equal(AifErrorKind.NotTrusted, e.Kind);

        // The same name, not the same key.
        var (impostorAnchor, _) = NewAnchor("Server-Authority");
        var e2 = await Assert.ThrowsAsync<ExternalJoinException>(() => ExternalMember.JoinAsync("guest", impostorAnchor.Object(), real.Rendezvous));
        Assert.Equal(AifErrorKind.NotTrusted, e2.Kind);
    }

    // A role the service does not admit: NOT_AUTHORISED.
    [Fact]
    public async Task ARoleTheServiceDoesNotAdmitIsRefused()
    {
        await using var service = Authority(Open, out var anchor);
        var e = await Assert.ThrowsAsync<ExternalJoinException>(() => ExternalMember.JoinAsync("guest", anchor.Object(), service.Rendezvous, role: "Moderator"));
        Assert.Equal(AifErrorKind.NotAuthorised, e.Kind);
        Assert.Empty(service.Members);
    }

    // A role that requires attestation, from a Controller with none: NOT_AUTHORISED, and the
    // reason.
    [Fact]
    public async Task ARoleThatRequiresAttestationIsRefusedWithoutIt()
    {
        var (anchor, key) = NewAnchor("Server-Authority");
        var policy = ExternalPolicy.Parse("""{ "Roles": ["Participant"], "DefaultRole": "Participant", "Attest": ["Participant"] }""");
        await using var service = new ExternalAuthorityService(anchor, key, policy,
            attestation: new Attestation.Policy(SimulatedManufacturer.Make().Root, _ => null));
        var e = await Assert.ThrowsAsync<ExternalJoinException>(() => ExternalMember.JoinAsync("guest", anchor.Object(), service.Rendezvous));
        Assert.Equal(AifErrorKind.NotAuthorised, e.Kind);
        Assert.Contains("Attestation", e.Message);
    }

    // A payload written by reference travels apart, on the payload channel, and arrives intact; the
    // Message that carries its reference states its length and hash, signed.
    [Fact]
    public async Task APayloadTravelsApartOnThePayloadChannel()
    {
        var (anchor, key) = NewAnchor("Server-Authority");
        await using var service = new ExternalAuthorityService(anchor, key, ExternalPolicy.Parse(Open));
        await using var host = Host(service, anchor, key);
        await using var member = await ExternalMember.JoinAsync("guest", anchor.Object(), service.Rendezvous);
        await using var guest = Guest(member);
        Assert.True(await Until(() => guest.Hub.Linked.Count == 1 && guest.Hub.PayloadLinked.Count == 1 && host.Hub.PayloadLinked.Count == 1), "linked with a payload channel");

        var data = RandomNumberGenerator.GetBytes(3 * 1024 * 1024);
        const string reference = "aif:payload/TST/1";
        guest.Payloads.Held[reference] = data;
        await guest.Gives.Writer.WriteAsync(($$"""{"Header":"{{Data}}","ObjectID":"big","Entries":[{"DataLength":{{data.Length}},"DataURI":"{{reference}}"}]}""", DateTimeOffset.UtcNow));

        Assert.True(await Until(() => !host.Got.IsEmpty), "the Host received it");
        host.Got.TryPeek(out var got);
        Assert.True(got!.Json.Contains(reference), got.Json[..Math.Min(600, got.Json.Length)]);                                  // by reference: the bytes did not travel in the Message
        Assert.True(got.Json.Length < 4096);
        var stated = Assert.Single(got.Payloads!);
        Assert.Equal(data.Length, stated.Length);
        var fetched = got.Fetch!(stated);
        Assert.Equal(data, fetched);
        Assert.Equal(1, guest.Hub.Fetched);
        Assert.Equal(0, host.Hub.Refused);
    }

    // A payload that is not the one the signed Message states is refused and counted.
    [Fact]
    public async Task APayloadThatDoesNotMatchItsMessageIsRefused()
    {
        var (anchor, key) = NewAnchor("Server-Authority");
        await using var service = new ExternalAuthorityService(anchor, key, ExternalPolicy.Parse(Open));
        await using var host = Host(service, anchor, key);
        await using var member = await ExternalMember.JoinAsync("guest", anchor.Object(), service.Rendezvous);
        // This guest states the wrong hash for what it offers.
        await using var guest = new Module(member.Trust.ControllerId, member.Trust.Admission, member.Discovery, member.Trust.Sign, member.Trust.Verify,
            (json, payloads, binding) => member.Trust.SignPayloads(json, payloads.Select(p => p with { Hash = new string('0', 64) }).ToList(), binding));
        Assert.True(await Until(() => guest.Hub.PayloadLinked.Count == 1 && host.Hub.PayloadLinked.Count == 1));

        guest.Payloads.Held["aif:payload/TST/7"] = [1, 2, 3, 4];
        await guest.Gives.Writer.WriteAsync(($$"""{"Header":"{{Data}}","ObjectID":"lie","Entries":[{"DataLength":4,"DataURI":"aif:payload/TST/7"}]}""", DateTimeOffset.UtcNow));

        Assert.True(await Until(() => !host.Got.IsEmpty));
        host.Got.TryPeek(out var got);
        Assert.Null(got!.Fetch!(got.Payloads![0]));
        Assert.Equal(1, host.Hub.Refused);
    }

    // A Controller with no payload channel (the other end opened none) is given the data in the
    // Message.
    [Fact]
    public async Task WithoutAPayloadChannelThePayloadIsInlined()
    {
        var (anchor, key) = NewAnchor("Server-Authority");
        await using var service = new ExternalAuthorityService(anchor, key, ExternalPolicy.Parse(Open));
        await using var host = Host(service, anchor, key);
        await using var member = await ExternalMember.JoinAsync("guest", anchor.Object(), service.Rendezvous);
        // A hub with no ReadOutputApart opens no payload channel and sends the data inline.
        var inline = new ConcurrentQueue<ExternalMessage>();
        var gives = Channel.CreateUnbounded<(string, DateTimeOffset)>();
        var store = new Store();
        await using var guest = new ExternalHub(new ExternalHub.Options
        {
            ControllerId = member.Trust.ControllerId, ModuleType = Service, Discovery = member.Discovery, Admission = member.Trust.Admission,
            AnnounceEvery = TimeSpan.FromMilliseconds(100)
        })
        {
            Outputs = [(Data, 1)],
            ReadOutput = async (_, _, timeout) =>
            {
                using var cancel = new CancellationTokenSource(timeout);
                try { var m = await gives.Reader.ReadAsync(cancel.Token); return (store.Inline(m.Item1), m.Item2); } catch (OperationCanceledException) { return null; }
            },
            Deliver = m => { inline.Enqueue(m); return Task.CompletedTask; },
            Sign = member.Trust.Sign, Verify = member.Trust.Verify
        };
        guest.Start();
        Assert.True(await Until(() => guest.Linked.Count == 1));
        Assert.Empty(guest.PayloadLinked);

        store.Held["aif:payload/TST/9"] = [9, 9, 9];
        await gives.Writer.WriteAsync(($$"""{"Header":"{{Data}}","ObjectID":"inl","Entries":[{"DataLength":3,"DataURI":"aif:payload/TST/9"}]}""", DateTimeOffset.UtcNow));
        Assert.True(await Until(() => !host.Got.IsEmpty));
        host.Got.TryPeek(out var got);
        Assert.Null(got!.Payloads);
        Assert.Contains(Convert.ToBase64String([9, 9, 9]), got.Json);
    }
    // THE CONTROLLER API (Basic API 3.9): MPAI_AIFU_External_Authority makes one Controller the Authority
    // of a service, MPAI_AIFU_External_Join joins another to it, on the Modules they run - TST-PAY, whose
    // Input and Payload Ports are External. The Authority publishes its anchor and its rendezvous. What the
    // Module of one writes by reference reaches the other apart, on the payload channel, and is resolved
    // there when its reader asks; a Controller told to trust another anchor does not join (NOT_TRUSTED);
    // the links close at Leave.
    [Fact]
    public async Task TheControllerApiAdmitsJoinsAndExchangesApart()
    {
        const string module = "1TST-PAY-V1.0-I01";
        var dir = Path.Combine(Path.GetTempPath(), "mpai-external-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.EnumerateFiles(ContinuousTests.Amds, "*.json"))
            File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        var amd = Path.Combine(dir, module + ".json");
        File.WriteAllText(amd, File.ReadAllText(amd)
            .Replace("\"Name\": \"Input\",   \"Direction\": \"Input\",  \"DataType\": \"TST-TXT-V1.0\",                  \"Technology\": \"Software\", \"Protocol\": \"\", \"IsRemote\": false",
                     "\"Name\": \"Input\",   \"Direction\": \"Input\",  \"DataType\": \"TST-TXT-V1.0\",                  \"Technology\": \"Software\", \"Protocol\": \"\", \"IsRemote\": true")
            .Replace("\"PortNumber\": 1, \"Technology\": \"Software\", \"Protocol\": \"\", \"IsRemote\": false", "\"PortNumber\": 1, \"Technology\": \"Software\", \"Protocol\": \"\", \"IsRemote\": true"));
        ControllerApi Make() => new(dir, Path.Combine(dir, "no-settings.json"), new ContinuousAims());
        using var server = Make();
        using var client = Make();
        using var stranger = Make();
        foreach (var api in new[] { server, client, stranger }) Assert.Equal(AifError.OK, api.StartFlow(module));

        Assert.Equal(AifError.OK, server.ExternalAuthority(module, Open));
        var anchor = server.ExternalAnchor(module)!;
        var rendezvous = server.ExternalRendezvous(module)!;

        // A Controller told to trust another anchor.
        var (other, _) = NewAnchor("Another-Authority");
        Assert.Equal(AifError.NotTrusted, stranger.ExternalJoin(module, other.Object().ToJsonString(), rendezvous));
        Assert.Contains("does not prove", stranger.ExternalError);
        // A role the service does not admit.
        Assert.Equal(AifError.NotAuthorised, stranger.ExternalJoin(module, anchor, rendezvous, role: "Moderator"));

        Assert.Equal(AifError.OK, client.ExternalJoin(module, anchor, rendezvous));
        var hubServer = Assert.Single(server.ExternalHubsOf(module));
        var hubClient = Assert.Single(client.ExternalHubsOf(module));
        Assert.True(await Until(() => hubServer.Linked.Count == 1 && hubClient.Linked.Count == 1 && hubServer.PayloadLinked.Count == 1 && hubClient.PayloadLinked.Count == 1),
                    "linked, with a payload channel");

        // The client's Module writes a payload by reference; it reaches the server apart, and the server's reader resolves it.
        Assert.Equal(AifError.OK, client.InputWrite(module, ContinuousTests.Text, 1, "hello", 2000));
        Assert.True(await Until(() => hubServer.Received >= 1 && hubClient.Fetched >= 1), "the payload was fetched on the payload channel");
        Assert.Equal(0, hubServer.Refused);
        Assert.Equal(0, hubClient.Refused);

        // Leave closes the links and discards the credential.
        Assert.Equal(AifError.OK, client.ExternalLeave(module));
        Assert.True(await Until(() => hubServer.Linked.Count == 0), "the server's link closed");
        foreach (var api in new[] { server, client, stranger }) api.StopFlow(module);
        Directory.Delete(dir, true);
    }

    // THE EXTERNAL PORTS AS AN AIM OF A CONTROLLER CALLS THEM (Basic API 5): the Controllers linked and the Metadata of their Modules (External_List), a probe, a write to an External Input Port and a
    // read of an External Output Port of an AIM of the Module of the other Controller, by controllerID, AIM_name, Data Type and Port Number. What is read carries the stamp of the Controller that wrote
    // it and its signature; a Port that is not an External Port of an AIM of the Module is not reached.
    [Fact]
    public async Task AnAimWritesReadsAndProbesTheExternalPortsOfAnotherController()
    {
        const string module = "1TST-PAY-V1.0-I01";
        var dir = Path.Combine(Path.GetTempPath(), "mpai-external-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.EnumerateFiles(ContinuousTests.Amds, "*.json"))
            File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        var amd = Path.Combine(dir, module + ".json");
        File.WriteAllText(amd, File.ReadAllText(amd)
            .Replace("\"Name\": \"Input\",   \"Direction\": \"Input\",  \"DataType\": \"TST-TXT-V1.0\",                  \"Technology\": \"Software\", \"Protocol\": \"\", \"IsRemote\": false",
                     "\"Name\": \"Input\",   \"Direction\": \"Input\",  \"DataType\": \"TST-TXT-V1.0\",                  \"Technology\": \"Software\", \"Protocol\": \"\", \"IsRemote\": true")
            .Replace("\"PortNumber\": 1, \"Technology\": \"Software\", \"Protocol\": \"\", \"IsRemote\": false", "\"PortNumber\": 1, \"Technology\": \"Software\", \"Protocol\": \"\", \"IsRemote\": true"));
        ControllerApi Make() => new(dir, Path.Combine(dir, "no-settings.json"), new ContinuousAims()) { ExternalPull = true };
        using var server = Make();
        using var client = Make();
        foreach (var api in new[] { server, client }) Assert.Equal(AifError.OK, api.StartFlow(module));
        Assert.Equal(AifError.OK, server.ExternalAuthority(module, Open));
        Assert.Equal(AifError.OK, client.ExternalJoin(module, server.ExternalAnchor(module)!, server.ExternalRendezvous(module)!));
        var toServer = Assert.Single(client.ExternalHubsOf(module));
        var atServer = Assert.Single(server.ExternalHubsOf(module));
        Assert.True(await Until(() => toServer.List().Count == 1 && atServer.List().Count == 1 && toServer.List()[0].Metadata.Length > 0));

        // External_List: the Controllers linked, with the Metadata of their Modules; the controllerID is the link's, and the Messages carry the Controller's name.
        var peer = toServer.List()[0];
        Assert.True(peer.ControllerID > 0);
        Assert.Contains(module, peer.Metadata);

        var said = new ConcurrentQueue<string>();
        toServer.Said += said.Enqueue;
        atServer.Said += said.Enqueue;
        var text = ContinuousTests.Text;
        Assert.Equal(ExternalOutcome.Ok, await toServer.ProbeAsync(peer.ControllerID, module, text, 1, 500));                        // an Input Port an AIM can write to
        Assert.Equal(ExternalOutcome.NoSuchPort, await toServer.ProbeAsync(peer.ControllerID, "1NOT-AN-AIM-V1.0-I01", text, 1, 500));
        Assert.Equal(ExternalOutcome.NoSuchPort, await toServer.ProbeAsync(peer.ControllerID, module, text, 9, 500));
        Assert.Equal(ExternalOutcome.NoSuchController, await toServer.ProbeAsync(peer.ControllerID + 1000, module, text, 1, 500));

        // Nothing is produced yet: the Output Port gives nothing within the time.
        var empty = await toServer.ReadAsync(peer.ControllerID, module, text, 1, 100);
        Assert.Equal(ExternalOutcome.NotProduced, empty.Outcome);

        // A write to an External Input Port of the Module of the other Controller; its AIM answers on its External Output Port, which is read from here.
        Assert.Equal(ExternalOutcome.NoSuchPort, await toServer.WriteAsync(peer.ControllerID, "1NOT-AN-AIM-V1.0-I01", text, 1, ContinuousAims.Entry(System.Text.Encoding.UTF8.GetBytes("hello"), null, 5), 1000));
        Assert.Equal(ExternalOutcome.Ok, await toServer.WriteAsync(peer.ControllerID, module, text, 1, ContinuousAims.Entry(System.Text.Encoding.UTF8.GetBytes("hello"), null, 5), 2000));
        var read = await toServer.ReadAsync(peer.ControllerID, module, text, 1, 5000);
        Assert.Equal(ExternalOutcome.Ok, read.Outcome);
        Assert.False(string.IsNullOrEmpty(read.Json));
        Assert.True((DateTimeOffset.UtcNow - read.Stamp).Duration() < TimeSpan.FromMinutes(1));               // the stamp of the Controller that wrote it
        Assert.True(atServer.Refused + toServer.Refused == 0, string.Join("; ", said));
        Assert.Equal(1, server.ExternalDropped(module));                                                       // the write to an AIM the Module does not have: dropped, and counted

        // The link closes, and so does the controllerID: nothing is reached through it.
        Assert.Equal(AifError.OK, client.ExternalLeave(module));
        Assert.Equal(ExternalOutcome.NoSuchController, await toServer.ProbeAsync(peer.ControllerID, module, text, 1, 500));
        foreach (var api in new[] { server, client }) api.StopFlow(module);
        Directory.Delete(dir, true);
    }

    // MPAI_AIFU_External_Join with rendezvous NULL (Basic API 3.9.1): the Controller presents itself by announcement in range - the Authority named by the anchor says where it admits - and is
    // admitted as at a rendezvous; where the Authority of that anchor announces nothing in range, the Join fails and says why.
    [Fact]
    public async Task AControllerJoinsByAnnouncementInRange()
    {
        const string module = "1TST-PAY-V1.0-I01";
        var dir = Path.Combine(Path.GetTempPath(), "mpai-external-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.EnumerateFiles(ContinuousTests.Amds, "*.json"))
            File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        var amd = Path.Combine(dir, module + ".json");
        File.WriteAllText(amd, File.ReadAllText(amd)
            .Replace("\"Name\": \"Input\",   \"Direction\": \"Input\",  \"DataType\": \"TST-TXT-V1.0\",                  \"Technology\": \"Software\", \"Protocol\": \"\", \"IsRemote\": false",
                     "\"Name\": \"Input\",   \"Direction\": \"Input\",  \"DataType\": \"TST-TXT-V1.0\",                  \"Technology\": \"Software\", \"Protocol\": \"\", \"IsRemote\": true")
            .Replace("\"PortNumber\": 1, \"Technology\": \"Software\", \"Protocol\": \"\", \"IsRemote\": false", "\"PortNumber\": 1, \"Technology\": \"Software\", \"Protocol\": \"\", \"IsRemote\": true"));
        var port = 41100 + Random.Shared.Next(800);
        ControllerApi Make() => new(dir, Path.Combine(dir, "no-settings.json"), new ContinuousAims()) { ExternalAnnouncementPort = port, ExternalAnnouncementWait = TimeSpan.FromSeconds(3) };
        using var server = Make();
        using var client = Make();
        using var stranger = Make();
        foreach (var api in new[] { server, client, stranger }) Assert.Equal(AifError.OK, api.StartFlow(module));

        Assert.Equal(AifError.OK, server.ExternalAuthority(module, Open));
        var anchor = server.ExternalAnchor(module)!;

        var (other, _) = NewAnchor("Another-Authority");
        stranger.ExternalAnnouncementWait = TimeSpan.FromSeconds(1);
        Assert.Equal(AifError.Failed, stranger.ExternalJoin(module, other.Object().ToJsonString(), null));      // nothing of that Authority is announced in range
        Assert.Contains("announcement", stranger.ExternalError);

        var joined = client.ExternalJoin(module, anchor, null);
        Assert.True(joined == AifError.OK, client.ExternalError);
        var hubServer = Assert.Single(server.ExternalHubsOf(module));
        var hubClient = Assert.Single(client.ExternalHubsOf(module));
        Assert.True(await Until(() => hubServer.Linked.Count == 1 && hubClient.Linked.Count == 1), "linked");

        foreach (var api in new[] { server, client, stranger }) api.StopFlow(module);
        Directory.Delete(dir, true);
    }

    // TRUST IS NOT KEPT FROM THE OPENING OF A LINK (Zero Trust: no caching of trust; Communication 9: a link lasts until the credential expires): a Controller whose credential has
    // expired is no longer linked, and the decision is in the Trace of the Controller that made it, with the admissions in the Trace of the Authority.
    [Fact]
    public async Task ALinkEndsWhenTheCredentialOfTheControllerItAdmittedExpiresAndTheDecisionsAreInTheTrace()
    {
        var (anchor, key) = NewAnchor("Server-Authority");
        await using var service = new ExternalAuthorityService(anchor, key, ExternalPolicy.Parse("""{ "Roles": ["Host", "Participant"], "DefaultRole": "Participant", "AuthorityRole": "Host", "Exchange": [["Host", "Participant"]], "LifetimeMinutes": 0.05 }"""));
        await using var host = Host(service, anchor, key);
        await using var member = await ExternalMember.JoinAsync("guest", anchor.Object(), service.Rendezvous);
        await using var guest = Guest(member);
        Assert.True(await Until(() => host.Hub.PayloadLinked.Count == 1 && guest.Hub.PayloadLinked.Count == 1));    // linked while the credential holds (3 s)

        Assert.True(await Until(() => host.Hub.ClosedOnExpiry >= 1, 20000));                                          // then the link ends: the credential of the guest has expired
        Assert.True(await Until(() => host.Hub.PayloadLinked.Count == 0));

        var closed = host.Trace!.Records.Select(r => r["TrustOperation"]!.AsObject()).Where(o => (string?)o["OperationType"] == "CloseLink").ToList();
        Assert.Single(closed);
        Assert.Contains("expired", (string?)closed[0]["FailureReason"]);
        Assert.Null(AIF.Trust.TrustTrace.Verify(host.Trace.Records, anchor.Object()));                                // the Trace is a signed, hash-chained record
        var admitted = service.Trace.Records.Select(r => r["TrustOperation"]!.AsObject()).Where(o => (string?)o["OperationType"] == "AdmitController").ToList();
        Assert.Contains(admitted, o => (string?)o["TargetID"] == "guest" && (string?)o["Status"] == "Success");
        Assert.Null(AIF.Trust.TrustTrace.Verify(service.Trace.Records, anchor.Object()));
    }

    // A controllerID is not reused for another Controller within one run (Basic API 5): the one that left may come back with its key, another may not take the id.
    [Fact]
    public async Task AControllerIdIsNotTakenByAnotherControllerAfterItsOwnerLeft()
    {
        var (anchor, key) = NewAnchor("Server-Authority");
        await using var service = new ExternalAuthorityService(anchor, key, ExternalPolicy.Parse(Open));
        var first = await ExternalMember.JoinAsync("guest", anchor.Object(), service.Rendezvous);
        await first.DisposeAsync();
        Assert.True(await Until(() => !service.Members.Contains("guest")));
        var other = await Assert.ThrowsAsync<ExternalJoinException>(() => ExternalMember.JoinAsync("guest", anchor.Object(), service.Rendezvous));   // a new key under the same id
        Assert.Equal(AifErrorKind.NotAuthorised, other.Kind);
        Assert.Contains("admitted before", other.Message);
        Assert.Contains(service.Trace.Records.Select(r => r["TrustOperation"]!.AsObject()), o => (string?)o["OperationType"] == "RefuseController" && (string?)o["TargetID"] == "guest");
    }
}
