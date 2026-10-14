using AIF.Controller;
using AIF.Store;

using Mpai.Paf.Pdx;

namespace Mpai.Paf.Vcr;

// THE AIMs OF THE VCR, as the Controller asks for them by their AIM Instance. The composite - the VCR itself - is a graph the Controller builds from its Metadata.
// Portable Avatar Demultiplexing is the one of PAF-PDX (Mpai.Paf.Pdx), the same as the Virtual Meeting Secretary's.
public sealed class VcrProvider(AmdStore? store = null) : IAimProvider
{
    public const string Pdx = "1PAF-PDX-V1.6-I01", Msc = "1PAF-MSC-V1.6-I01";

    public bool CanCreate(string aimName) => aimName is Msc || (aimName is Pdx && store is not null);

    public string? ImplementationOf(string aimName) => CanCreate(aimName) ? typeof(VcrProvider).Assembly.Location : null;

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage) => aimName switch
    {
        Pdx when store is not null => new PdxAimProcessor(aimName, AimPortReader.Load(store, aimName)),
        Msc => new MultimodalSceneCreation(aimName, settings),
        _ => throw new ArgumentException($"{aimName} is not an AIM of the VCR.")
    };
}
