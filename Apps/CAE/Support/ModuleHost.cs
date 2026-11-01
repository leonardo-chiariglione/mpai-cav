using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using AIF.Controller;
using AIF.Store;
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

    // THE DEPLOYMENT OF A CONTROLLER AND THE HOST OF ITS AIMs, which the Apps of CAE use to run a Module: this is the Controller's side, not the User Agent's. It does not read the Metadata of the Module or name an AIM:
    // the Controller runs every AIM of the Module on the AIM host (AllAimsApart), the Metadata stays as the Implementer wrote it, and the settings of the AIMs are a file of the deployment - the file
    // of the folder AIMs, and, where the User Agent chose a profile of the deployment ("openvoice"), AIMs\aim-settings.<profile>.json over it.
    //
    // The Controller verifies each AIM before it runs (MPAI-AIF Zero Trust): the AIM host measures the binary that implements it, and the Controller asks the Store whether it is the one the Store
    // approved. The Store here is the reference Store (ReferenceStore): it approves the binaries of this build when the host starts. providers: where the AIM host finds the AIMs of the Module
    // (assembly in the folder of the App, type); storage: where the Controller keeps the Module's Shared Storage - the Controller's own business: the User Agent names no place.
    public static ModuleHost Start(string module, string? profile = null, Action<string>? say = null, IReadOnlyList<(string Assembly, string Type)>? providers = null, string? storage = null)
    {
        var root = Root; say ??= _ => { };
        var work = Path.Combine(Path.GetTempPath(), "mpai-" + module + "-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(work);
        var amds = Path.Combine(work, "amds"); Directory.CreateDirectory(amds);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "AIMs", "AMDs"), "*.json")) File.Copy(file, Path.Combine(amds, Path.GetFileName(file)));
        var approved = ReferenceStore.ApproveBuild(amds, AppContext.BaseDirectory);
        say($"The Store approved {approved} Implementation(s) of this build.");

        var all = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "AIMs", "aim-settings.json")))!.AsObject();
        if (profile is not null)
        {
            var overlay = Path.Combine(root, "AIMs", $"aim-settings.{profile}.json");
            if (!File.Exists(overlay)) throw new InvalidOperationException($"The deployment has no settings profile '{profile}' ({overlay}).");
            foreach (var (aim, own) in JsonNode.Parse(File.ReadAllText(overlay))!.AsObject())
            {
                var merged = all[aim] as JsonObject ?? new JsonObject(); all[aim] = merged;
                foreach (var (name, value) in own!.AsObject()) merged[name] = value?.DeepClone();
            }
        }
        var settingsPath = Path.Combine(work, "aim-settings.json"); File.WriteAllText(settingsPath, all.ToJsonString());

        // THE TRUST ANCHORS of the Controller and of the AIM host, made for this run: the host serves only the Controller whose anchor it was given, and the Controller uses only the host whose anchor it
        // holds; each proves it holds its key when the link opens (the Trust Protocol, MPAI-PTF). The key of each stays with its owner.
        var now = DateTimeOffset.UtcNow;
        var controllerKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var controllerAnchor = new AIF.Trust.TrustAnchorKey("controller-" + Guid.NewGuid().ToString("N")[..8], controllerKey, now.AddMinutes(-5), now.AddDays(1));
        var hostKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var hostAnchor = new AIF.Trust.TrustAnchorKey("aimhost-" + Guid.NewGuid().ToString("N")[..8], hostKey, now.AddMinutes(-5), now.AddDays(1));
        var hostAnchorFile = Path.Combine(work, "host-anchor.json"); AIF.Trust.TrustAnchorKey.Save(hostAnchorFile, hostAnchor.AnchorId, hostKey, now.AddMinutes(-5), now.AddDays(1));
        var trustFile = Path.Combine(work, "controller-anchor.json"); File.WriteAllText(trustFile, controllerAnchor.Object().ToJsonString());
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = root };
        foreach (var arg in new[]
        {
            Path.Combine(root, "AIF", "AimHost", "bin", "Debug", "net10.0", "AIF.AimHost.dll"), "--port", "0", "--anchor", hostAnchorFile, "--trust", trustFile, "--amds", amds, "--settings", settingsPath,
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
            api.Controller.AimHosts[module] = address;
            api.Controller.AllAimsApart = true;
            api.Controller.Trust = new AIF.Trust.TrustDomain(controllerAnchor, controllerKey);
            api.Controller.HostAnchors.Add(hostAnchor.Object());
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