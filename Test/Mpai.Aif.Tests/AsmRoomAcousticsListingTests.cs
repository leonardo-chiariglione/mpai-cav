using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Aif.Tests;

// THE ROOM ACOUSTICS BUTTON of the ASM window asks for the Acoustic Profile of the scene ("Acoustic Profile", the scene's name) and shows the answer in a panel. A scene made by "place a room 4 by 4" has no
// Acoustic Profile of its own: the answer must still come, with no profile in it, so that the panel shows every property as not set.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class AsmRoomAcousticsListingTests
{
    private static async Task<UserCommand> Run(AsmRig rig, UserCommandRQ data)
    {
        var command = AsmRig.Command(data);
        return (await rig.Ask(command)).Merged(command.UserCommandID);
    }

    [Fact]
    public async Task TheAcousticProfileOfASceneThatHasNoneIsAnsweredAsOneThatHasNothingSet()
    {
        using var rig = new AsmRig();
        await rig.OpenAudio(AsmRig.Sound("violin"));
        await Run(rig, new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = "stage", Objects = [AsmRig.Put("violin", 1)] } });

        var said = await Run(rig, new UserCommandRQ { ListedObjects = new ObjectListing { Kind = "Acoustic Profile", ObjectID = "stage" } });
        Assert.Equal("Done", said.UserCommandRS!.Outcome);
        var listed = Assert.Single(said.UserCommandRS.ListedObjects!);
        Assert.Equal("Acoustic Profile", listed.Kind);
        Assert.Equal("stage", listed.ObjectID);
        Assert.Null(listed.AcousticProfile);
    }
}
