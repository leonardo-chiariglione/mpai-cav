using System.Text.Json.Nodes;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Aif.Tests;

// EVERYTHING IN ASM IS FULL; A BASIC OBJECT OR SCENE IS THE CASE OF ONE PART (the author, 2026/11/08). The acquisition functions (audio, speech, visual object acquisition) give Basic or Full Objects, so
// whatever comes after takes Full, or generality is lost. These tests give the editing AIMs a Full Audio Object of several Basic ones, a Full Speech Object, a Full Audio Scene and a Full Speech Scene
// of several sub-scenes - and, each time, the Basic case as the case of one part, which behaves as it did.
//
// CHOICES (the author may overturn them): a Basic Audio or Speech Scene holds Basic Objects, so a Full Object is placed in it PART BY PART, each part where the Object's placement and its own put it; a
// Full Scene is the frame the open Basic Scene is edited in and is given back whole, with each Basic Scene as it now stands; the Level and the Acoustic Profile of a Full Audio Object are those of each
// Basic Audio Object in it; a Speech Object of several is not translated (name a Basic Speech Object).
[Trait("Group", "Fast")]
public class AsmFullTests
{
    private static readonly PointOfView Listener = new() { PointOfViewID = "user", CartPosition = [0, 0, 1.6], Orientation = [0, 0, 0] };

    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;
    private static string Ucm(AsmRig rig, UserCommandRQ rq) => AsmRig.Json(AsmRig.Command(rq));

    // The parts, each one metre further along the x axis than the one before (their own placement inside the Object).
    private static BasicAudioObject[] Parts(params string[] names) => names.Select((n, i) => AsmRig.Sound(n, 1, (byte)(i + 1))).ToArray();

    // The response of the AIM says every action was done; if not, why.
    private static void Done(string reportJson)
    {
        var rs = AsmRig.Response(reportJson);
        Assert.True(rs.Outcome == "Done", rs.Outcome + ": " + string.Join("; ", rs.Actions.Where(x => x.Outcome != "Done").Select(x => x.Action + " " + x.ObjectID + " " + x.Reason)));
    }

    // ---- Audio Object Editing ----------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFullAudioObjectOfSeveralBasicOnesIsKeptOpenAndEditedPartByPart()
    {
        using var rig = new AsmRig();
        var full = Forms.FullAuo("band", Parts("drums", "bass", "keys"));
        var said = await rig.Give(rig.Aoe, (rig.AoePorts.Input(AsmRig.Ucm), Forms.Acquire(full)));
        var given = Parse(said.Ports[rig.AoePorts.Output(AsmRig.Auo)]);
        Assert.Equal("band", given["AudioObjectID"]!.GetValue<string>());
        Assert.Equal(3, given["BasicAudioObjects"]!.AsArray().Count);                                         // all the parts, not the first

        // It is open: a command that names no Object acts on it - each Basic Audio Object has the loudness.
        var loud = await rig.Give(rig.Aoe, (rig.AoePorts.Input(AsmRig.Ucm), Ucm(rig, new UserCommandRQ
        {
            ModifiedObjects = new ObjectChanges { Objects = [new ObjectChange { ObjectID = AsmRig.Ref("band"), LUFS = -20 }] }
        })));
        Done(loud.Ports[rig.AoePorts.Output(AsmRig.Ucm)]);
        var after = Parse(loud.Ports[rig.AoePorts.Output(AsmRig.Auo)]);
        Assert.Equal(3, after["BasicAudioObjects"]!.AsArray().Count);
        foreach (var part in after["BasicAudioObjects"]!.AsArray())
            Assert.Equal(-20.0, part!["BAObjectIDOrBAObject"]![0]!["BasicAudioObjectProperties"]!["Level"]!.GetValue<double>());

        // Where the listener stands: on the Audio Object.
        var heard = await rig.Give(rig.Aoe, (rig.AoePorts.Input(AsmRig.Ucm), Ucm(rig, new UserCommandRQ { UserPoV = new PointOfView { PointOfViewID = "behind", CartPosition = [0, -2, 1.6], Orientation = [0, 0, 0] } })));
        Assert.Equal("behind", Parse(heard.Ports[rig.AoePorts.Output(AsmRig.Auo)])["UserPoV"]!["PointOfViewID"]!.GetValue<string>());
    }

