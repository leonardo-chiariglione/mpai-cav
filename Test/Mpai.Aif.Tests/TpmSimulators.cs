using System.Diagnostics;
using System.Net.Sockets;

namespace Mpai.Aif.Tests;

// THE SIMULATED ROOTS OF TRUST OF THE TESTS (M3223 3.5): Microsoft's TPM 2.0
// reference simulator (ms-tpm-20-ref v1.83r1), built in WSL under ~/mpai-tpm, one
// instance per party - each on its own port, in its own folder, so each has its own
// seeds and NV. Started, detached, where none is listening, and left running for the
// runs that follow (a port just released is not free again for a minute); stopped
// with WSL, or by: wsl -d Ubuntu -- pkill tpm2-simulator. A test that uses a TPM
// boots it, which starts its registers from zero.
public static class TpmSimulators
{
    public const string Distribution = "Ubuntu";
    public const string Simulator = "~/mpai-tpm/ms-tpm-20-ref-1.83r1/TPMCmd/Simulator/src/tpm2-simulator";

    // The parties: the Controller, a host, a host whose code is altered, and one for
    // the tests of the TPM alone. Each uses its port and the next.
    public const int Controller = 2421, Host = 2431, AlteredHost = 2441, Alone = 2451;

    private static readonly object gate = new();

    // The simulator on this port, started where none listens; its address.
    public static (string Host, int Port) At(int port)
    {
        lock (gate)
        {
            var clock = Stopwatch.StartNew();
            TimeSpan? lastStart = null;
            while (!Listening(port))
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(90))
                    throw new InvalidOperationException($"The TPM simulator did not start on port {port}: is it built in WSL at {Simulator}? See ~/mpai-tpm/run/{port}/simulator.log.");
                if (lastStart is null || clock.Elapsed - lastStart > TimeSpan.FromSeconds(5))
                {
                    Start(port);
                    lastStart = clock.Elapsed;
                }
                Thread.Sleep(200);
            }
            return ("127.0.0.1", port);
        }
    }

    private static void Start(int port)
    {
        var info = new ProcessStartInfo("wsl.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-d", Distribution, "--", "bash", "-c",
                     $"mkdir -p ~/mpai-tpm/run/{port} && cd ~/mpai-tpm/run/{port} && " +
                     $"(setsid nohup {Simulator} {port} > simulator.log 2>&1 < /dev/null &); sleep 1" })
            info.ArgumentList.Add(a);
        using var starting = Process.Start(info)!;
        starting.WaitForExit(15000);
    }

    private static bool Listening(int port)
    {
        try
        {
            using var tcp = new TcpClient();
            return tcp.ConnectAsync("127.0.0.1", port).Wait(500) && tcp.Connected;
        }
        catch { return false; }
    }
}
