using AIF.Controller;
using Mpai.Aif.Api;

namespace Mpai.Aif.Tests;

// Remote AIMs (M3217): one Controller runs one Module, wherever its AIMs run; an
// AIM placed on another machine - Relation External - is instantiated there by an
// AIM host and reached through the Remote transport. The test Modules are in
// Test/Data/Phase5, each with an Internal twin for comparison.
//
// Today: a Module with an AIM placed elsewhere and no AIM host named for it is
// refused; its Internal twin is built by the Controller. Placed: the AIMs placed
// run on an AIM host in a process of its own, and the Module gives what its twin
// gives.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class RemoteTests
{
    public const string Text = "TST-TXT-V1.0";

    public static string Amds => Path.Combine(Repository.Root, "Test", "Data", "Phase5");

    private static readonly (string Module, bool Continuous)[] Modules =
        [("TST-RXC", false), ("TST-RXL", false), ("TST-RLP", true), ("TST-RLL", true), ("TST-RCP", false), ("TST-RPY", true)];

    // Where each Module's placed AIMs are said to run: the AIM Instances, or the
    // composite that contains them.
    private static readonly Dictionary<string, string[]> Hosted = new()
    {
        ["TST-RXC"] = ["1TST-UPP-V1.0-I01", "1TST-SLP-V1.0-I01", "1TST-RPT-V1.0-I01"],
        ["TST-RLP"] = ["1TST-ACC-V1.0-I01"],
        ["TST-RCP"] = ["1TST-RIN-V1.0-I01"],
        ["TST-RPY"] = ["1TST-PRD-V1.0-I01"]
    };

    [Fact]
    public void Placed()
    {
        var result = new Dictionary<string, string>();
        using var hostProcess = new HostProcess();
        foreach (var (module, continuous) in Modules)
        {
            var aims = new RemoteAims();
            using var api = new ControllerApi(Amds, Path.Combine(Amds, "no-settings.json"), aims);
            api.Controller.AimHostKey = HostProcess.Key;
            foreach (var aim in Hosted.GetValueOrDefault(module) ?? []) api.Controller.AimHosts[aim] = hostProcess.Address;
            result[module] = Run(api, module, continuous, aims);
        }
        Expected.Match("remote-placed.json", result);
    }

    private static string Run(ControllerApi api, string module, bool continuous, RemoteAims aims)
    {
        var name = $"1{module}-V1.0-I01";
        try { api.StartFlow(name); }
        catch (InvalidOperationException refused) { return "refused: " + refused.Message; }
        string outcome;
        if (continuous)
        {
            api.InputWrite(name, Text, 1, "x", 2000);
            var read = api.OutputRead(name, Text, 1, 2000);
            outcome = "a write, then #1: " + (read.Ok ? $"'{MetadataTests.Short(read.Json!, 40)}'" : read.Error.ToString());
        }
        else
        {
            var run = api.Advance(name, [new ControllerApi.Datum(Text, "x")]);
            outcome = $"an exchange: {run.Error}; " + string.Join("; ", run.Outputs.OrderBy(o => o.PortNumber).Select(o => $"#{o.PortNumber} '{o.Json}'"));
        }
        api.StopFlow(name);
        return $"{outcome}; built by the Controller: {string.Join(", ", aims.Built.OrderBy(b => b))}";
    }

    [Fact]
    public void Today()
    {
        var result = new Dictionary<string, string>();
        foreach (var (module, continuous) in Modules)
        {
            var aims = new RemoteAims();
            using var api = new ControllerApi(Amds, Path.Combine(Amds, "no-settings.json"), aims);
            result[module] = Run(api, module, continuous, aims);
        }
        Expected.Match("remote-today.json", result);
    }
}

// The test AIMs of Phase 5, as the Controller's own provider builds them. Built
// records which it built: an AIM placed on a host is not among them.
public sealed class RemoteAims : IAimProvider
{
    private readonly TestAims phase3 = new();
    private readonly ContinuousAims phase4 = new();
    public List<string> Built { get; } = new();

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage)
    {
        lock (Built) Built.Add(aimName[1..8]);
        return aimName switch
        {
            "1TST-DSC-V1.0-I01" or "1TST-ACC-V1.0-I01" or "1TST-PWR-V1.0-I01" or "1TST-PRD-V1.0-I01" => phase4.Create(aimName, settings, storage),
            _ => phase3.Create(aimName, settings, storage)
        };
    }
}