    [Fact]
    public async Task ALoneBasicAudioObjectIsTheCaseOfOnePartAndBehavesAsItDid()
    {
        using var rig = new AsmRig();
        var said = await rig.Give(rig.Aoe, (rig.AoePorts.Input(AsmRig.Ucm), Forms.Acquire(Forms.Auo(AsmRig.Sound("solo")))));
        var given = Parse(said.Ports[rig.AoePorts.Output(AsmRig.Auo)]);
        Assert.Equal(1, given["BasicAudioObjects"]!.AsArray().Count);
        var loud = await rig.Give(rig.Aoe, (rig.AoePorts.Input(AsmRig.Ucm), Ucm(rig, new UserCommandRQ { ModifiedObjects = new ObjectChanges { Objects = [new ObjectChange { LUFS = -23 }] } })));
        Assert.Equal(-23.0, Parse(Forms.Bao(loud.Ports[rig.AoePorts.Output(AsmRig.Auo)]))["BasicAudioObjectProperties"]!["Level"]!.GetValue<double>());
    }

    // ---- Audio Scene Editing ----------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFullAudioObjectIsPlacedInASceneOnePartAtATimeAtThePlaceItsOwnPlacementGives()
    {
        using var rig = new AsmRig();
        await rig.Give(rig.Aoe, (rig.AoePorts.Input(AsmRig.Ucm), Forms.Acquire(Forms.FullAuo("band", Parts("drums", "bass", "keys")))));      // the parts are at x = 0, 1, 2 inside the Object
        var said = await rig.Give(rig.Ase, (rig.AsePorts.Input(AsmRig.Ucm), Ucm(rig, new UserCommandRQ
        {
            UserPoV = Listener,
            AddedObjects = new ObjectPlacements { ObjectID = "stage", Objects = [AsmRig.Put("band", 10)] }                      // the Object stands at x = 10
        })));
        Done(said.Ports[rig.AsePorts.Output(AsmRig.Ucm)]);
        var scene = Parse(Forms.Bas(said.Ports[rig.AsePorts.Output(AsmRig.Asd)]));
        var members = scene["BasicAudioSceneDescriptorsData"]!.AsArray();
        Assert.Equal(3, members.Count);
        var xs = members.Select(m => m!["AudioObjectSpaceTime"]!["SpatialAttitude1"]!["Position"]!["CartPosition"]![0]!.GetValue<double>()).OrderBy(x => x).ToArray();
        Assert.Equal([10.0, 11.0, 12.0], xs);
        Assert.Equal(["bass", "drums", "keys"], members.Select(m => m!["AObjectIDOrAObject"]![0]!["BasicAudioObjectID"]!.GetValue<string>()).OrderBy(n => n).ToArray());
    }

    [Fact]
    public async Task ABasicAudioObjectIsPlacedInASceneAsItWas()
    {
        using var rig = new AsmRig();
        await rig.Give(rig.Aoe, (rig.AoePorts.Input(AsmRig.Ucm), Forms.Acquire(Forms.Auo(AsmRig.Sound("solo")))));
        var said = await rig.Give(rig.Ase, (rig.AsePorts.Input(AsmRig.Ucm), Ucm(rig, new UserCommandRQ { UserPoV = Listener, AddedObjects = new ObjectPlacements { ObjectID = "stage", Objects = [AsmRig.Put("solo", 4)] } })));
        var members = Parse(Forms.Bas(said.Ports[rig.AsePorts.Output(AsmRig.Asd)]))["BasicAudioSceneDescriptorsData"]!.AsArray();
        Assert.Single(members);
        Assert.Equal(4.0, members[0]!["AudioObjectSpaceTime"]!["SpatialAttitude1"]!["Position"]!["CartPosition"]![0]!.GetValue<double>());
    }

