using Mpai.Core;

namespace Mpai.Aims.Ocr;

// Builds MMC-OCR-V2.5 from deployment settings: DetModel, ClsModel, RecModel and
// KeysFile (Models/OCR, see MODELS.md); a relative path is resolved against the
// application's root. With DetModel not set, the models the package carries.
public static class OcrFactory
{
    public static IOcrAim Create(string instanceId, IReadOnlyDictionary<string, string> settings)
    {
        var det = Value(settings, "DetModel");
        if (det is null)
        {
            Console.WriteLine($"[{instanceId}] DetModel not set - using the models RapidOcrNet carries.");
            return new RapidOcrAim(instanceId);
        }
        return new RapidOcrAim(instanceId, new RapidOcrConfiguration
        {
            DetModel = det,
            ClsModel = Value(settings, "ClsModel") ?? "",
            RecModel = Value(settings, "RecModel") ?? "",
            KeysFile = Value(settings, "KeysFile") ?? ""
        });
    }

    private static string? Value(IReadOnlyDictionary<string, string> settings, string key) =>
        settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? MpaiPaths.Resolve(value) : null;
}
