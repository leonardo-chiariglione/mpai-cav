using AIF.Channels;
using AIF.Controller;

using Mpai.Cav.Recordings;

namespace Mpai.Aif.Tests;

// THE PORTS OF ONE AIM, FED A DRIVE: what the Controller would give an AIM of ESS
// Stage 1, in the order of the drive's times, as fast as the AIM takes them; what
// the AIM writes, kept with the drive time of the input that caused it. For
// testing one AIM against the ground truth, without a Module around it.
public sealed class DrivePorts(IEnumerable<(TimeSpan At, string DataType, int PortNumber, string Json)> inputs) : IAimPorts
{
    private readonly Queue<(TimeSpan At, string DataType, int PortNumber, string Json)> queue = new(inputs.OrderBy(m => m.At));
    private TimeSpan now;

    public static DrivePorts Of(IEnumerable<DriveMessage> messages, params string[] dataTypes) =>
        new(messages.Where(m => dataTypes.Contains(m.DataType)).Select(m => (m.At, m.DataType, m.PortNumber, m.Json)));

    public List<(TimeSpan At, string DataType, int PortNumber, string Json)> Written { get; } = [];

    public ValueTask<PortMessage?> ReadAsync(string dataType, int portNumber = 1, int timeoutMs = -1)
    {
        if (queue.Count == 0 || queue.Peek().DataType != dataType || queue.Peek().PortNumber != portNumber) return ValueTask.FromResult<PortMessage?>(null);
        var m = queue.Dequeue();
        now = m.At;
        return ValueTask.FromResult<PortMessage?>(new PortMessage { DataType = m.DataType, PortNumber = m.PortNumber, Json = m.Json });
    }

    public ValueTask<bool> WriteAsync(string dataType, int portNumber, string json, int timeoutMs = -1)
    {
        Written.Add((now, dataType, portNumber, json));
        return ValueTask.FromResult(true);
    }

    // The next input, whichever Port it is on; none when the drive is over, which
    // ends the AIM's loop.
    public ValueTask<(string DataType, int PortNumber)?> SelectAsync(int timeoutMs, params (string DataType, int PortNumber)[] ports)
    {
        while (queue.Count > 0 && !ports.Contains((queue.Peek().DataType, queue.Peek().PortNumber))) queue.Dequeue();
        return ValueTask.FromResult<(string, int)?>(queue.Count == 0 ? null : (queue.Peek().DataType, queue.Peek().PortNumber));
    }

    public int Pending(string dataType, int portNumber = 1) => queue.Count(m => m.DataType == dataType && m.PortNumber == portNumber);
    public long Dropped(string dataType, int portNumber = 1) => 0;
    public DateTimeOffset Now => DateTimeOffset.UnixEpoch + now;
    public Task NextPeriodAsync() => Task.CompletedTask;
    public string PutPayload(string dataType, int portNumber, ReadOnlyMemory<byte> data) => throw new NotSupportedException();
    public ReadOnlyMemory<byte> GetPayload(string reference) => throw new NotSupportedException();
    public void ReleasePayload(string reference) { }
}
