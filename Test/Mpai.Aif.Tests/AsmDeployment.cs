using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Mpai.Aif.Tests;

// AUDIO SCENE MANAGEMENT AS IT IS DEPLOYED: the Module's AIMs on an AIM host (AIF.AimHost), the Controller (AsmServer) reaching them, and a URL at which a User
// Agent reaches the Controller through the Controller API. Disposing it ends both processes.
public sealed class AsmDeployment : IDisposable
{
    public const int Port = 5109;
    public const string Module = "1CAE-ASM-V1.0-I01";
    public static string Url => $"https://localhost:{Port}/";

    // Every AIM of the Module, as the Controller is told to find them.
    public static readonly string[] Placed =
    [
        "1CAE-AOE-V1.0-I01", "1CAE-ASE-V1.0-I01", "1CAE-SOE-V1.0-I01", "1CAE-SSE-V1.0-I01", "1OSD-BME-V1.5-I01", "1MMC-TST-V2.5-I01"
    ];

    public static string Exe => Path.Combine(Repository.Root, "Apps", "CAE", "ASM", "Server", "bin", "Debug", "net10.0", "AsmServer.exe");

    // Why it cannot be run here, or null.
    public static string? SkipReason() =>
        !Directory.Exists(Path.Combine(Repository.Root, "Models")) ? "Models is absent: the model files are obtained separately." :
        !File.Exists(Exe) ? "AsmServer is not built: " + Exe :
        System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == Port) ? $"Port {Port} is in use." : null;

    public HostProcess Host { get; }
    public string Log { get { lock (log) return log.ToString(); } }
    public string Storage { get; }

    private readonly Process server;
    private readonly System.Text.StringBuilder log = new();

    public AsmDeployment()
    {
        var root = Repository.Root;
        var bin = AppContext.BaseDirectory;
        Host = new HostProcess(amds: Repository.Amds, provider: Path.Combine(bin, "Mpai.Cae.Asm.dll") + ":Mpai.Cae.Asm.AsmProvider",
            extra: ["--provider", Path.Combine(bin, "Mpai.Providers.dll") + ":Mpai.Providers.LeafAimsProvider",
                    "--settings", Path.Combine(root, "AIMs", "aim-settings.json")],
            workingDirectory: root);

        Storage = Path.Combine(Path.GetTempPath(), "asm-server-storage-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(Path.GetTempPath(), "mpai-test-asm-server.json");
        var hosts = new JsonObject(); foreach (var aim in Placed) hosts[aim] = Host.Address;
        File.WriteAllText(config, new JsonObject
        {
            ["ListenUrl"] = Url, ["AmdDirectory"] = Repository.Amds, ["SettingsPath"] = Path.Combine(root, "AIMs", "aim-settings.json"),
            ["AimHosts"] = hosts, ["AimHostKey"] = HostProcess.Key, ["SharedStorage"] = Storage
        }.ToJsonString());

        var ready = new ManualResetEventSlim();
        server = new Process
        {
            StartInfo = new ProcessStartInfo(Exe, $"\"{config}\"") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true }
        };
        server.OutputDataReceived += (_, e) => { if (e.Data is null) return; lock (log) log.AppendLine(e.Data); if (e.Data.Contains("[MAS] Listening")) ready.Set(); };
        server.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
        server.Start(); server.BeginOutputReadLine(); server.BeginErrorReadLine();
        if (!ready.Wait(TimeSpan.FromMinutes(3)))
        {
            Dispose();
            throw new InvalidOperationException("AsmServer did not start listening:\n" + ServiceFixture.Tail(Log, 25) + "\nThe AIM host said:\n" + Host.Output);
        }
    }

    public string Said => "The Controller said:\n" + ServiceFixture.Tail(Log, 25) + "\nThe AIM host said:\n" + Host.Output;

    public void Dispose()
    {
        try { if (!server.HasExited) server.Kill(entireProcessTree: true); } catch { }
        server.Dispose();
        Host.Dispose();
    }
}
