using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.SpatialAudio;

namespace Mpai.Aif.Tests;

// HOW THE SCENE IS RENDERED is a property of the scene: RenderingMode (and ChannelCount, for a Multichannel one) of the Basic Audio Scene Descriptors and of the Audio Scene Descriptors, changed by the Module
// (ASE) on a command (ModifiedScene) like the other properties of the scene. The renderer, the User Agent's, reads it from the scene it is given. The sound output of the machine is the User Agent's and is
// not in the scene.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class AsmRenderingModeTests
{
    private static async Task<UserCommand> Run(AsmRig rig, UserCommandRQ data)
    {
        var command = AsmRig.Command(data);
        return (await rig.Ask(command)).Merged(command.UserCommandID);
    }

    // The Audio Scene Descriptors the Module gave last.
    private static string LastScene(AsmRig rig) => rig.Said.Last(s => s.Contains("\"Header\":\"OSD-ASD-V1.5\""));

    [Fact]
    public async Task TheModeSaidIsInTheSceneTheModuleGivesAndAnotherEditDoesNotChangeIt()
    {
        using var rig = new AsmRig();
        await rig.OpenAudio(AsmRig.Sound("violin"));
        await Run(rig, new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = "stage", Objects = [AsmRig.Put("violin", 1)] } });
        Assert.Null(AudioRendering.LayoutOf(LastScene(rig)));                                 // a scene that was not told how is rendered as the renderer does by default

        var said = await Run(rig, new UserCommandRQ { ModifiedScene = new SceneChange { RenderingMode = "Multichannel", ChannelCount = 6 } });
        Assert.Equal("Done", said.UserCommandRS!.Outcome);
        Assert.Equal(SpatialRenderer.Layout.Surround51, AudioRendering.LayoutOf(LastScene(rig)));

        await Run(rig, new UserCommandRQ { ModifiedScene = new SceneChange { AcousticProfile = new AcousticProfile { AcousticProfileID = "walls", Absorption = 0.3 } } });
        await Run(rig, new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = "stage", Objects = [AsmRig.Put("violin", -1)] } });
        Assert.Equal(SpatialRenderer.Layout.Surround51, AudioRendering.LayoutOf(LastScene(rig)));       // the edits of the acoustics and of the members keep how it is rendered

        await Run(rig, new UserCommandRQ { ModifiedScene = new SceneChange { RenderingMode = "Binaural" } });
        Assert.Equal(SpatialRenderer.Layout.Binaural, AudioRendering.LayoutOf(LastScene(rig)));        // another mode replaces it, and the count of the old one with it
        Assert.DoesNotContain("\"ChannelCount\"", LastScene(rig));
    }

    [Theory]
    [InlineData("Surround", 3)]
    [InlineData("Multichannel", 5)]
    [InlineData("Multichannel", null)]
    public async Task AModeTheRendererCannotRenderIsRefusedWithTheReason(string mode, int? channels)
    {
        using var rig = new AsmRig();
        await rig.OpenAudio(AsmRig.Sound("violin"));
        await Run(rig, new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = "stage", Objects = [AsmRig.Put("violin", 1)] } });
        var said = await Run(rig, new UserCommandRQ { ModifiedScene = new SceneChange { RenderingMode = mode, ChannelCount = channels } });
        Assert.NotEqual("Done", said.UserCommandRS!.Outcome);
    }

    [Theory]
    [InlineData("""{"RenderingMode":"Binaural"}""", SpatialRenderer.Layout.Binaural)]
    [InlineData("""{"RenderingMode":"Stereo"}""", SpatialRenderer.Layout.Stereo)]
    [InlineData("""{"RenderingMode":"Mono"}""", SpatialRenderer.Layout.Mono)]
    [InlineData("""{"RenderingMode":"Multichannel","ChannelCount":4}""", SpatialRenderer.Layout.Quad)]
    [InlineData("""{"RenderingMode":"Multichannel","ChannelCount":6}""", SpatialRenderer.Layout.Surround51)]
    [InlineData("""{"RenderingMode":"Multichannel","ChannelCount":8}""", SpatialRenderer.Layout.Surround71)]
    [InlineData("""{"SubAudioScenes":[{"SubASceneIDOrSubAScene":[{"RenderingMode":"Stereo"}]}]}""", SpatialRenderer.Layout.Stereo)]   // stated by the first sub-scene that states one
    public void TheRendererRendersTheSceneAsItSays(string scene, SpatialRenderer.Layout expected) => Assert.Equal(expected, AudioRendering.LayoutOf(scene));

    [Theory]
    [InlineData("""{"Header":"OSD-ASD-V1.5"}""")]                                  // says nothing of it
    [InlineData("""{"RenderingMode":"Multichannel"}""")]                           // Multichannel without a count
    [InlineData("""{"RenderingMode":"Ambisonics"}""")]                             // not a way this renderer renders
    [InlineData("not json")]
    public void ASceneThatSaysNothingUsableIsRenderedAsTheRendererDoesByDefault(string scene) => Assert.Null(AudioRendering.LayoutOf(scene));
}
