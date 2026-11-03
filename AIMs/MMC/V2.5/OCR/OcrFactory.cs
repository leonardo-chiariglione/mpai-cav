// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
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
