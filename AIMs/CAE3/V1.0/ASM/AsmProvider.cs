using AIF.Controller;
using AIF.SharedStorage;
using Mpai.Cae.Ami;
using Mpai.Cae.Aoe;
using Mpai.Cae.Ase;
using Mpai.Cae.Soe;
using Mpai.Cae.Sse;
using Mpai.Osd.Mse;
using Mpai.Core;

namespace Mpai.Cae.Asm;

// THE AIMs OF AUDIO SCENE MANAGEMENT (CAE-ASM), as the Controller asks for them by
// their AIM Instance. The Module is a graph the Controller builds from its Metadata
// (1CAE-ASM-V1.0-I01), as the author's Reference Model draws it (2026/10/04): Audio
// Scene Management Interface (CAE-AMI), the one AIM that touches the boundary, in front of Audio
// Object Editing -> Audio Scene Editing, Speech Object Editing -> Speech Scene
// Editing, Multimodal Scene Editing (OSD-MSE), which composes their Scenes, and
// Text and Speech Translation (MMC-TST, from its own provider). All keep
// their Objects and Scenes in the Module's Shared Storage. Capture and playback are
// the User Agent's (the author: "AOD, SOD are for UA").
public sealed class AsmProvider : IAimProvider
{
    public const string Ami = "1CAE-AMI-V1.0-I01", Aoe = "1CAE-AOE-V1.0-I01", Ase = "1CAE-ASE-V1.0-I01", Soe = "1CAE-SOE-V1.0-I01", Sse = "1CAE-SSE-V1.0-I01", Mse = "1OSD-MSE-V1.5-I01";
    public const string Module = "1CAE-ASM-V1.0-I01";

    private readonly AmdStoreHolder _amds;

    public AsmProvider(string amds) => _amds = new AmdStoreHolder(amds);

    // The way an AIM host builds a provider: it hands over the AIM Metadata it holds (AIF.AimHost --provider <assembly>:<Type>).
    public AsmProvider(AIF.Store.AmdStore store) => _amds = new AmdStoreHolder(store);

    public bool CanCreate(string aimName) => aimName is Ami or Aoe or Ase or Soe or Sse or Mse;

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
            Ami => new AmiAimProcessor(aimName, ports, SchemaOf(aimName, settings)),
            Aoe => new AoeAimProcessor(aimName, new AoeAim(Assets(), Setting("MInstanceID", "ASM")), ports),
            Ase => new AseAimProcessor(aimName, new AseAim(Assets(), Setting("MInstanceID", "ASM")), ports),
            Soe => new SoeAimProcessor(aimName, Assets(), ports),
            Sse => new SseAimProcessor(aimName, Assets(), ports, Setting("MInstanceID", "ASM")),
            Mse => new MseAimProcessor(aimName, Assets(), ports, Setting("MInstanceID", "ASM")),
            _ => throw new ArgumentException($"{aimName} is not an AIM of Audio Scene Management.")
        };
    }

    // The schema of the User Command that the Access of Audio Scene Management Interface names (CommandSchema): the address it is published at, and the file the Controller obtained
    // for it (the Setting of that name). Without an entry in the Metadata there is no schema, and no Application JSON Instance conforms.
    private CommandSchemaCheck? SchemaOf(string aimName, IReadOnlyDictionary<string, string> settings)
    {
        var store = _amds.Store;
        var resolved = store.FindByAimName(aimName);
        if (resolved is null) return null;
        var entry = AccessEntry.ReadFrom(store.GetAMD(resolved).RootElement).FirstOrDefault(e => e.Setting == "CommandSchema" && !e.IsPlaceholder);
        if (entry is null) return null;
        settings.TryGetValue("CommandSchema", out var file);
        settings.TryGetValue("SchemasRoot", out var root);
        return new CommandSchemaCheck(entry.Uri, file, root);
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
