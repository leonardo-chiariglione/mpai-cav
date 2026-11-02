using System;
using System.IO;
using System.Threading.Tasks;

using AIF.Controller;
using AIF.Store;

using Mpai.Aif.Api;
using Mpai.Aif.PortData;
using Mpai.Core;
using Mpai.Mas.Server;
using Mpai.Mas.Service;
using Mpai.Providers;

namespace Mpai.Mmc.Controller;

// MmcController - THE CONTROLLER of the stand-alone Apps of MPAI-MMC (ACR, AMC, AMQ, MAC, MCA, MTS, NPR), a program of its own.
//
//   MmcController [--listen http://127.0.0.1:5058]
//
// The Apps are User Agents: each reads its workflow, and the workflow asks this Controller, through the Controller API, to start its Module (the L3); this Controller starts the Module and its
// AIMs as the L3 says. The Controller API is carried over the routes of Mpai.Mas.Server, on the loopback. The same Controller holds the Module that makes the machine's voice (PAF-RSR), which
// the Apps start in the same way. Nothing here is the person's: the Apps only supply the devices.
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string? Value(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var url = Value("--listen") ?? "http://127.0.0.1:5058";
        Console.WriteLine("=== MPAI-MMC stand-alone Apps: the Controller ===");
        if (!url.StartsWith("http://127.0.0.1", StringComparison.Ordinal) && !url.StartsWith("http://localhost", StringComparison.Ordinal))
        { Console.WriteLine("FATAL: this Controller listens on the loopback only (no token, no certificate is configured)."); return 1; }

        var amds = MpaiPaths.Amds; var settings = MpaiPaths.Settings;
        if (!Directory.Exists(amds)) { Console.WriteLine($"FATAL: the AMD directory does not exist: {amds}"); return 1; }
        PortDataSchema.Sink = (dataType, direction, complaint) => Console.WriteLine($"[SCHEMA] {dataType} {direction} {complaint}");
        var store = new AmdStore(amds);
        store.Scan();
        using var north = new ControllerApi(amds, settings,
            s => new CompositeProvider(new AmcProvider(s), new McaProvider(s), new MtsProvider(s), new NprProvider(s), new AmqProvider(s),
                new AcrApp.AcrProvider(s), new HciMac.MacProvider(s, MpaiPaths.Gallery)));
        var runner = new ControllerApiRunner(north, store);
        var server = new MasServer(runner, PortDataCodecs.Default(), url);
        Console.WriteLine($"  Listening:   {url}");
        Console.WriteLine($"  AMDs:        {amds}");
        Console.WriteLine("The Controller is ready; the Apps start their Modules.");
        await server.RunAsync();
        return 0;
    }
}
