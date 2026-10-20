using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

using AIF.Trust;

namespace Mpai.Aih.Hsp;

// THE SECURITY MODEL OF AIH-HSP IS THAT OF AIF (MPAI-PTF V1.0, used by MPAI-AIF V3.0): trust is established between the parties before they exchange data, and the
// data they exchange carries its Data Exchange Metadata - who sent it, and a signature over it. The Health Back End is the TRUST AUTHORITY: a Front End, or a
// Third-Party User, is admitted by it - given an anchor of its own and a credential the Back End issues - and the Back End trusts what the parties it admitted send,
// and they trust what the Back End sends. A Front End is Zero Trust: it trusts the Back End and nothing else, and it never speaks to another Front End (there is no
// way to: it holds the Back End's Trust Anchor and no other party's).
//
// A party is admitted with the subject it speaks for (an End User's Front End, its End User; a Third-Party User, itself): a request that concerns another subject
// is refused, though it is signed.
public sealed class BackEndTrust
{
    private readonly TrustAuthority authority;
    private readonly TrustAuthority.Admission self;
    private readonly TrustProtocol own;
    private readonly ConcurrentDictionary<string, (JsonObject Anchor, string Subject)> admitted = new();
    private readonly ConcurrentDictionary<string, (TrustAuthority.Admission Admission, TrustProtocol Protocol, string Subject)> enrolled = new();

    public string BackEndId { get; }
    public string AuthorityName => authority.City;

    public BackEndTrust(string backEndId = "hbe-1")
    {
        BackEndId = backEndId;
        authority = new TrustAuthority(backEndId);
        self = authority.Admit(backEndId, TimeSpan.FromDays(30));
        own = authority.ProtocolFor(self);
    }

    // A party is ENROLLED with the Back End (outside the exchange of data: a registration of the party's identity), which gives it its anchor, its key and its
    // credential, and the Back End's anchor to trust.
    public PartyTrust Enroll(string partyId, string subject)
    {
        var admission = authority.Admit(partyId, TimeSpan.FromDays(1));
        var protocol = authority.ProtocolFor(admission);
        var party = new PartyTrust(partyId, subject, admission, protocol, self.Anchor.Object(), authority.City, this);
        enrolled[partyId] = (admission, protocol, subject);
        return party;
    }

    // THE TRUST ESTABLISHED, on a link whose ends present the certificates given (placeholders in process): the party asks, the Back End answers, the party
    // checks the answer. Only a party that passes is admitted to exchange data.
    internal string? Establish(PartyTrust party, string partyCertificate, string backEndCertificate)
    {
        var request = party.Protocol.Request(backEndCertificate);
        var (response, admittedId, refused) = own.Answer(request, backEndCertificate, partyCertificate);
        if (refused is not null || admittedId is null) return "the Back End refused: " + refused;
        if (party.Protocol.Check(response, partyCertificate) is { } why) return "the party refused the Back End: " + why;
        var (admission, _, subject) = enrolled[admittedId];
        admitted[admittedId] = (admission.Anchor.Object(), subject);
        return null;
    }

    internal JsonObject? AnchorOf(string id) => admitted.TryGetValue(id, out var a) ? a.Anchor : null;
    internal string? SubjectOf(string id) => admitted.TryGetValue(id, out var a) ? a.Subject : null;

    // What the Back End sends is signed by the Back End.
    internal string Sign(string json) => ExternalSigning.Sign(json, self.Anchor, self.Key, authority.City, DateTimeOffset.UtcNow);
    internal JsonObject BackEndAnchor => self.Anchor.Object();
}

// A PARTY ADMITTED, as it holds itself: its anchor and key, its credential, the Back End's anchor; it signs what it sends and verifies what it receives.
public sealed class PartyTrust
{
    private readonly BackEndTrust backEnd;
    private readonly JsonObject backEndAnchor;
    private readonly string authority;

    internal TrustAuthority.Admission Admission { get; }
    internal TrustProtocol Protocol { get; }
    public string PartyId { get; }
    public string Subject { get; }
    public bool Established { get; private set; }

    internal PartyTrust(string partyId, string subject, TrustAuthority.Admission admission, TrustProtocol protocol, JsonObject backEndAnchor, string authority, BackEndTrust backEnd)
    {
        PartyId = partyId; Subject = subject; Admission = admission; Protocol = protocol; this.backEndAnchor = backEndAnchor; this.authority = authority; this.backEnd = backEnd;
    }

