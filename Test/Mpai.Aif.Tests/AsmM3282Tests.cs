using System.Text.Json.Nodes;
using System.Threading.Tasks;

using Mpai.Core;
using Xunit;

namespace Mpai.Aif.Tests;

// THE SOFTWARE FOLLOWS CAE-UCM AND CAE-ACP AS M3282 CHANGED THEM (the author, 2026/10/28), through the editing AIMs of ASM as one Module (AsmRig): a Spatial Attitude that states only the parts that
// change, the Scene read back (time, level, duration, Listener, room), the room changed in part, an Acoustic Profile by its identifier and kept, the loudness of an Object in its entry.
public class AsmM3282Tests
{
    private static SimpleTime Between(double start, double end) => new()
    {
        SimpleTimeID = "t", SimpleTimeData = [new TimeSegment { FlagsByte = 0, StartTime = start, EndTime = end, AccuracyMode = "single", AccuracyPlusMinus = 0.0, TimeType = false, TimeUnit = "00" }]
    };

    private static async Task<UserCommand> Run(AsmRig rig, UserCommandRQ data)
    {
        var command = AsmRig.Command(data);
        return (await rig.Ask(command)).Merged(command.UserCommandID);
    }

    private static async Task<ListedObject> ListedOf(AsmRig rig, string kind, string? name)
    {
        var said = await Run(rig, new UserCommandRQ { ListedObjects = new ObjectListing { Kind = kind, ObjectID = name } });
        Assert.Equal("Done", said.UserCommandRS!.Outcome);
        return Assert.Single(said.UserCommandRS.ListedObjects!, o => name is null || o.ObjectID == name);
    }

    private const string Room = "{\"Header\":\"OSD-CSP-V1.5\",\"ClosedSpaceID\":\"room\",\"Shape\":[{\"Header\":\"OSD-SLC-V1.5\",\"SimpleLocationData\":[{\"RightParallelepiped\":{\"Header\":\"OSD-RPP-V1.5\",\"Dimensions\":\"3D\",\"CartPositions\":[[-3,-2,0],[3,-2,0],[-3,2,0],[-3,-2,3]]}}]}]}";

