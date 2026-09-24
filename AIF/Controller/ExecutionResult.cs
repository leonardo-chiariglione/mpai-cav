namespace AIF.Controller;

// The outcome of running a composite. A run completes: an exchange gives what it
// has and asks for what it wants, and a Module never waits for a User Agent to
// supply an input it did not undertake to supply (M3205 5.1).
public sealed class ExecutionResult
{
    public required Message Completed { get; init; }

    public static ExecutionResult Complete(Message message) =>
        new() { Completed = message };
}
