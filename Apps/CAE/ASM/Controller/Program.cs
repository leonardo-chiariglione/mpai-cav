using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;
using AIF.Store;

using Mpai.Aif.Api;
using Mpai.Aif.PortData;
using Mpai.Cae.Asm;
using Mpai.Cae.AsmVoice;
using Mpai.Core;
using Mpai.Mas.Server;
using Mpai.Mas.Service;
using Mpai.Providers;

namespace Mpai.Cae.Asm.Controller;

// AsmController - THE CONTROLLER of Audio Scene Management by voice, a program of its own.
//
//   AsmController [--listen http://127.0.0.1:5057] [--live] [--scene stage] [--storage <folder>] [--keep-until yyyy-mm-dd]
//
// The User Agent (the window) is another program. It writes to this Controller and reads from it through the Controller API (carried over the routes of Mpai.Mas.Server) and tells it to start the
// execution of the L3 of the Module (1CAE-ASM-V1.0-I01, with the Voice Command Interface inside it); this Controller then starts the Module and its AIMs, as the L3 says, and binds the Module's Shared
// Storage where it was told here. The Controller holds the Module that makes the machine's voice (1PAF-RSR-V1.6-I01) as well, which the same User Agent starts in the same way.
// Nothing here is the person's: the window only supplies the devices.
internal static class Program
{
    private const string Asm = AsmVoiceModules.Asm, Asr = "1MMC-ASR-V2.5-I01";

    private static async Task<int> Main(string[] args)
    {
        string? Value(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var url = Value("--listen") ?? "http://127.0.0.1:5057";
        var live = Array.IndexOf(args, "--live") >= 0;
        var scene = Value("--scene") ?? "stage";
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ASM");
        var storage = Value("--storage") ?? Path.Combine(downloads, "module-storage");
        var keepUntil = Value("--keep-until");
        Console.WriteLine("=== Audio Scene Management by voice: the Controller ===");
        if (!url.StartsWith("http://127.0.0.1", StringComparison.Ordinal) && !url.StartsWith("http://localhost", StringComparison.Ordinal))
        { Console.WriteLine("FATAL: this Controller listens on the loopback only (no token, no certificate is configured)."); return 1; }

        // The files of this run, and the Access of the AIMs: the settings the person's window used to build for itself are the Controller's.
        var run = Path.Combine(Path.GetTempPath(), "mpai-asm-controller-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(run);
        var hintFile = Path.Combine(run, "recogniser-hint.txt");
        // THE RECOGNISER IS TOLD WHAT IS LIKELY TO BE SAID (whisper's initial prompt): rooms, metres, ceilings, levels are heard as such, and numbers as numbers ("for" is not "four").
        File.WriteAllText(hintFile, "Audio scene words: room, ceiling, metres, centre, scene, LUFS, degrees, undo, remove, delete.");
        var settings = new JsonObject { [Asr] = new JsonObject { ["PromptFile"] = hintFile } };
        var jsi = new JsonObject { ["Scene"] = scene };
        if (!live) jsi["GrammarOnly"] = "true";          // the demonstration: the grammar alone understands, nothing needs the language model
        settings[AsmVoiceModules.Jsi] = jsi;
        var prepared = AsmVoiceModules.Prepare(MpaiPaths.Root, Path.Combine(run, "module"), settings, null);

        PortDataSchema.Sink = (dataType, direction, complaint) => Console.WriteLine($"[SCHEMA] {dataType} {direction} {complaint}");
        var store = new AmdStore(prepared.Amds);
        store.Scan();
        using var north = new ControllerApi(prepared.Amds, prepared.Settings,
            s => new CompositeProvider(new AsmProvider(prepared.Amds), new VciProvider(s), new LeafAimsProvider(s), new AmqProvider(s)));
        var runner = new ControllerApiRunner(north, store);

        // THE USER AGENT STARTS THE MODULE (the route Start of the Controller API); the Controller does what the L3 says, and keeps the Module's Shared Storage where it was told.
        runner.Started += module =>
        {
            if (module != Asm) return;
            Directory.CreateDirectory(storage);
            var bound = north.SharedStorageInit(runner.IdOf(Asm), storage);
            Console.WriteLine($"  {Asm}: started; its Shared Storage is {storage} ({bound})");
            if (keepUntil is not null)
                Console.WriteLine($"  What is stored from now on is kept until {keepUntil}: {north.SharedStorageKeep(storage, "Data", AIF.SharedStorage.StorageTime.Parse("until " + keepUntil))}");
        };

        var server = new MasServer(runner, PortDataCodecs.Default(), url);
        Console.WriteLine($"  Listening:   {url}");
        Console.WriteLine($"  Language model: {(live ? "on (live)" : "off (the grammar alone understands)")}");
        Console.WriteLine($"  Scene:       {scene}");
        Console.WriteLine($"  Storage:     {storage}");
        Console.WriteLine($"The Controller is ready; the User Agent starts {Asm}.");
        await server.RunAsync();
        return 0;
    }
}
