using AIF.Controller;
using AIF.SharedStorage;
using Mpai.Aims.Audio;
using Mpai.Cae.Aoe;
using Mpai.Cae.Ase;
using Mpai.Core;

namespace Mpai.Cae.Asm;

// THE AIMs OF AUDIO SCENE MANAGEMENT (CAE-ASM), as the Controller asks for them by
// their AIM Instance. The Module itself is a graph the Controller builds from its
// Metadata (1CAE-ASM-V1.0-I01): Audio Object Acquisition -> Audio Object Editing ->
// Audio Scene Editing -> Audio Object Delivery.
//
// Audio Object Editing and Audio Scene Editing keep the Objects and Scenes in the
// Module's Shared Storage. Audio Object Acquisition passes on the audio its device
// gives it; capturing from a file it captures at 48 kHz (the General Assembly:
// AOA at 48 kHz, SOA at 16 kHz). Audio Object Delivery renders with Steam Audio
// (setting SteamAudio) and delivers to a folder (setting OutputFolder).
public sealed class AsmProvider : IAimProvider
{
    public const string Aoa = "1CAE-AOA-V1.0-I01", Aoe = "1CAE-AOE-V1.0-I01", Ase = "1CAE-ASE-V1.0-I01", Aod = "1CAE-AOD-V1.0-I01";
    public const string Module = "1CAE-ASM-V1.0-I01";

    private readonly AmdStoreHolder _amds;

    public AsmProvider(string amds) => _amds = new AmdStoreHolder(amds);

    public bool CanCreate(string aimName) => aimName is Aoa or Aoe or Ase or Aod;

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
            Aoa => new AoaAimProcessor(aimName, new FileAudioAcquisition(MpaiPaths.Resolve(Setting("AudioFile", "Models/none.wav")), 48000), ports),
            Aoe => new AoeAimProcessor(aimName, new AoeAim(Assets(), Setting("MInstanceID", "ASM")), ports),
            Ase => new AseAimProcessor(aimName, new AseAim(Assets(), Setting("MInstanceID", "ASM")), ports),
            Aod => new AodAimProcessor(aimName, new FileAudioDelivery(MpaiPaths.Resolve(Setting("OutputFolder", Path.Combine(Path.GetTempPath(), "asm-out")))), ports,
                                       MpaiPaths.Resolve(Setting("SteamAudio", "Models/SteamAudio")), Setting("Layout", "Binaural")),
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
