using System.Text.RegularExpressions;

using Mpai.Aif.Api;

namespace Mpai.Aif.Tests;

// Lifecycle, status, reports and failures across the host (M3217 3.2, 3.3, 3.6):
// Pause, Resume, StopAim and Status reaching an AIM on the host, in an exchange
// and in a Continuous Module; an AIM on the host stopping one here; the host
// killed during a run, under each OnDegraded.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class RemoteLifecycleTests
{
    private const string Text = RemoteTests.Text;

    private static ControllerApi Api(HostProcess hostProcess, params string[] placed)
    {
        var api = new ControllerApi(RemoteTests.Amds, Path.Combine(RemoteTests.Amds, "no-settings.json"), new RemoteAims());
        api.Controller.AimHostKey = HostProcess.Key;
        foreach (var aim in placed) api.Controller.AimHosts[aim] = hostProcess.Address;
        return api;
    }

    private static string Shown(ControllerApi.Read read) => read.Error + (read.Json is { } v ? $" '{v}'" : "");

    private static string Outputs(ControllerApi.Result run) =>
        $"{run.Error}; " + string.Join("; ", run.Outputs.OrderBy(o => o.PortNumber).Select(o => $"#{o.PortNumber} '{o.Json}'"));

    // The reason a link went is the operating system's words: only that it went is kept.
    private static string StatusOf(ControllerApi.ModuleStatus status, string aim) =>
        !status.Ok ? status.Error.ToString()
        : string.Join("; ", status.Aims.Where(a => a.Aim == aim).Select(a =>
            $"{a.Status}" + (a.Reason.Length > 0 ? $" ({Regex.Replace(a.Reason, @"host \S+ was lost: [^;]*", "host <host> was lost")})" : "") +
            (a.Reports.Count > 0 ? $" reported '{string.Join("', '", a.Reports)}'" : "")));

    [Fact]
    public void Lifecycle()
    {
        var result = new Dictionary<string, string>();
        using var hostProcess = new HostProcess();

        // AN EXCHANGE: TST-UPP, TST-SLP, TST-RPT on the host.
        using (var api = Api(hostProcess, "1TST-UPP-V1.0-I01", "1TST-SLP-V1.0-I01", "1TST-RPT-V1.0-I01"))
        {
            const string rxc = "1TST-RXC-V1.0-I01";
            api.StartFlow(rxc);
            result["exchange: a run"] = Outputs(api.Advance(rxc, [new ControllerApi.Datum(Text, "x")]));
            result["exchange: TST-RPT's report"] = StatusOf(api.Status(rxc), "1TST-RPT-V1.0-I01");

            api.Pause(rxc);
            var held = Task.Run(() => api.Advance(rxc, [new ControllerApi.Datum(Text, "p")]));
            var waited = !held.Wait(800);
            api.Resume(rxc);
            result["exchange: Pause, a run, Resume"] = (waited ? "held" : "not held") + "; then " + Outputs(held.Result);

            result["exchange: StopAim TST-UPP, on the host"] = api.StopAim(rxc, "1TST-UPP-V1.0-I01").ToString();
            result["exchange: the next run"] = Outputs(api.Advance(rxc, [new ControllerApi.Datum(Text, "s")]));
            result["exchange: TST-UPP's status"] = StatusOf(api.Status(rxc), "1TST-UPP-V1.0-I01");
            api.StopFlow(rxc);
        }

        // AN AIM ON THE HOST STOPPING ONE HERE: TST-KIL stops TST-UPP.
        using (var api = Api(hostProcess, "1TST-KIL-V1.0-I01"))
        {
            const string rkm = "1TST-RKM-V1.0-I01";
            api.StartFlow(rkm);
            api.Advance(rkm, [new ControllerApi.Datum(Text, "x")]);
            result["TST-KIL on the host stopped TST-UPP here"] = StatusOf(api.Status(rkm), "1TST-UPP-V1.0-I01");
            result["TST-RKM: the next run"] = Outputs(api.Advance(rkm, [new ControllerApi.Datum(Text, "y")]));
            api.StopFlow(rkm);
        }

        // A CONTINUOUS MODULE: TST-ACC on the host.
        using (var api = Api(hostProcess, "1TST-ACC-V1.0-I01"))
        {
            const string rlp = "1TST-RLP-V1.0-I01";
            api.StartFlow(rlp);
            api.InputWrite(rlp, Text, 1, "a", 2000);
            result["continuous: a write, then #1"] = Shown(api.OutputRead(rlp, Text, 1, 2000));
            result["continuous: TST-ACC's status, from its host"] = StatusOf(api.Status(rlp), "1TST-ACC-V1.0-I01");

            api.Pause(rlp);
            api.InputWrite(rlp, Text, 1, "b", 2000);
            var paused = Shown(api.OutputRead(rlp, Text, 1, 800));
            api.Resume(rlp);
            result["continuous: Pause, a write, #1; Resume, #1"] = paused + "; " + Shown(api.OutputRead(rlp, Text, 1, 2000));

            result["continuous: StopAim TST-ACC, on the host"] = api.StopAim(rlp, "1TST-ACC-V1.0-I01").ToString();
            api.InputWrite(rlp, Text, 1, "c", 2000);
            result["continuous: a write, then #1"] += "; after StopAim: " + Shown(api.OutputRead(rlp, Text, 1, 800));
            result["continuous: TST-ACC's status after StopAim"] = StatusOf(api.Status(rlp), "1TST-ACC-V1.0-I01");
            api.StopFlow(rlp);
        }

        Expected.Match("remote-lifecycle.json", result);
    }

    // THE HOST KILLED during a run: its AIM DEGRADED, and each OnDegraded applied.
    [Fact]
    public void HostKilled()
    {
        var result = new Dictionary<string, string>();

        foreach (var (module, policy) in new[] { ("TST-RLP", "Continue"), ("TST-RLA", "StopAIM"), ("TST-RLS", "StopModule") })
        {
            using var hostProcess = new HostProcess();
            using var api = Api(hostProcess, "1TST-ACC-V1.0-I01");
            var name = $"1{module}-V1.0-I01";
            api.StartFlow(name);
            api.InputWrite(name, Text, 1, "a", 2000);
            var before = Shown(api.OutputRead(name, Text, 1, 2000));
            hostProcess.Kill();
            Thread.Sleep(500);
            var write = api.InputWrite(name, Text, 1, "b", 1000);
            var read = Shown(api.OutputRead(name, Text, 1, 800));
            result[$"{module}, OnDegraded {policy}"] =
                $"before: #1 {before}; the host killed; a write: {write}, #1 {read}; TST-ACC {StatusOf(api.Status(name), "1TST-ACC-V1.0-I01")}";
            api.StopFlow(name);
        }

        // An exchange: the AIM fails when fired, and OnDegraded Continue applies.
        using (var hostProcess = new HostProcess())
        using (var api = Api(hostProcess, "1TST-UPP-V1.0-I01", "1TST-SLP-V1.0-I01", "1TST-RPT-V1.0-I01"))
        {
            const string rxc = "1TST-RXC-V1.0-I01";
            api.StartFlow(rxc);
            var before = Outputs(api.Advance(rxc, [new ControllerApi.Datum(Text, "a")]));
            hostProcess.Kill();
            Thread.Sleep(500);
            var status = StatusOf(api.Status(rxc), "1TST-UPP-V1.0-I01");
            var after = Outputs(api.Advance(rxc, [new ControllerApi.Datum(Text, "b")]));
            result["TST-RXC, OnDegraded Continue"] =
                $"before: {before}; the host killed: TST-UPP {status}; a run: {after}; TST-UPP {StatusOf(api.Status(rxc), "1TST-UPP-V1.0-I01")}";
            api.StopFlow(rxc);
        }

        Expected.Match("remote-host-killed.json", result);
    }
}
