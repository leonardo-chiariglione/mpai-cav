using AIF.Controller;

namespace Mpai.Aims.Ocr;

// Plug-in for MMC-OCR-V2.5: the engine built by OcrFactory from the settings.
public sealed class OcrPlugin : IAimPlugin
{
    public string AimName => "MMC-OCR-V2.5";
    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new OcrAimProcessor(AimName, OcrFactory.Create(AimName, settings), ports);
}
