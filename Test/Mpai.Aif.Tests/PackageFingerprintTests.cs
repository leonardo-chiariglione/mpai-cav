using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;

using AIF.Controller;
using AIF.Store;

namespace Mpai.Aif.Tests;

// THE HASHES OF THE IMPLEMENTER-TO-CONTROLLER WORKFLOW. The Implementer submits the L3s of AMQ to the Store; the Store measures BinaryName.dll in the package each
// names and records its fingerprint. A Controller that fetches the packages measures what it has and puts it to the Store (MPAI_AIFS_VerifyImplementation): the
// packages as submitted are approved, a package altered after submission is refused - and refused, not built by another provider in its place.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class PackageFingerprintTests
{
    private static readonly string[] Leaves = ["1MMC-ASR-V2.5-I03", "1MMC-TIQ-V2.5-I03", "1MMC-TTS-V2.5-I03", "1PAF-PMX-V1.6-I03"];
    private const string Composite = "1MMC-AMQ-V2.5-I02";

    // The L3s of MPAIStore/L3s/AMQ with the address of the packages replaced by a folder.
    private static string Copy(string source, string packages, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source, "*.json"))
        {
            var text = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(file), "(file:///[^\"]*/(Packages|upload)|https://github.com/leonardo-chiariglione/packages/releases/download/amq-v[0-9.]+)", new Uri(packages).AbsoluteUri.TrimEnd('/'));
            File.WriteAllText(Path.Combine(target, Path.GetFileName(file)), text);
        }
        return target;
    }

    private static AmdStore Scanned(string folder) { var store = new AmdStore(folder); store.Scan(); return store; }

    [SkippableFact]
    public async Task AnAlteredPackageIsRefusedAndTheOnesSubmittedAreApproved()
    {
        var source = Path.Combine(Repository.Root, "MPAIStore", "Packages");
        var l3s = Path.Combine(Repository.Root, "MPAIStore", "L3s", "AMQ");
        Skip.IfNot(Directory.Exists(l3s) && File.Exists(Path.Combine(source, "MMC-ASR-V2.5-I03-win-x64.zip")), "The packages of AMQ are not in " + source);

        using var store = new StoreTests.RunningStore();
        foreach (var zip in Directory.GetFiles(source, "*-I03-win-x64.zip")) File.Copy(zip, Path.Combine(store.Packages, Path.GetFileName(zip)));

        // 1. THE IMPLEMENTER SUBMITS: the leaves first, then the Composite AIM that names them.
        var submitted = Copy(l3s, store.Packages, Path.Combine(store.Root, "submitted"));
        foreach (var id in Leaves.Append(Composite))
        {
            var (status, body) = await StoreTests.Submit(store.Http, JsonNode.Parse(File.ReadAllText(Path.Combine(submitted, id + ".json")))!, null, StoreTests.Admin);
            Assert.True(status == HttpStatusCode.Created, $"{id}: {status} {body}");
        }
        foreach (var id in Leaves)
            Assert.True(Directory.GetFiles(Path.Combine(store.StoreFolder, "approved"), id + ".json").Length == 1, $"{id}: the Store recorded no fingerprint");

        // 2. THE CONTROLLER FETCHES what the L3s name - the packages as submitted - and the Store approves each.
        var said = new List<string>();
        var approvals = new HttpAimSource(store.Url);
        var provider = new PackageAimProvider(Scanned(submitted), Path.Combine(store.Root, "cache-good"), line => said.Add(line), approvals);
        foreach (var id in Leaves)
        {
            Assert.True(provider.CanCreate(id), $"{id}: {string.Join(" / ", said)}");
            Assert.NotNull(provider.ImplementationOf(id));
        }

        // 3. ONE PACKAGE IS ALTERED after submission: a byte added to its binary, the zip put where the L3 of that AIM names it.
        var altered = Path.Combine(store.Root, "altered"); Directory.CreateDirectory(altered);
        foreach (var zip in Directory.GetFiles(store.Packages, "*.zip")) File.Copy(zip, Path.Combine(altered, Path.GetFileName(zip)));
        var tts = Path.Combine(altered, "MMC-TTS-V2.5-I03-win-x64.zip");
        using (var archive = ZipFile.Open(tts, ZipArchiveMode.Update))
        {
            var entry = archive.Entries.First(e => e.FullName == "Mpai.Mmc.Tts.dll");
            byte[] bytes; using (var s = entry.Open()) { using var m = new MemoryStream(); s.CopyTo(m); bytes = m.ToArray(); }
            entry.Delete();
            using var again = archive.CreateEntry("Mpai.Mmc.Tts.dll").Open();
            again.Write(bytes); again.WriteByte(0);
        }
        var changed = Copy(l3s, altered, Path.Combine(store.Root, "changed"));
        said.Clear();
        var strict = new PackageAimProvider(Scanned(changed), Path.Combine(store.Root, "cache-altered"), line => said.Add(line), approvals);

        Assert.True(strict.CanCreate("1MMC-TTS-V2.5-I03"), "a refused AIM is still the provider's: another provider must not build it in its place");
        Assert.Null(strict.ImplementationOf("1MMC-TTS-V2.5-I03"));
        var refusal = Assert.Throws<TrustRefusedException>(() => strict.Create("1MMC-TTS-V2.5-I03", new Dictionary<string, string>(), null));
        Assert.Contains("not the binary the Store approved", refusal.Message);
        // the packages left as they were are still approved
        foreach (var id in Leaves.Where(l => l != "1MMC-TTS-V2.5-I03")) Assert.NotNull(strict.ImplementationOf(id));

        // 4. WITHOUT A STORE nothing is asked: the same altered package is taken, which is what the Service says when it prints that packages are not verified.
        var unchecked_ = new PackageAimProvider(Scanned(changed), Path.Combine(store.Root, "cache-unchecked"), _ => { });
        Assert.NotNull(unchecked_.ImplementationOf("1MMC-TTS-V2.5-I03"));
    }
}
