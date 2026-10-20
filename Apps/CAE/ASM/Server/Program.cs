using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using AIF.Controller;
using AIF.Store;

using Mpai.Aif.Api;
using Mpai.Aif.PortData;
using Mpai.Core;
using Mpai.Mas.Server;
using Mpai.Mas.Service;

namespace Mpai.Cae.Asm.Server;

// AsmServer - the Controller of Audio Scene Management, for the User Agent that manages audio scenes.
//
//   AsmServer [path-to-asm-server.json]
//
// The Controller and the Module are different processes: the Controller is this program, the Module's AIMs run on an AIM host (AIF.AimHost, a program of
// its own) that the configuration names - "AimHosts" gives, for each AIM of the Module, the host that runs it, and "AimHostKey" the key by which the host
// admits this Controller. The Controller holds no AIM. The User Agent starts the Module (1CAE-ASM-V1.0-I01) through the Controller API, which this program
// carries over the same routes as the other servers (Mpai.Mas.Server), and writes and reads its Ports by Data Type and Port Number.
//
// The L3 of the Module in the repository places its AIMs Internal, as the tests that build them in one process want. Here every AIM that "AimHosts" names is
// placed External: this program copies the L3s to a folder of its own and says so there, so that the repository's L3 is not changed by a deployment.
internal static class Program
{
    private const string AsmModule = "1CAE-ASM-V1.0-I01";

    private static async Task<int> Main(string[] args)
    {
        var configPath = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "asm-server.json");
        Console.WriteLine("=== Audio Scene Management: the Controller ===");

        MasServerConfig config;
        try { config = MasServerConfig.Load(configPath); }
        catch (Exception ex) { Console.WriteLine($"FATAL: {configPath} could not be read: {ex.Message}"); return 1; }

        var amdDir       = config.AmdDirectory ?? MpaiPaths.Amds;
        var settingsPath = config.SettingsPath ?? MpaiPaths.Settings;
        Console.WriteLine($"  Listen:        {config.ListenUrl}");
        Console.WriteLine($"  AMDs:          {amdDir}");

        if (!Directory.Exists(amdDir)) { Console.WriteLine($"FATAL: the AMD directory does not exist: {amdDir}"); return 1; }
        if (!File.Exists(settingsPath)) { Console.WriteLine($"FATAL: the settings file does not exist: {settingsPath}"); return 1; }
        var hosts = config.AimHosts ?? new();
        if (hosts.Count == 0) { Console.WriteLine("FATAL: AimHosts names no AIM host: the Module's AIMs are to run in a process of their own."); return 1; }

        System.Security.Cryptography.X509Certificates.X509Certificate2? certificate;
        System.Security.Cryptography.X509Certificates.X509Certificate2Collection? authority;
        try { certificate = config.LoadCertificate(); authority = config.LoadAuthority(); }
        catch (Exception ex) { Console.WriteLine($"FATAL: {ex.Message}"); return 1; }
        if (config.IsHttps && certificate is null && !config.IsLoopback) { Console.WriteLine("FATAL: no certificate configured, and the listening address is not loopback."); return 1; }
        if (string.IsNullOrWhiteSpace(config.BearerToken) && !config.IsLoopback) { Console.WriteLine("FATAL: no bearer token configured, and the listening address is not loopback."); return 1; }

        // The L3s, with the AIMs the hosts run placed External.
        var placed = Path.Combine(Path.GetTempPath(), "mpai-asm-amds");
        if (Directory.Exists(placed)) Directory.Delete(placed, true);
        Directory.CreateDirectory(placed);
        foreach (var file in Directory.EnumerateFiles(amdDir, "*.json"))
        {
            var text = File.ReadAllText(file);
            foreach (var aim in hosts.Keys)
                text = Regex.Replace(text, "(\"AIMName\"\\s*:\\s*\"" + Regex.Escape(aim) + "\"\\s*,\\s*\"Relation\"\\s*:\\s*)\"Internal\"", "$1\"External\"");
            File.WriteAllText(Path.Combine(placed, Path.GetFileName(file)), text);
        }
        Console.WriteLine($"  Placed AMDs:   {placed}");
        foreach (var (aim, where) in hosts.OrderBy(h => h.Key, StringComparer.Ordinal)) Console.WriteLine($"    {aim,-22} on {where}");

        PortDataSchema.Sink = (dataType, direction, complaint) => Console.WriteLine($"[SCHEMA] {dataType} {direction} {complaint}");

        var store = new AmdStore(placed);
        store.Scan();
        Console.WriteLine($"  AMDs found: {store.Count}");

        // THE CONTROLLER HOLDS NO AIM: every AIM of the Module is placed, and a provider here could build none of them.
        using var north = new ControllerApi(placed, settingsPath, _ => new CompositeProvider());
        foreach (var (aim, where) in hosts) north.Controller.AimHosts[aim] = where;
        north.Controller.AimHostKey = config.AimHostKey ?? "";

        var runner = new Mpai.Mas.Service.ControllerApiRunner(north, store);

        // THE MODULE IS STARTED HERE, as the Service starts its own: the Controller keeps its storage where the configuration says, and the User Agent that
        // starts it through the Controller API shares this instance (a remote User Agent cannot name a path on this machine).
        var storage = config.SharedStorage ?? Path.Combine(MpaiPaths.SharedStorage, "asm");
        var failure = runner.Start(AsmModule);
        if (failure is not null) { Console.WriteLine($"FATAL: {AsmModule} did not start: {failure}"); return 1; }
        north.SharedStorageInit(AsmModule, storage);
        Console.WriteLine($"  {AsmModule}: started; its Shared Storage is {storage}");
        var server = new MasServer(runner, PortDataCodecs.Default(), config.ListenUrl,
            string.IsNullOrWhiteSpace(config.BearerToken) ? null : config.BearerToken, certificate, authority);

        Console.WriteLine($"  Data Types:   {string.Join(", ", PortDataCodecs.Default().KnownDataTypes)}");
        Console.WriteLine($"The Controller is ready; the User Agent starts {AsmModule}.");
        await server.RunAsync();
        return 0;
    }
}
