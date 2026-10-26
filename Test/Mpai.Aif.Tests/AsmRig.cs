using System.Text.Json;
using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.SharedStorage;
using Mpai.Cae.Asm;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.SpatialAudio;

namespace Mpai.Aif.Tests;

// THE AUDIO SCENE MANAGEMENT AIMs OVER ONE SHARED STORAGE, with the Audio Scene Management Interface in front, and a Controller's routing between them (CAE-ASM, the author,
// 2026/10/26): the Interface is built by the provider of the Module, as the Controller asks for it; what it gives on the Port of an Editing AIM is delivered to that AIM, and
// what the Editing AIMs give is delivered to the Interface, until nothing more is given. What is left is what leaves the Module at its boundary. Used by the tests of the
// Interface and of the features of an Object.
internal sealed class AsmRig : IDisposable
{
    public const string Ucm = "CAE-UCM-V1.0", Opv = "OSD-OPV-V1.5", Auo = "OSD-AUO-V1.5", Asd = "OSD-ASD-V1.5", Spo = "OSD-SPO-V1.5", Ssd = "OSD-SSD-V1.5", Msd = "OSD-MSD-V1.5", Aji = "MMC-AJI-V2.5";
    public const string AmiName = "1CAE-AMI-V1.0-I01";

    public readonly string Root = Path.Combine(Path.GetTempPath(), "asm-rig-" + Guid.NewGuid().ToString("N"));
    public readonly FileSharedStorage Storage;
    public readonly AimPortReader AmiPorts, AoePorts, AsePorts, SoePorts, SsePorts, MsePorts;
    public readonly IAimProcessor Ami, Aoe, Ase, Soe, Sse, Mse;
    public readonly List<string> Said = new();           // everything the AIMs gave, to be searched
    public readonly List<string> Delivered = new();      // every User Command request the Interface gave an Editing AIM
    public readonly List<string> Left = new();           // every Port the Interface gave at the boundary, with the order they came

    private sealed record Editor(IAimProcessor Aim, string Command, string? Pov, string? Object, string Response, string? DataOut, string? DataIn);
    private readonly Editor[] _editors;

    public AsmRig(string? schemasRoot = null)
    {
        Storage = new FileSharedStorage(Root, "CAE-ASM", "ASM-Rig");
        var store = new AIF.Store.AmdStore(Repository.Amds); store.Scan();
        AmiPorts = AimPortReader.Load(store, AmiName);
        AoePorts = AimPortReader.Load(store, "1CAE-AOE-V1.0-I01"); AsePorts = AimPortReader.Load(store, "1CAE-ASE-V1.0-I01");
        SoePorts = AimPortReader.Load(store, "1CAE-SOE-V1.0-I01"); SsePorts = AimPortReader.Load(store, "1CAE-SSE-V1.0-I01");
        MsePorts = AimPortReader.Load(store, "1OSD-MSE-V1.5-I01");

        // The Interface as the Module's provider makes it; the Editing AIMs as it makes them, over the one Shared Storage.
        var provider = new AsmProvider(Repository.Amds);
        var settings = new Dictionary<string, string> { ["SchemasRoot"] = schemasRoot ?? Repository.Schemas };
        Ami = provider.Create(AsmProvider.Ami, settings, Storage);
        Aoe = provider.Create(AsmProvider.Aoe, settings, Storage); Ase = provider.Create(AsmProvider.Ase, settings, Storage);
        Soe = provider.Create(AsmProvider.Soe, settings, Storage); Sse = provider.Create(AsmProvider.Sse, settings, Storage);
        Mse = provider.Create(AsmProvider.Mse, settings, Storage);

        _editors =
        [
            new(Aoe, AoePorts.Input(Ucm), AoePorts.Input(Opv), AoePorts.Input(Auo), AoePorts.Output(Ucm), AoePorts.Output(Auo), AmiPorts.Input(Auo, 2)),
            new(Ase, AsePorts.Input(Ucm), AsePorts.Input(Opv), AsePorts.Input(Asd), AsePorts.Output(Ucm), AsePorts.Output(Asd), AmiPorts.Input(Asd, 2)),
            new(Soe, SoePorts.Input(Ucm), SoePorts.Input(Opv), SoePorts.Input(Spo, 1), SoePorts.Output(Ucm), SoePorts.Output(Spo, 1), AmiPorts.Input(Spo, 2)),
            new(Sse, SsePorts.Input(Ucm), SsePorts.Input(Opv), SsePorts.Input(Ssd), SsePorts.Output(Ucm), SsePorts.Output(Ssd), AmiPorts.Input(Ssd, 2)),
            new(Mse, MsePorts.Input(Ucm), MsePorts.Input(Opv), null, MsePorts.Output(Ucm), MsePorts.Output(Msd), AmiPorts.Input(Msd, 1))
        ];
    }

    // ---- the boundary ---------------------------------------------------------------------------------------------------------------

    public string RqIn(int _ = 1) => AmiPorts.Input(Ucm, 1);                // the one Port of the Composite at which a User Command arrives
    public string RsOut(int k) => AmiPorts.Output(Ucm, k);                  // the Port at which its response leaves
    public string AjiIn => AmiPorts.Input(Aji);
    public string AjiOut => AmiPorts.Output(Aji);

