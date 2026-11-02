using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIF.Controller;
using AIF.SharedStorage;
using Mpai.Cae.Aoe;
using Mpai.Cae.Ase;
using Mpai.Mmc.Soe;
using Mpai.Mmc.Sse;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Osd.Mse;

namespace Mpai.Aif.Tests;

// OBJECTS AND SCENES ARE NAMED BY THE USER (the author, 2026/10/26). In the data, the commands, the context and the replies an Object or a Scene is identified only by the name the
// User gave it - "violin" - the value of BasicAudioObjectID, AudioObjectID, AudioSceneDescriptorsID, BasicSpeechObjectID, SpeechSceneDescriptorsID ... The Module keeps its own
// storage keys (BAO000001 ...) and a table between the two; a key never leaves it. Judged: a second Object or Scene given a name that is taken is "violin 2", "violin 3" (the same
// sound given again is the same Object, not a new one); a command carries its amounts as numbers - a command without them is reported as failed, never guessed, and the
// schema requires them; the List operation answers with names only (no string of any output is a storage key), each editor listing what it keeps and saying who lists the rest;
// an Object, a Scene or an Audio Object that would be created with no name is not created: the report says that a name is needed.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class AsmNamesTests
{
    private static readonly IReadOnlyDictionary<string, Json.Schema.JsonSchema> All = AIF.Metadata.PublishedSchemas.At(Repository.Schemas);

    private static bool Valid(string schema, string json)
    {
        var s = All[Path.GetFullPath(Path.Combine(Repository.Schemas, schema))];
        using var doc = JsonDocument.Parse(json);
        lock (AIF.Metadata.PublishedSchemas.Lock) return s.Evaluate(doc.RootElement).IsValid;
    }

    // A string that is a storage key of the Module: its typed prefix and its number.
    private static readonly Regex Key = new(@"^(BAO|AUO|BSO|SPO|ASD|SSD|BAS|BSS|MSD)\d+$");

    // Every string value of the JSON that is a storage key.
    private static List<string> KeysIn(string json)
    {
        var found = new List<string>();
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o: foreach (var p in o) Walk(p.Value); break;
                case JsonArray a: foreach (var e in a) Walk(e); break;
                case JsonValue v when v.TryGetValue<string>(out var s) && Key.IsMatch(s): found.Add(s); break;
            }
        }
        Walk(JsonNode.Parse(json));
        return found;
    }

    private static BasicAudioObject Sound(string name, byte seed = 1) => new()
    {
        BasicAudioObjectID = name, BasicAudioObjectData = [new InlineAudioData(Convert.ToBase64String(Enumerable.Repeat(seed, 64).ToArray()))], DescrMetadata = name
    };
    private static BasicSpeechObject Speech(string name, byte seed = 1) => new() { BasicSpeechObjectID = name, Data = Enumerable.Repeat(seed, 64).ToArray() };

    private static UserCommand Command(UserCommandRQ data) => new() { UserCommandID = Guid.NewGuid().ToString("N")[..8], UserCommandTime = SimpleTime.At(DateTimeOffset.UtcNow), UserCommandRQ = data };
    private static string Json_(UserCommand c) => MpaiJson.ToJson(c);
    private static ManagedObject Ref(string name) => new() { ObjectID = name };
    private static SpatialAttitude At(double x) => new()
    {
        ObjectSpatialAttitudeID = Guid.NewGuid().ToString(),
        Position = new Position { PositionID = Guid.NewGuid().ToString(), CartPosition = [x, 2, 0] },
        Orientation = new Orientation { OrientationID = Guid.NewGuid().ToString(), EulerAngles = [0, 0, 180] }
    };
    private static ObjectPlacement Put(string name, double x) => new() { ObjectID = Ref(name), SpatialAttitude = At(x) };

    // The Audio Scene Management AIMs over one Shared Storage, each through its Ports.
    private sealed class Module : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "asm-names-" + Guid.NewGuid().ToString("N"));
        public readonly FileSharedStorage Storage;
        public readonly AimPortReader AoePorts, AsePorts, SoePorts, SsePorts, MsePorts;
        public IAimProcessor Aoe, Ase, Soe, Sse, Mse;
        public readonly List<string> Said = new();     // everything the AIMs gave, to be searched for keys

        public Module()
        {
            Storage = new FileSharedStorage(Root, "CAE-ASM", "ASM-Names");
            var store = new AIF.Store.AmdStore(Repository.Amds); store.Scan();
            AoePorts = AimPortReader.Load(store, "1CAE-AOE-V1.0-I01"); AsePorts = AimPortReader.Load(store, "1CAE-ASE-V1.0-I01");
            SoePorts = AimPortReader.Load(store, "1MMC-SOE-V2.5-I01"); SsePorts = AimPortReader.Load(store, "1MMC-SSE-V2.5-I01");
            MsePorts = AimPortReader.Load(store, "1OSD-MSE-V1.5-I01");
            Aoe = NewAoe(); Ase = NewAse(); Soe = NewSoe(); Sse = NewSse(); Mse = NewMse();
        }

        public IAimProcessor NewAoe() => new AoeAimProcessor("1CAE-AOE-V1.0-I01", new AoeAim(Storage), AoePorts);
        public IAimProcessor NewAse() => new AseAimProcessor("1CAE-ASE-V1.0-I01", new AseAim(Storage), AsePorts);
        public IAimProcessor NewSoe() => new SoeAimProcessor("1MMC-SOE-V2.5-I01", Storage, SoePorts);
        public IAimProcessor NewSse() => new SseAimProcessor("1MMC-SSE-V2.5-I01", Storage, SsePorts);
        public IAimProcessor NewMse() => new MseAimProcessor("1OSD-MSE-V1.5-I01", Storage, MsePorts, "ASM");

        public async Task<Message> Give(IAimProcessor aim, params (string Port, string Json)[] inputs)
        {
            var o = await aim.ProcessAsync(new Message { MessageId = Guid.NewGuid().ToString(), Ports = inputs.ToDictionary(i => i.Port, i => i.Json) });
            foreach (var p in o.Ports.Values) if (p.Length > 0) Said.Add(p);
            return o;
        }

        public string AoeIn => AoePorts.Input("CAE-UCM-V1.0");        // a sound arrives in a User Command (AcquiredObject)
        public string AoeCommand => AoePorts.Input("CAE-UCM-V1.0");
        public string AoeOut => AoePorts.Output("OSD-AUO-V1.5");
        public string AoeReport => AoePorts.Output("CAE-UCM-V1.0");
        public string AseCommand => AsePorts.Input("CAE-UCM-V1.0");
        public string AseOut => AsePorts.Output("OSD-ASD-V1.5");
        public string AseReport => AsePorts.Output("CAE-UCM-V1.0");
        public string SoeIn => SoePorts.Input("CAE-UCM-V1.0");        // a speech arrives in a User Command (AcquiredObject)
        public string SoeCommand => SoePorts.Input("CAE-UCM-V1.0");
        public string SoeOut => SoePorts.Output("OSD-SPO-V1.5", 1);
        public string SoeReport => SoePorts.Output("CAE-UCM-V1.0");
        public string SseCommand => SsePorts.Input("CAE-UCM-V1.0");
        public string SseOut => SsePorts.Output("OSD-SSD-V1.5");
        public string SseReport => SsePorts.Output("CAE-UCM-V1.0");
        public string MseScene1 => MsePorts.Input("OSD-ASD-V1.5", 1);
        public string MseScene2 => MsePorts.Input("OSD-ASD-V1.5", 2);
        public string MseCommand => MsePorts.Input("CAE-UCM-V1.0");
        public string MseOut => MsePorts.Output("OSD-MSD-V1.5");
        public string MseReport => MsePorts.Output("CAE-UCM-V1.0");

        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    private static UserCommandRS Report(Message m, string port) => MpaiJson.FromJson<UserCommand>(m.Ports[port]).UserCommandRS!;
    private static string Name(Message m, string port) => JsonNode.Parse(m.Ports[port])!["AudioObjectID"]?.GetValue<string>() ?? "";

    // ---- a name that is taken is numbered ---------------------------------------------------------------------------------

    [Fact]
    public async Task ASecondObjectWithTheSameNameIsNumbered()
    {
        using var module = new Module();

        // Audio Object Editing: a different sound with the name of one it keeps is "violin 2", then "violin 3"; the same sound given again is the same Object.
        string Opened(Message m) => JsonNode.Parse(Forms.Bao(m.Ports[module.AoeOut]))!["BasicAudioObjectID"]!.GetValue<string>();
        Assert.Equal("violin", Opened(await module.Give(module.Aoe, (module.AoeIn, Forms.Acquire(Forms.Auo(Sound("violin", 1)))))));
        Assert.Equal("violin 2", Opened(await module.Give(module.Aoe, (module.AoeIn, Forms.Acquire(Forms.Auo(Sound("violin", 2)))))));
        Assert.Equal("violin", Opened(await module.Give(module.Aoe, (module.AoeIn, Forms.Acquire(Forms.Auo(Sound("violin", 1)))))));
        Assert.Equal("violin 3", Opened(await module.Give(module.Aoe, (module.AoeIn, Forms.Acquire(Forms.Auo(Sound("violin", 3)))))));
        Assert.Equal(3, module.Storage.MPAI_AIFM_SharedStorage_List("BAO").Count);                        // three Objects, not four

        // An edit is a new version of the Object: it keeps its name, it is not a second Object.
        var edit = await module.Give(module.Aoe, (module.AoeCommand, Json_(Command(new UserCommandRQ { ModifiedObjects = new ObjectChanges { Objects = [new ObjectChange { ObjectID = new ManagedObject { ObjectID = "violin 3" }, LUFS = -20 }] } }))));
        Assert.Equal("violin 3", Opened(edit));
        Assert.Equal("violin 3", Report(edit, module.AoeReport).Actions[0].ObjectID);

        // Speech Object Editing: the same.
        string Heard(Message m) => JsonNode.Parse(Forms.Bso(m.Ports[module.SoeOut]))!["BasicSpeechObjectID"]!.GetValue<string>();
        Assert.Equal("hello", Heard(await module.Give(module.Soe, (module.SoeIn, Forms.Acquire(MpaiJson.ToJson(Speech("hello", 1)))))));
        Assert.Equal("hello 2", Heard(await module.Give(module.Soe, (module.SoeIn, Forms.Acquire(MpaiJson.ToJson(Speech("hello", 2)))))));
        Assert.Equal("hello", Heard(await module.Give(module.Soe, (module.SoeIn, Forms.Acquire(MpaiJson.ToJson(Speech("hello", 1)))))));

        // Audio Scene Editing: a Scene made while another with the name exists is "stage 2".
        async Task<string> SceneNamed(string scene, string member)
        {
            var ase = module.NewAse();
            var made = await module.Give(ase, (module.AseCommand, Json_(Command(new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = scene, Objects = [Put(member, 1)] } }))));
            Assert.Equal("Done", Report(made, module.AseReport).Outcome);
            return JsonNode.Parse(made.Ports[module.AseOut])!["AudioSceneDescriptorsID"]!.GetValue<string>();
        }
        Assert.Equal("stage", await SceneNamed("stage", "violin"));
        Assert.Equal("stage 2", await SceneNamed("stage", "violin 2"));
        Assert.Equal("stage 3", await SceneNamed("stage", "violin"));

        // Speech Scene Editing, and Multimodal Scene Editing: the same.
        async Task<string> SpeechSceneNamed(string scene)
        {
            var sse = module.NewSse();
            var made = await module.Give(sse, (module.SseCommand, Json_(Command(new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = scene, Objects = [Put("hello", 0)] } }))));
            Assert.Equal("Done", Report(made, module.SseReport).Outcome);
            return JsonNode.Parse(made.Ports[module.SseOut])!["SpeechSceneDescriptorsID"]!.GetValue<string>();
        }
        Assert.Equal("dialogue", await SpeechSceneNamed("dialogue"));
        Assert.Equal("dialogue 2", await SpeechSceneNamed("dialogue"));

        string Mixed(Message m) => JsonNode.Parse(m.Ports[module.MseOut])!["MultimodalSceneDescriptorsID"]!.GetValue<string>();
        async Task<string> MixNamed(string mix)
        {
            var mse = module.NewMse();
            await module.Give(mse, (module.MseScene1, Forms.Asd(new BasicAudioSceneDescriptors { MInstanceID = "ASM", BasicAudioSceneDescriptorsID = "stage", BASSpaceTime = new SpaceTime() })),
                                    (module.MseScene2, Forms.Ssd(new BasicSpeechSceneDescriptors { MInstanceID = "ASM", BasicSpeechSceneDescriptorsID = "dialogue" })));
            return Mixed(await module.Give(mse, (module.MseCommand, Json_(Command(new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = mix, Objects = [Put("stage", 0), Put("dialogue", 0)] } })))));
        }
        Assert.Equal("mix", await MixNamed("mix"));
        Assert.Equal("mix 2", await MixNamed("mix"));

        // A Basic Audio Object composed into an Audio Object takes a taken name too.
        var band = async (string name) => Name(await module.Give(module.Aoe, (module.AoeCommand, Json_(Command(new UserCommandRQ
            { AddedObjects = new ObjectPlacements { ObjectID = name, Objects = [Put("violin", 0), Put("violin 2", 2)] } })))), module.AoeOut);
        Assert.Equal("band", await band("band"));
        Assert.Equal("band 2", await band("band"));

        // Nothing that was given out is a storage key.
        Assert.Empty(module.Said.SelectMany(KeysIn));
    }

    // ---- an amount is a number the command carries -------------------------------------------------------------------------

    [Fact]
    public void TheSchemaRequiresTheAmounts()
    {
        const string schema = "CAE3/V1.0/data/UserCommand.json";
        string Rq(string rq) => $$"""{ "Header": "CAE-UCM-V1.0", "UserCommandID": "c1", "UserCommandTime": {{TimeJson}}, "UserCommandRQ": {{rq}} }""";
        static string Where(double x) => MpaiJson.ToJson(At(x));

        // Moved: where to is required.
        Assert.True(Valid(schema, Rq($$"""{ "MovedObjects": { "Objects": [ { "ObjectID": "violin", "NewSpatialAttitude": {{Where(3)}} } ] } }""")));
        Assert.False(Valid(schema, Rq("""{ "MovedObjects": { "Objects": [ { "ObjectID": "violin" } ] } }""")));
        Assert.False(Valid(schema, Rq($$"""{ "MovedObjects": { "Objects": [ { "NewSpatialAttitude": {{Where(3)}} } ] } }""")));         // which Object?
        Assert.True(Valid(schema, Rq($$"""{ "MovedObjects": { "Objects": [ { "OldSpatialAttitude": {{Where(1)}}, "NewSpatialAttitude": {{Where(3)}} } ] } }""")));

        // Added: where each is put is required, and the name of what is made is a name.
        Assert.True(Valid(schema, Rq($$"""{ "AddedObjects": { "ObjectID": "stage", "Objects": [ { "ObjectID": "violin", "SpatialAttitude": {{Where(1)}} } ] } }""")));
        Assert.False(Valid(schema, Rq("""{ "AddedObjects": { "ObjectID": "stage", "Objects": [ { "ObjectID": "violin" } ] } }""")));
        Assert.False(Valid(schema, Rq($$"""{ "AddedObjects": { "ObjectID": "", "Objects": [ { "ObjectID": "violin", "SpatialAttitude": {{Where(1)}} } ] } }""")));

        // Changed: the Object, and what changes.
        Assert.True(Valid(schema, Rq($$"""{ "ChangedObjects": { "Objects": [ { "ObjectID": "violin", "SpatialAttitude": {{Where(1)}} } ] } }""")));
        Assert.False(Valid(schema, Rq("""{ "ChangedObjects": { "Objects": [ { "ObjectID": "violin" } ] } }""")));

        // Modified: an Acoustic Profile, or the loudness as a number.
        Assert.False(Valid(schema, Rq("""{ "ModifiedObjects": { "Objects": [ { "ObjectID": "violin" } ] } }""")));
        Assert.False(Valid(schema, Rq("""{ "LUFS": -18, "ModifiedObjects": { "Objects": [ { "ObjectID": "violin" } ] } }""")));      // the top-level LUFS is the Scene's: it is no amount for an entry
        Assert.True(Valid(schema, Rq("""{ "ModifiedObjects": { "Objects": [ { "ObjectID": "violin", "LUFS": -18 } ] } }""")));
        Assert.False(Valid(schema, Rq("""{ "LUFS": "a bit louder" }""")));
        Assert.True(Valid(schema, Rq("""{ "LUFS": -12 }""")));

        // List: the Kind, from the Kinds there are.
        Assert.True(Valid(schema, Rq("""{ "ListedObjects": { "Kind": "Audio Scene" } }""")));
        Assert.True(Valid(schema, Rq("""{ "ListedObjects": { "Kind": "All" } }""")));
        Assert.False(Valid(schema, Rq("""{ "ListedObjects": { } }""")));
        Assert.False(Valid(schema, Rq("""{ "ListedObjects": { "Kind": "BAO000001" } }""")));
    }

    private static readonly string TimeJson = MpaiJson.ToJson(SimpleTime.At(DateTimeOffset.UtcNow));

    [Fact]
    public async Task ACommandWithoutItsAmountIsReportedNotGuessed()
    {
        using var module = new Module();
        await module.Give(module.Aoe, (module.AoeIn, Forms.Acquire(Forms.Auo(Sound("violin")))));
        await module.Give(module.Aoe, (module.AoeIn, Forms.Acquire(Forms.Auo(Sound("drums", 2)))));
        var built = await module.Give(module.Ase, (module.AseCommand, Json_(Command(new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = "stage", Objects = [Put("violin", 1), Put("drums", 3)] } }))));
        Assert.Equal("Done", Report(built, module.AseReport).Outcome);
        double[] Xs(Message m) => JsonNode.Parse(Forms.Bas(m.Ports[module.AseOut]))!["BasicAudioSceneDescriptorsData"]!.AsArray()
            .Select(e => (double)e!["AudioObjectSpaceTime"]!["SpatialAttitude1"]!["Position"]!["CartPosition"]![0]!).ToArray();
        Assert.Equal([1.0, 3.0], Xs(built));

        // A move with no "to": failed, and the Scene is as it was.
        var noTo = await module.Give(module.Ase, (module.AseCommand, Json_(Command(new UserCommandRQ { MovedObjects = new ObjectMovements { Objects = [new ObjectMovement { ObjectID = Ref("violin") }] } }))));
        var said = Report(noTo, module.AseReport);
        Assert.Equal("Failed", said.Outcome);
        Assert.Equal("MovedObjects", said.Actions[0].Action);
        Assert.Contains("required", said.Actions[0].Reason);
        Assert.Equal([1.0, 3.0], Xs(noTo));

        // A "to" with no coordinates is no number either.
        var empty = new SpatialAttitude { ObjectSpatialAttitudeID = "a", Position = new Position { PositionID = "p" }, Orientation = new Orientation { OrientationID = "o" } };
        var noNumbers = await module.Give(module.Ase, (module.AseCommand, Json_(Command(new UserCommandRQ { MovedObjects = new ObjectMovements { Objects = [new ObjectMovement { ObjectID = Ref("violin"), NewSpatialAttitude = empty }] } }))));
        Assert.Equal("Failed", Report(noNumbers, module.AseReport).Outcome);
        Assert.Contains("three numbers", Report(noNumbers, module.AseReport).Actions[0].Reason);
        Assert.Equal([1.0, 3.0], Xs(noNumbers));

        // A change with none, and an Object added with no place.
        var noWhere = await module.Give(module.Ase, (module.AseCommand, Json_(Command(new UserCommandRQ { ChangedObjects = new ObjectChanges { Objects = [new ObjectChange { ObjectID = Ref("drums") }] } }))));
        Assert.Equal("Failed", Report(noWhere, module.AseReport).Outcome);
        var noPlace = await module.Give(module.Ase, (module.AseCommand, Json_(Command(new UserCommandRQ { AddedObjects = new ObjectPlacements { Objects = [new ObjectPlacement { ObjectID = Ref("drums") }] } }))));
        Assert.Equal("Failed", Report(noPlace, module.AseReport).Outcome);
        Assert.Equal([1.0, 3.0], Xs(noPlace));

        // The loudness: no Acoustic Profile and no number is nothing to modify; a word is not a number.
        var nothing = await module.Give(module.Aoe, (module.AoeCommand, Json_(Command(new UserCommandRQ { ModifiedObjects = new ObjectChanges { Objects = [new ObjectChange { ObjectID = Ref("drums") }] } }))));
        Assert.Equal("Failed", Report(nothing, module.AoeReport).Outcome);
        Assert.Contains("required", Report(nothing, module.AoeReport).Actions[0].Reason);
        var word = JsonNode.Parse(Json_(Command(new UserCommandRQ { LUFS = -12 })))!;
        word["UserCommandRQ"]!["LUFS"] = "a bit louder";
        var notANumber = await module.Give(module.Aoe, (module.AoeCommand, word.ToJsonString()));
        var rejected = Report(notANumber, module.AoeReport);
        Assert.Equal("Failed", rejected.Outcome);
        Assert.Equal("LUFS", rejected.Actions[0].Action);
        Assert.Contains("number", rejected.Actions[0].Reason);
        Assert.Equal(word["UserCommandID"]!.GetValue<string>(), MpaiJson.FromJson<UserCommand>(notANumber.Ports[module.AoeReport]).UserCommandID);   // under the command's own ID
        Assert.True(Valid("CAE3/V1.0/data/UserCommand.json", notANumber.Ports[module.AoeReport]));

        // Speech Scene Editing, and Multimodal Scene Editing: the same.
        await module.Give(module.Soe, (module.SoeIn, Forms.Acquire(MpaiJson.ToJson(Speech("hello")))));
        var sse = await module.Give(module.Sse, (module.SseCommand, Json_(Command(new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = "dialogue", Objects = [Put("hello", 0)] } }))));
        Assert.Equal("Done", Report(sse, module.SseReport).Outcome);
        var sseNoTo = await module.Give(module.Sse, (module.SseCommand, Json_(Command(new UserCommandRQ { MovedObjects = new ObjectMovements { Objects = [new ObjectMovement { ObjectID = Ref("hello") }] } }))));
        Assert.Equal("Failed", Report(sseNoTo, module.SseReport).Outcome);
        await module.Give(module.Mse, (module.MseScene1, Forms.Asd(new BasicAudioSceneDescriptors { MInstanceID = "ASM", BasicAudioSceneDescriptorsID = "stage", BASSpaceTime = new SpaceTime() })),
                                      (module.MseScene2, sse.Ports[module.SseOut]));
        var mseNoPlace = await module.Give(module.Mse, (module.MseCommand, Json_(Command(new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = "mix", Objects = [new ObjectPlacement { ObjectID = Ref("stage") }] } }))));
        Assert.Equal("Failed", Report(mseNoPlace, module.MseReport).Outcome);

        Assert.All(module.Said.Where(s => s.Contains("UserCommandRS")), s => Assert.True(Valid("CAE3/V1.0/data/UserCommand.json", s)));
        Assert.Empty(module.Said.SelectMany(KeysIn));
    }

    // ---- the List operation -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AListGivesOnlyNames()
    {
        using var module = new Module();
        await module.Give(module.Aoe, (module.AoeIn, Forms.Acquire(Forms.Auo(Sound("violin", 1)))));
        await module.Give(module.Aoe, (module.AoeIn, Forms.Acquire(Forms.Auo(Sound("drums", 2)))));
        await module.Give(module.Aoe, (module.AoeCommand, Json_(Command(new UserCommandRQ { ModifiedObjects = new ObjectChanges { Objects = [new ObjectChange { ObjectID = new ManagedObject { ObjectID = "drums" }, LUFS = -18 }] } }))));            // a new version of drums: still one Object
        await module.Give(module.Aoe, (module.AoeCommand, Json_(Command(new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = "band", Objects = [Put("violin", 0), Put("drums", 2)] } }))));
        var stage = await module.Give(module.Ase, (module.AseCommand, Json_(Command(new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = "stage", Objects = [Put("violin", 1), Put("drums", 3)] } }))));
        await module.Give(module.Soe, (module.SoeIn, Forms.Acquire(MpaiJson.ToJson(Speech("hello")))));
        var dialogue = await module.Give(module.Sse, (module.SseCommand, Json_(Command(new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = "dialogue", Objects = [Put("hello", 0)] } }))));
        await module.Give(module.Mse, (module.MseScene1, stage.Ports[module.AseOut]), (module.MseScene2, dialogue.Ports[module.SseOut]));
        await module.Give(module.Mse, (module.MseCommand, Json_(Command(new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = "mix", Objects = [Put("stage", 0), Put("dialogue", 0)] } }))));

        async Task<UserCommandRS> List(IAimProcessor aim, string commandPort, string reportPort, string? kind)
        {
            var said = await module.Give(aim, (commandPort, Json_(Command(new UserCommandRQ { ListedObjects = new ObjectListing { Kind = kind } }))));
            var rs = Report(said, reportPort);
            Assert.True(Valid("CAE3/V1.0/data/UserCommand.json", said.Ports[reportPort]), said.Ports[reportPort]);
            Assert.Empty(KeysIn(said.Ports[reportPort]));                                   // names only
            return rs;
        }
        static string Line(ListedObject o) => $"{o.ObjectID} ({o.Kind}){(o.MemberObjectIDs is null ? "" : ": " + string.Join(", ", o.MemberObjectIDs))}";

        // Audio Object Editing: everything it keeps, or one Kind; the Objects it keeps once, by name.
        var all = await List(module.Aoe, module.AoeCommand, module.AoeReport, "All");
        Assert.Equal("Done", all.Outcome);
        Assert.Equal(["band (Audio Object): violin, drums", "drums (Basic Audio Object)", "violin (Basic Audio Object)"], all.ListedObjects!.Select(Line));
        Assert.Equal(["band (Audio Object): violin, drums"], (await List(module.Aoe, module.AoeCommand, module.AoeReport, "Audio Object")).ListedObjects!.Select(Line));
        Assert.Equal(["drums (Basic Audio Object)", "violin (Basic Audio Object)"], (await List(module.Aoe, module.AoeCommand, module.AoeReport, "Basic Audio Object")).ListedObjects!.Select(Line));

        // Each editor lists what it keeps.
        Assert.Equal(["stage (Audio Scene): violin, drums"], (await List(module.Ase, module.AseCommand, module.AseReport, "Audio Scene")).ListedObjects!.Select(Line));
        Assert.Equal(["hello (Basic Speech Object)"], (await List(module.Soe, module.SoeCommand, module.SoeReport, "All")).ListedObjects!.Select(Line));
        Assert.Equal(["dialogue (Speech Scene): hello"], (await List(module.Sse, module.SseCommand, module.SseReport, "Speech Scene")).ListedObjects!.Select(Line));
        Assert.Equal(["mix (Multimodal Scene): stage, dialogue"], (await List(module.Mse, module.MseCommand, module.MseReport, "Multimodal Scene")).ListedObjects!.Select(Line));

        // What another editor keeps is not listed here: the report says who lists it.
        var elsewhere = await List(module.Aoe, module.AoeCommand, module.AoeReport, "Speech Scene");
        Assert.Equal("Failed", elsewhere.Outcome);
        Assert.Equal("NotSupported", elsewhere.Actions[0].Outcome);
        Assert.Contains("Speech Scene Editing", elsewhere.Actions[0].Reason);
        Assert.Null(elsewhere.ListedObjects);

        // No Kind, or one there is not: failed.
        Assert.Equal("Failed", (await List(module.Ase, module.AseCommand, module.AseReport, null)).Outcome);
        Assert.Equal("Failed", (await List(module.Sse, module.SseCommand, module.SseReport, "BAO000001")).Outcome);

        // Nothing any AIM gave, in any run, holds a storage key.
        Assert.Empty(module.Said.SelectMany(KeysIn));
    }

    // ---- a name is needed ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACreationWithNoNameAsksForOne()
    {
        using var module = new Module();

        // A sound with no name: not created - the report says a name is needed, and nothing is open.
        var unnamed = await module.Give(module.Aoe, (module.AoeIn, Forms.Acquire(Forms.Auo(Sound("")))));
        var said = Report(unnamed, module.AoeReport);
        Assert.Equal("Failed", said.Outcome);
        Assert.Equal("AcquiredObject", said.Actions[0].Action);
        Assert.Contains("name is needed", said.Actions[0].Reason);
        Assert.False(unnamed.Ports.ContainsKey(module.AoeOut));
        Assert.Empty(module.Storage.MPAI_AIFM_SharedStorage_List("BAO"));
        Assert.True(Valid("CAE3/V1.0/data/UserCommand.json", unnamed.Ports[module.AoeReport]));

        // Named, it is created; a name made of spaces is no name.
        await module.Give(module.Aoe, (module.AoeIn, Forms.Acquire(Forms.Auo(Sound("violin")))));
        await module.Give(module.Aoe, (module.AoeIn, Forms.Acquire(Forms.Auo(Sound("drums", 2)))));
        var blank = await module.Give(module.Aoe, (module.AoeIn, Forms.Acquire(Forms.Auo(Sound("   ", 3)))));
        Assert.Contains("name is needed", Report(blank, module.AoeReport).Actions[0].Reason);
        Assert.Equal(2, module.Storage.MPAI_AIFM_SharedStorage_List("BAO").Count);

        // An Audio Object made of Objects with no name given by the command: not made.
        var noName = await module.Give(module.Aoe, (module.AoeCommand, Json_(Command(new UserCommandRQ { AddedObjects = new ObjectPlacements { Objects = [Put("violin", 0), Put("drums", 2)] } }))));
        Assert.Equal("Failed", Report(noName, module.AoeReport).Outcome);
        Assert.Contains("name is needed", Report(noName, module.AoeReport).Actions[0].Reason);
        Assert.Empty(module.Storage.MPAI_AIFM_SharedStorage_List("AUO"));

        // A Scene made with no name: not made, and no Scene is open.
        var noScene = await module.Give(module.Ase, (module.AseCommand, Json_(Command(new UserCommandRQ { AddedObjects = new ObjectPlacements { Objects = [Put("violin", 1)] } }))));
        Assert.Equal("Failed", Report(noScene, module.AseReport).Outcome);
        Assert.Contains("name is needed", Report(noScene, module.AseReport).Actions[0].Reason);
        Assert.False(noScene.Ports.ContainsKey(module.AseOut));
        Assert.Empty(module.Storage.MPAI_AIFM_SharedStorage_List("BAS"));

        // Speech: an Object with no name is not kept, nor a Scene.
        var noSpeech = await module.Give(module.Soe, (module.SoeIn, Forms.Acquire(MpaiJson.ToJson(Speech("")))));
        Assert.Contains("name is needed", Report(noSpeech, module.SoeReport).Actions[0].Reason);
        Assert.Empty(module.Storage.MPAI_AIFM_SharedStorage_List("BSO"));
        await module.Give(module.Soe, (module.SoeIn, Forms.Acquire(MpaiJson.ToJson(Speech("hello")))));
        var noSpeechScene = await module.Give(module.Sse, (module.SseCommand, Json_(Command(new UserCommandRQ { AddedObjects = new ObjectPlacements { Objects = [Put("hello", 0)] } }))));
        Assert.Contains("name is needed", Report(noSpeechScene, module.SseReport).Actions[0].Reason);
        Assert.Empty(module.Storage.MPAI_AIFM_SharedStorage_List("BSS"));

        // A Multimodal Scene with no name: not made.
        await module.Give(module.Mse, (module.MseScene1, Forms.Asd(new BasicAudioSceneDescriptors { MInstanceID = "ASM", BasicAudioSceneDescriptorsID = "stage", BASSpaceTime = new SpaceTime() })),
                                      (module.MseScene2, Forms.Ssd(new BasicSpeechSceneDescriptors { MInstanceID = "ASM", BasicSpeechSceneDescriptorsID = "dialogue" })));
        var noMix = await module.Give(module.Mse, (module.MseCommand, Json_(Command(new UserCommandRQ { AddedObjects = new ObjectPlacements { Objects = [Put("stage", 0), Put("dialogue", 0)] } }))));
        Assert.Equal("Failed", Report(noMix, module.MseReport).Outcome);
        Assert.Contains("name is needed", Report(noMix, module.MseReport).Actions[0].Reason);
        Assert.False(noMix.Ports.ContainsKey(module.MseOut));
        Assert.Empty(module.Storage.MPAI_AIFM_SharedStorage_List("MSD"));

        // Nothing was made up for the User: no name of any report or output is a storage key.
        Assert.Empty(module.Said.SelectMany(KeysIn));
    }
}
