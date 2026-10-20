using System.Text.Json.Nodes;
using Mpai.Aih;
using Mpai.Aih.Hsp;

namespace Mpai.Aif.Tests;

// THE SECURITY MODEL OF AIH-HSP is that of AIF (MPAI-PTF): the Health Back End is the Trust Authority; the Front Ends and the Third-Party Users it admits trust it
// and it trusts them, and nobody else; trust is established before data is exchanged; what is exchanged is signed.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class AihTrustTests
{
    private sealed class Stub : IBackEnd
    {
        public List<string> Heard { get; } = [];
        public string Register(string r) { Heard.Add("Register"); return AihSamples.RegisterResponse("user-1").ToJsonString(); }
        public string Process(string r) { Heard.Add("Process"); return AihJson.ProcessResponse("user-1", ["ahd-1"], true).ToJsonString(); }
        public string DeIdentify(string r) { Heard.Add("DeIdentify"); return "{}"; }
        public string Audit(string r) { Heard.Add("Audit"); return "{}"; }
    }

    private static string Request(string user) => new JsonObject
    {
        ["Header"] = "AIH-AHP-V1.1", ["UserID"] = user, ["AIHDataProcessID"] = "ahp-1", ["AIHDataProcessTime"] = AihSamples.Time("t"),
        ["AIHDataProcess"] = new JsonArray(new JsonObject { ["Request"] = new JsonObject { ["AIHDataOrAIHDataID"] = new JsonArray("ahd-1"), ["ProcessingType"] = "Store", ["Tokens"] = AihSamples.Tokens(user) } })
    }.ToJsonString();

    [Fact]
    public void AnAdmittedPartyIsHeardAndAnsweredWithASignedAnswer()
    {
        var trust = new BackEndTrust(); var inner = new Stub();
        var party = trust.Enroll("hfe:user-1", "user-1"); party.Establish();
        var client = new TrustedClient(party, new TrustedBackEnd(inner, trust));

        var answer = client.Process(Request("user-1"));
        Assert.Equal(["Process"], inner.Heard);
        Assert.NotNull(JsonNode.Parse(answer)!["DataXMData"]!["Security"]!["Integrity"]!["Signature"]);        // signed by the Back End, and verified by the party
    }

    [Fact]
    public void NothingIsExchangedBeforeTrustIsEstablished()
    {
        var trust = new BackEndTrust(); var inner = new Stub();
        var party = trust.Enroll("hfe:user-1", "user-1");                                         // enrolled, but trust not established
        var client = new TrustedClient(party, new TrustedBackEnd(inner, trust));
        Assert.Throws<UnauthorizedAccessException>(() => client.Process(Request("user-1")));
        // and a signed message of a party the Back End did not admit by the protocol is refused at its door
        var door = new TrustedBackEnd(inner, trust);
        var signed = SignedBy(party, Request("user-1"));
        Assert.Throws<UnauthorizedAccessException>(() => door.Process(signed));
        Assert.Empty(inner.Heard);
    }

    private static string SignedBy(PartyTrust party, string json) =>
        (string)typeof(PartyTrust).GetMethod("Sign", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(party, [json])!;

    [Fact]
    public void WhatIsNotSignedOrIsChangedIsRefused()
    {
        var trust = new BackEndTrust(); var inner = new Stub();
        var party = trust.Enroll("hfe:user-1", "user-1"); party.Establish();
        var door = new TrustedBackEnd(inner, trust);

        Assert.Throws<UnauthorizedAccessException>(() => door.Process(Request("user-1")));                 // unsigned
        var signed = SignedBy(party, Request("user-1"));
        var changed = JsonNode.Parse(signed)!.AsObject();
        changed["AIHDataProcessID"] = "ahp-2";                                                              // changed after signing
        Assert.Throws<UnauthorizedAccessException>(() => door.Process(changed.ToJsonString()));
        Assert.Empty(inner.Heard);
        door.Process(signed);                                                                               // as signed, it is heard
        Assert.Equal(["Process"], inner.Heard);
    }

    [Fact]
    public void APartySpeaksOnlyForItsSubject()
    {
        var trust = new BackEndTrust(); var inner = new Stub();
        var party = trust.Enroll("hfe:user-1", "user-1"); party.Establish();
        var client = new TrustedClient(party, new TrustedBackEnd(inner, trust));
        Assert.Throws<UnauthorizedAccessException>(() => client.Process(Request("user-2")));               // signed, but for another End User
        Assert.Empty(inner.Heard);
    }

    [Fact]
    public void ThePartiesAdmittedByAnotherAuthorityAreNotTrusted()
    {
        var trust = new BackEndTrust("hbe-1"); var other = new BackEndTrust("hbe-2"); var inner = new Stub();
        var stranger = other.Enroll("hfe:user-1", "user-1"); stranger.Establish();                       // admitted by another Back End
        var door = new TrustedBackEnd(inner, trust);
        Assert.Throws<UnauthorizedAccessException>(() => door.Process(SignedBy(stranger, Request("user-1"))));
        Assert.Empty(inner.Heard);
    }

    [Fact]
    public void ABackEndAnswerSignedByAnotherIsNotTrustedByTheParty()
    {
        var trust = new BackEndTrust("hbe-1"); var impostor = new BackEndTrust("hbe-1");                  // the same name, another authority
        var party = trust.Enroll("hfe:user-1", "user-1"); party.Establish();
        var inner = new Stub();
        var client = new TrustedClient(party, new SwappedAnswers(new TrustedBackEnd(inner, trust), impostor));
        Assert.Throws<UnauthorizedAccessException>(() => client.Process(Request("user-1")));
    }

    // A door whose answers are signed by someone else: what an impostor would send.
    private sealed class SwappedAnswers(IBackEnd door, BackEndTrust impostor) : IBackEnd
    {
        private string Swap(string signedAnswer)
        {
            var bare = JsonNode.Parse(signedAnswer)!.AsObject(); bare.Remove("DataXMData");
            return (string)typeof(BackEndTrust).GetMethod("Sign", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(impostor, [bare.ToJsonString()])!;
        }
        public string Register(string r) => Swap(door.Register(r));
        public string Process(string r) => Swap(door.Process(r));
        public string DeIdentify(string r) => Swap(door.DeIdentify(r));
        public string Audit(string r) => Swap(door.Audit(r));
    }
}
