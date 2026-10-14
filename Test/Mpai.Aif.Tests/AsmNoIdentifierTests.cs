using System.Text.Json.Nodes;

using AIF.Controller;
using AIF.SharedStorage;
using Mpai.Cae.Aoe;
using Mpai.Cae.Ase;
using Mpai.Core;

namespace Mpai.Aif.Tests;

// THE USER AGENT KNOWS NO IDENTIFIER OF AN OBJECT (the author, 2026/10/14). An Object is given to the Module itself - the Object or ID of the data types - and the Module reads
// the identifier that is in it; an Object in a Scene is named by where it is (its PoV); the Audio Object the Module makes comes back whole, with its Objects in it.
[Trait("Group", "Fast")]
public class AsmNoIdentifierTests
{
    private static BasicAudioObject Sound(string name) => new()
    {
        BasicAudioObjectID = "", BasicAudioObjectData = [new InlineAudioData(Convert.ToBase64String(new byte[64]))], DescrMetadata = name
    };

    private static UserCommand Command(UserCommandData data) => new() { UserCommandID = Guid.NewGuid().ToString(), UserCommandTime = SimpleTime.At(DateTimeOffset.UtcNow), UserCommandData = data };

    private static SpatialAttitude At(double x) => new()
    {
        ObjectSpatialAttitudeID = Guid.NewGuid().ToString(),
        Position = new Position { PositionID = Guid.NewGuid().ToString(), CartPosition = [x, 2, 0] },
        Orientation = new Orientation { OrientationID = Guid.NewGuid().ToString(), EulerAngles = [0, 0, 180] }
    };

    private static ManagedObject Itself(string baoJson) => new() { BasicAudioObject = MpaiJson.FromJson<BasicAudioObject>(baoJson) };

    // The members of a Scene: the name each carries in its description, and where it is along x.
    private static List<(string Name, double X)> Members(string scene) =>
        JsonNode.Parse(scene)!["BasicAudioSceneDescriptorsData"]!.AsArray()
            .Select(e => ((string)e!["AObjectIDOrAObject"]![0]!["DescrMetadata"]!, (double)e["AudioObjectSpaceTime"]!["SpatialAttitude1"]!["Position"]!["CartPosition"]![0]!)).ToList();

