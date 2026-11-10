using AIF.Controller;
using AIF.SharedStorage;
using Mpai.Providers;

namespace Mpai.Aif.Tests;

// A provider that gives the AIMs of LeafAimsProvider and writes down, for each AIM, what it was given and what it answered: to see, in a test of a composite, which AIM did not give its output.
internal sealed class TracingProvider : IAimProvider, IDisposable
{
    private readonly IAimProvider _inner;
    private readonly List<string> _log;
    public TracingProvider(AIF.Store.AmdStore store, List<string> log) { _inner = new LeafAimsProvider(store); _log = log; }
    public TracingProvider(IAimProvider inner, List<string> log) { _inner = inner; _log = log; }

    public bool CanCreate(string aimName) => _inner.CanCreate(aimName);
    public string? ImplementationOf(string aimName) => _inner.ImplementationOf(aimName);
    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, ISharedStorage? storage) => new Wrapped(_inner.Create(aimName, settings, storage), aimName, _log);
    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, ISharedStorage? storage, ISharedStorage? privateStorage, IRuledStorage? moduleStorage, IAccessReader? access)
        => new Wrapped(_inner.Create(aimName, settings, storage, privateStorage, moduleStorage, access), aimName, _log);
    public void Dispose() => (_inner as IDisposable)?.Dispose();

    private sealed class Wrapped : IAimProcessor
    {
        private readonly IAimProcessor _p; private readonly string _name; private readonly List<string> _log;
        public Wrapped(IAimProcessor p, string name, List<string> log) { _p = p; _name = name; _log = log; }
        public string InstanceId => _p.InstanceId;
        public async Task<Message> ProcessAsync(Message message)
        {
            Message r;
            try { r = await _p.ProcessAsync(message); }
            catch (Exception ex) { lock (_log) _log.Add($"{_name}: threw {ex.GetType().Name}: {ex.Message}"); throw; }
            lock (_log) _log.Add($"{_name}: in [{string.Join(", ", message.Ports.Keys)}] -> {(r.IsError ? "ERROR " + r.Payload : "out [" + string.Join(", ", r.Ports.Keys) + "]")}");
            return r;
        }
    }
}
