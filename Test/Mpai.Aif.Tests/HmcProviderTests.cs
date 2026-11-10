using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.Store;
using Mpai.Hmc.Provider;
using Mpai.Osd.Tma;

namespace Mpai.Aif.Tests;

// COMMUNICATING ENTITIES IN CONTEXT (HMC-CEC V2.2): the provider builds what the L3 of the composite and those of the composites it holds name, down to the leaves.
// The models are not loaded here: a leaf is asked whether it can be built, and the ones with no model are built.
[Trait("Group", "Fast")]
public class HmcProviderTests
{
    private static readonly AmdStore Amds = Scan();
    private static AmdStore Scan() { var s = new AmdStore(Repository.Amds); s.Scan(); return s; }

    // The leaves of an L3, through its composites, by name.
    private static IEnumerable<string> Leaves(string aimName)
    {
        var l3 = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Amds, aimName + ".json")))!;
        var subs = l3["SubAIMs"]?.AsArray().Select(s => s!["Identifier"]!["AIMName"]!.GetValue<string>()).ToArray() ?? [];
        if (subs.Length == 0) return [aimName];
        return subs.SelectMany(Leaves);
    }

    [Fact]
    public void EveryLeafOfTheCompositeIsBuiltByTheProviders()
    {
        using var providers = new DisposableProvider(HmcProviders.For(Amds, Repository.Root));
        var leaves = Leaves(HmcProvider.Cec).Distinct().ToArray();
        Assert.Contains("1HMC-SID-V2.2-I01", leaves);
        Assert.Contains("1OSD-MSM-V1.5-I01", leaves);
        Assert.Contains("1CVE-VII-V1.0-I01", leaves);
        Assert.All(leaves, leaf => Assert.True(providers.Inner.CanCreate(leaf), "no provider builds " + leaf));
    }

    [Fact]
    public void TheHmcsOwnLeavesAreBuiltAndMsmCombines()
    {
        var provider = new HmcProvider(Amds);
        var sid = provider.Create("1HMC-SID-V2.2-I01", new Dictionary<string, string>(), null);
        Assert.IsType<Mpai.Hmc.Sid.HmcSidAimProcessor>(sid);
        var msm = provider.Create("1OSD-MSM-V1.5-I01", new Dictionary<string, string>(), null);
        var ports = AimPortReader.Load(Amds, "1OSD-MSM-V1.5-I01");
        var visual = new JsonObject { ["Header"] = "OSD-BVS-V1.5", ["BasicVisualSceneDescriptorsID"] = "eyes" };
        var audio = new JsonObject { ["Header"] = "OSD-BAS-V1.5", ["BasicAudioSceneDescriptorsID"] = "ears" };
        var first = msm.ProcessAsync(new Message { MessageId = "m", Ports = new Dictionary<string, string> { [ports.Input("OSD-BVS-V1.5")] = visual.ToJsonString() } }).GetAwaiter().GetResult();
        Assert.False(first.IsError, first.Payload);
        var second = msm.ProcessAsync(new Message { MessageId = "m", Ports = new Dictionary<string, string> { [ports.Input("OSD-BAS-V1.5")] = audio.ToJsonString() } }).GetAwaiter().GetResult();
        Assert.Equal(2, JsonNode.Parse(second.Ports[ports.Output("OSD-BMS-V1.5")])!["BasicMultimodalSceneDescriptorsData"]!.AsArray().Count);   // the ECU's one Multimodal Scene
    }

    private sealed class DisposableProvider(IAimProvider inner) : IDisposable
    {
        public IAimProvider Inner { get; } = inner;
        public void Dispose() { (Inner as IDisposable)?.Dispose(); }
    }
}
