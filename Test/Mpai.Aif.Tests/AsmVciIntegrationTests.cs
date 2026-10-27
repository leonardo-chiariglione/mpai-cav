using System.Text.Json;
using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Mmc.Jsi;

namespace Mpai.Aif.Tests;

// THE VOICE COMMAND INTERFACE IN AUDIO SCENE MANAGEMENT (the author, 2026/10/27). The Voice Command Interface issues one User Command request to all the editing AIMs and takes their
// responses back on one Port: the JSON Interface gathers the responses to one command (Responders) and makes one of them. Judged here: what the editing AIMs answer to a command
// that is not for them, the merge of their answers, the gathering by the JSON Interface, the L2 of the Module, and the Application Context of the application against the structure of one.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class AsmVciIntegrationTests
{
    private static ReportAction Action(string action, string obj, string outcome = "Done", string? reason = null) => new() { Action = action, ObjectID = obj, Outcome = outcome, Reason = reason };

    private static UserCommand Answer(string id, params ReportAction[] actions) => new()
    {
        UserCommandID = id, UserCommandTime = SimpleTime.At(DateTimeOffset.UtcNow),
        UserCommandRS = new UserCommandRS
        {
            ReportTime = SimpleTime.At(DateTimeOffset.UtcNow),
            Outcome = actions.Length == 0 ? "Ignored" : actions.All(a => a.Outcome == "Done") ? "Done" : "Failed",
            Actions = actions.ToList()
        }
    };

    // ---- the merge of the answers ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void WhatOneAimDidStandsOverWhatTheOthersCouldNot()
    {
        var merged = UserCommandMerge.Merge("c", [
            Answer("c", Action("AddedObjects", "violin", "Failed", "the Module keeps no such Audio Object")),
            Answer("c", Action("AddedObjects", "violin")),
            Answer("c")]);
        Assert.Equal("c", merged.UserCommandID);
        Assert.Equal("Done", merged.UserCommandRS!.Outcome);
        Assert.Single(merged.UserCommandRS.Actions!);
        Assert.Equal("Done", merged.UserCommandRS.Actions![0].Outcome);
    }

    [Fact]
    public void WhenNoneDidAnythingTheCommandWasIgnoredAndWhenAllFailedItFailed()
    {
        Assert.Equal("Ignored", UserCommandMerge.Merge("c", [Answer("c"), Answer("c")]).UserCommandRS!.Outcome);
        Assert.Equal("Failed", UserCommandMerge.Merge("c", [Answer("c", Action("RemovedObjects", "harp", "Failed", "none")), Answer("c", Action("RemovedObjects", "harp", "Failed", "none"))]).UserCommandRS!.Outcome);
        Assert.Equal("Partly", UserCommandMerge.Merge("c", [Answer("c", Action("MovedObjects", "violin")), Answer("c", Action("RemovedObjects", "harp", "Failed", "none"))]).UserCommandRS!.Outcome);
    }

    // ---- the editing AIMs, given one command by the Voice Command Interface ------------------------------------------------------------------------------

    [Fact]
    public async Task EveryEditingAimAnswersACommandAndTheAnswersMakeOne()
    {
        using var rig = new AsmRig();
        // A sound arrives in a User Command: Audio Object Editing takes it; the others have nothing to do with it.
        var acquire = AsmRig.Command(new UserCommandRQ { AcquiredObject = new ManagedObject { BasicAudioObject = AsmRig.Sound("violin") } });
        var answers = await rig.Ask(acquire);
        Assert.All(answers.Responses, r => Assert.NotNull(r));                                  // each of the five answers
        Assert.Equal("Done", answers.Of(0)!.UserCommandRS!.Outcome);
        Assert.Contains(answers.Of(0)!.UserCommandRS!.Actions!, a => a.Action == "AcquiredObject" && a.Outcome == "Done");
        Assert.Equal("Done", answers.Merged(acquire.UserCommandID).UserCommandRS!.Outcome);

        // The sound is placed in a scene: Audio Scene Editing does it; Speech Scene Editing says it keeps no such Object; one answer comes of them.
        var add = AsmRig.Command(new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = "stage", Objects = [AsmRig.Put("violin", 1)] } });
        var merged = (await rig.Ask(add)).Merged(add.UserCommandID);
        if (merged.UserCommandRS!.Outcome != "Done") Assert.Fail(AsmRig.Json(merged));
        Assert.Contains(merged.UserCommandRS.Actions!, a => a.Action == "AddedObjects" && a.Outcome == "Done");

        // The list of everything: the answers add up.
        var list = AsmRig.Command(new UserCommandRQ { ListedObjects = new ObjectListing { Kind = "All" } });
        var all = (await rig.Ask(list)).Merged(list.UserCommandID);
        Assert.Contains(all.UserCommandRS!.ListedObjects!, o => o.ObjectID == "violin");
        Assert.Contains(all.UserCommandRS.ListedObjects!, o => o.ObjectID == "stage");
    }

    [Fact]
    public async Task ASoundByItsNameOpensTheObjectTheModuleKeeps()
    {
        using var rig = new AsmRig();
        await rig.OpenAudio(AsmRig.Sound("violin"));
        var open = AsmRig.Command(new UserCommandRQ { AcquiredObject = new ManagedObject { ObjectID = "violin" } });
        var merged = (await rig.Ask(open)).Merged(open.UserCommandID);
        Assert.Contains(merged.UserCommandRS!.Actions!, a => a.Action == "AcquiredObject" && a.Outcome == "Done");
        var unknown = AsmRig.Command(new UserCommandRQ { AcquiredObject = new ManagedObject { ObjectID = "harp" } });
        Assert.Equal("Failed", (await rig.Ask(unknown)).Merged(unknown.UserCommandID).UserCommandRS!.Outcome);
    }

    // ---- deleting from where the Module keeps things -----------------------------------------------------------------------------------------------------

    private static UserCommand Delete(params string[] names) =>
        AsmRig.Command(new UserCommandRQ { DeletedObjects = new ObjectPlacements { Objects = names.Select(n => new ObjectPlacement { ObjectID = AsmRig.Ref(n) }).ToList() } });

    [Fact]
    public async Task ASceneAndAnObjectAreDeletedByTheirNamesAnObjectInASceneNotUntilTheSceneIs()
    {
        using var rig = new AsmRig();
        async Task<UserCommand> Run(UserCommand command) => (await rig.Ask(command)).Merged(command.UserCommandID);
        await rig.OpenAudio(AsmRig.Sound("violin")); await rig.OpenAudio(AsmRig.Sound("drums", 1, 2));
        Assert.Equal("Done", (await Run(AsmRig.Command(new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = "stage", Objects = [AsmRig.Put("violin", 1)] } }))).UserCommandRS!.Outcome);

        // The violin is a member of the scene: it is not deleted, and the response says by what.
        var refused = await Run(Delete("violin"));
        Assert.Equal("Failed", refused.UserCommandRS!.Outcome);
        Assert.Contains(refused.UserCommandRS.Actions!, a => a.Action == "DeletedObjects" && (a.Reason ?? "").Contains("stage"));

        // The drums are in no scene: deleted. A name the Module keeps nothing under is reported failed by every editor.
        Assert.Equal("Done", (await Run(Delete("drums"))).UserCommandRS!.Outcome);
        Assert.Equal("Failed", (await Run(Delete("harp"))).UserCommandRS!.Outcome);

        // The scene, then the object it held.
        Assert.Equal("Done", (await Run(Delete("stage"))).UserCommandRS!.Outcome);
        Assert.Equal("Done", (await Run(Delete("violin"))).UserCommandRS!.Outcome);

        var all = await Run(AsmRig.Command(new UserCommandRQ { ListedObjects = new ObjectListing { Kind = "All" } }));
        Assert.True(all.UserCommandRS!.ListedObjects is null or { Count: 0 }, AsmRig.Json(all));
        // What went can be given again under the name.
        await rig.OpenAudio(AsmRig.Sound("violin"));
        var again = await Run(AsmRig.Command(new UserCommandRQ { ListedObjects = new ObjectListing { Kind = "All" } }));
        Assert.Contains(again.UserCommandRS!.ListedObjects!, o => o.ObjectID == "violin");
    }

    [Fact]
    public async Task ARoomIsMadeInAnEmptySceneAndOnlyAudioSceneEditingAnswersForIt()
    {
        using var rig = new AsmRig();
        var room = AsmRig.Command(new UserCommandRQ
        {
            AddedObjects = new ObjectPlacements { ObjectID = "stage", Objects = [] },
            ClosedSpace = new System.Text.Json.Nodes.JsonArray(System.Text.Json.Nodes.JsonNode.Parse(
                "{\"Header\":\"OSD-CSP-V1.5\",\"ClosedSpaceID\":\"room\",\"Shape\":[{\"Header\":\"OSD-SLC-V1.5\",\"SimpleLocationData\":[{\"RightParallelepiped\":{\"Header\":\"OSD-RPP-V1.5\",\"Dimensions\":\"3D\",\"CartPositions\":[[-2,-2,0],[2,-2,0],[-2,2,0],[-2,-2,2.5]]}}]}]}")!)
        });
        var answers = await rig.Ask(room);
        var merged = answers.Merged(room.UserCommandID);
        Assert.Equal("Done", merged.UserCommandRS!.Outcome);
        Assert.Contains(merged.UserCommandRS.Actions!, a => a.Action == "ClosedSpace" && a.Outcome == "Done");
        Assert.DoesNotContain(merged.UserCommandRS.Actions!, a => a.Outcome != "Done");               // no noise from the editors that have nothing to do with a room
        var list = AsmRig.Command(new UserCommandRQ { ListedObjects = new ObjectListing { Kind = "Audio Scene" } });
        Assert.Contains((await rig.Ask(list)).Merged(list.UserCommandID).UserCommandRS!.ListedObjects!, o => o.ObjectID == "stage");
    }

    // ---- the JSON Interface gathers the answers ----------------------------------------------------------------------------------------------------------

    private static JsiAimProcessor Gathering(StructuredModel model, int responders) =>
        new(JsiRig.Jsi, JsiRig.Ports(JsiRig.Jsi), JsiRig.Configuration() with { Responders = responders }, model);

    private static readonly string Move = "{\"Working\":\"move\",\"Command\":{\"UserCommandRQ\":{\"MovedObjects\":{\"Objects\":[{\"ObjectID\":\"violin\",\"NewSpatialAttitude\":{\"Position\":{\"CartPosition\":[-1.5,2,0]},\"Orientation\":{\"Orientation\":[0,0,180]}}}]}}}}";

    [Fact]
    public async Task TheAnswersOfAllTheEditingAimsAreOneResponseTheyAreToldOnce()
    {
        var jsi = Gathering((_, _, _) => Task.FromResult(Move), 5);
        var ports = JsiRig.Ports(JsiRig.Jsi);
        var sent = JsiRig.CommandOf(await jsi.ProcessAsync(JsiRig.Say(ports, "move the violin 1 metre to the left", JsiRig.Context(JsiRig.Scene()))), ports)!;
        var id = (string)sent["UserCommandID"]!;

        async Task<Message> Give(UserCommand answer) =>
            await jsi.ProcessAsync(new Message { MessageId = Guid.NewGuid().ToString(), Ports = new() { [JsiRig.Response(ports)] = MpaiJson.ToJson(answer) } });

        // Four of the five have nothing to do with it, one did: nothing is said until the fifth has answered.
        for (var k = 0; k < 3; k++) Assert.Null(JsiRig.FactsOf(await Give(Answer(id)), ports));
        Assert.Null(JsiRig.FactsOf(await Give(Answer(id, Action("MovedObjects", "violin"))), ports));
        var told = JsiRig.FactsOf(await Give(Answer(id)), ports);
        Assert.NotNull(told);
        Assert.Contains("move the violin 1 metre to the left", told);
        Assert.Contains("result done", told);
        Assert.Contains("on violin", told);
    }

    [Fact]
    public async Task AnAnswerToACommandThatIsNotTheirsIsNotToldAndAListingIsTheState()
    {
        var jsi = Gathering((_, _, _) => Task.FromResult(Move), 5);
        var ports = JsiRig.Ports(JsiRig.Jsi);
        var told = await jsi.ProcessAsync(new Message { MessageId = "1", Ports = new() { [JsiRig.Response(ports)] = MpaiJson.ToJson(Answer("asked-by-the-user-agent", Action("MovedObjects", "violin"))) } });
        Assert.Null(JsiRig.FactsOf(told, ports));
        Assert.Null(JsiRig.FactsOf(await jsi.ProcessAsync(new Message { MessageId = "2", Ports = new() { [JsiRig.Response(ports)] = JsiRig.Context(JsiRig.Scene()).ToJsonString() } }), ports));
    }

    // ---- the L2 of the Module --------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheVoiceCommandInterfaceIsInsideTheModuleAndTheAmiAndTheAjiAreGone()
    {
        var l2 = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Schemas, "CAE3", "V1.0", "AIMs", "AudioSceneManager.json")))!;
        var names = l2["SubAIMs"]!.AsArray().Select(s => (string)s!["Identifier"]!["AIMName"]!).ToList();
        Assert.Contains("MMC-VCI-V2.5", names);
        Assert.DoesNotContain("CAE-AMI-V1.0", names);
        var text = l2.ToJsonString();
        Assert.DoesNotContain("MMC-AJI", text);
        Assert.DoesNotContain("OSD-OPV", text);                                                  // the User Command carries the User Point of View
        Assert.False(File.Exists(Path.Combine(Repository.Schemas, "CAE3", "V1.0", "AIMs", "AudioSceneManagementInterface.json")));
        Assert.False(File.Exists(Path.Combine(Repository.Schemas, "MMC", "V2.5", "data", "ApplicationJSONInstance.json")));

        // The command of the Voice Command Interface goes to the five editing AIMs; their five responses come back to it, and go out at the Ports of the Composite.
        var topology = l2["Topology"]!.AsArray();
        int From(string aim, string dataType, string toAim) =>
            topology.Count(e => (string)e!["Output"]!["AIMName"]! == aim && (string)e["Output"]!["DataType"]! == dataType && (string)e["Input"]!["AIMName"]! == toAim);
        foreach (var editor in new[] { "CAE-AOE-V1.0", "CAE-ASE-V1.0", "MMC-SOE-V2.5", "MMC-SSE-V2.5", "OSD-MSE-V1.5" })
        {
            Assert.Equal(1, From("MMC-VCI-V2.5", "CAE-UCM-V1.0", editor));
            Assert.Equal(1, From(editor, "CAE-UCM-V1.0", "MMC-VCI-V2.5"));
            Assert.Equal(1, From(editor, "CAE-UCM-V1.0", ""));
            Assert.Equal(1, From("", "CAE-UCM-V1.0", editor));
        }
    }

    // ---- the guide of the application, against the structure of a guide ------------------------------------------------------------------------------------

    [Fact]
    public void TheApplicationContextOfAudioSceneManagementFollowsTheStructureOfOne()
    {
        var schema = AIF.Metadata.PublishedSchemas.At(Repository.Schemas)[Path.GetFullPath(Path.Combine(Repository.Schemas, "MMC", "V2.5", "data", "ApplicationContext.json"))];
        using var doc = JsonDocument.Parse(File.ReadAllText(JsiRig.ContextFile));
        EvaluationResultsText(schema, doc, out var valid, out var why);
        Assert.True(valid, why);
    }

    private static void EvaluationResultsText(Json.Schema.JsonSchema schema, JsonDocument doc, out bool valid, out string why)
    {
        lock (AIF.Metadata.PublishedSchemas.Lock)
        {
            var result = schema.Evaluate(doc.RootElement, new Json.Schema.EvaluationOptions { OutputFormat = Json.Schema.OutputFormat.List });
            valid = result.IsValid;
            why = string.Join("\n", (result.Details ?? []).Where(d => !d.IsValid && d.Errors is not null).SelectMany(d => d.Errors!.Select(e => d.InstanceLocation + ": " + e.Value)).Distinct().Take(15));
        }
    }
}
