using System.Text.Json.Nodes;

using AIF.Controller;
using Mpai.Aif.Api;

namespace Mpai.Aif.Tests;

// THE MAS-APP MODULES, with stand-in AIMs (M3215 3.4): the L3s of the repository,
// each basic AIM replaced by one that answers on every Output Port its L3 declares
// - Automatic Speech Recognition, as the real one does, on the Output Port
// matching the input it was given - and what each Module then gives, recorded.
// Recorded first on MachineExecutor and on the Channels side by side, which gave
// the same outputs in every case; MachineExecutor is gone, and the record stays.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class StandInTests
{
    private static readonly (string Module, (string DataType, int Number, string Json)[] Inputs)[] Cases =
    {
        ("1MMC-MAD-V2.5-I01", [("OSD-BSO-V1.5", 1, "speech")]),
        ("1MMC-MAD-V2.5-I01", [("OSD-BTO-V1.5", 1, "typed")]),
        ("1MMC-AMQ-V2.5-I01", [("OSD-BSO-V1.5", 2, "reply")]),
        ("1MMC-AMQ-V2.5-I01", [("OSD-BVO-V1.5", 1, "picture"), ("OSD-BSO-V1.5", 1, "question")]),
        ("1MMC-AMQ-V2.5-I01", [("OSD-BTO-V1.5", 1, "welcome")]),
        ("1MMC-MAT-V2.5-I01", [("OSD-BSO-V1.5", 1, "speech"), ("OSD-SEL-V1.5", 1, "en-it")]),
        ("1MMC-MPD-V2.5-I01", [("OSD-BSO-V1.5", 1, "speech")]),
        ("1PAF-RSR-V1.6-I01", [("OSD-BTO-V1.5", 1, "words"), ("OSD-BTO-V1.5", 2, "words")]),
        ("1MAS-APP-V1.0-I01", [("OSD-BTO-V1.5", 1, "hello")])
    };

    [Fact]
    public void MasAppModules()
    {
        var result = new Dictionary<string, string>();
        foreach (var (module, inputs) in Cases)
        {
            using var api = new ControllerApi(Repository.Amds, Path.Combine(Repository.Amds, "no-settings.json"), store => new StandIns(store));
            api.StartFlow(module);
            var run = api.Advance(module, inputs.Select(i => new ControllerApi.Datum(i.DataType, i.Number, i.Json)));
            api.StopFlow(module);
            var key = $"{module} given {string.Join(", ", inputs.Select(i => $"{i.DataType}#{i.Number}"))}";
            result[key] = "same: " + run.Error + ": " + string.Join("; ", run.Outputs.OrderBy(o => o.DataType).ThenBy(o => o.PortNumber)
                                                                          .Select(o => $"{o.DataType}#{o.PortNumber} {o.Json}"));
        }
        Expected.Match("standins.json", result);
    }

    // Each basic AIM answers on every Output Port of its L3 with "<AIM>.<Port>(<what it got>)";
    // speech recognition on the one Output Port whose number is that of the input it got.
    private sealed class StandIns(AIF.Store.AmdStore store) : IAimProvider
    {
        public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage)
        {
            var l3 = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Amds, aimName + ".json")))!;
            var ports = l3["ExternalPorts"]!.AsArray().Select(p => (
                Name: p!["Name"]!.GetValue<string>(),
                Direction: p["Direction"]!.GetValue<string>(),
                Number: p["PortNumber"]?.GetValue<int>() ?? 1)).ToList();
            var outputs = ports.Where(p => p.Direction == "Output").ToList();
            var inputs = ports.Where(p => p.Direction == "Input").ToList();
            var short_ = aimName[1..8];
            return new StandIn(aimName, m =>
            {
                var got = string.Join("+", m.Ports.OrderBy(p => p.Key).Select(p => p.Value));
                var answer = aimName.Contains("MMC-ASR")
                    ? outputs.Where(o => inputs.Any(i => m.Ports.ContainsKey(i.Name) && i.Number == o.Number)).ToList()
                    : outputs;
                return new Message { Ports = answer.ToDictionary(o => o.Name, o => $"{short_}.{o.Name}({got})") };
            });
        }
    }

    private sealed class StandIn(string id, Func<Message, Message> run) : IAimProcessor
    {
        public string InstanceId { get; } = id;
        public Task<Message> ProcessAsync(Message message) => Task.FromResult(run(message));
    }
}
