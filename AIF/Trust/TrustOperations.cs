using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace AIF.Trust;

// THE TRUST OPERATIONS OF A PARTY THAT HAS NO TRUST DOMAIN OF ITS OWN - a Controller that joined a service, the Authority of a service: each trust decision it makes (a link
// checked, a Message refused, a Controller admitted or refused, a link closed at the end of its credential) is a PTF Trust Operation, signed, and a record of its Trace, a signed
// hash-chained AIF Trace (AIF V3.0 Zero Trust 2.9: every trust decision is recorded).
public sealed class TrustOperations
{
    private readonly TrustAnchorKey anchor;
    private readonly ECDsa key;
    private readonly Func<DateTimeOffset> now;

    public TrustOperations(TrustAnchorKey anchor, ECDsa key, Func<DateTimeOffset>? now = null, string? traceFile = null)
    {
        this.anchor = anchor;
        this.key = key;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        Trace = new TrustTrace(anchor, key, this.now, traceFile);
    }

    public TrustTrace Trace { get; }
    public string ActorId => anchor.AnchorId;

    // As TrustProtocol records a check of the other end.
    public Action<string, string, string, string?> Recorder => (type, targetType, targetId, failure) => Operation(type, targetType, targetId, failure);

    public JsonObject Operation(string type, string targetType, string targetId, string? failure)
    {
        var operation = new JsonObject
        {
            ["Header"] = "PTF-TOP-V1.0",
            ["TrustOperationID"] = $"{ActorId}#TOP-{Guid.NewGuid():N}",
            ["TrustOperationTime"] = new JsonObject
            {
                ["Header"] = "OSD-TIM-V1.5", ["TimeID"] = $"{ActorId}#TIM-{Guid.NewGuid():N}", ["Data"] = PtfIdentity.Time(now())
            },
            ["OperationType"] = type,
            ["TargetType"] = targetType,
            ["TargetID"] = targetId,
            ["ActorID"] = ActorId,
            ["Status"] = failure is null ? "Success" : "Failure"
        };
        if (failure is not null) operation["FailureReason"] = failure;
        PtfSignature.Sign(operation, key, anchor.AnchorId);
        Trace.Append(operation);
        return operation;
    }
}
