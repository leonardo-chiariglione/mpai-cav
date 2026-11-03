using System.Security.Cryptography;
using System.Text.Json.Nodes;

using AIF.Controller;
using AIF.Store;

using Mpai.Aif.Api;
using Mpai.Cae.Asm;
using Mpai.Providers;

namespace Mpai.Cae.AsmVoice;

// THE MODULE OF ASM-VCI.orch: Audio Scene Management (CAE-ASM-V1.0), with the Voice Command Interface (MMC-VCI-V2.5) inside it. The workflow uses the Controller API only; this builds
// the Controller it runs under, with the AIMs of Audio Scene Management, those of the Voice Command Interface (the speech recogniser, the JSON Interface, the dialogue, the speech
// synthesiser) and the leaf AIMs both name (the translation).
//
// THE ACCESS OF THE JSON INTERFACE IS READ FROM THE FILES OF THIS SOFTWARE until the schema of the User Command and the guide are on the sites their L3 names (the author does not
// publish yet): the L3s of the Modules are copied to a folder of the run, with the addresses of those three entries replaced by the files and their SHA-256 by the files' own. The
// repository's L3s are not changed; the Controller still refuses what it cannot fetch or whose hash differs.
public static class AsmVoiceModules
{
    public const string Vci = "1MMC-VCI-V2.5-I01", Asm = "1CAE-ASM-V1.0-I01", Jsi = "1MMC-JSI-V2.5-I02";

    // THE GUIDE OF THIS RUN, when the run makes a scene of its own: the one scene this application makes is called as the run says (a new, empty scene at each start), in every place the guide names it.
    private static string ContextWithScene(string root, string folder, string sceneName)
    {
        static JsonNode? Rename(JsonNode? node, string name)
        {
            switch (node)
            {
                case JsonObject o: { var copy = new JsonObject(); foreach (var (k, v) in o) copy[k] = Rename(v, name); return copy; }
                case JsonArray a: return new JsonArray(a.Select(x => Rename(x, name)).ToArray());
                case JsonValue v when v.TryGetValue<string>(out var text) && text == "stage": return JsonValue.Create(name);
                default: return node?.DeepClone();
            }
        }
        var path = Path.Combine(folder, "AsmApplicationContext.json");
        File.WriteAllText(path, Rename(JsonNode.Parse(File.ReadAllText(ContextFile(root))), sceneName)!.ToJsonString());
        return path;
    }

    public static string ContextFile(string root) => Path.Combine(root, "Apps", "CAE", "ASM", "Access", "AsmApplicationContext.json");
    public static string CommandSchemaFile(string root) => Path.Combine(root, "schemas", "CAE3", "V1.0", "data", "UserCommand.json");

    // The folder of the L3s of the run (a copy of AIMs\AMDs with the Access of the JSON Interface at the local files) and the file of the settings of the AIMs.
    public sealed record Prepared(string Folder, string Amds, string Settings);

    public static Prepared Prepare(string root, string? folder = null, JsonObject? settings = null, string? sceneName = null)
    {
        folder ??= Path.Combine(Path.GetTempPath(), "mpai-asm-voice-" + Guid.NewGuid().ToString("N")[..8]);
        var amds = Path.Combine(folder, "amds"); Directory.CreateDirectory(amds);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "AIMs", "AMDs"), "*.json")) File.Copy(file, Path.Combine(amds, Path.GetFileName(file)), true);

        static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        var l3Path = Path.Combine(amds, Jsi + ".json");
        var l3 = JsonNode.Parse(File.ReadAllText(l3Path))!;
        foreach (var entry in l3["AccessSources"]!.AsArray())
        {
            var file = (string)entry!["Name"]! switch { "ApplicationContext" => sceneName is null ? ContextFile(root) : ContextWithScene(root, folder, sceneName), "CommandSchema" or "ResponseSchema" => CommandSchemaFile(root), _ => null };
            if (file is null) continue;
            entry["Sha256"] = Hash(file);
            entry["Mirrors"] = new JsonArray(new Uri(file).AbsoluteUri);
        }
        File.WriteAllText(l3Path, l3.ToJsonString());

        var all = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "AIMs", "aim-settings.json")))!.AsObject();
        var jsi = all[Jsi] as JsonObject ?? new JsonObject(); all[Jsi] = jsi;
        jsi["SchemasRoot"] = Path.Combine(root, "schemas");
        jsi["Grammar"] = "true";            // the application's grammar first, the language model only for what it does not match
        jsi["SayPlainly"] = "true";          // a response that did all that was asked is said in a few words, without the dialogue's language model
        jsi["RefreshState"] = "true";          // an application answers: the JSON Interface asks it what it holds before it works out a request
        foreach (var (aim, own) in settings ?? [])
        {
            var merged = all[aim] as JsonObject ?? new JsonObject(); all[aim] = merged;
            foreach (var (name, value) in own!.AsObject()) merged[name] = value?.DeepClone();
        }
        var settingsPath = Path.Combine(folder, "aim-settings.json");
        File.WriteAllText(settingsPath, all.ToJsonString());
        return new Prepared(folder, amds, settingsPath);
    }

    // The Controller of the run: the AIMs of Audio Scene Management, and those of the Voice Command Interface (the speech recogniser, the JSON Interface, the
    // dialogue, the speech synthesiser), and the leaf AIMs both name (the translation of Audio Scene Management). `vci` replaces the provider of the second where a test has speech or a language model of its own.
    public static ControllerApi Controller(Prepared prepared, Func<AmdStore, IAimProvider>? vci = null) =>
        new(prepared.Amds, prepared.Settings, store => new CompositeProvider(new AsmProvider(prepared.Amds), vci?.Invoke(store) ?? new VciProvider(store), new LeafAimsProvider(store)));
}
