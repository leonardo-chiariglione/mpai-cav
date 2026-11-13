namespace AIF.Controller;

// A Message carries MPAI Data Objects between AIMs.
// It also carries an AimContext — the lifecycle signals (StopToken, PauseGate)
// the AIM uses to honour Start/Pause/Resume/Stop from the Controller.
// The AIM never holds these signals beyond the duration of one ProcessAsync call.
public sealed record Message
{
    // Reserved MessageTypes understood by the framework itself.
    public const string ErrorType     = "Error";
    public const string CancelledType = "Cancelled";

    public string MessageId   { get; init; } = string.Empty;
    public string MessageType { get; init; } = string.Empty;

    // Primary output (legacy single-object transport).
    public string DataType { get; init; } = string.Empty;
    public string Payload  { get; init; } = string.Empty;

    // Future transport model.
    public List<DataObjectMessage> Inputs { get; init; } = new();

    // Port-keyed routing (current routing model).
    public Dictionary<string, string> Ports { get; init; } = new();

    // FURTHER MESSAGES AT A PORT: where an AIM gives more than one Message at one of its Output Ports from one input, the first is in Ports and the others here, in the order
    // they are to be sent (a Module run continuously sends them all, one after the other; a Module run per exchange gives one Message per Port, that of Ports).
    public Dictionary<string, List<string>> More { get; init; } = new();

    // What the AIM gives at each Output Port, in order: the Message of Ports, then the further ones.
    public IEnumerable<(string Port, string Json)> AllOutputs()
    {
        foreach (var (name, json) in Ports)
        {
            yield return (name, json);
            if (More.TryGetValue(name, out var more)) foreach (var next in more) yield return (name, next);
        }
    }

    // Lifecycle context — injected by AimHost, consumed by the AIM processor.
    // AimContext.None when no lifecycle management is needed (e.g. unit tests).
    public AimContext Context { get; init; } = AimContext.None;

    // The AIM that failed, when this Message reports a failure.
    public string FailedAim { get; init; } = string.Empty;

    public bool IsError     => MessageType == ErrorType;
    public bool IsCancelled => MessageType == CancelledType;

    public static Message Error(string messageId, string failedAim, string reason) =>
        new() { MessageId = messageId, MessageType = ErrorType,
                FailedAim = failedAim, Payload = reason };

    public static Message Cancelled(string messageId, string aimName, string reason) =>
        new() { MessageId = messageId, MessageType = CancelledType,
                FailedAim = aimName, Payload = reason };
}
