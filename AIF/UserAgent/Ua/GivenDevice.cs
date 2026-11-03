using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;


namespace Mpai.Ua;

// A DEVICE THAT GIVES ONE DATUM: what the person gave the App (a file, a few words), for the workflow to offer to a Port - `offer Voice (OSD-BSO-V1.5:1) from device "Voice"`. The
// Controller sees only the datum, at the Port the workflow names by Data Type and Port Number.
public sealed class GivenDevice(string dataType, string json) : IDevice
{
    public Task DeliverAsync(string type, string data, CancellationToken cancel) => Task.CompletedTask;

    public async IAsyncEnumerable<(string DataType, string Json)> ReadAsync([EnumeratorCancellation] CancellationToken cancel)
    {
        await Task.CompletedTask;
        yield return (dataType, json);
    }

    public Task SafeStopAsync() => Task.CompletedTask;
}