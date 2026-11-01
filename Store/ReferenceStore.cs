using System.Text.Json;

namespace AIF.Store;

// THE REFERENCE STORE of a development or demonstration environment: a folder of L3s with the approved fingerprints of the Implementations they name. An Implementer submits a build; the Store
// approves the fingerprint of each binary an L3 names (BinaryName) that the build holds, and records it beside the L3s (approved/<AIM>.json, as ImplementationFingerprints keeps it). A Controller
// that verifies what it runs asks this Store (MPAI_AIFS_VerifyImplementation); a binary changed after the approval is not the one the Store approved, and is refused.
//
// This is not the MPAI Store: it approves whatever it is given, at the time it is given it. It is the Store's side of the interface, so that the Controller's side is exercised.
public static class ReferenceStore
{
    // Approves, for every L3 in the folder, each binary it names that the build folder holds as <BinaryName>.dll. Returns the number of approvals made.
    public static int ApproveBuild(string l3Folder, string buildFolder, DateTimeOffset? at = null)
    {
        var fingerprints = new ImplementationFingerprints(l3Folder);
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(l3Folder, "*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (!document.RootElement.TryGetProperty("Implementations", out var list) || list.ValueKind != JsonValueKind.Array) continue;
            var aim = Path.GetFileNameWithoutExtension(file);
            foreach (var item in list.EnumerateArray())
            {
                if (!item.TryGetProperty("BinaryName", out var name) || name.GetString() is not { Length: > 0 } binary) continue;
                var path = Path.Combine(buildFolder, binary + ".dll");
                if (!File.Exists(path)) continue;
                fingerprints.Approve(aim, binary, ImplementationFingerprints.Of(path), at ?? DateTimeOffset.UtcNow);
                count++;
            }
        }
        return count;
    }
}