    [Fact]
    public async Task ASpatialAttitudeThatStatesOnlyTheTimeOrOnlyThePositionChangesOnlyThat()
    {
        using var rig = new AsmRig();
        await rig.OpenAudio(AsmRig.Sound("violin", 2));
        Assert.Equal("Done", (await Run(rig, new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = "stage", Objects = [AsmRig.Put("violin", 1)] } })).UserCommandRS!.Outcome);

        // Only the time: the position and the orientation are those the Object had.
        var timed = await Run(rig, new UserCommandRQ { MovedObjects = new ObjectMovements { Objects = [new ObjectMovement
            { ObjectID = AsmRig.Ref("violin"), NewSpatialAttitude = new SpatialAttitude { SpatialAttitudeTime = Between(2, 5), PositionStated = false, OrientationStated = false } }] } });
        Assert.Equal("Done", timed.UserCommandRS!.Outcome);
        var member = (await ListedOf(rig, "Audio Scene", "stage")).Members![0];
        Assert.Equal([1.0, 2.0, 0.0], member.PoV!.Position.CartPosition);
        Assert.Equal([0.0, 0.0, 180.0], member.PoV.Orientation.EulerAngles);
        Assert.Equal(2, member.Time!.SimpleTimeData[0].StartTime);
        Assert.Equal(5, member.Time.SimpleTimeData[0].EndTime);

        // Only the position: the time is the one it had.
        var moved = await Run(rig, new UserCommandRQ { MovedObjects = new ObjectMovements { Objects = [new ObjectMovement
            { ObjectID = AsmRig.Ref("violin"), NewSpatialAttitude = new SpatialAttitude { Position = new Position { PositionID = "p", CartPosition = [-1, 3, 0] }, OrientationStated = false } }] } });
        Assert.Equal("Done", moved.UserCommandRS!.Outcome);
        member = (await ListedOf(rig, "Audio Scene", "stage")).Members![0];
        Assert.Equal([-1.0, 3.0, 0.0], member.PoV!.Position.CartPosition);
        Assert.Equal([0.0, 0.0, 180.0], member.PoV.Orientation.EulerAngles);
        Assert.Equal(2, member.Time!.SimpleTimeData[0].StartTime);

        // A change that states nothing is refused, with the reason.
        var nothing = await Run(rig, new UserCommandRQ { MovedObjects = new ObjectMovements { Objects = [new ObjectMovement
            { ObjectID = AsmRig.Ref("violin"), NewSpatialAttitude = new SpatialAttitude { PositionStated = false, OrientationStated = false } }] } });
        Assert.Equal("Failed", nothing.UserCommandRS!.Outcome);
    }

    [Fact]
    public async Task TheSceneIsReadBackWithItsDurationTheLevelAndTheTimeOfEachMemberItsListenerAndItsRoom()
    {
        using var rig = new AsmRig();
        await rig.OpenAudio(AsmRig.Sound("violin", 2));

        // The loudness of an Object is the LUFS of its entry in ModifiedObjects; the Scene tells the one its member has (a Scene holds the version of the Object it was made with).
        var louder = await Run(rig, new UserCommandRQ { ModifiedObjects = new ObjectChanges { Objects = [new ObjectChange { ObjectID = AsmRig.Ref("violin"), LUFS = -18 }] } });
        Assert.Equal("Done", louder.UserCommandRS!.Outcome);
        await Run(rig, new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = "stage", Objects = [AsmRig.Put("violin", 1)] } });
        await Run(rig, new UserCommandRQ { MovedObjects = new ObjectMovements { Objects = [new ObjectMovement
            { ObjectID = AsmRig.Ref("violin"), NewSpatialAttitude = new SpatialAttitude { SpatialAttitudeTime = Between(1, 0), PositionStated = false, OrientationStated = false } }] } });
        var scene = await ListedOf(rig, "Audio Scene", "stage");
        Assert.Equal(-18, scene.Members![0].Level);
        Assert.Equal(3.0, scene.Duration);                                                  // begins at 1 s and lasts the 2 s of the sound

        // The Listener, and the room (none yet, then one).
        var listener = await ListedOf(rig, "Listener", "stage");
        Assert.NotNull(listener.UserPoV);
        Assert.Null((await ListedOf(rig, "Closed Space", "stage")).ClosedSpace);
        Assert.Equal("Done", (await Run(rig, new UserCommandRQ { ClosedSpace = new JsonArray(JsonNode.Parse(Room)) })).UserCommandRS!.Outcome);
        Assert.Equal("room", (string?)(await ListedOf(rig, "Closed Space", "stage")).ClosedSpace!["ClosedSpaceID"]);

        // The loudness of the Scene is not carried by the Audio Scene Descriptors: said, not guessed.
        var scenePart = await Run(rig, new UserCommandRQ { LUFS = -23 });
        Assert.Contains(scenePart.UserCommandRS!.Actions!, a => a.Action == "LUFS" && a.Outcome == "NotSupported");
    }

    private static JsonNode Wall(string material, string face) =>
        JsonNode.Parse("{\"RightParallelepipedID\":\"room\",\"Face\":\"" + face + "\",\"MaterialIDOrMaterial\":[\"" + material + "\"]}")!;

    private static AcousticProfile WallsOf(params JsonNode[] walls) => new() { AcousticProfileID = "walls", Walls = new JsonArray(walls) };

    [Fact]
    public async Task TheWallsGivenAreMergedIntoTheWallsOfTheProfileAndTheOthersKeepTheirMaterial()
    {
        using var rig = new AsmRig();
        await rig.OpenAudio(AsmRig.Sound("violin"));
        await Run(rig, new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = "stage", Objects = [AsmRig.Put("violin", 1)] } });
        await Run(rig, new UserCommandRQ { ClosedSpace = new JsonArray(JsonNode.Parse(Room)) });

        Assert.Equal("Done", (await Run(rig, new UserCommandRQ { ModifiedScene = new SceneChange { AcousticProfile = WallsOf(Wall("carpet", "Floor"), Wall("glass", "Left")) } })).UserCommandRS!.Outcome);
        Assert.Equal("Done", (await Run(rig, new UserCommandRQ { ModifiedScene = new SceneChange { AcousticProfile = WallsOf(Wall("wood", "Floor")) } })).UserCommandRS!.Outcome);

        var walls = (await ListedOf(rig, "Acoustic Profile", "stage")).AcousticProfile!.Walls!;
        Assert.Equal(2, walls.Count);                                                       // the floor named twice is one wall; the left one stays
        Assert.Equal("wood", (string?)walls.Single(w => (string?)w!["Face"] == "Floor")!["MaterialIDOrMaterial"]![0]);
        Assert.Equal("glass", (string?)walls.Single(w => (string?)w!["Face"] == "Left")!["MaterialIDOrMaterial"]![0]);

        // A wall is also a Polygon or a SurfaceID: each is a wall of its own name, merged by it; and the default material replaces.
        var poly = JsonNode.Parse("{\"Polygon\":[[-3,-2,0],[3,-2,0],[3,2,0],[-3,2,0]],\"MaterialIDOrMaterial\":[\"concrete\"]}")!;
        var curved = JsonNode.Parse("{\"SurfaceID\":\"dome\",\"MaterialIDOrMaterial\":[\"plaster\"]}")!;
        await Run(rig, new UserCommandRQ { ModifiedScene = new SceneChange { AcousticProfile = new AcousticProfile { AcousticProfileID = "walls", Walls = new JsonArray(poly, curved), DefaultMaterial = new JsonArray("foam") } } });
        var profile = (await ListedOf(rig, "Acoustic Profile", "stage")).AcousticProfile!;
        Assert.Equal(4, profile.Walls!.Count);
        Assert.Equal("foam", (string?)profile.DefaultMaterial![0]);
        Assert.NotNull(profile.Walls.Single(w => (string?)w!["SurfaceID"] == "dome"));
    }

    [Fact]
    public void TheWallsOfTheProfileAreWhatTheRendererReadsFaceByFaceAPolygonOnAFaceIsThatFace()
    {
        string Scene(string walls, string rest) => "{\"ClosedSpace\":[" + Room + "],\"AcousticProfile\":{\"Header\":\"CAE-ACP-V1.0\",\"AcousticProfileID\":\"p\"" + rest + ",\"Walls\":[" + walls + "]}}";
        string Mat(double a) => "[{\"Header\":\"OSD-MTR-V1.5\",\"MaterialID\":\"m\",\"Acoustic\":{\"Absorption\":[{\"FrequencyHz\":1000,\"Fraction\":" + a.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}]}}]";
        var bare = Mpai.SpatialAudio.AudioRendering.RoomOf(Scene("", ",\"Absorption\":0.1"))!;
        var floor = Mpai.SpatialAudio.AudioRendering.RoomOf(Scene("{\"RightParallelepipedID\":\"room\",\"Face\":\"Floor\",\"MaterialIDOrMaterial\":" + Mat(0.9) + "}", ",\"Absorption\":0.1"))!;
        var polygon = Mpai.SpatialAudio.AudioRendering.RoomOf(Scene("{\"Polygon\":[[-3,-2,0],[3,-2,0],[3,2,0],[-3,2,0]],\"MaterialIDOrMaterial\":" + Mat(0.9) + "}", ",\"Absorption\":0.1"))!;
        var curved = Mpai.SpatialAudio.AudioRendering.RoomOf(Scene("{\"SurfaceID\":\"dome\",\"MaterialIDOrMaterial\":" + Mat(0.9) + "}", ",\"Absorption\":0.1"))!;
        Assert.True(floor.Time < bare.Time);                                                // a floor that absorbs shortens the tail
        Assert.Equal(floor.Time, polygon.Time, 6);                                          // a Polygon on the floor is the floor
        Assert.Equal(bare.Time, curved.Time, 6);                                            // a surface the box does not have is left out
    }

    [Fact]
    public async Task AnAcousticProfileGivenInFullIsKeptUnderItsIdentifierAndThenGivenByIt()
    {
        using var rig = new AsmRig();
        await rig.OpenAudio(AsmRig.Sound("violin"));
        await Run(rig, new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = "stage", Objects = [AsmRig.Put("violin", 1)] } });

        var warm = new AcousticProfile { AcousticProfileID = "warm", Absorption = 0.4, Diffusion = 0.6 };
        Assert.Equal("Done", (await Run(rig, new UserCommandRQ { ModifiedScene = new SceneChange { AcousticProfile = warm } })).UserCommandRS!.Outcome);

        // Kept: listed with no Scene named; the Scene's own is listed with its name.
        var kept = (await Run(rig, new UserCommandRQ { ListedObjects = new ObjectListing { Kind = "Acoustic Profile" } })).UserCommandRS!.ListedObjects!;
        Assert.Contains(kept, o => o.ObjectID == "warm" && o.AcousticProfile!.Absorption == 0.4);
        Assert.Equal(0.4, (await ListedOf(rig, "Acoustic Profile", "stage")).AcousticProfile!.Absorption);

        // A second Scene gets it by its identifier alone (which is a string in the command).
        await Run(rig, new UserCommandRQ { NewScene = "hall" });
        var json = JsonNode.Parse(AsmRig.Json(AsmRig.Command(new UserCommandRQ { ModifiedScene = new SceneChange { AcousticProfile = new AcousticProfile { AcousticProfileID = "warm", Reference = true } } })))!;
        Assert.Equal("warm", (string?)json["UserCommandRQ"]!["ModifiedScene"]!["AcousticProfile"]);
        var applied = await rig.Ask(json.ToJsonString());
        Assert.Equal("Done", applied.Merged((string)json["UserCommandID"]!).UserCommandRS!.Outcome);
        Assert.Equal(0.6, (await ListedOf(rig, "Acoustic Profile", "hall")).AcousticProfile!.Diffusion);

        // An identifier nothing is kept under is refused, with the reason; a kept profile is deleted by its identifier.
        var unknown = await Run(rig, new UserCommandRQ { ModifiedScene = new SceneChange { AcousticProfile = new AcousticProfile { AcousticProfileID = "cold", Reference = true } } });
        Assert.Contains(unknown.UserCommandRS!.Actions!, a => a.Action == "ModifiedScene" && a.Outcome == "Failed" && (a.Reason ?? "").Contains("cold"));
        var deleted = await Run(rig, new UserCommandRQ { DeletedObjects = new ObjectPlacements { Objects = [new ObjectPlacement { AcousticProfileID = "warm" }] } });
        Assert.Equal("Done", deleted.UserCommandRS!.Outcome);
        Assert.DoesNotContain((await Run(rig, new UserCommandRQ { ListedObjects = new ObjectListing { Kind = "Acoustic Profile" } })).UserCommandRS!.ListedObjects ?? [], o => o.ObjectID == "warm");
    }
}
