using System.Text.Json;
using System.Text.Json.Nodes;
using AIF.Controller;
using Mpai.Cae.Ami;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Aif.Tests;

// THE AUDIO SCENE MANAGEMENT INTERFACE (CAE-AMI, the author, 2026/10/26): the one AIM of the Composite CAE-ASM that touches its boundary. Its left-hand side is the boundary - the
// Ports of the Composite, unchanged - and its right-hand side serves the Editing AIMs as the boundary served them before. Judged:
//   - it forwards, unchanged, what the boundary forwarded: User Commands to the Editing AIM of the Port, their responses back to the same Port, User PoVs, Objects and Scenes;
//   - an Application JSON Instance (MMC-AJI) whose schema is the one its Access names and whose Instance conforms is turned into a User Command request, sent to the Editing
//     AIMs as any other, and its response leaves wrapped in an Application JSON Instance that names the one it answers (InReplyTo), under the same schema;
//   - an Instance that does not conform - or an Application JSON Instance that is not one - is answered, not thrown at: a response that reports it Failed, wrapped the same way,
//     and no Editing AIM is asked;
//   - a list of everything (Kind All) is sent to every Editing AIM and their answers are one response - for a Port and for an Application JSON Instance;
//   - the L2 and the L3 of the Composite are wired through it: each connection that started or ended at the boundary now goes boundary - Interface - Editing AIM, with the Data
//     Types and the Port Numbers of the boundary unchanged, and each input of an Editing AIM has exactly one source.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class AsmInterfaceTests
{
    private static readonly IReadOnlyDictionary<string, Json.Schema.JsonSchema> All = AIF.Metadata.PublishedSchemas.At(Repository.Schemas);
    private const string UserCommandUri = "https://schemas.mpai.community/CAE3/V1.0/data/UserCommand.json";

    private static bool Valid(string schema, string json)
    {
        var s = All[Path.GetFullPath(Path.Combine(Repository.Schemas, schema))];
        using var doc = JsonDocument.Parse(json);
        lock (AIF.Metadata.PublishedSchemas.Lock) return s.Evaluate(doc.RootElement).IsValid;
    }

    private static string Aji(UserCommand command, string? id = null, string? uri = null) => new JsonObject
    {
        ["Header"] = "MMC-AJI-V2.5", ["AJIID"] = id ?? Guid.NewGuid().ToString("N")[..8], ["ApplicationSchemaURI"] = uri ?? UserCommandUri,
        ["Instance"] = JsonNode.Parse(MpaiJson.ToJson(command))
    }.ToJsonString();

    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    // ---- it forwards what the boundary forwarded ------------------------------------------------------------------------------------

    [Fact]
    public async Task ItForwardsWhatTheBoundaryForwarded()
    {
        using var rig = new AsmRig();

        // Objects: an Audio Object arriving at the boundary reaches Audio Object Editing, which gives it back; it leaves at the boundary as it came out of the Editing AIM.
        var left = await rig.Through((rig.AmiPorts.Input(AsmRig.Auo, 1), Forms.Auo(AsmRig.Sound("violin"))));
        var auo = left.Last(rig.AmiPorts.Output(AsmRig.Auo, 1));
        Assert.NotNull(auo);
        Assert.Equal("violin", Obj(Forms.Bao(auo!))["BasicAudioObjectID"]!.GetValue<string>());

        // A User Command at each Port reaches the Editing AIM of the Port, exactly as it was; its response leaves at the same Port, exactly as the AIM gave it.
        var rqs = new[]
        {
            AsmRig.Command(new UserCommandRQ { LUFS = -20 }),
            AsmRig.Command(new UserCommandRQ { ListedObjects = new ObjectListing { Kind = "Audio Scene" } }),
            AsmRig.Command(new UserCommandRQ { ListedObjects = new ObjectListing { Kind = "Speech Object" } }),
            AsmRig.Command(new UserCommandRQ { ListedObjects = new ObjectListing { Kind = "Speech Scene" } }),
            AsmRig.Command(new UserCommandRQ { ListedObjects = new ObjectListing { Kind = "Multimodal Scene" } })
        };
        for (var k = 1; k <= 5; k++)
        {
            var json = AsmRig.Json(rqs[k - 1]);
            rig.Delivered.Clear();
            var output = await rig.Through((rig.RqIn(k), json));
            Assert.Equal([json], rig.Delivered);                                                       // unchanged, to one Editing AIM
            var answers = output.At(rig.RsOut(k));
            Assert.Single(answers);                                                                    // the response, at the Port it was asked at
            Assert.Equal(rqs[k - 1].UserCommandID, MpaiJson.FromJson<UserCommand>(answers[0]).UserCommandID);
            Assert.True(Valid("CAE3/V1.0/data/UserCommand.json", answers[0]));
            for (var other = 1; other <= 5; other++) if (other != k) Assert.Empty(output.At(rig.RsOut(other)));
        }

        // The Editing AIM that answers is the one of the Port: the loudness went to Audio Object Editing, whose open Object is the violin.
        var loud = await rig.Through((rig.RqIn(1), AsmRig.Json(AsmRig.Command(new UserCommandRQ { LUFS = -23 }))));
        Assert.Equal("Done", AsmRig.Response(loud.Last(rig.RsOut(1))!).Outcome);
        Assert.Equal(-23, Obj(Forms.Bao(loud.Last(rig.AmiPorts.Output(AsmRig.Auo, 1))!))["BasicAudioObjectProperties"]!["Level"]!.GetValue<double>());

        // A Scene arriving, and a Speech Object, go to their Editing AIM and come out as it gives them.
        var speech = await rig.Through((rig.AmiPorts.Input(AsmRig.Spo, 1), Forms.Spo(AsmRig.Speech("hello"))));
        Assert.Equal("hello", Obj(Forms.Bso(speech.Last(rig.AmiPorts.Output(AsmRig.Spo, 1))!))["BasicSpeechObjectID"]!.GetValue<string>());
        var stage = await rig.Through((rig.RqIn(2), AsmRig.Json(AsmRig.Command(new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = "stage", Objects = [AsmRig.Put("violin", 1)] } }))));
        Assert.Equal("Done", AsmRig.Response(stage.Last(rig.RsOut(2))!).Outcome);
        Assert.Equal("stage", Obj(stage.Last(rig.AmiPorts.Output(AsmRig.Asd, 1))!)["AudioSceneDescriptorsID"]!.GetValue<string>());

        // Nothing the Interface gave is a storage key, and nothing it gave but a response and a Scene is more than once.
        Assert.Empty(rig.Said.SelectMany(s => System.Text.RegularExpressions.Regex.Matches(s, "\"(BAO|AUO|BSO|SPO|BAS|ASD|BSS|SSD|MSD)\\d{6}\"").Select(m => m.Value)));
    }

    // ---- an Application JSON Instance ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnInstanceIsExecutedAndAnsweredInAnInstance()
    {
        using var rig = new AsmRig();
        await rig.Through((rig.AmiPorts.Input(AsmRig.Auo, 1), Forms.Auo(AsmRig.Sound("violin"))));

        var command = AsmRig.Command(new UserCommandRQ { LUFS = -19 });
        var aji = Aji(command, id: "aji-1");
        var output = await rig.Through((rig.AjiIn, aji));

        // The command was executed by Audio Object Editing, as any other: a new version of the violin with the loudness.
        Assert.Single(rig.Delivered.Where(d => d.Contains(command.UserCommandID)));
        var back = Assert.Single(output.At(rig.AjiOut));
        Assert.True(Valid("MMC/V2.5/data/ApplicationJSONInstance.json", back), back);
        var answer = Obj(back);
        Assert.Equal("aji-1", answer["InReplyTo"]!.GetValue<string>());                                // it names the instance it answers
        Assert.Equal(UserCommandUri, answer["ApplicationSchemaURI"]!.GetValue<string>());              // the same schema
        Assert.NotEqual("aji-1", answer["AJIID"]!.GetValue<string>());
        var instance = answer["Instance"]!.ToJsonString();
        Assert.True(Valid("CAE3/V1.0/data/UserCommand.json", instance), instance);
        var rs = AsmRig.Response(instance);
        Assert.Equal("Done", rs.Outcome);
        Assert.Equal("LUFS", rs.Actions[0].Action);
        Assert.Equal(command.UserCommandID, MpaiJson.FromJson<UserCommand>(instance).UserCommandID);
        Assert.Empty(output.At(rig.RsOut(1)));                                                         // the response is the Instance's, not a Port's
        Assert.Equal(-19, Obj(Forms.Bao(output.Last(rig.AmiPorts.Output(AsmRig.Auo, 1))!))["BasicAudioObjectProperties"]!["Level"]!.GetValue<double>());

        // A command for a Scene editor goes to the Scene editors and no other: the named Object is placed by the one that keeps it.
        var place = AsmRig.Command(new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = "stage", Objects = [AsmRig.Put("violin", 1)] } });
        rig.Delivered.Clear();
        var placed = await rig.Through((rig.AjiIn, Aji(place, id: "aji-2")));
        var placedBack = Obj(Assert.Single(placed.At(rig.AjiOut)));
        Assert.Equal("aji-2", placedBack["InReplyTo"]!.GetValue<string>());
        var placedRs = AsmRig.Response(placedBack["Instance"]!.ToJsonString());
        Assert.Equal("Done", placedRs.Outcome);                                                        // the Audio Scene Editing AIM placed it; the others kept no such Object
        Assert.Equal(3, rig.Delivered.Count);                                                          // Audio, Speech and Multimodal Scene Editing
        Assert.Single(placedRs.Actions);
        Assert.Equal("stage", placed.Last(rig.AmiPorts.Output(AsmRig.Asd, 1)) is { } scene ? Obj(scene)["AudioSceneDescriptorsID"]!.GetValue<string>() : null);

        // A revocation goes to the AIM that executed the command revoked, and is answered in an Instance.
        var revoke = AsmRig.Command(new UserCommandRQ { RevokedUserCommandID = command.UserCommandID });
        rig.Delivered.Clear();
        var revoked = await rig.Through((rig.AjiIn, Aji(revoke, id: "aji-3")));
        Assert.Single(rig.Delivered);
        var revokedRs = AsmRig.Response(Obj(Assert.Single(revoked.At(rig.AjiOut)))["Instance"]!.ToJsonString());
        Assert.Contains(revokedRs.Actions, a => a.Action == "RevokedUserCommandID");
    }

    [Fact]
    public async Task AnInstanceThatDoesNotConformIsAnsweredFailed()
    {
        using var rig = new AsmRig();
        await rig.Through((rig.AmiPorts.Input(AsmRig.Auo, 1), Forms.Auo(AsmRig.Sound("violin"))));
        rig.Delivered.Clear();

        async Task<UserCommandRS> Failed(string aji, string inReplyTo, string? expectedUri = UserCommandUri)
        {
            var output = await rig.Through((rig.AjiIn, aji));
            var back = Assert.Single(output.At(rig.AjiOut));
            Assert.True(Valid("MMC/V2.5/data/ApplicationJSONInstance.json", back), back);
            var answer = Obj(back);
            Assert.Equal(inReplyTo, answer["InReplyTo"]?.GetValue<string>());
            if (expectedUri is not null) Assert.Equal(expectedUri, answer["ApplicationSchemaURI"]!.GetValue<string>());
            var instance = answer["Instance"]!.ToJsonString();
            Assert.True(Valid("CAE3/V1.0/data/UserCommand.json", instance), instance);
            var rs = AsmRig.Response(instance);
            Assert.Equal("Failed", rs.Outcome);
            Assert.All(rs.Actions, a => { Assert.Equal("Failed", a.Outcome); Assert.False(string.IsNullOrEmpty(a.Reason)); });
            Assert.Empty(rig.Delivered);                                                                // no Editing AIM was asked
            return rs;
        }

        // An Instance that is not a User Command: a field the schema does not have; a placement with no place; a loudness that is a word.
        var odd = Obj(Aji(AsmRig.Command(new UserCommandRQ { LUFS = -10 }), id: "a1"));
        odd["Instance"]!["UserCommandRQ"]!["Louder"] = true;
        var rs1 = await Failed(odd.ToJsonString(), "a1");
        Assert.Contains("does not conform", rs1.Actions[0].Reason);
        Assert.Equal("LUFS", rs1.Actions[0].Action);

        var noPlace = Obj(Aji(AsmRig.Command(new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = "x", Objects = [AsmRig.Put("violin", 1)] } }), id: "a2"));
        noPlace["Instance"]!["UserCommandRQ"]!["AddedObjects"]!["Objects"]![0]!.AsObject().Remove("SpatialAttitude");
        var rs2 = await Failed(noPlace.ToJsonString(), "a2");
        Assert.Equal("AddedObjects", rs2.Actions[0].Action);

        var word = Obj(Aji(AsmRig.Command(new UserCommandRQ { LUFS = -10 }), id: "a3"));
        word["Instance"]!["UserCommandRQ"]!["LUFS"] = "a bit louder";
        await Failed(word.ToJsonString(), "a3");

        // Another schema than the one of the Access.
        var other = Aji(AsmRig.Command(new UserCommandRQ { LUFS = -10 }), id: "a4", uri: "https://schemas.mpai.community/CAE3/V1.0/data/SomethingElse.json");
        var rs4 = await Failed(other, "a4", "https://schemas.mpai.community/CAE3/V1.0/data/SomethingElse.json");
        Assert.Contains("ApplicationSchemaURI", rs4.Actions[0].Reason);

        // A response is not a command.
        var response = Obj(Aji(new CommandReport(AsmRig.Command(new UserCommandRQ { LUFS = -10 })).Report(), id: "a5"));
        var rs5 = await Failed(response.ToJsonString(), "a5");
        Assert.Contains("response", rs5.Actions[0].Reason);

        // Not an Application JSON Instance at all: still an answer, with nothing to name; and not JSON at all: the same.
        var headless = Obj(Aji(AsmRig.Command(new UserCommandRQ { LUFS = -10 }), id: "a6")); headless.Remove("Header");
        await Failed(headless.ToJsonString(), "a6");
        var garbage = await rig.Through((rig.AjiIn, "this is not JSON"));
        var garbageBack = Obj(Assert.Single(garbage.At(rig.AjiOut)));
        Assert.Null(garbageBack["InReplyTo"]);
        Assert.Equal("Failed", AsmRig.Response(garbageBack["Instance"]!.ToJsonString()).Outcome);
        Assert.Empty(rig.Delivered);
    }

    [Fact]
    public async Task ASchemaThatIsNotAvailableAnswersFailed()
    {
        // No schemas folder, no file of Access: nothing can be said to conform, and the Instance is answered Failed.
        const string missing = "https://schemas.mpai.community/CAE3/V1.0/data/NoSuchSchema.json";
        var check = new CommandSchemaCheck(missing, file: Path.Combine(Path.GetTempPath(), "nothing-" + Guid.NewGuid().ToString("N") + ".json"), schemasRoot: Repository.Schemas);
        var store = new AIF.Store.AmdStore(Repository.Amds); store.Scan();
        var ami = new AmiAimProcessor("1CAE-AMI-V1.0-I01", AimPortReader.Load(store, AsmRig.AmiName), check);
        var ports = AimPortReader.Load(store, AsmRig.AmiName);
        var said = await ami.ProcessAsync(new Message { MessageId = "m", Ports = new() { [ports.Input(AsmRig.Aji)] = Aji(AsmRig.Command(new UserCommandRQ { LUFS = -10 }), id: "n1", uri: missing) } });
        var answer = Obj(said.Ports[ports.Output(AsmRig.Aji)]);
        Assert.Equal("n1", answer["InReplyTo"]!.GetValue<string>());
        Assert.Contains("not available", AsmRig.Response(answer["Instance"]!.ToJsonString()).Actions[0].Reason);
    }

    // ---- the list of all -------------------------------------------------------------------------------------------------------------

    private static async Task Everything(AsmRig rig)
    {
        await rig.Through((rig.AmiPorts.Input(AsmRig.Auo, 1), Forms.Auo(AsmRig.Sound("violin"))));
        await rig.Through((rig.AmiPorts.Input(AsmRig.Spo, 1), Forms.Spo(AsmRig.Speech("hello"))));
        await rig.Through((rig.RqIn(2), AsmRig.Json(AsmRig.Command(new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = "stage", Objects = [AsmRig.Put("violin", 1)] } }))));
        await rig.Through((rig.RqIn(4), AsmRig.Json(AsmRig.Command(new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = "dialogue", Objects = [AsmRig.Put("hello", 0)] } }))));
    }

    [Fact]
    public async Task AListOfAllIsSentToEveryEditingAimAndAnsweredOnce()
    {
        using var rig = new AsmRig();
        await Everything(rig);
        rig.Delivered.Clear();

        // At a Port of the boundary: one response, at the Port the command came by; the list is what every Editing AIM keeps.
        var command = AsmRig.Command(new UserCommandRQ { ListedObjects = new ObjectListing { Kind = "All" } });
        var output = await rig.Through((rig.RqIn(3), AsmRig.Json(command)));
        Assert.Equal(5, rig.Delivered.Count);
        Assert.All(rig.Delivered, d => Assert.Equal(AsmRig.Json(command), d));
        for (var k = 1; k <= 5; k++) Assert.Equal(k == 3 ? 1 : 0, output.At(rig.RsOut(k)).Count);
        var one = output.Last(rig.RsOut(3))!;
        Assert.True(Valid("CAE3/V1.0/data/UserCommand.json", one), one);
        Assert.Equal(command.UserCommandID, MpaiJson.FromJson<UserCommand>(one).UserCommandID);
        var rs = AsmRig.Response(one);
        Assert.Equal("Done", rs.Outcome);
        Assert.Equal(["Basic Audio Object", "Audio Scene", "Basic Speech Object", "Speech Scene"], rs.ListedObjects!.Select(o => o.Kind));
        Assert.Equal(["violin", "stage", "hello", "dialogue"], rs.ListedObjects!.Select(o => o.ObjectID));
        Assert.Single(rs.Actions);                                                                      // listing done, said once

        // By an Application JSON Instance: the same, in an Instance.
        rig.Delivered.Clear();
        var listing = AsmRig.Command(new UserCommandRQ { ListedObjects = new ObjectListing { Kind = "All" } });
        var viaAji = await rig.Through((rig.AjiIn, Aji(listing, id: "list-1")));
        Assert.Equal(5, rig.Delivered.Count);
        var back = Obj(Assert.Single(viaAji.At(rig.AjiOut)));
        Assert.Equal("list-1", back["InReplyTo"]!.GetValue<string>());
        var listed = AsmRig.Response(back["Instance"]!.ToJsonString());
        Assert.Equal(["violin", "stage", "hello", "dialogue"], listed.ListedObjects!.Select(o => o.ObjectID));
        Assert.True(Valid("MMC/V2.5/data/ApplicationJSONInstance.json", back.ToJsonString()));

        // A list of one Kind goes to the one AIM that keeps it.
        rig.Delivered.Clear();
        var kind = await rig.Through((rig.AjiIn, Aji(AsmRig.Command(new UserCommandRQ { ListedObjects = new ObjectListing { Kind = "Speech Scene" } }), id: "list-2")));
        Assert.Single(rig.Delivered);
        Assert.Equal(["dialogue"], AsmRig.Response(Obj(kind.Last(rig.AjiOut)!)["Instance"]!.ToJsonString()).ListedObjects!.Select(o => o.ObjectID));

        // Nothing is a storage key.
        Assert.Empty(rig.Said.SelectMany(s => System.Text.RegularExpressions.Regex.Matches(s, "\"(BAO|AUO|BSO|SPO|BAS|ASD|BSS|SSD|MSD)\\d{6}\"").Select(m => m.Value)));
    }

    [Fact]
    public void ManyAnswersAreMergedIntoOne()
    {
        UserCommand Answer(params (string Action, string? Id, string Outcome)[] actions) => new()
        {
            UserCommandID = "c", UserCommandTime = SimpleTime.At(DateTimeOffset.UtcNow),
            UserCommandRS = new UserCommandRS { Outcome = "x", Actions = actions.Select(a => new ReportAction { Action = a.Action, ObjectID = a.Id, Outcome = a.Outcome, Reason = a.Outcome == "Done" ? null : "no such Object" }).ToList() }
        };
        // The same action answered by three AIMs: the one that did it stands; an action only one AIM answers is kept.
        var merged = AmiAimProcessor.Merge("c", [Answer(("AddedObjects", "x", "Failed")), Answer(("AddedObjects", "x", "Done")), Answer(("AddedObjects", "x", "Failed"), ("UserPoV", null, "Failed"))]);
        Assert.Equal("c", merged.UserCommandID);
        Assert.Equal(["AddedObjects Done", "UserPoV Failed"], merged.UserCommandRS!.Actions.Select(a => $"{a.Action} {a.Outcome}"));
        Assert.Equal("Partly", merged.UserCommandRS.Outcome);
        Assert.Equal("Ignored", AmiAimProcessor.Merge("c", [Answer(), Answer()]).UserCommandRS!.Outcome);
        Assert.Equal("Failed", AmiAimProcessor.Merge("c", [Answer(("AddedObjects", "x", "Failed")), Answer(("AddedObjects", "x", "NotSupported"))]).UserCommandRS!.Outcome);
        Assert.Single(AmiAimProcessor.Merge("c", [Answer(("ListedObjects", null, "Done")), Answer(("ListedObjects", null, "Done"))]).UserCommandRS!.Actions);
    }

    [Fact]
    public void ACommandOfAnInstanceReachesTheEditingAimsItAsksFor()
    {
        static int[] Route(UserCommandRQ rq, Func<string, int[]?>? sent = null) => AmiAimProcessor.Route(rq, sent);
        Assert.Equal([1], Route(new UserCommandRQ { LUFS = -10 }));
        Assert.Equal([1], Route(new UserCommandRQ { ModifiedObjects = new ObjectChanges { Objects = [new ObjectChange { ObjectID = AsmRig.Ref("a") }] } }));
        Assert.Equal([2], Route(new UserCommandRQ { ModifiedScene = new SceneChange() }));
        Assert.Equal([3], Route(new UserCommandRQ { TranslatedObjects = new ObjectTranslations { Objects = [new ObjectTranslation { ObjectID = AsmRig.Ref("a") }] } }));
        Assert.Equal([2, 4, 5], Route(new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = "s", Objects = [AsmRig.Put("a", 1)] } }));
        Assert.Equal([2, 4, 5], Route(new UserCommandRQ { MovedObjects = new ObjectMovements { Objects = [new ObjectMovement { ObjectID = AsmRig.Ref("a"), NewSpatialAttitude = AsmRig.At(1) }] } }));
        Assert.Equal([1], Route(new UserCommandRQ { ChangedObjects = new ObjectChanges { Objects = [new ObjectChange { ObjectID = AsmRig.Ref("a"), Level = -12 }] } }));
        Assert.Equal([2, 4, 5], Route(new UserCommandRQ { ChangedObjects = new ObjectChanges { Objects = [new ObjectChange { ObjectID = AsmRig.Ref("a"), SpatialAttitude = AsmRig.At(1) }] } }));
        Assert.Equal([1], Route(new UserCommandRQ { ListedObjects = new ObjectListing { Kind = "Basic Audio Object" } }));
        Assert.Equal([5], Route(new UserCommandRQ { ListedObjects = new ObjectListing { Kind = "Multimodal Scene" } }));
        Assert.Equal([1, 2, 3, 4, 5], Route(new UserCommandRQ { ListedObjects = new ObjectListing { Kind = "All" } }));
        Assert.Equal([3], Route(new UserCommandRQ { RevokedUserCommandID = "r" }, id => id == "r" ? [3] : null));
        Assert.Equal([1, 2, 3, 4, 5], Route(new UserCommandRQ { RevokedUserCommandID = "unknown" }, _ => null));
    }

    // ---- the Composite is wired through it --------------------------------------------------------------------------------------------

    [Fact]
    public void TheCompositeIsWiredThroughTheInterface()
    {
        // The L3 and the L2 of the Composite; the boundary of the Composite is what it was, plus the two Ports of the application interface.
        foreach (var (file, ami) in new[] { (Path.Combine(Repository.Amds, "1CAE-ASM-V1.0-I01.json"), "1CAE-AMI-V1.0-I01"), (Path.Combine(Repository.Schemas, "CAE3", "V1.0", "AIMs", "AudioSceneManager.json"), "CAE-AMI-V1.0") })
        {
            var asm = JsonNode.Parse(File.ReadAllText(file))!;
            var amiL = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Amds, "1CAE-AMI-V1.0-I01.json")))!;
            Assert.Contains(asm["SubAIMs"]!.AsArray(), s => s!["Identifier"]!["AIMName"]!.GetValue<string>() == ami);

            static string Key(JsonNode p) => $"{p["Direction"]} {(p["DataType"] is JsonValue ? p["DataType"]!.GetValue<string>() : "?")}#{p["PortNumber"]?.GetValue<int>() ?? 1}";
            var boundary = asm["ExternalPorts"]!.AsArray().Select(p => Key(p!)).ToList();
            Assert.Contains("Input MMC-AJI-V2.5#1", boundary);
            Assert.Contains("Output MMC-AJI-V2.5#1", boundary);

            // The Interface has, on its left, each Port of the boundary - Data Type and Port Number the same - and the application interface.
            var left = amiL["ExternalPorts"]!.AsArray().Select(p => p!).Where(p => !((p["Name"]!.GetValue<string>()).Contains("Editor") || p["Name"]!.GetValue<string>().EndsWith("FromEditor") || p["Name"]!.GetValue<string>().EndsWith("ToEditor")))
                .Select(p => Key(p)).ToList();
            foreach (var key in boundary.Where(b => !b.Contains("?")))
                Assert.Contains(key, left);

            // Every line that touches the boundary has the Interface at its other end; no Editing AIM is connected to the boundary.
            var lines = asm["Topology"]!.AsArray().Select(l => l!).ToList();
            foreach (var line in lines)
            {
                var from = line["Output"]!["AIMName"]!.GetValue<string>(); var to = line["Input"]!["AIMName"]!.GetValue<string>();
                if (from == "") Assert.Equal(ami, to);
                if (to == "") Assert.Equal(ami, from);
            }

            // Each input of an Editing AIM has exactly one source.
            var inputs = lines.Where(l => l["Input"]!["AIMName"]!.GetValue<string>() is { Length: > 0 } a && a != ami)
                .GroupBy(l => $"{l["Input"]!["AIMName"]}|{l["Input"]!["DataType"]}|{l["Input"]!["PortNumber"]?.GetValue<int>() ?? 1}");
            Assert.All(inputs, g => Assert.Single(g));

            // The Interface's own Ports are used once on each side.
            var amiInputs = lines.Where(l => l["Input"]!["AIMName"]!.GetValue<string>() == ami).GroupBy(l => $"{l["Input"]!["DataType"]}|{l["Input"]!["PortNumber"]?.GetValue<int>() ?? 1}");
            Assert.All(amiInputs, g => Assert.Single(g));
            var amiOutputs = lines.Where(l => l["Output"]!["AIMName"]!.GetValue<string>() == ami).GroupBy(l => $"{l["Output"]!["DataType"]}|{l["Output"]!["PortNumber"]?.GetValue<int>() ?? 1}");
            Assert.All(amiOutputs, g => Assert.Single(g));
        }
    }

    [Fact]
    public void TheAccessOfTheInterfaceNamesTheSchemaOfTheUserCommand()
    {
        var l3 = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Amds, "1CAE-AMI-V1.0-I01.json")))!;
        var entry = Assert.Single(l3["AccessSources"]!.AsArray())!;
        Assert.Equal("CommandSchema", entry["Setting"]!.GetValue<string>());
        Assert.Equal(UserCommandUri, entry["Uri"]!.GetValue<string>());
        // The SHA-256 pins the schema as it is in this repository: a schema changed without the Metadata following is found here.
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(Repository.Schemas, "CAE3", "V1.0", "data", "UserCommand.json"))));
        Assert.Equal(sha, entry["Sha256"]!.GetValue<string>(), ignoreCase: true);
        var l2 = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Schemas, "CAE3", "V1.0", "AIMs", "AudioSceneManagementInterface.json")))!;
        Assert.Equal("CommandSchema", Assert.Single(l2["AccessSources"]!.AsArray())!["Name"]!.GetValue<string>());
    }
}
