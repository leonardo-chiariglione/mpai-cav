using AIF.Controller;
using AIF.SharedStorage;
using Mpai.Cae.Aoe;
using Mpai.Cae.Ase;
using Mpai.Core;

namespace Mpai.Cae.Asm;

// THE AIM OF AUDIO SCENE MANAGEMENT (CAE-ASM), as the Controller asks for it by its
// AIM Instance. The Module is a graph the Controller builds from its Metadata
// (1CAE-ASM-V1.0-I01): Audio Scene Editing, which executes every User Command (the
// author, 2026/10/04: "ASE is ASM"), and Text and Speech Translation (MMC-TST, from
// its own provider). Audio Scene Editing keeps the Objects and Scenes in the Module's
// Shared Storage. Capture and playback are the User Agent's (the author: "AOD, SOD are
// for UA"): the Physical Layer's Microphone and Loudspeaker Units, the latter
// rendering spatially (Mpai.SpatialAudio).
public sealed class AsmProvider : IAimProvider
{
    public const string Ase = "1CAE-ASE-V1.0-I01";
    public const string Module = "1CAE-ASM-V1.0-I01";

    private readonly AmdStoreHolder _amds;

    public AsmProvider(string amds) => _amds = new AmdStoreHolder(amds);

    public bool CanCreate(string aimName) => aimName is Ase;

    public string? ImplementationOf(string aimName) => CanCreate(aimName) ? typeof(AsmProvider).Assembly.Location : null;

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, ISharedStorage? storage) =>
        Create(aimName, settings, storage, null, null);

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, ISharedStorage? storage,
                                ISharedStorage? privateStorage, IRuledStorage? moduleStorage)
    {
        var ports = AimPortReader.Load(_amds.Store, aimName);
        string Setting(string key, string fallback) => settings.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;
        ISharedStorage Assets() => storage ?? throw new InvalidOperationException($"{aimName} keeps its Assets in Shared Storage, and the Module has none.");
        return aimName switch
        {
            Ase => new AseAimProcessor(aimName, new AoeAim(Assets(), Setting("MInstanceID", "ASM")), new AseAim(Assets(), Setting("MInstanceID", "ASM")), ports),
            _ => throw new ArgumentException($"{aimName} is not an AIM of Audio Scene Management.")
        };
    }

    // The AIM Metadata, scanned once.
    private sealed class AmdStoreHolder(string amds)
    {
        private AIF.Store.AmdStore? _store;
        public AIF.Store.AmdStore Store { get { if (_store is null) { _store = new AIF.Store.AmdStore(amds); _store.Scan(); } return _store; } }
    }
}