    public async Task<Message> Give(IAimProcessor aim, params (string Port, string Json)[] inputs)
    {
        var o = await aim.ProcessAsync(new Message { MessageId = Guid.NewGuid().ToString(), Ports = inputs.ToDictionary(i => i.Port, i => i.Json) });
        foreach (var p in o.Ports.Values) if (p.Length > 0) Said.Add(p);
        return o;
    }

    // WHAT LEAVES THE MODULE for what arrives at its boundary: the Interface is given the inputs; what it gives an Editing AIM is delivered to it, and what the Editing AIMs
    // give is delivered to the Interface, until nothing more is given. The Ports of the boundary, in the order they were given.
    public async Task<Boundary> Through(params (string Port, string Json)[] inputs)
    {
        var boundary = new List<(string Port, string Json)>();
        var waiting = new List<(string Port, string Json)>(inputs);
        for (var round = 0; round < 30 && waiting.Count > 0; round++)
        {
            // One message for each Port in a run, as a Channel gives it.
            var batch = new List<(string Port, string Json)>();
            foreach (var item in waiting.ToList())
                if (batch.All(b => b.Port != item.Port)) { batch.Add(item); waiting.Remove(item); }
            var given = await Give(Ami, batch.ToArray());

            foreach (var (port, json) in given.Ports)
            {
                var delivered = false;
                for (var k = 1; k <= 5; k++)
                {
                    var editor = _editors[k - 1];
                    if (port == AmiPorts.Output(Ucm, k + 5))
                    {
                        Delivered.Add(json);
                        var answered = await Give(editor.Aim, (editor.Command, json));
                        if (answered.Ports.TryGetValue(editor.Response, out var response)) waiting.Add((AmiPorts.Input(Ucm, k + 5), response));
                        if (editor.DataOut is not null && answered.Ports.TryGetValue(editor.DataOut, out var data)) waiting.Add((editor.DataIn!, data));
                        delivered = true;
                    }
                    else if (port == AmiPorts.Output(Opv, k))
                    {
                        await Give(editor.Aim, (editor.Pov!, json));
                        delivered = true;
                    }
                }
                // Objects and Scenes towards the Editing AIM that takes them (the Port 2 of the Interface).
                foreach (var (to, k) in new[] { (AmiPorts.Output(Auo, 2), 1), (AmiPorts.Output(Asd, 2), 2), (AmiPorts.Output(Spo, 2), 3), (AmiPorts.Output(Ssd, 2), 4) })
                    if (port == to)
                    {
                        var editor = _editors[k - 1];
                        var answered = await Give(editor.Aim, (editor.Object!, json));
                        if (answered.Ports.TryGetValue(editor.Response, out var response)) waiting.Add((AmiPorts.Input(Ucm, k + 5), response));
                        if (editor.DataOut is not null && answered.Ports.TryGetValue(editor.DataOut, out var data)) waiting.Add((editor.DataIn!, data));
                        delivered = true;
                    }
                if (!delivered) { boundary.Add((port, json)); Left.Add(json); }
            }
        }
        return new Boundary(boundary);
    }

    public sealed class Boundary(List<(string Port, string Json)> all)
    {
        public IReadOnlyList<(string Port, string Json)> All { get; } = all;
        public IReadOnlyList<string> At(string port) => All.Where(a => a.Port == port).Select(a => a.Json).ToList();
        public string? Last(string port) => At(port).LastOrDefault();
    }

    // ---- the data -------------------------------------------------------------------------------------------------------------------

    public static UserCommand Command(UserCommandRQ data) => new() { UserCommandID = Guid.NewGuid().ToString("N")[..8], UserCommandTime = SimpleTime.At(DateTimeOffset.UtcNow), UserCommandRQ = data };
    public static string Json(UserCommand c) => MpaiJson.ToJson(c);
    public static ManagedObject Ref(string name) => new() { ObjectID = name };
    public static SpatialAttitude At(double x, double y = 2) => new()
    {
        ObjectSpatialAttitudeID = Guid.NewGuid().ToString(),
        Position = new Position { PositionID = Guid.NewGuid().ToString(), CartPosition = [x, y, 0] },
        Orientation = new Orientation { OrientationID = Guid.NewGuid().ToString(), EulerAngles = [0, 0, 180] }
    };
    public static ObjectPlacement Put(string name, double x, double y = 2) => new() { ObjectID = Ref(name), SpatialAttitude = At(x, y) };

    // A sound of the seconds given: a WAV of silence at 8 kHz, so that its Duration is known.
    public static BasicAudioObject Sound(string name, double seconds = 1, byte seed = 1) => new()
    {
        BasicAudioObjectID = name, DescrMetadata = name,
        BasicAudioObjectData = [new InlineAudioData(Convert.ToBase64String(SceneAudio.Wav(Enumerable.Range(0, (int)(8000 * seconds)).Select(i => seed * 0.001f * (i % 7)).ToArray(), 1, 8000)))]
    };
    public static BasicSpeechObject Speech(string name, double seconds = 0.5) =>
        new() { BasicSpeechObjectID = name, Data = SceneAudio.Wav(Enumerable.Range(0, (int)(8000 * seconds)).Select(i => 0.002f * (i % 5)).ToArray(), 1, 8000) };

    public static UserCommandRS Response(string json) => MpaiJson.FromJson<UserCommand>(json).UserCommandRS!;

    public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
}
