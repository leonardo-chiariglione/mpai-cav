using AIF.Controller;

namespace Mpai.Paf.Vcr;

// THE AIM OF THE VCR, as the Controller asks for it by its AIM Instance. The Videoconference Client Receiver is one AIM: the demultiplexing it needs
// is inside it (the routine of Mpai.Paf.Pdx) and does not surface.
public sealed class VcrProvider : IAimProvider
{
    public const string Vcr = "1PAF-VCR-V1.6-I01";

    public bool CanCreate(string aimName) => aimName is Vcr;

    public string? ImplementationOf(string aimName) => CanCreate(aimName) ? typeof(VcrProvider).Assembly.Location : null;

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage) => aimName switch
    {
        Vcr => new VideoconferenceClientReceiver(aimName, settings),
        _ => throw new ArgumentException($"{aimName} is not an AIM of the VCR.")
    };
}
