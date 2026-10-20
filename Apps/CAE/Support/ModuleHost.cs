using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using AIF.Controller;
using Mpai.Aif.Api;

namespace Mpai.Cae.Apps;

// THE MODULE OF AN APP, STARTED AS IT IS RUN: a Controller of its own and, in a process of its own, an AIM host that runs the Module's AIMs (the Controller holds none). The Module's
// L3 places its AIMs Internal, as the tests that build them in one process want; here each is placed External, in a copy of the L3s made for the run - the repository's are not changed.
// What the App then does is run the User Agent on this Controller's API (Api); it ends with the App.
//
// settings: the settings of AIMs of the Instance, by AIM Instance, over those of AIMs/aim-settings.json (for instance the Text To Speech's voice).
public sealed class ModuleHost : IDisposable
{
    private readonly Process _host;
    private readonly string _work;
    public ControllerApi Api { get; }
    public string Module { get; }

    private ModuleHost(string module, ControllerApi api, Process host, string work) { Module = module; Api = api; _host = host; _work = work; }

    // The folder of the software: the one that holds AIMs\AMDs and AIF\AimHost.
    public static string Root
    {
        get
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, "AIMs", "AMDs")) && Directory.Exists(Path.Combine(d.FullName, "AIF", "AimHost"))) return d.FullName;
            throw new InvalidOperationException("The folder of the software (the one with AIMs\\AMDs and AIF\\AimHost) is not above " + AppContext.BaseDirectory);
        }
    }

    // The provider that builds the AIMs of the Usc Modules; another Module names its own (assembly file, type) in `providers`.
    public static readonly (string Assembly, string Type)[] UscProviders = [("Mpai.Cae.Usc.dll", "Mpai.Cae.Usc.UscProvider")];

    // providers: where the AIM host finds the AIMs of the Module (assembly in the folder of the App, type). storage: where the Controller keeps the Module's Shared Storage - the Controller's own business: the User Agent names no place.
    public static ModuleHost Start(string module, JsonObject? settings = null, Action<string>? say = null, IReadOnlyList<(string Assembly, string Type)>? providers = null, string? storage = null)
    {
        var root = Root; say ??= _ => { };
        var work = Path.Combine(Path.GetTempPath(), "mpai-" + module + "-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(work);
        var amds = Path.Combine(work, "amds"); Directory.CreateDirectory(amds);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "AIMs", "AMDs"), "*.json")) File.Copy(file, Path.Combine(amds, Path.GetFileName(file)));

        var l3Path = Path.Combine(amds, module + ".json");
        if (!File.Exists(l3Path)) throw new InvalidOperationException($"There is no L3 for {module} in AIMs\\AMDs.");
        var l3 = File.ReadAllText(l3Path);
        var aims = (JsonNode.Parse(l3)?["SubAIMs"] as JsonArray ?? []).Select(s => s?["Identifier"]?["AIMName"]?.GetValue<string>()).OfType<string>().ToArray();
        foreach (var aim in aims) l3 = Regex.Replace(l3, "(\"AIMName\"\\s*:\\s*\"" + Regex.Escape(aim) + "\"\\s*,\\s*\"Relation\"\\s*:\\s*)\"Internal\"", "$1\"External\"");
        File.WriteAllText(l3Path, l3);

        var all = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "AIMs", "aim-settings.json")))!.AsObject();
        foreach (var (aim, own) in settings ?? [])
        {
            var merged = all[aim] as JsonObject ?? new JsonObject(); all[aim] = merged;
            foreach (var (name, value) in own!.AsObject()) merged[name] = value?.DeepClone();
        }
        var settingsPath = Path.Combine(work, "aim-settings.json"); File.WriteAllText(settingsPath, all.ToJsonString());

        var key = Guid.NewGuid().ToString("N");
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = root };
        foreach (var arg in new[]
        {
            Path.Combine(root, "AIF", "AimHost", "bin", "Debug", "net10.0", "AIF.AimHost.dll"), "--port", "0", "--key", key, "--amds", amds, "--settings", settingsPath,
            "--storage", Path.Combine(work, "aimhost")
        }) info.ArgumentList.Add(arg);
        foreach (var (assembly, type) in providers ?? UscProviders) { info.ArgumentList.Add("--provider"); info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, assembly) + ":" + type); }
        var process = Process.Start(info) ?? throw new InvalidOperationException("The AIM host did not start.");
        try
        {
            var clock = Stopwatch.StartNew(); string? address = null;
            while (clock.Elapsed < TimeSpan.FromSeconds(60) && address is null)
            {
                var line = process.StandardOutput.ReadLine() ?? throw new InvalidOperationException("The AIM host stopped: " + process.StandardError.ReadToEnd());
                var m = Regex.Match(line, @"listening on (\d+)");
                if (m.Success) address = "localhost:" + m.Groups[1].Value;
            }
            if (address is null) throw new InvalidOperationException("The AIM host did not say where it listens.");
            var log = Environment.GetEnvironmentVariable("MPAI_HOSTLOG") == "1";            // what the AIM host says, for a run that is being examined
            _ = System.Threading.Tasks.Task.Run(() => { while (process.StandardOutput.ReadLine() is { } line) { if (log) Console.WriteLine("[host] " + line); } });
            _ = System.Threading.Tasks.Task.Run(() => { while (process.StandardError.ReadLine() is { } line) { if (log) Console.WriteLine("[host!] " + line); } });
            say($"The AIMs of {module} run on an AIM host at {address}.");

            var api = new ControllerApi(amds, settingsPath, _ => new CompositeProvider());
            api.Controller.AimHostKey = key;
            foreach (var aim in aims) api.Controller.AimHosts[aim] = address;
            if (storage is not null)
            {
                Directory.CreateDirectory(storage);
                var started = api.StartFlow(module); if (started != AifError.OK) throw new InvalidOperationException($"The Controller would not start {module}: {started}.");
                var kept = api.SharedStorageInit(module, storage); if (kept != AifError.OK) throw new InvalidOperationException($"The Controller would not keep the storage of {module} in {storage}: {kept}.");
            }
            return new ModuleHost(module, api, process, work);
        }
        catch { try { process.Kill(true); } catch { } throw; }
    }

    public void Dispose()
    {
        try { Api.Dispose(); } catch { }
        try { if (!_host.HasExited) _host.Kill(true); } catch { }
        try { Directory.Delete(_work, true); } catch { }
    }
}