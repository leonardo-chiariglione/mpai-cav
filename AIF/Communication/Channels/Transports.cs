using System.Collections.Concurrent;

namespace AIF.Channels;

// What both transports share: one Channel's reader ends, and the writer's stamp.
internal sealed class ChannelCore
{
    public ChannelSpec Spec { get; }
    public IReadOnlyDictionary<PortEnd, ReaderQueue> Readers { get; }
    private readonly IClock clock;
    private long sequence;

    public long Written => Interlocked.Read(ref sequence);

    public ChannelCore(ChannelSpec spec, IClock clock)
    {
        Spec = spec;
        this.clock = clock;
        Readers = spec.Readers.ToDictionary(r => r.Reader, r => new ReaderQueue(r.Behaviour, clock));
    }

    // The writer's end stamps the Message; the writer supplies no stamp.
    public void Stamp(PortMessage message)
    {
        message.Stamp    = clock.Now;
        message.Written  = clock.Monotonic;
        message.Sequence = Interlocked.Increment(ref sequence);
    }

    public async ValueTask<bool> DeliverAsync(PortMessage message, int timeoutMs, CancellationToken cancel)
    {
        var all = true;
        foreach (var reader in Readers.Values)
            all &= await reader.PutAsync(message, timeoutMs, cancel);
        return all;
    }

    public void Close()
    {
        foreach (var reader in Readers.Values) reader.Close();
    }
}

// A reader end, whichever transport serves its Channel.
internal sealed class ReaderEnd : IChannelReader
{
    private readonly ReaderQueue queue;

    public ReaderEnd(ChannelSpec spec, PortEnd reader, ReaderQueue queue)
    {
        Spec = spec;
        Reader = reader;
        this.queue = queue;
    }

    public ChannelSpec Spec { get; }
    public PortEnd Reader { get; }
    public ValueTask<PortMessage?> ReadAsync(int timeoutMs = -1, CancellationToken cancel = default) => queue.TakeAsync(timeoutMs, cancel);
    public bool TryRead(out PortMessage? message) => queue.TryTake(out message);
    public Task WaitAsync(CancellationToken cancel = default) => queue.Arrival().WaitAsync(cancel);
    public int  Pending   => queue.Pending;
    public long Taken     => Interlocked.Read(ref queue.Taken);
    public long Dropped   => Interlocked.Read(ref queue.Dropped);
    public long Discarded => Interlocked.Read(ref queue.Discarded);
}

// Channels opened through one transport, by identity: the writer's end and the
// readers' ends of one Channel meet here.
public abstract class ChannelTransport : IChannelTransport
{
    private readonly ConcurrentDictionary<string, ChannelCore> channels = new();
    protected IClock Clock { get; }

    protected ChannelTransport(IClock? clock) => Clock = clock ?? SystemClock.Instance;

    public abstract string Name { get; }

    internal ChannelCore Core(ChannelSpec spec) => channels.GetOrAdd(spec.Id, _ => new ChannelCore(spec, Clock));

    public abstract IChannelWriter OpenWriter(ChannelSpec spec);

    public IChannelReader OpenReader(ChannelSpec spec, PortEnd reader)
    {
        var core = Core(spec);
        if (!core.Readers.TryGetValue(reader, out var queue))
            throw new InvalidOperationException($"{reader} is not a reader of the Channel {spec.Id}.");
        return new ReaderEnd(spec, reader, queue);
    }

    // The Channels of one Module instance are closed when it stops: a reader
    // waiting returns, a writer waiting gives up.
    public void Close(string module)
    {
        foreach (var (id, core) in channels)
            if (core.Spec.Module == module && channels.TryRemove(id, out _))
                core.Close();
    }
}

// INPROCESS: writer and readers in one process. The writer puts each Message into
// every reader's bounded queue itself; nothing relays it, and it is shared, not
// copied (M3205 3.6.2).
public sealed class InProcessTransport : ChannelTransport
{
    public InProcessTransport(IClock? clock = null) : base(clock) { }

    public override string Name => "InProcess";

    public override IChannelWriter OpenWriter(ChannelSpec spec) => new Writer(Core(spec));

    private sealed class Writer(ChannelCore core) : IChannelWriter
    {
        public ChannelSpec Spec => core.Spec;
        public long Written => core.Written;

        public ValueTask<bool> WriteAsync(PortMessage message, int timeoutMs = -1, CancellationToken cancel = default)
        {
            core.Stamp(message);
            return core.DeliverAsync(message, timeoutMs, cancel);
        }
    }
}

// CONTROLLER: the Controller relays each Message from the writer to every reader,
// as the present implementation does, and can observe it on the way - the place
// for a recorder or an inspector. One relay per Channel keeps the order of its
// Messages.
public sealed class ControllerTransport : ChannelTransport
{
    public ControllerTransport(IClock? clock = null) : base(clock) { }

    public override string Name => "Controller";

    // Called with every Message the Controller relays.
    public Action<ChannelSpec, PortMessage>? Observer { get; set; }

    public long Relayed => Interlocked.Read(ref relayed);
    private long relayed;

    private readonly ConcurrentDictionary<string, Relay> relays = new();

    public override IChannelWriter OpenWriter(ChannelSpec spec)
    {
        var core = Core(spec);
        return new Writer(relays.GetOrAdd(spec.Id, _ => new Relay(this, core)), core);
    }

    private sealed class Writer(Relay relay, ChannelCore core) : IChannelWriter
    {
        public ChannelSpec Spec => core.Spec;
        public long Written => core.Written;

        public async ValueTask<bool> WriteAsync(PortMessage message, int timeoutMs = -1, CancellationToken cancel = default)
        {
            core.Stamp(message);
            return await relay.HandAsync(message, timeoutMs, cancel);
        }
    }

    // The Controller's hop: Messages handed over in order, observed, delivered.
    private sealed class Relay
    {
        private readonly ControllerTransport transport;
        private readonly ChannelCore core;
        private readonly SemaphoreSlim one = new(1, 1);

        public Relay(ControllerTransport transport, ChannelCore core)
        {
            this.transport = transport;
            this.core = core;
        }

        public async ValueTask<bool> HandAsync(PortMessage message, int timeoutMs, CancellationToken cancel)
        {
            await one.WaitAsync(cancel);
            try
            {
                transport.Observer?.Invoke(core.Spec, message);
                Interlocked.Increment(ref transport.relayed);
                return await core.DeliverAsync(message, timeoutMs, cancel);
            }
            finally { one.Release(); }
        }
    }
}
