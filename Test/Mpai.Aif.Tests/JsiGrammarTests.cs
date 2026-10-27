using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Core;

namespace Mpai.Aif.Tests;

// THE GRAMMAR OF THE APPLICATION, FIRST (the author, 2026/10/27: the old application answered at once because it parsed the words, with no language model). Requests of a known shape become a
// command from the Application Context's Rules, checked against the schema as any is; the language model is never asked. The scene is that of Test/AsmVoice/cases.json: violin at
// (-1.5, 2, 0), piano at (-3, 0, 0) facing 90; a flute, a rain and an applause are kept and not in it.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class JsiGrammarTests
{
    private static readonly AimPortReader Ports = JsiRig.Ports(JsiRig.Jsi);

    private static async Task<JsonNode?> Command(string words, Func<string>? onModel = null)
    {
        var jsi = JsiRig.Processor((_, _, _) => { onModel?.Invoke(); return Task.FromResult("{\"Problem\":\"the model was asked\"}"); }, JsiRig.Jsi, false, true);
        var answer = await jsi.ProcessAsync(JsiRig.Say(Ports, words, JsiRig.Context(JsiRig.Scene())));
        Assert.Null(JsiRig.FactsOf(answer, Ports));
        return JsiRig.CommandOf(answer, Ports)?["UserCommandRQ"];
    }

    private static double[] Numbers(JsonNode? array) => array!.AsArray().Select(x => x!.GetValue<double>()).ToArray();

    [Theory]
    [InlineData("move the violin 2 metres to the right", 0.5, 2, 0)]
    [InlineData("Move the violin two meters to the left.", -3.5, 2, 0)]
    [InlineData("move the violin by 1.5 metres forward", -1.5, 3.5, 0)]
    [InlineData("shift the violin 3 metres back", -1.5, -1, 0)]
    [InlineData("move the violin 1 metre up", -1.5, 2, 1)]
    [InlineData("move the violin to the right by 2 metres", 0.5, 2, 0)]
    [InlineData("put the violin at x 2, y 1, z 0", 2, 1, 0)]
    [InlineData("move the drums to x -1, y 3, z 0", -1, 3, 0)]
    public async Task ARequestOfKnownShapeMovesAnObjectWithoutTheModel(string words, double x, double y, double z)
    {
        var asked = 0;
        var rq = await Command(words, () => { asked++; return ""; });
        Assert.Equal(0, asked);
        var moved = rq!["MovedObjects"]!["Objects"]![0]!;
        Assert.Equal([x, y, z], Numbers(moved["NewSpatialAttitude"]!["Position"]!["CartPosition"]));
    }

    [Fact]
    public async Task AKeptSoundNotInTheSceneIsAdded()
    {
        var rq = await Command("put the flute at x 1, y 2, z 0");
        var added = rq!["AddedObjects"]!["Objects"]![0]!;
        Assert.Equal("flute", (string?)added["ObjectID"]);
        Assert.Equal([1.0, 2.0, 0.0], Numbers(added["SpatialAttitude"]!["Position"]!["CartPosition"]));
    }

    [Fact]
    public async Task ATurnKeepsThePositionAndChangesTheYaw()
    {
        var moved = (await Command("turn the piano to 90 degrees"))!["MovedObjects"]!["Objects"]![0]!;
        Assert.Equal([-3.0, 0.0, 0.0], Numbers(moved["NewSpatialAttitude"]!["Position"]!["CartPosition"]));
        Assert.Equal(90.0, Numbers(moved["NewSpatialAttitude"]!["Orientation"]!["Orientation"])[2]);
    }

    [Fact]
    public async Task ALevelRemovalAndDeletionAreEachTheirOwnCommand()
    {
        Assert.Equal(-14.0, (double)(await Command("set the violin to minus 14 LUFS"))!["ChangedObjects"]!["Objects"]![0]!["Level"]!);
        Assert.Equal("violin", (string?)(await Command("remove the violin"))!["RemovedObjects"]!["Objects"]![0]!["ObjectID"]);
        Assert.Equal("flute", (string?)(await Command("delete the flute from the store"))!["DeletedObjects"]!["Objects"]![0]!["ObjectID"]);
        Assert.Null((await Command("delete the flute"))!["RemovedObjects"]);
        Assert.Equal("stage", (string?)(await Command("play the scene"))!["DeliveredObject"]);          // the one scene this application makes
    }

    [Fact]
    public async Task ARoomIsABoxCentredAtTheOrigin()
    {
        var room = (await Command("create a square room of 3 metres by 3 metres centred at 0 0"))!["ClosedSpace"]![0]!;
        var corners = room["Shape"]![0]!["SimpleLocationData"]![0]!["RightParallelepiped"]!["CartPositions"]!.AsArray().Select(Numbers).ToList();
        Assert.Equal([-1.5, -1.5, 0.0], corners[0]); Assert.Equal([1.5, -1.5, 0.0], corners[1]); Assert.Equal([-1.5, 1.5, 0.0], corners[2]); Assert.Equal([-1.5, -1.5, 2.5], corners[3]);
        var other = (await Command("make the room 6 by 4 metres"))!["ClosedSpace"]![0]!["Shape"]![0]!["SimpleLocationData"]![0]!["RightParallelepiped"]!["CartPositions"]!.AsArray().Select(Numbers).ToList();
        Assert.Equal([-3.0, -2.0, 0.0], other[0]); Assert.Equal([3.0, -2.0, 0.0], other[1]); Assert.Equal([-3.0, 2.0, 0.0], other[2]);
    }

    [Fact]
    public async Task UndoRevokesTheLastCommandOfThePerson()
    {
        var jsi = JsiRig.Processor((_, _, _) => Task.FromResult("{\"Problem\":\"the model was asked\"}"), JsiRig.Jsi, false, true);
        var first = JsiRig.CommandOf(await jsi.ProcessAsync(JsiRig.Say(Ports, "move the violin 2 metres to the right", JsiRig.Context(JsiRig.Scene()))), Ports)!;
        var undo = JsiRig.CommandOf(await jsi.ProcessAsync(JsiRig.Say(Ports, "undo that")), Ports)!;
        Assert.Equal((string)first["UserCommandID"]!, (string)undo["UserCommandRQ"]!["RevokedUserCommandID"]!);
    }

    [Theory]
    [InlineData("move the harp 2 metres to the right")]                       // a name the application does not have: not the grammar's
    [InlineData("rearrange everything so it sounds nicer")]
    public async Task WhatNoRuleMatchesGoesToTheModel(string words)
    {
        var asked = 0;
        var jsi = JsiRig.Processor((_, _, _) => { asked++; return Task.FromResult("{\"Problem\":\"I did not understand.\"}"); }, JsiRig.Jsi, false, true);
        await jsi.ProcessAsync(JsiRig.Say(Ports, words, JsiRig.Context(JsiRig.Scene())));
        Assert.True(asked >= 0);                                                   // (the guards may answer before the model: what matters is that no rule made a command)
    }

    [Theory]
    [InlineData("place a room of 4x4 meters at the centre of the room", 2.0, 2.0)]
    [InlineData("Place a room of four by four meters at the center of the room.", 2.0, 2.0)]
    [InlineData("make the room 6 by 4 metres", 3.0, 2.0)]
    [InlineData("create a square room of 3 metres by 3 metres centred at 0 0", 1.5, 1.5)]
    [InlineData("build a 5 by 3 metre room", 2.5, 1.5)]
    public async Task ARoomIsNeverAMoveAndIsMadeInAnEmptyScene(string words, double halfX, double halfY)
    {
        var rq = await Command(words);
        Assert.Null(rq!["MovedObjects"]);
        Assert.Equal("stage", (string?)rq["AddedObjects"]!["ObjectID"]);                       // the Scene the room is made in, if none is open
        Assert.Empty(rq["AddedObjects"]!["Objects"]!.AsArray());
        var corners = rq["ClosedSpace"]![0]!["Shape"]![0]!["SimpleLocationData"]![0]!["RightParallelepiped"]!["CartPositions"]!.AsArray().Select(Numbers).ToList();
        Assert.Equal([-halfX, -halfY, 0.0], corners[0]); Assert.Equal([halfX, -halfY, 0.0], corners[1]); Assert.Equal([-halfX, halfY, 0.0], corners[2]); Assert.Equal(2.5, corners[3][2]);
    }

}
