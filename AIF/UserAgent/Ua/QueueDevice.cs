using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Mpai.Ua;

// A DEVICE THAT GIVES THE NEXT DATUM THE APP HAS: each time the workflow offers from it, it waits for the App to give one (Give) and hands over that one. It is what a workflow that loops - one
// exchange after another - offers from, as GivenDevice is for one exchange.
public sealed class QueueDevice(string dataType) : IDevice
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();

    public void Give(string json) => _queue.Writer.TryWrite(json);

    public Task DeliverAsync(string type, string data, CancellationToken cancel) => Task.CompletedTask;

    public async IAsyncEnumerable<(string DataType, string Json)> ReadAsync([EnumeratorCancellation] CancellationToken cancel)
    {
        var json = await _queue.Reader.ReadAsync(cancel);
        yield return (dataType, json);
    }

    public Task SafeStopAsync() => Task.CompletedTask;
}