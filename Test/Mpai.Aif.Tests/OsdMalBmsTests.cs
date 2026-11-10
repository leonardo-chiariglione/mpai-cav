using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.Store;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Osd.Mal;

namespace Mpai.Aif.Tests;

// OSD-MAL-V1.5 (Multimodal Alignment) gives, besides the aligned Audio and Visual Scene Descriptors and their geometry, the Basic Multimodal Scene Descriptors of the Scenes it was given - what the Basic Multimodal
// Scene Description (OSD-BMS) composite of the Reference Model (OSD deck, slide 11) needs of it.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class OsdMalBmsTests
{
    private const string Mal = "1OSD-MAL-V1.5-I01";

    [Fact]
    public async Task ItGivesTheBasicMultimodalSceneDescriptorsOfAllTheScenesItWasGivenInTheOrderAudioSpeechVisual()
    {
        var store = new AmdStore(Repository.Amds); store.Scan();
        var ports = AimPortReader.Load(store, Mal);
        var mal = new OsdMalAimProcessor(Mal, ports);
        var audio = new BasicAudioSceneDescriptors { MInstanceID = "m", BasicAudioSceneDescriptorsID = "ears", BASSpaceTime = new SpaceTime { SpaceTimeID = "t" } };
        var visual = new BasicVisualSceneDescriptors { MInstanceID = "m", BasicVisualSceneDescriptorsID = "eyes" };
        var speech = new JsonObject { ["Header"] = "OSD-BSS-V1.5", ["BasicSpeechSceneDescriptorsID"] = "voices" };

        var said = await mal.ProcessAsync(new Message
        {
            MessageId = "m",
            Ports = new Dictionary<string, string>
            {
                [ports.Input("OSD-BAS-V1.5")] = MpaiJson.ToJson(audio), [ports.Input("OSD-BVS-V1.5")] = MpaiJson.ToJson(visual), [ports.Input("OSD-BSS-V1.5")] = speech.ToJsonString()
            }
        });
        Assert.False(said.IsError, said.Payload);
        var bms = JsonNode.Parse(said.Ports[ports.Output("OSD-BMS-V1.5")])!;
        Assert.Equal("OSD-BMS-V1.5", (string)bms["Header"]!);
        var scenes = bms["BasicMultimodalSceneDescriptorsData"]!.AsArray();
        Assert.Equal(["OSD-BAS-V1.5", "OSD-BSS-V1.5", "OSD-BVS-V1.5"], scenes.Select(e => (string)e!["BXSOrBXSID"]!["Header"]!).ToArray());
        Assert.NotNull(scenes[0]!["BXSSpaceTime"]);                                                  // each Scene in the frame of the whole
        Assert.True(said.Ports.ContainsKey(ports.Output("OSD-BAG-V1.5")));                          // what it gave before it still gives
    }
}
