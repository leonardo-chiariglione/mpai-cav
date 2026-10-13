using System.Text.Json.Nodes;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Aif.Tests;

// THE AIMs UNDO (the author, 2026/10/11: the AIMs involved in a User Command must be able to undo): a revocation is executed by the AIM that executed the command
// revoked; its report says Done, or Failed with why. The last command first.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class RevocationAimTests
{
    private static Mpai.Core.UserCommand Command(Mpai.Core.UserCommandData data) => new()
    {
        UserCommandID = Guid.NewGuid().ToString("N")[..8], UserCommandTime = SimpleTime.At(DateTimeOffset.UtcNow), UserCommandData = data
    };
    private static Mpai.Core.UserCommand Revoke(Mpai.Core.UserCommand earlier) => Command(new Mpai.Core.UserCommandData { RevokedUserCommandID = earlier.UserCommandID });
    private static Mpai.Core.ManagedObject Ref(string id) => new() { ObjectID = id };
    private static SpatialAttitude At(double x) => new()
    {
        ObjectSpatialAttitudeID = Guid.NewGuid().ToString(),
        Position = new Position { PositionID = Guid.NewGuid().ToString(), CartPosition = [x, 2, 0] },
        Orientation = new Orientation { OrientationID = Guid.NewGuid().ToString(), EulerAngles = [0, 0, 180] }
    };
    private static BasicAudioObject Sound(string name) => new()
    {
        BasicAudioObjectID = name, BasicAudioObjectData = [new InlineAudioData(Convert.ToBase64String(new byte[64]))], DescrMetadata = name
    };

    private static async Task<(JsonNode? Out, Mpai.Core.UserCommandReport? Report)> Run(AIF.Controller.IAimProcessor aim, string? outPort, string reportPort, params (string Port, string Json)[] inputs)
    {
        var m = new AIF.Controller.Message { MessageId = Guid.NewGuid().ToString(), Ports = inputs.ToDictionary(i => i.Port, i => i.Json) };
        var o = await aim.ProcessAsync(m);
        var report = o.Ports.TryGetValue(reportPort, out var r) && r.Length > 0 ? Mpai.Core.MpaiJson.FromJson<Mpai.Core.UserCommand>(r).UserCommandReport : null;
        return (outPort is not null && o.Ports.TryGetValue(outPort, out var j) ? JsonNode.Parse(j) : null, report);
    }

    private static (string Root, AIF.SharedStorage.FileSharedStorage Storage, AIF.Store.AmdStore Store) Module()
    {
        var root = Path.Combine(Path.GetTempPath(), "asm-undo-" + Guid.NewGuid().ToString("N"));
        var store = new AIF.Store.AmdStore(Repository.Amds); store.Scan();
        return (root, new AIF.SharedStorage.FileSharedStorage(root, "CAE-ASM", "ASM-Undo"), store);
    }

    // An Object's version: the loudness changed, then revoked: the version before is the open one again.
    [Fact]
    public async Task AnEditOfAnObjectIsUndone()
    {
        var (root, storage, store) = Module();
        try
        {
            var ports = AIF.Controller.AimPortReader.Load(store, "1CAE-AOE-V1.0-I01");
            var aoe = new Mpai.Cae.Aoe.AoeAimProcessor("1CAE-AOE-V1.0-I01", new Mpai.Cae.Aoe.AoeAim(storage), ports);
            string objectIn = ports.Input("OSD-BAO-V1.5"), command = ports.Input("CAE-UCM-V1.0"), objectOut = ports.Output("OSD-BAO-V1.5"), report = ports.Output("CAE-UCM-V1.0");

            var (opened, _) = await Run(aoe, objectOut, report, (objectIn, Mpai.Core.MpaiJson.ToJson(Sound("music"))));
            var original = (string)opened!["BasicAudioObjectID"]!;

            var louder = Command(new Mpai.Core.UserCommandData { LUFS = -12 });
            var (changed, said) = await Run(aoe, objectOut, report, (command, Mpai.Core.MpaiJson.ToJson(louder)));
            Assert.NotEqual(original, (string)changed!["BasicAudioObjectID"]!);                     // a new version
            Assert.Equal("Done", said!.Outcome);

            var (restored, undone) = await Run(aoe, objectOut, report, (command, Mpai.Core.MpaiJson.ToJson(Revoke(louder))));
            Assert.Equal("Done", undone!.Outcome);
            Assert.Equal("RevokedUserCommandID", undone.Actions[0].Action);
            Assert.Equal(original, (string)restored!["BasicAudioObjectID"]!);                        // the version before is open again
            Assert.Equal(original, undone.Actions[0].ResultID);

            // Nothing is left to undo: the same revocation again is not done.
            var (_, again) = await Run(aoe, objectOut, report, (command, Mpai.Core.MpaiJson.ToJson(Revoke(louder))));
            Assert.Equal("Failed", again!.Outcome);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // The last command first: an earlier one is refused while a later one stands.
    [Fact]
    public async Task TheLastCommandIsUndoneFirst()
    {
        var (root, storage, store) = Module();
        try
        {
            var aoePorts = AIF.Controller.AimPortReader.Load(store, "1CAE-AOE-V1.0-I01");
            var aoe = new Mpai.Cae.Aoe.AoeAimProcessor("1CAE-AOE-V1.0-I01", new Mpai.Cae.Aoe.AoeAim(storage), aoePorts);
            var asePorts = AIF.Controller.AimPortReader.Load(store, "1CAE-ASE-V1.0-I01");
            var ase = new Mpai.Cae.Ase.AseAimProcessor("1CAE-ASE-V1.0-I01", new Mpai.Cae.Ase.AseAim(storage), asePorts);
            string objectIn = aoePorts.Input("OSD-BAO-V1.5"), objectOut = aoePorts.Output("OSD-BAO-V1.5"), objectReport = aoePorts.Output("CAE-UCM-V1.0"),
                   command = asePorts.Input("CAE-UCM-V1.0"), sceneOut = asePorts.Output("OSD-BAS-V1.5"), sceneReport = asePorts.Output("CAE-UCM-V1.0");
            var (a, _) = await Run(aoe, objectOut, objectReport, (objectIn, Mpai.Core.MpaiJson.ToJson(Sound("anna"))));
            var (b, _) = await Run(aoe, objectOut, objectReport, (objectIn, Mpai.Core.MpaiJson.ToJson(Sound("bruno"))));
            string idA = (string)a!["BasicAudioObjectID"]!, idB = (string)b!["BasicAudioObjectID"]!;
            int Members(JsonNode? scene) => scene?["BasicAudioSceneDescriptorsData"]?.AsArray().Count ?? 0;

            var addA = Command(new Mpai.Core.UserCommandData { AddedObjects = new Mpai.Core.ObjectPlacements { Objects = [new Mpai.Core.ObjectPlacement { ObjectID = Ref(idA), SpatialAttitude = At(-1) }] } });
            var addB = Command(new Mpai.Core.UserCommandData { AddedObjects = new Mpai.Core.ObjectPlacements { Objects = [new Mpai.Core.ObjectPlacement { ObjectID = Ref(idB), SpatialAttitude = At(1) }] } });
            var (one, _) = await Run(ase, sceneOut, sceneReport, (command, Mpai.Core.MpaiJson.ToJson(addA)));
            var (two, _) = await Run(ase, sceneOut, sceneReport, (command, Mpai.Core.MpaiJson.ToJson(addB)));
            Assert.Equal((1, 2), (Members(one), Members(two)));

            var (_, early) = await Run(ase, sceneOut, sceneReport, (command, Mpai.Core.MpaiJson.ToJson(Revoke(addA))));
            Assert.Equal("Failed", early!.Outcome);
            Assert.Contains("later", early.Actions[0].Reason);

            var (backToOne, second) = await Run(ase, sceneOut, sceneReport, (command, Mpai.Core.MpaiJson.ToJson(Revoke(addB))));
            Assert.Equal("Done", second!.Outcome);
            Assert.Equal(1, Members(backToOne));

            var (nothing, first) = await Run(ase, sceneOut, sceneReport, (command, Mpai.Core.MpaiJson.ToJson(Revoke(addA))));
            Assert.Equal("Done", first!.Outcome);
            Assert.Null(nothing);                                                                 // the Scene that the command created is gone: nothing is open

            var (_, unknown) = await Run(ase, sceneOut, sceneReport, (command, Mpai.Core.MpaiJson.ToJson(Command(new Mpai.Core.UserCommandData { RevokedUserCommandID = "never-sent" }))));
            Assert.Equal("Failed", unknown!.Outcome);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // A Speech Object is edited in place: what the command overwrote is put back.
    [Fact]
    public async Task ASpeechObjectEditedInPlaceIsPutBack()
    {
        var (root, storage, store) = Module();
        try
        {
            var ports = AIF.Controller.AimPortReader.Load(store, "1CAE-SOE-V1.0-I01");
            var soe = new Mpai.Cae.Soe.SoeAimProcessor("1CAE-SOE-V1.0-I01", storage, ports);
            string objectIn = ports.Input("OSD-BSO-V1.5", 1), command = ports.Input("CAE-UCM-V1.0"), report = ports.Output("CAE-UCM-V1.0");
            await Run(soe, null, report, (objectIn, Mpai.Core.MpaiJson.ToJson(new BasicSpeechObject { BasicSpeechObjectID = "BSO000001" })));
            Assert.Null(Mpai.Cae.Soe.SpeechStore.Get(storage, "BSO000001")!.BasicSpeechObjectSpaceTime);

            var place = Command(new Mpai.Core.UserCommandData { ChangedObjects = new Mpai.Core.ObjectChanges { Objects = [new Mpai.Core.ObjectChange { ObjectID = Ref("BSO000001"), SpatialAttitude = At(2) }] } });
            var (_, said) = await Run(soe, null, report, (command, Mpai.Core.MpaiJson.ToJson(place)));
            Assert.Equal("Done", said!.Outcome);
            Assert.NotNull(Mpai.Cae.Soe.SpeechStore.Get(storage, "BSO000001")!.BasicSpeechObjectSpaceTime);

            var (_, undone) = await Run(soe, null, report, (command, Mpai.Core.MpaiJson.ToJson(Revoke(place))));
            Assert.Equal("Done", undone!.Outcome);
            Assert.Null(Mpai.Cae.Soe.SpeechStore.Get(storage, "BSO000001")!.BasicSpeechObjectSpaceTime);   // where it was: nowhere in particular
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
