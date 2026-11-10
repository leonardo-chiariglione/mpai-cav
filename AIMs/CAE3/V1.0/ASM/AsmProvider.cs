using AIF.Controller;
using AIF.SharedStorage;
using Mpai.Cae.Aoe;
using Mpai.Cae.Ase;
using Mpai.Mmc.Soe;
using Mpai.Mmc.Sse;
using Mpai.Osd.Mse;
using Mpai.Core;

namespace Mpai.Cae.Asm;

// THE AIMs OF AUDIO SCENE MANAGEMENT (CAE-ASM), as the Controller asks for them by
// their AIM Instance. The Module is a graph the Controller builds from its Metadata
// (1CAE-ASM-V1.0-I01), as the author's Reference Model draws it (2026/10/27): Audio
// Object Editing -> Audio Scene Editing, Speech Object Editing -> Speech Scene
// Editing, Multimodal Scene Editing (OSD-MSE), which composes their Scenes, Text and
// Speech Translation (MMC-TST, from its own provider), and the Voice Command Interface
// (MMC-VCI), which MMC specifies and the provider of MMC supplies: it issues the User
// Commands to all the editing AIMs and takes their responses. All the editing AIMs keep
// their Objects and Scenes in the Module's Private Storage, by Data Type, Port and ID (ModuleStore), and offer what the Module gives in its Shared Storage. Capture and playback are
// the User Agent's (the author: "AOD, SOD are for UA").
public sealed class AsmProvider : IAimProvider
{
    public const string Aoe = "1CAE-AOE-V1.0-I01", Ase = "1CAE-ASE-V1.0-I01", Soe = "1MMC-SOE-V2.5-I01", Sse = "1MMC-SSE-V2.5-I01", Mse = "1OSD-MSE-V1.5-I01";
    public const string Module = "1CAE-ASM-V1.0-I01";

    private readonly AmdStoreHolder _amds;

    public AsmProvider(string amds) => _amds = new AmdStoreHolder(amds);

    // The way an AIM host builds a provider: it hands over the AIM Metadata it holds (AIF.AimHost --provider <assembly>:<Type>).
    public AsmProvider(AIF.Store.AmdStore store) => _amds = new AmdStoreHolder(store);

    public bool CanCreate(string aimName) => aimName is Aoe or Ase or Soe or Sse or Mse;

    public string? ImplementationOf(string aimName) => CanCreate(aimName) ? typeof(AsmProvider).Assembly.Location : null;

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, ISharedStorage? storage) =>
        Create(aimName, settings, storage, null, null);

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, ISharedStorage? storage,
                                ISharedStorage? privateStorage, IRuledStorage? moduleStorage)
    {
        var ports = AimPortReader.Load(_amds.Store, aimName);
        string Setting(string key, string fallback) => settings.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;
        // WHERE THE EDITORS KEEP WHAT THEY EDIT (the author, 2026/11/08): in the Private Storage of the Module, which its AIMs share, by Data Type, Port and the Object's own ID; what the Module
        // offers to other Modules is also put in its Shared Storage, with the Module name in the key. A Module with no Private Storage cannot run them: nothing is kept in Shared Storage instead.
        ModuleStore Keep() => new(moduleStorage, Module, storage, aimName);
        return aimName switch
        {
            Aoe => new AoeAimProcessor(aimName, new AoeAim(Keep(), Setting("MInstanceID", "ASM")), ports),
            Ase => new AseAimProcessor(aimName, new AseAim(Keep(), Setting("MInstanceID", "ASM")), ports),
            Soe => new SoeAimProcessor(aimName, Keep(), ports),
            Sse => new SseAimProcessor(aimName, Keep(), ports, Setting("MInstanceID", "ASM")),
            Mse => new MseAimProcessor(aimName, Keep(), privateStorage, ports, Setting("MInstanceID", "ASM")),
            _ => throw new ArgumentException($"{aimName} is not an AIM of Audio Scene Management.")
        };
    }

    // The AIM Metadata, scanned once.
    private sealed class AmdStoreHolder
    {
        private readonly string? _amds;
        private AIF.Store.AmdStore? _store;
        public AmdStoreHolder(string amds) => _amds = amds;
        public AmdStoreHolder(AIF.Store.AmdStore store) => _store = store;
        public AIF.Store.AmdStore Store { get { if (_store is null) { _store = new AIF.Store.AmdStore(_amds!); _store.Scan(); } return _store; } }
    }
}
