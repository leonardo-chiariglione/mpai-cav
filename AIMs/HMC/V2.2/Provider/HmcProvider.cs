using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.Store;

using Mpai.Aims.Asr;
using Mpai.Cav.Hci;
using Mpai.Core;
using Mpai.Hmc.Sid;
using Mpai.Osd.Tma;
using Mpai.Providers;

namespace Mpai.Hmc.Provider;

// THE SUB-AIMs OF COMMUNICATING ENTITIES IN CONTEXT (HMC-CEC V2.2), by the names the L3s give them. What the leaves of 1HMC-CEC-V2.2-I01 are, composites included (the Multimodal Frontend Description, the Entity and Context
// Understanding, Text and Speech Translation, Personal Status Extraction), is built by the providers that already build them: the HCI's (the scene and the identification AIMs), the leaf provider (ASR, NLU, TTT, PMX,
// the emotion AIMs) and the Voice Command Interface's (Entity Dialogue Processing, the instance I03). Three leaves are the HMC's own and are built here:
//   HMC-SID  - Multimodal Scene Integration and Description
//   MMC-ASR  - the instance I02 (the Speech to Recognise on Port 3, as the ECU gives it)
//   OSD-MSM  - Multimodal Scene Multiplexing, combining: the Multimodal Scene Descriptors of the ECU hold the Visual and the Audio Scene Descriptors last received
public sealed class HmcProvider(AmdStore store) : IAimProvider
{
    public const string Cec = "1HMC-CEC-V2.2-I01";

    public string? ImplementationOf(string aimName) => aimName switch
    {
        "1HMC-SID-V2.2-I01" => typeof(HmcSidAimProcessor).Assembly.Location,
        "1MMC-ASR-V2.5-I02" => typeof(AsrAimProcessor).Assembly.Location,
        "1OSD-MSM-V1.5-I01" => typeof(MultimodalSceneMultiplexing).Assembly.Location,
        _ => null
    };

    public bool CanCreate(string aimName) => aimName is "1HMC-SID-V2.2-I01" or "1MMC-ASR-V2.5-I02" or "1OSD-MSM-V1.5-I01";

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage) =>
        aimName switch
        {
            "1HMC-SID-V2.2-I01" => new HmcSidAimProcessor(aimName, AimPortReader.Load(store, aimName)),
            "1MMC-ASR-V2.5-I02" => new AsrAimProcessor(aimName, AsrFactory.Create(settings), AimPortReader.Load(store, aimName)),
            "1OSD-MSM-V1.5-I01" => new MsmForTheModule(aimName, AimPortReader.Load(store, aimName)),
            _ => throw new NotSupportedException($"HmcProvider does not provide '{aimName}'.")
        };
}

// The providers of the Module of 1HMC-CEC-V2.2-I01: the HMC's own, then those that build the rest.
public static class HmcProviders
{
    public static CompositeProvider For(AmdStore store, string root) =>
        new(new HmcProvider(store), new HciProvider(store, root), new LeafAimsProvider(store), new VciProvider(store));
}

// Multimodal Scene Multiplexing (combining) as the Module runs it: a Message at a time. The Scenes in the Message are handed to the AIM, and the Multimodal Scene it gives last is what the Message answers with;
// the AIM keeps the Scenes it has been given, so the next Message gives the Multimodal Scene of all of them.
internal sealed class MsmForTheModule(string instanceId, AimPortReader ports) : IAimProcessor
{
    private readonly MultimodalSceneMultiplexing msm = new(instanceId, combine: true);
    private readonly (string DataType, string Port)[] inputs = new[] { "OSD-BSS-V1.5", "OSD-BAS-V1.5", "OSD-BVS-V1.5", "OSD-BTO-V1.5", "OSD-IID-V1.5" }
        .Where(t => ports.InputOrDefault(t, "").Length > 0).Select(t => (t, ports.Input(t))).ToArray();
    private readonly string bms = ports.Output("OSD-BMS-V1.5");

    public string InstanceId => instanceId;

    public Task<Message> ProcessAsync(Message message)
    {
        JsonObject? last = null;
        foreach (var (dataType, port) in inputs)
        {
            if (!message.Ports.TryGetValue(port, out var json) || JsonNode.Parse(json) is not JsonObject datum) continue;
            foreach (var given in msm.Handle(dataType, 1, datum))
                if (given.DataType == "OSD-BMS-V1.5") last = given.Json;
        }
        if (last is null) return Task.FromResult(Message.Error(message.MessageId, instanceId, "no Scene to multiplex"));
        return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = message.MessageType, Ports = new Dictionary<string, string> { [bms] = last.ToJsonString() } });
    }
}
