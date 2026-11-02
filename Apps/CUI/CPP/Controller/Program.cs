using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using AIF.Store;

using Mpai.Aif.Api;
using Mpai.Aif.PortData;
using Mpai.Core;
using Mpai.Cui;
using Mpai.Mas.Server;
using Mpai.Mas.Service;

namespace Mpai.Cui.Controller;

// CuiController - THE CONTROLLER of Company Performance Prediction, a program of its own.
//
//   CuiController [--listen http://127.0.0.1:5061] [--model <FinancialDefault-Polish5y.json>]
//
// The User Agent is another program. It tells this Controller, through the Controller API, to start the Module (1CUI-CPP-V2.1-I01), writes the data of a company to its Ports and reads what it gives;
// this Controller starts the Module and its AIMs as the L3 says. The financial model the Prediction AIMs use is Access data: the Controller obtains it from where the Metadata of the AIMs says and
// checks it against its SHA-256 (the address is on GitHub, valid once the branch is merged). --model names a file on this machine to obtain it from instead; the SHA-256 is still the one the
// Metadata pins. Without a model that can be obtained, the AIMs use their built-in reference model.
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string? Value(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var url = Value("--listen") ?? "http://127.0.0.1:5061";
        var model = Value("--model");
        Console.WriteLine("=== Company Performance Prediction: the Controller ===");
        if (!url.StartsWith("http://127.0.0.1", StringComparison.Ordinal) && !url.StartsWith("http://localhost", StringComparison.Ordinal))
        { Console.WriteLine("FATAL: this Controller listens on the loopback only (no token, no certificate is configured)."); return 1; }
        var amds = MpaiPaths.Amds; var settings = MpaiPaths.Settings;
        if (!Directory.Exists(amds)) { Console.WriteLine($"FATAL: the AIM Metadata folder does not exist: {amds}"); return 1; }
        if (model is not null)
        {
            if (!File.Exists(model)) { Console.WriteLine($"FATAL: the model file does not exist: {model}"); return 1; }
            // The Metadata, with the address of the model replaced by this file: the Controller obtains it as it obtains anything of Access.
            var copy = Path.Combine(Path.GetTempPath(), "cui-controller-" + Guid.NewGuid().ToString("N")[..8], "amds");
            Directory.CreateDirectory(copy);
            var uri = new Uri(Path.GetFullPath(model)).AbsoluteUri;
            foreach (var file in Directory.EnumerateFiles(amds, "*.json"))
                File.WriteAllText(Path.Combine(copy, Path.GetFileName(file)), Regex.Replace(File.ReadAllText(file), "\"https://raw[.]githubusercontent[.]com/[^\"]*FinancialDefault-Polish5y[.]json\"", "\"" + uri + "\""));
            amds = copy;
        }
        PortDataSchema.Sink = (dataType, direction, complaint) => Console.WriteLine($"[SCHEMA] {dataType} {direction} {complaint}");
        var store = new AmdStore(amds);
        store.Scan();
        using var north = new ControllerApi(amds, settings, new CuiProvider());
        var runner = new ControllerApiRunner(north, store);
        runner.Started += module => Console.WriteLine($"  {module}: started");
        var server = new MasServer(runner, PortDataCodecs.Default(), url);
        Console.WriteLine($"  Listening:   {url}");
        Console.WriteLine($"  Model:       {(model is null ? "from the address in the Metadata (the AIMs use their reference model where it cannot be obtained)" : model)}");
        Console.WriteLine("The Controller is ready; the User Agent starts 1CUI-CPP-V2.1-I01.");
        await server.RunAsync();
        return 0;
    }
}