    // Establishes trust with the Back End. Nothing is exchanged before.
    public void Establish()
    {
        var partyCertificate = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var backEndCertificate = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        if (backEnd.Establish(this, partyCertificate, backEndCertificate) is { } why) throw new UnauthorizedAccessException("Trust with the Back End was not established: " + why);
        Established = true;
    }

    internal string Sign(string json) => ExternalSigning.Sign(json, Admission.Anchor, Admission.Key, authority, DateTimeOffset.UtcNow);

    // The Back End's answer is good if the Back End signed it.
    internal string Verify(string json)
    {
        var outcome = ExternalSigning.Verify(json, backEnd.BackEndId, id => id == backEnd.BackEndId ? backEndAnchor : null);
        return outcome == ExternalSigning.Outcome.Valid ? json : throw new UnauthorizedAccessException($"The Back End's answer is not trusted: {outcome}.");
    }
}

// WHAT A PARTY HAS OF THE BACK END once trust is established: the Back End's operations, signed on the way out and verified on the way in.
public sealed class TrustedClient(PartyTrust party, IBackEnd gate) : IBackEnd
{
    private string Call(Func<string, string> operation, string json)
    {
        if (!party.Established) throw new UnauthorizedAccessException("Trust with the Back End is not established: nothing is exchanged.");
        return party.Verify(operation(party.Sign(json)));
    }

    public string Register(string registerRequest) => Call(gate.Register, registerRequest);
    public string Process(string aihDataProcess) => Call(gate.Process, aihDataProcess);
    public string DeIdentify(string deIdAndAnonym) => Call(gate.DeIdentify, deIdAndAnonym);
    public string Audit(string audit) => Call(gate.Audit, audit);
}

// THE FRONT END's DOOR, for the one thing the Back End asks of it: a request for the model it trained is heard only if the Back End signed it, and the answer is signed
// by the Front End's party.
public sealed class TrustedFrontEnd(IFrontEnd inner, PartyTrust party) : IFrontEnd
{
    public string Learn(string signedRequest) => party.Sign(inner.Learn(party.Verify(signedRequest)));
}

// WHAT THE BACK END HAS OF AN ADMITTED FRONT END: its request signed by the Back End, the answer verified as signed by the party it admitted.
public sealed class TrustedFrontEndClient(IFrontEnd door, BackEndTrust trust, string partyId) : IFrontEnd
{
    public string Learn(string federatedLearnRequest)
    {
        var answer = door.Learn(trust.Sign(federatedLearnRequest));
        return ExternalSigning.Verify(answer, partyId, trust.AnchorOf) == ExternalSigning.Outcome.Valid
            ? answer : throw new UnauthorizedAccessException($"The answer is not signed by '{partyId}'.");
    }
}

// THE BACK END's DOOR: what comes in is admitted only if it is signed by a party the Back End admitted, over the Object as it came, and concerns the subject that
// party speaks for; what goes out is signed by the Back End.
public sealed class TrustedBackEnd(IBackEnd inner, BackEndTrust trust) : IBackEnd
{
    private string Admit(string json, Func<JsonObject, string?> subjectOf, Func<string, string> operation)
    {
        var obj = JsonNode.Parse(json) as JsonObject ?? throw new UnauthorizedAccessException("Not a JSON object.");
        var signer = obj["DataXMData"]?["Security"]?["Integrity"]?["KeyID"]?.GetValue<string>() ?? throw new UnauthorizedAccessException("Unsigned: the Back End takes nothing from a party that has not signed it.");
        var outcome = ExternalSigning.Verify(json, signer, trust.AnchorOf);
        if (outcome != ExternalSigning.Outcome.Valid) throw new UnauthorizedAccessException($"Not trusted: {outcome}.");
        if (subjectOf(obj) != trust.SubjectOf(signer)) throw new UnauthorizedAccessException($"'{signer}' does not speak for '{subjectOf(obj)}'.");
        return trust.Sign(operation(json));
    }

    public string Register(string registerRequest) => Admit(registerRequest, o =>
        AihJson.RegisterRequestOf(o) is { } r ? r["UserProfile"]?["UserID"]?.GetValue<string>() ?? r["ThirdPartyUserProfile"]?["ThirdPartyUserID"]?.GetValue<string>() : null, inner.Register);

    public string Process(string aihDataProcess) => Admit(aihDataProcess, o => o["UserID"]?.GetValue<string>(), inner.Process);
    public string DeIdentify(string deIdAndAnonym) => Admit(deIdAndAnonym, o => o["EndUserID"]?.GetValue<string>(), inner.DeIdentify);
    public string Audit(string audit) => Admit(audit, o => o["UserID"]?.GetValue<string>(), inner.Audit);
}