    [Fact]
    public async Task AnObjectIsGivenItselfAndNamedByItsPoV()
    {
        var root = Path.Combine(Path.GetTempPath(), "asm-noid-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new FileSharedStorage(root, "CAE-ASM", "ASM-Test");
            var store = new AIF.Store.AmdStore(Repository.Amds); store.Scan();
            var aoePorts = AimPortReader.Load(store, "1CAE-AOE-V1.0-I01"); var asePorts = AimPortReader.Load(store, "1CAE-ASE-V1.0-I01");
            var aoe = new AoeAimProcessor("1CAE-AOE-V1.0-I01", new AoeAim(storage), aoePorts);
            var ase = new AseAimProcessor("1CAE-ASE-V1.0-I01", new AseAim(storage), asePorts);

            async Task<Message> Give(IAimProcessor aim, params (string Port, string Json)[] inputs) =>
                await aim.ProcessAsync(new Message { MessageId = Guid.NewGuid().ToString(), Ports = inputs.ToDictionary(i => i.Port, i => i.Json) });
            string Object(Message m) => m.Ports[aoePorts.Output("OSD-BAO-V1.5")];
            string Scene(Message m) => m.Ports[asePorts.Output("OSD-BAS-V1.5")];
            UserCommandReport Report(Message m, IAimProcessor aim) => MpaiJson.FromJson<UserCommand>(m.Ports[(aim == aoe ? aoePorts : asePorts).Output("CAE-UCM-V1.0")]).UserCommandReport!;

            // Two sounds, given to the Module: what it gives back is the Object, as the User Agent will hold it.
            var violin = Object(await Give(aoe, (aoePorts.Input("OSD-BAO-V1.5"), MpaiJson.ToJson(Sound("violin")))));
            var drums = Object(await Give(aoe, (aoePorts.Input("OSD-BAO-V1.5"), MpaiJson.ToJson(Sound("drums")))));

            // Both in the Scene: the Objects themselves, each where it is put.
            var built = await Give(ase, (asePorts.Input("CAE-UCM-V1.0"), MpaiJson.ToJson(Command(new UserCommandData
            {
                AddedObjects = new ObjectPlacements { Objects = [new ObjectPlacement { ObjectID = Itself(violin), SpatialAttitude = At(1) }, new ObjectPlacement { ObjectID = Itself(drums), SpatialAttitude = At(3) }] }
            }))));
            Assert.Equal("Done", Report(built, ase).Outcome);
            Assert.Equal([("violin", 1.0), ("drums", 3.0)], Members(Scene(built)));

            // The violin made quieter: the command holds the Object to modify, itself, and no Object is opened beforehand.
            var profile = new AcousticProfile { AcousticProfileID = "p", FrequencyRange = new FrequencyRange { MinFrequencyHz = 40, MaxFrequencyHz = 16000 }, Loudness = -18 };
            var modified = await Give(aoe, (aoePorts.Input("CAE-UCM-V1.0"), MpaiJson.ToJson(Command(new UserCommandData
            {
                ModifiedObjects = new ObjectChanges { Objects = [new ObjectChange { ObjectID = Itself(violin), AcousticProfile = profile }] }
            }))));
            Assert.Equal("Done", Report(modified, aoe).Outcome);
            var quieter = Object(modified);
            Assert.Equal(-18, (double)JsonNode.Parse(quieter)!["BasicAudioObjectProperties"]!["AcousticProfile"]!["Loudness"]!);

            // The new version in the Scene in place of the old one, in one command: the one at the PoV taken out, the new one put there.
            var replaced = await Give(ase, (asePorts.Input("CAE-UCM-V1.0"), MpaiJson.ToJson(Command(new UserCommandData
            {
                RemovedObjects = new ObjectPlacements { Objects = [new ObjectPlacement { SpatialAttitude = At(1) }] },
                AddedObjects = new ObjectPlacements { Objects = [new ObjectPlacement { ObjectID = Itself(quieter), SpatialAttitude = At(1) }] }
            }))));
            Assert.Equal("Done", Report(replaced, ase).Outcome);
            var members = Members(Scene(replaced));
            Assert.Equal(2, members.Count);
            Assert.Contains(("violin", 1.0), members);
            Assert.Equal(-18, (double)JsonNode.Parse(Scene(replaced))!["BasicAudioSceneDescriptorsData"]![1]!["AObjectIDOrAObject"]![0]!["BasicAudioObjectProperties"]!["AcousticProfile"]!["Loudness"]!);

            // The drums moved: named by where they are, and nothing else.
            var moved = await Give(ase, (asePorts.Input("CAE-UCM-V1.0"), MpaiJson.ToJson(Command(new UserCommandData
            {
                MovedObjects = new ObjectMovements { Objects = [new ObjectMovement { OldSpatialAttitude = At(3), NewSpatialAttitude = At(5) }] }
            }))));
            Assert.Equal("Done", Report(moved, ase).Outcome);
            Assert.Contains(("drums", 5.0), Members(Scene(moved)));

            // An Audio Object made of them, each with its place: it comes back whole, with its Objects in it.
            var made = await Give(aoe, (aoePorts.Input("CAE-UCM-V1.0"), MpaiJson.ToJson(Command(new UserCommandData
            {
                AddedObjects = new ObjectPlacements { Objects = [new ObjectPlacement { ObjectID = Itself(quieter), SpatialAttitude = At(1) }, new ObjectPlacement { ObjectID = Itself(drums), SpatialAttitude = At(5) }] }
            }))));
            Assert.Equal("Done", Report(made, aoe).Outcome);
            var auo = JsonNode.Parse(made.Ports[aoePorts.Output("OSD-AUO-V1.5")])!;
            Assert.Equal(2, auo["BasicAudioObjects"]!.AsArray().Count);
            Assert.All(auo["BasicAudioObjects"]!.AsArray(), e => Assert.Contains("BasicAudioObjectData", e!.ToJsonString()));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
