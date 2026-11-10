using System.Text.RegularExpressions;
using Mpai.Cae.Asm;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Mmc.Soe;

namespace Mpai.Aif.Tests;

// THE STORAGE RULE (the author, 2026/11/08): "always write data type and port number. When Shared Storage also write the Module name as indicated in L3." What the editing AIMs of
// Audio Scene Management keep is kept as instances of Data Types, at <Data Type>#<Port>/<the Object's own ID>@<version> in the Private Storage of the Module; the Objects and Scenes the Module
// gives are also in its Shared Storage, at <Module name>/<Data Type>#<Port>/<ID>. No table between a name and a key, no counter.
[Trait("Group", "Fast")]
public class AsmStorageRuleTests
{
    private static readonly PointOfView Listener = new() { PointOfViewID = "user", CartPosition = [0, 0, 1.6], Orientation = [0, 0, 0] };

    private static async Task Work(AsmRig rig)
    {
        await rig.Ask(AsmRig.Command(new UserCommandRQ { AcquiredObject = new ManagedObject { BasicAudioObject = AsmRig.Sound("violin") } }));
        await rig.Ask(AsmRig.Command(new UserCommandRQ { UserPoV = Listener, AddedObjects = new ObjectPlacements { ObjectID = "stage", Objects = [AsmRig.Put("violin", 2)] } }));
        await rig.OpenSpeech(AsmRig.Speech("hello"));
        await rig.Ask(AsmRig.Command(new UserCommandRQ { UserPoV = Listener, AddedObjects = new ObjectPlacements { ObjectID = "dialogue", Objects = [AsmRig.Put("hello", 3)] } }));
        await rig.Ask(AsmRig.Command(new UserCommandRQ { ChangedObjects = new ObjectChanges { Objects = [new ObjectChange { ObjectID = AsmRig.Ref("violin"), Level = -9 }] } }));    // a new version
    }

    private static IReadOnlyList<string> PrivateKeys(AsmRig rig) => AsmStorage.Handle(rig.Private, AsmProvider.Aoe).MPAI_AIFM_RuledStorage_List(null, "");

    [Fact]
    public async Task TheEditorsKeepWhatTheyEditInThePrivateStorageAndOfferTheirObjectsAndScenesInTheSharedStorageWithTheModuleNameInTheKey()
    {
        using var rig = new AsmRig();
        await Work(rig);

        // Private: the working data, by Data Type, Port, ID and version.
        var keys = PrivateKeys(rig);
        foreach (var expected in new[] { "OSD-BAO-V1.5#1/violin@1", "OSD-BAO-V1.5#1/violin@2", "OSD-ASD-V1.5#1/stage@1", "OSD-BSO-V1.5#2/hello@1", "OSD-SSD-V1.5#1/dialogue@1" })
            Assert.Contains(expected, keys);

        // Shared: only what the Module offers - the Audio Object, Audio Scene, Speech Object and Speech Scene it gives - the Module name first, and no version.
        var shared = rig.Storage.MPAI_AIFM_SharedStorage_List("");
        Assert.NotEmpty(shared);
        Assert.All(shared, k => Assert.Matches($@"^{Regex.Escape(AsmProvider.Module)}/(OSD-AUO-V1\.5|OSD-ASD-V1\.5|OSD-SPO-V1\.5|OSD-SSD-V1\.5|OSD-MSD-V1\.5)#\d+/[^@/]+$", k));
        foreach (var expected in new[] { "OSD-AUO-V1.5#1/violin", "OSD-ASD-V1.5#1/stage", "OSD-SPO-V1.5#1/hello", "OSD-SSD-V1.5#1/dialogue" })
            Assert.Contains($"{AsmProvider.Module}/{expected}", shared);
        Assert.DoesNotContain(shared, k => k.Contains("OSD-BAO") || k.Contains("OSD-BSO") || k.Contains("CAE-ACP"));      // the sounds, the speech and the profiles are the Module's own
    }

    [Fact]
    public async Task EveryStoredKeyIsADataTypeAPortAndAnIdAndNoTableOrCounterIsKept()
    {
        using var rig = new AsmRig();
        await Work(rig);
        var keys = PrivateKeys(rig);
        Assert.NotEmpty(keys);
        Assert.All(keys, k => Assert.Matches(@"^[A-Z]{3}-[A-Z]{3}-V\d+\.\d+#\d+/.+@\d+$", k));
        foreach (var forbidden in new[] { "name:", "keyname:", "prevkey:", "ref:", "refby:", "_counter:" })
        {
            Assert.DoesNotContain(keys, k => k.StartsWith(forbidden, StringComparison.Ordinal));
            Assert.DoesNotContain(rig.Storage.MPAI_AIFM_SharedStorage_List(""), k => k.StartsWith(forbidden, StringComparison.Ordinal));
        }
        // The older keys (BAO000001 ...) are not made either.
        Assert.DoesNotContain(keys, k => Regex.IsMatch(k, @"^(BAO|AUO|BAS|BSO|SPO|BSS|MSD)\d{6}"));
    }

    [Fact]
    public async Task SpeechSceneEditingReadsTheSpeechObjectSpeechObjectEditingKeptByDataTypeAndPortAndDoesNotWriteIt()
    {
        using var rig = new AsmRig();
        await rig.OpenSpeech(AsmRig.Speech("hello"));
        await rig.Ask(AsmRig.Command(new UserCommandRQ { UserPoV = Listener, AddedObjects = new ObjectPlacements { ObjectID = "dialogue", Objects = [AsmRig.Put("hello", 3)] } }));

        // The Speech Object is where Speech Object Editing keeps it: OSD-BSO-V1.5 #2, by its ID. The Speech Scene Editing AIM's own view of the Module's Private Storage reads it there ...
        var sse = AsmStorage.Keep(rig.Private, AsmProvider.Sse);
        Assert.True(sse.Holds(Kinds.BasicSpeechObject, "hello"));
        Assert.NotNull(SpeechStore.Get(sse, "hello"));
        // ... the Scene holds it by that ID ...
        var scene = Mpai.Core.MpaiJson.FromJson<BasicSpeechSceneDescriptors>(System.Text.Encoding.UTF8.GetString(sse.Get(Kinds.SpeechScene, "dialogue")!));
        Assert.Equal(["hello"], scene.BasicSpeechSceneItems.Select(i => i.SpeechObject!.BasicSpeechObjectID));
        // ... and it does not write it: a datum another AIM wrote is not overwritten.
        Assert.Throws<UnauthorizedAccessException>(() => SpeechStore.Put(sse, AsmRig.Speech("hello")));
        // The keys under that Data Type and Port are Speech Object Editing's alone: every one is a Speech Object stored at <Data Type>#<Port>/<ID>@<version>.
        Assert.Equal(["OSD-BSO-V1.5#2/hello@1"], PrivateKeys(rig).Where(k => k.StartsWith("OSD-BSO")));
    }
}