    [Fact]
    public async Task AFullAudioSceneOfSeveralSubScenesIsTheFrameTheOpenSceneIsEditedInAndIsGivenWhole()
    {
        using var rig = new AsmRig();
        foreach (var n in new[] { "drums", "bass" }) await rig.Give(rig.Aoe, (rig.AoePorts.Input(AsmRig.Ucm), Forms.Acquire(Forms.Auo(AsmRig.Sound(n)))));
        string Scene(string scene, string o, double x) => Ucm(rig, new UserCommandRQ { UserPoV = Listener, NewScene = scene, AddedObjects = new ObjectPlacements { ObjectID = scene, Objects = [AsmRig.Put(o, x)] } });
        var a = await rig.Give(rig.Ase, (rig.AsePorts.Input(AsmRig.Ucm), Scene("stage-a", "drums", 1)));
        var b = await rig.Give(rig.Ase, (rig.AsePorts.Input(AsmRig.Ucm), Scene("stage-b", "bass", 2)));
        var basicA = Forms.Bas(a.Ports[rig.AsePorts.Output(AsmRig.Asd)]); var basicB = Forms.Bas(b.Ports[rig.AsePorts.Output(AsmRig.Asd)]);

        // A Full Audio Scene of the two arrives: the first is open, and what the Module gives is the Full Scene.
        var full = Forms.FullAsd("concert", basicA, basicB);
        var opened = await rig.Give(rig.Ase, (rig.AsePorts.Input(AsmRig.Asd), full));
        var given = Parse(opened.Ports[rig.AsePorts.Output(AsmRig.Asd)]);
        Assert.Equal("concert", given["AudioSceneDescriptorsID"]!.GetValue<string>());
        Assert.Equal(2, given["SubAudioScenes"]!.AsArray().Count);

        // An Object put in the open Scene is in the first sub-scene; the other is as it was; both are still given.
        var edited = await rig.Give(rig.Ase, (rig.AsePorts.Input(AsmRig.Ucm), Ucm(rig, new UserCommandRQ { AddedObjects = new ObjectPlacements { Objects = [AsmRig.Put("bass", 3)] } })));
        var now = Parse(edited.Ports[rig.AsePorts.Output(AsmRig.Asd)])["SubAudioScenes"]!.AsArray();
        Assert.Equal(2, now.Count);
        int Members(JsonNode sub) => sub["SubASceneIDOrSubAScene"]![0]!["BasicAudioSceneDescriptorsData"]!.AsArray().Count;
        Assert.Equal(2, Members(now[0]!)); Assert.Equal(1, Members(now[1]!));

        // The other sub-scene is opened by its name, and edited: the first stays as it is.
        var second = await rig.Give(rig.Ase, (rig.AsePorts.Input(AsmRig.Ucm), Ucm(rig, new UserCommandRQ { OpenedScene = "stage-b", AddedObjects = new ObjectPlacements { Objects = [AsmRig.Put("drums", 5)] } })));
        var last = Parse(second.Ports[rig.AsePorts.Output(AsmRig.Asd)])["SubAudioScenes"]!.AsArray();
        Assert.Equal(2, last.Count); Assert.Equal(2, Members(last[0]!)); Assert.Equal(2, Members(last[1]!));
    }

    [Fact]
    public async Task ABasicAudioSceneArrivingAloneIsTheCaseOfOnePartAndIsGivenAsOneSubScene()
    {
        using var rig = new AsmRig();
        await rig.Give(rig.Aoe, (rig.AoePorts.Input(AsmRig.Ucm), Forms.Acquire(Forms.Auo(AsmRig.Sound("drums")))));
        var a = await rig.Give(rig.Ase, (rig.AsePorts.Input(AsmRig.Ucm), Ucm(rig, new UserCommandRQ { UserPoV = Listener, AddedObjects = new ObjectPlacements { ObjectID = "stage-a", Objects = [AsmRig.Put("drums", 1)] } })));
        var opened = await rig.Give(rig.Ase, (rig.AsePorts.Input(AsmRig.Asd), Forms.Asd(Forms.Bas(a.Ports[rig.AsePorts.Output(AsmRig.Asd)]))));
        Assert.Equal(1, Parse(opened.Ports[rig.AsePorts.Output(AsmRig.Asd)])["SubAudioScenes"]!.AsArray().Count);
    }

    // ---- Speech Object Editing and Speech Scene Editing -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFullSpeechObjectIsOpenAndItsPartsAreEditedAndPlacedInASceneOneAtATime()
    {
        using var rig = new AsmRig();
        var full = Forms.FullSpo("panel", AsmRig.Speech("anna"), AsmRig.Speech("ben"), AsmRig.Speech("carla"));
        Mpai.Mmc.Soe.SpeechStore.KeepObject(AsmStorage.Keep(rig.Private, Mpai.Cae.Asm.AsmProvider.Soe), Mpai.Mmc.Soe.SpeechStore.Parse(full));      // a Speech Object of three, kept as Speech Object Editing keeps it (its L3 has no Port that takes one from outside the Module, and Speech Scene Editing only reads Speech Objects)

        // It is opened by its name; the listener, on the Speech Object that is open: every part has it.
        var heard = await rig.Give(rig.Soe, (rig.SoePorts.Input(AsmRig.Ucm), Ucm(rig, new UserCommandRQ
        {
            AcquiredObject = AsmRig.Ref("panel"),
            UserPoV = new PointOfView { PointOfViewID = "left", CartPosition = [-1, 0, 1.6], Orientation = [0, 0, 0] }
        })));
        Assert.Equal(3, Parse(heard.Ports[rig.SoePorts.Output(AsmRig.Spo, 1)])["BasicSpeechObjects"]!.AsArray().Count);
        Done(heard.Ports[rig.SoePorts.Output(AsmRig.Ucm)]);
        foreach (var part in Parse(heard.Ports[rig.SoePorts.Output(AsmRig.Spo, 1)])["BasicSpeechObjects"]!.AsArray())
            Assert.Equal("left", part!["BSObjectIDOrBSObject"]![0]!["UserPoV"]!["PointOfViewID"]!.GetValue<string>());

        // Placed in a Speech Scene, part by part.
        var said = await rig.Give(rig.Sse, (rig.SsePorts.Input(AsmRig.Ucm), Ucm(rig, new UserCommandRQ { UserPoV = Listener, AddedObjects = new ObjectPlacements { ObjectID = "round", Objects = [AsmRig.Put("panel", 5)] } })));
        Done(said.Ports[rig.SsePorts.Output(AsmRig.Ucm)]);
        var scene = Parse(Forms.Bss(said.Ports[rig.SsePorts.Output(AsmRig.Ssd)]));
        Assert.Equal(3, scene["BasicSpeechSceneDescriptors"]!.AsArray().Count);
    }

    [Fact]
    public async Task AFullSpeechSceneOfSeveralSubScenesIsTheFrameTheOpenSceneIsEditedInAndIsGivenWhole()
    {
        using var rig = new AsmRig();
        foreach (var n in new[] { "anna", "ben" }) await rig.OpenSpeech(AsmRig.Speech(n));
        string Scene(string scene, string o, double x) => Ucm(rig, new UserCommandRQ { UserPoV = Listener, AddedObjects = new ObjectPlacements { ObjectID = scene, Objects = [AsmRig.Put(o, x)] } });
        var a = await rig.Give(rig.Sse, (rig.SsePorts.Input(AsmRig.Ucm), Scene("round-a", "anna", 1)));
        var basicA = Forms.Bss(a.Ports[rig.SsePorts.Output(AsmRig.Ssd)]);
        // The second Basic Speech Scene is given in full and is not yet kept: arriving in a Full Scene it is kept.
        var second = new BasicSpeechSceneDescriptors
        {
            BasicSpeechSceneDescriptorsID = "round-b", UserPoV = Listener, ObjectCount = 1,
            BasicSpeechSceneItems = [new BasicSpeechSceneItem { SpeechObject = new BasicSpeechObject { BasicSpeechObjectID = "ben" }, ObjectSpaceTime = new SpaceTime { SpatialAttitude1 = AsmRig.At(2) } }]
        };
        var full = Forms.FullSsd("meeting", basicA, MpaiJson.ToJson(second));
        var opened = await rig.Give(rig.Sse, (rig.SsePorts.Input(AsmRig.Ssd), full));
        var given = Parse(opened.Ports[rig.SsePorts.Output(AsmRig.Ssd)]);
        Assert.Equal("meeting", given["SpeechSceneDescriptorsID"]!.GetValue<string>());
        Assert.Equal(2, given["SubSpeechScenes"]!.AsArray().Count);
        var list = await rig.Give(rig.Sse, (rig.SsePorts.Input(AsmRig.Ucm), Ucm(rig, new UserCommandRQ { ListedObjects = new ObjectListing { Kind = ListKinds.SpeechScene } })));
        Assert.Contains("round-b", AsmRig.Json(MpaiJson.FromJson<UserCommand>(list.Ports[rig.SsePorts.Output(AsmRig.Ucm)])));      // the Scene that was only in the Full Scene is kept
    }

    // ---- Multimodal Scene Editing -------------------------------------------------------------------------------------------------------------------------------------

    // Multimodal Scene Editing takes the Scenes of any medium, Basic or not, the same way: a Full Audio Scene on its Port is a sub-scene of the Multimodal Scene as a Basic one is.
    [Fact]
    public async Task MultimodalSceneEditingTakesAFullAudioSceneAsItTakesABasicOne()
    {
        using var rig = new AsmRig();
        foreach (var n in new[] { "drums", "bass" }) await rig.Give(rig.Aoe, (rig.AoePorts.Input(AsmRig.Ucm), Forms.Acquire(Forms.Auo(AsmRig.Sound(n)))));
        string Scene(string scene, string o, double x) => Ucm(rig, new UserCommandRQ { UserPoV = Listener, NewScene = scene, AddedObjects = new ObjectPlacements { ObjectID = scene, Objects = [AsmRig.Put(o, x)] } });
        var a = await rig.Give(rig.Ase, (rig.AsePorts.Input(AsmRig.Ucm), Scene("stage-a", "drums", 1)));
        var b = await rig.Give(rig.Ase, (rig.AsePorts.Input(AsmRig.Ucm), Scene("stage-b", "bass", 2)));
        var full = Forms.FullAsd("concert", Forms.Bas(a.Ports[rig.AsePorts.Output(AsmRig.Asd)]), Forms.Bas(b.Ports[rig.AsePorts.Output(AsmRig.Asd)]));
        var taken = await rig.Give(rig.Mse, (rig.MsePorts.Input(AsmRig.Asd, 1), full), (rig.MsePorts.Input(AsmRig.Ucm), Ucm(rig, new UserCommandRQ
        {
            UserPoV = Listener, AddedObjects = new ObjectPlacements { ObjectID = "world", Objects = [AsmRig.Put("concert", 0)] }
        })));
        Done(taken.Ports[rig.MsePorts.Output(AsmRig.Ucm)]);
    }
}
