using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Mmc.Jsi;

namespace Mpai.Aif.Tests;

// THE JSON INTERFACE (MMC-JSI-V2.5), with a language model that is the test's. Judged:
//   - the schema the model is shown is the schema of the command reduced by the guide of the application - what it lists and what is required, names limited to those of the
//     state of the application, no descriptions in the shape - and what the model gives, completed, conforms to the whole schema;
//   - a command that conforms leaves in an Application JSON Instance under the schema of the Access; one that does not is given back to the model once, with the fault; after
//     the second, no command leaves and the facts say so;
//   - a number the request does not give is asked for, a request that is not for the application or names no object is answered, neither is sent to the model, and the model
//     can say what is missing itself: in every case no command, and facts that say what is missing in plain words;
//   - the state of the application is respected: its names and where its objects are are what the model is told, and the names are what it can choose from;
//   - a response of the application becomes facts: what was done, with the request it answers; an earlier command can be named to be undone;
//   - the AIM knows no application: without the schemas and the guide in its Access it sends nothing.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class JsiTests
{
    private const string MoveViolin =
        "{\"Working\":\"MovedObjects violin: x=-1.5-1=-2.5.\",\"Command\":{\"UserCommandRQ\":{\"MovedObjects\":{\"Objects\":[{\"ObjectID\":\"violin\",\"NewSpatialAttitude\":{\"Position\":{\"CartPosition\":[-2.5,2,0]},\"Orientation\":{\"Orientation\":[0,0,180]}}}]}}}}";
    private const string MoveViolinWithoutOrientation =
        "{\"Working\":\"x\",\"Command\":{\"UserCommandRQ\":{\"MovedObjects\":{\"Objects\":[{\"ObjectID\":\"violin\",\"NewSpatialAttitude\":{\"Position\":{\"CartPosition\":[-2.5,2,0]}}}]}}}}";

    private sealed record Call(string System, List<(string Role, string Content)> Turns, JsonNode Schema);

    private static (StructuredModel Model, List<Call> Calls) Fake(params string[] answers)
    {
        var calls = new List<Call>();
        StructuredModel model = (system, turns, schema) =>
        {
            calls.Add(new Call(system, turns.ToList(), schema.DeepClone()));
            return Task.FromResult(answers[Math.Min(calls.Count, answers.Length) - 1]);
        };
        return (model, calls);
    }

    private static readonly AimPortReader Ports = JsiRig.Ports(JsiRig.Jsi);
    private static JsonNode Context() => JsiRig.Context(JsiRig.Scene());

    private static Task<Message> Ask(JsiAimProcessor jsi, string words, JsonNode? context = null) => jsi.ProcessAsync(JsiRig.Say(Ports, words, context));

    // ---- the Application Context on its Port ---------------------------------------------------------------------------------------------------

    private static Message GiveContext(string json) =>
        new() { MessageId = Guid.NewGuid().ToString(), Ports = new Dictionary<string, string> { [Ports.Input("MMC-APC-V2.5")] = json } };

    [Fact]
    public async Task AnApplicationContextOnItsPortReplacesTheOneOfAccess()
    {
        var (model, calls) = Fake(MoveViolin);
        var jsi = JsiRig.Processor(model);
        var given = JsonNode.Parse(File.ReadAllText(JsiRig.ContextFile))!.AsObject();
        given["Application"] = "A test application whose words are stranger than the ones of Access.";
        var said = await jsi.ProcessAsync(GiveContext(given.ToJsonString()));
        Assert.Equal("Nothing", said.MessageType);                       // the context is taken; nothing is said and nothing is sent
        await Ask(jsi, "move the violin 1 metre to the right", Context());
        Assert.Contains("stranger than the ones of Access", calls[0].System);
    }

    [Fact]
    public async Task AnApplicationContextThatIsNotOneIsRefusedAndTheOneInUseStays()
    {
        var (model, calls) = Fake(MoveViolin);
        var jsi = JsiRig.Processor(model);
        var bad = JsonNode.Parse(File.ReadAllText(JsiRig.ContextFile))!.AsObject();
        bad["Application"] = "A test application that must not be used.";
        bad["Header"] = "MMC-XXX-V2.5";
        await jsi.ProcessAsync(GiveContext(bad.ToJsonString()));
        bad["Header"] = "MMC-APC-V2.5"; bad["Include"] = "not a list";
        await jsi.ProcessAsync(GiveContext(bad.ToJsonString()));
        await Ask(jsi, "move the violin 1 metre to the right", Context());
        Assert.DoesNotContain("must not be used", calls[0].System);
    }

    // ---- the shape the model is shown ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheModelIsShownTheSchemaReducedByTheGuide()
    {
        var guide = ApplicationContext.Parse(File.ReadAllText(JsiRig.ContextFile));
        var names = new Dictionary<string, IReadOnlyList<string>> { ["Present"] = ["concert", "violin", "cello"], ["Members"] = ["violin", "cello"], ["Stored"] = ["flute"] };
        var view = new ModelView(new SchemaLibrary(Repository.Schemas), guide, names);
        Assert.NotNull(view.Build(JsiRig.UserCommandUri));
        var shown = view.ForModel().ToJsonString();

        // What the guide lists is there, and what it does not list is not: no metadata, no times, no accuracy, no second form of an object.
        Assert.Contains("MovedObjects", shown);
        Assert.Contains("NewSpatialAttitude", shown);
        Assert.DoesNotContain("DataXMData", shown);
        Assert.DoesNotContain("DescrMetadata", shown);
        Assert.DoesNotContain("UserCommandRS", shown);
        Assert.DoesNotContain("CartAccuracy", shown);
        Assert.DoesNotContain("description", shown);                // a grammar has no use for them
        Assert.DoesNotContain("x-fill", shown);                     // what the AIM fills in is not the model's
        Assert.DoesNotContain("Header", shown);                     // constants are filled in afterwards
        Assert.DoesNotContain("\"UserCommandID\"", shown);

        // The names are those the application told: the members for an object, the stored sounds for a sound to add.
        var moved = view.ForModel()["properties"]!["UserCommandRQ"]!["properties"]!["MovedObjects"]!["properties"]!["Objects"]!["items"]!["properties"]!["ObjectID"]!;
        Assert.Equal(["violin", "cello"], moved["enum"]!.AsArray().Select(x => (string)x!).ToArray());
        var added = view.ForModel()["properties"]!["UserCommandRQ"]!["properties"]!["AddedObjects"]!["properties"]!["Objects"]!["items"]!["properties"]!["ObjectID"]!;
        Assert.Equal(["flute"], added["enum"]!.AsArray().Select(x => (string)x!).ToArray());

        // The outline tells the fields in the guide's words.
        var outline = view.Outline();
        Assert.Contains("Moves, places or turns objects", outline);
        Assert.Contains("[x, y, z] in metres", outline);
    }

    [Fact]
    public void WhatTheModelGivesIsCompletedToTheWholeSchema()
    {
        var guide = ApplicationContext.Parse(File.ReadAllText(JsiRig.ContextFile));
        var view = new ModelView(new SchemaLibrary(Repository.Schemas), guide);
        view.Build(JsiRig.UserCommandUri);
        var check = new SchemaCheck(JsiRig.UserCommandUri, JsiRig.UserCommandFile, Repository.Schemas);

        var given = JsonNode.Parse(MoveViolin)!["Command"]!;
        var completed = view.Complete(given);
        Assert.Equal("CAE-UCM-V1.0", (string?)completed["Header"]);
        Assert.False(string.IsNullOrEmpty((string?)completed["UserCommandID"]));
        Assert.NotNull(completed["UserCommandTime"]);
        Assert.Equal("OSD-OSA-V1.5", (string?)completed["UserCommandRQ"]!["MovedObjects"]!["Objects"]![0]!["NewSpatialAttitude"]!["Header"]);
        Assert.Empty(check.Violations(completed.ToJsonString())!);
        // The model's own values are not touched.
        Assert.Equal(-2.5, completed["UserCommandRQ"]!["MovedObjects"]!["Objects"]![0]!["NewSpatialAttitude"]!["Position"]!["CartPosition"]![0]!.GetValue<double>());
        // Two commands do not share an identifier.
        Assert.NotEqual((string?)completed["UserCommandID"], (string?)view.Complete(given)["UserCommandID"]);
    }

    [Fact]
    public void EveryExampleOfTheGuideIsACommandOfTheSchemaOrAProblem()
    {
        var guide = ApplicationContext.Parse(File.ReadAllText(JsiRig.ContextFile));
        var view = new ModelView(new SchemaLibrary(Repository.Schemas), guide);
        view.Build(JsiRig.UserCommandUri);
        var check = new SchemaCheck(JsiRig.UserCommandUri, JsiRig.UserCommandFile, Repository.Schemas);
        Assert.NotEmpty(guide.Examples);
        foreach (var example in guide.Examples)
        {
            if (example["Command"] is not { } command) { Assert.False(string.IsNullOrWhiteSpace((string?)example["Problem"]), (string?)example["Request"]); continue; }
            var completed = view.Complete(command);
            Assert.True(check.Violations(completed.ToJsonString())!.Count == 0, $"{example["Request"]}: {string.Join("; ", check.Violations(completed.ToJsonString())!)}");
        }
    }

    // The guide's paths exist in the schema it is for: a path that does not is a mistake in the guide, not a field that is quietly absent.
    [Fact]
    public void EveryPathOfTheGuideIsInTheSchema()
    {
        var guide = ApplicationContext.Parse(File.ReadAllText(JsiRig.ContextFile));
        var all = new ModelView(new SchemaLibrary(Repository.Schemas), ApplicationContext.Parse(File.ReadAllText(JsiRig.ContextFile)));
        all.Build(JsiRig.UserCommandUri);
        var shown = all.Full;
        JsonNode? At(string path)
        {
            JsonNode? node = shown;
            foreach (var part in path.Split('.'))
            {
                while (node?["items"] is JsonObject items) node = items;
                node = node?["properties"]?[part];
            }
            return node;
        }
        foreach (var path in guide.Include.Concat(guide.Require).Concat(guide.Names.Select(n => n.Path)).Concat(guide.Descriptions.Keys).Concat(guide.Triggers.Select(t => t.Path)))
            Assert.True(At(path) is not null, $"{path} is in the guide and not in the schema as the model is shown it");
    }

    // ---- a request becomes a command --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACommandThatConformsLeavesInACommand()
    {
        var (model, calls) = Fake(MoveViolin);
        var jsi = JsiRig.Processor(model);
        var answer = await Ask(jsi, "move the violin 1 metre to the left", Context());

        Assert.Single(calls);
        var aji = JsiRig.CommandOf(answer, Ports);
        Assert.NotNull(aji);
        var check = new SchemaCheck(JsiRig.UserCommandUri, JsiRig.UserCommandFile, Repository.Schemas);
        Assert.Empty(check.Violations(aji.ToJsonString())!);
        Assert.NotNull(aji["UserCommandRQ"]!["MovedObjects"]);
        // A command that was sent is not said: the person is told when the application answers.
        Assert.Null(JsiRig.FactsOf(answer, Ports));
    }

    [Fact]
    public async Task ACommandThatDoesNotConformIsGivenBackOnceWithTheFault()
    {
        var (model, calls) = Fake(MoveViolinWithoutOrientation, MoveViolin);
        var jsi = JsiRig.Processor(model);
        var answer = await Ask(jsi, "move the violin 1 metre to the left", Context());

        Assert.Equal(2, calls.Count);
        var last = calls[1].Turns;
        Assert.Equal("assistant", last[^2].Role);
        Assert.Equal(MoveViolinWithoutOrientation, last[^2].Content);                       // what it said
        Assert.Equal("user", last[^1].Role);
        Assert.Contains("That is not accepted", last[^1].Content);                          // and what was wrong with it
        Assert.Contains("Orientation", last[^1].Content);
        Assert.NotNull(JsiRig.CommandOf(answer, Ports));
        Assert.Null(JsiRig.FactsOf(answer, Ports));
    }

    [Fact]
    public async Task ACommandThatDoesNotConformTwiceGivesNoCommandAndFacts()
    {
        var (model, calls) = Fake(MoveViolinWithoutOrientation, MoveViolinWithoutOrientation, MoveViolin);
        var jsi = JsiRig.Processor(model);
        var answer = await Ask(jsi, "move the violin 1 metre to the left", Context());

        Assert.Equal(2, calls.Count);                                                       // once more, not again
        Assert.Null(JsiRig.CommandOf(answer, Ports));
        var facts = JsiRig.FactsOf(answer, Ports)!;
        Assert.StartsWith("No command was formed", facts);
        Assert.Contains("does not conform", facts);
    }

    [Fact]
    public async Task AnAnswerThatIsNotJsonIsGivenBackToo()
    {
        var (model, calls) = Fake("I will move the violin.", MoveViolin);
        var answer = await Ask(JsiRig.Processor(model), "move the violin 1 metre to the left", Context());
        Assert.Equal(2, calls.Count);
        Assert.Contains("not JSON", calls[1].Turns[^1].Content);
        Assert.NotNull(JsiRig.CommandOf(answer, Ports));
    }

    [Fact]
    public async Task ACommandWithNothingInItIsNotACommand()
    {
        var empty = "{\"Working\":\"x\",\"Command\":{\"UserCommandRQ\":{}}}";
        var (model, calls) = Fake(empty, empty);
        var answer = await Ask(JsiRig.Processor(model), "move the violin 1 metre to the left", Context());
        Assert.Null(JsiRig.CommandOf(answer, Ports));
        Assert.Contains("No command was formed", JsiRig.FactsOf(answer, Ports));
    }

    // ---- a request that cannot become a command ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("move the violin a bit to the left")]
    [InlineData("a little more cello")]
    [InlineData("make the drums louder")]
    [InlineData("bring the voice closer")]
    [InlineData("turn up the voice")]
    public async Task ANumberThatIsNotGivenIsAskedForAndNothingIsSent(string words)
    {
        var (model, calls) = Fake(MoveViolin);
        var answer = await Ask(JsiRig.Processor(model), words, Context());
        Assert.Empty(calls);                                                                // the model is not asked to guess
        Assert.Null(JsiRig.CommandOf(answer, Ports));
        var facts = JsiRig.FactsOf(answer, Ports)!;
        Assert.StartsWith("No command was formed", facts);
        Assert.Contains("By how much", facts);                                              // the number is asked for
    }

    [Theory]
    [InlineData("lower the piano a metre")]
    [InlineData("move the violin half a metre to the left")]
    [InlineData("bring the voice 2 metres closer")]
    public async Task AWordOfAmountWithANumberIsNotAsked(string words)
    {
        var (model, calls) = Fake(MoveViolin);
        await Ask(JsiRig.Processor(model), words, Context());
        Assert.Single(calls);
    }

    [Theory]
    [InlineData("save the scene")]
    [InlineData("stop")]
    [InlineData("what is the weather like")]
    public async Task ARequestThatIsNotForTheApplicationIsAnsweredWithoutTheModel(string words)
    {
        var (model, calls) = Fake(MoveViolin);
        var answer = await Ask(JsiRig.Processor(model), words, Context());
        Assert.Empty(calls);
        Assert.Null(JsiRig.CommandOf(answer, Ports));
        Assert.Contains("not something I can do", JsiRig.FactsOf(answer, Ports));
    }

    [Theory]
    [InlineData("move it 1 metre to the left")]
    [InlineData("rotate the guitar to 90 degrees")]
    [InlineData("move the instrument 1 metre closer")]
    public async Task AnObjectThatIsNotNamedIsAskedForWithTheNamesThereAre(string words)
    {
        var (model, calls) = Fake(MoveViolin);
        var answer = await Ask(JsiRig.Processor(model), words, Context());
        Assert.Empty(calls);
        Assert.Null(JsiRig.CommandOf(answer, Ports));
        var facts = JsiRig.FactsOf(answer, Ports)!;
        Assert.Contains("Which object do you mean?", facts);
        Assert.Contains("violin, cello, voice, drums, piano", facts);                       // the names the application told
    }

    [Theory]
    [InlineData("move the violent 1 metre to the left")]                                     // speech recognition: a letter or two
    [InlineData("shift the fiddle 0.5 metres to the left")]                                  // the guide says what a fiddle is
    [InlineData("set the whole scene to minus 23 LUFS")]                                     // no object, the scene
    [InlineData("move me to 0, -2, 0")]                                                      // the listener
    [InlineData("undo that")]                                                                // an earlier command
    public async Task AnObjectThatIsNamedIsNotAskedFor(string words)
    {
        var (model, calls) = Fake(MoveViolin);
        await Ask(JsiRig.Processor(model), words, Context());
        Assert.NotEmpty(calls);          // the model is asked (what it says is not the point here)
    }

    [Fact]
    public async Task TheModelCanSayWhatIsMissingItself()
    {
        var (model, calls) = Fake("{\"Working\":\"Problem: no saxophone.\",\"Problem\":\"There is no saxophone to add; the stored sounds are flute, rain and applause.\"}");
        var answer = await Ask(JsiRig.Processor(model), "add the saxophone at 1, 1, 0", Context());
        Assert.Single(calls);
        Assert.Null(JsiRig.CommandOf(answer, Ports));
        Assert.Equal("No command was formed. There is no saxophone to add; the stored sounds are flute, rain and applause.", JsiRig.FactsOf(answer, Ports));
    }

    // ---- the state of the application ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheStateOfTheApplicationIsWhatTheModelIsToldAndChoosesFrom()
    {
        var (model, calls) = Fake(MoveViolin);
        var jsi = JsiRig.Processor(model);
        await Ask(jsi, "move the violin 1 metre to the left", Context());

        var request = calls[0].Turns[^1].Content;
        Assert.Contains("violin: position = (-1.5, 2, 0); orientation = (0, 0, 180)", request);       // where it is, to work from
        Assert.Contains("drums: position = (0, -2, 0); orientation = (0, 0, 0)", request);
        Assert.Contains("Scene \"concert\" with 5 objects", request);
        Assert.Contains("Stored sounds that can be added: flute, rain, applause", request);
        Assert.Contains("Request: move the violin 1 metre to the left", request);
        Assert.Contains("left is x smaller", request);                                                  // a word of direction, in the application's terms
        var names = calls[0].Schema["anyOf"]![0]!["properties"]!["Command"]!["properties"]!["UserCommandRQ"]!["properties"]!["MovedObjects"]!["properties"]!["Objects"]!["items"]!["properties"]!["ObjectID"]!["enum"]!;
        Assert.Equal(["violin", "cello", "voice", "drums", "piano"], names.AsArray().Select(x => (string)x!).ToArray());
        // The examples are turns before the request, in the same form.
        Assert.True(calls[0].Turns.Count > 4 && calls[0].Turns[0].Role == "user" && calls[0].Turns[1].Role == "assistant");
    }

    [Fact]
    public async Task TheStateIsKeptUntilTheNextOne()
    {
        var (model, calls) = Fake(MoveViolin, MoveViolin);
        var jsi = JsiRig.Processor(model);
        await jsi.ProcessAsync(new Message { MessageId = "1", Ports = new() { [JsiRig.Response(Ports)] = Context().ToJsonString() } });      // the state alone is nothing said
        Assert.Empty(calls);
        await Ask(jsi, "move the violin 1 metre to the left");                                  // the words come later
        Assert.Contains("violin: position", calls[0].Turns[^1].Content);

        // A newer state replaces it.
        var scene = JsiRig.Scene().DeepClone();
        scene["members"]!["violin"]!["position"] = new JsonArray(7.0, 7.0, 0.0);
        await Ask(jsi, "move the violin 1 metre to the left", JsiRig.Context(scene));
        Assert.Contains("violin: position = (7, 7, 0)", calls[1].Turns[^1].Content);
    }

    // THE APPLICATION CONTEXT THAT IS NOT A LISTING IS IGNORED (the coordinator, 2026/10/26): the response to a command that listed nothing, given as the context, does not wipe the state.
    [Fact]
    public async Task AContextThatIsNotAListingIsIgnoredAndTheStateIsKept()
    {
        var (model, calls) = Fake(MoveViolin);
        var jsi = JsiRig.Processor(model);
        await jsi.ProcessAsync(new Message { MessageId = "1", Ports = new() { [JsiRig.Response(Ports)] = Context().ToJsonString() } });
        var done = JsiRig.ResponseTo("earlier", "Done", ("MovedObjects", "violin", "Done", null));
        await jsi.ProcessAsync(new Message { MessageId = "2", Ports = new() { [JsiRig.Response(Ports)] = done.ToJsonString() } });
        await Ask(jsi, "move the violin 1 metre to the left");
        Assert.Contains("violin: position", calls[0].Turns[^1].Content);
        Assert.DoesNotContain("The scene is not known", calls[0].Turns[^1].Content);
    }

    private const string AddFlute =
        "{\"Working\":\"AddedObjects flute at 1, 1, 0.\",\"Command\":{\"UserCommandRQ\":{\"AddedObjects\":{\"Objects\":[{\"ObjectID\":\"flute\",\"SpatialAttitude\":{\"Position\":{\"CartPosition\":[1,1,0]},\"Orientation\":{\"Orientation\":[0,0,180]}}}]}}}}";

    // WHAT THE APPLICATION GIVES AT A PLACE (the guide's Fill, by path): the name of the scene a command adds to, whatever the model says there.
    [Fact]
    public async Task TheGuideFillsWhatTheApplicationGivesAtAPath()
    {
        var (model, _) = Fake(AddFlute);
        var answer = await Ask(JsiRig.Processor(model), "add the flute at 1, 1, 0", Context());
        var command = JsiRig.CommandOf(answer, Ports);
        Assert.NotNull(command);
        Assert.Equal("stage", (string?)command!["UserCommandRQ"]!["AddedObjects"]!["ObjectID"]);
    }

    // A LISTING THAT ANSWERS A COMMAND THIS INTERFACE DID NOT SEND is the application's user agent asking: it is the state, and is not told.
    [Fact]
    public async Task AListingThatAnswersACommandNotSentHereIsTheStateAndIsNotTold()
    {
        var (model, calls) = Fake(MoveViolin);
        var jsi = JsiRig.Processor(model);
        var listing = (JsonObject)Context();
        var answer = await jsi.ProcessAsync(new Message { MessageId = "r", Ports = new() { [JsiRig.Response(Ports)] = listing.ToJsonString() } });
        Assert.Null(JsiRig.FactsOf(answer, Ports));
        await Ask(jsi, "move the violin 1 metre to the left");
        Assert.Contains("violin: position", calls[0].Turns[^1].Content);
    }

    // A REQUEST THAT IS ALWAYS THE SAME COMMAND is that command, sent without the language model: "what is in the scene" asks the application for a listing.
    [Theory]
    [InlineData("What is in the scene?")]
    [InlineData("what's in the scene right now")]
    public async Task AShortcutIsACommandWithoutTheModel(string words)
    {
        var (model, calls) = Fake(MoveViolin);
        var answer = await Ask(JsiRig.Processor(model), words, Context());
        Assert.Empty(calls);
        var command = JsiRig.CommandOf(answer, Ports);
        Assert.NotNull(command);
        Assert.Equal("Audio Scene", (string?)command!["UserCommandRQ"]!["ListedObjects"]!["Kind"]);
        Assert.Equal("stage", (string?)command["UserCommandRQ"]!["ListedObjects"]!["ObjectID"]);
        Assert.Empty(new SchemaCheck(JsiRig.UserCommandUri, JsiRig.UserCommandFile, Repository.Schemas).Violations(command.ToJsonString())!);
    }

    [Fact]
    public async Task AShortcutForTheSoundsListsEverything()
    {
        var (model, calls) = Fake(MoveViolin);
        var command = JsiRig.CommandOf(await Ask(JsiRig.Processor(model), "which sounds do I have", Context()), Ports);
        Assert.Empty(calls);
        Assert.Equal("All", (string?)command!["UserCommandRQ"]!["ListedObjects"]!["Kind"]);
    }

    // LISTINGS THAT COME ONE AFTER ANOTHER ADD UP: the sounds kept from one, the scene with where its members are from another; a listing that only names the members of the scene does
    // not take their places away.
    [Fact]
    public async Task ListingsThatComeOneAfterAnotherAddUpToTheState()
    {
        var (model, calls) = Fake(MoveViolin);
        var jsi = JsiRig.Processor(model);
        JsonObject Listing(string id, JsonArray listed)
        {
            var aji = JsiRig.ResponseTo(id, "Done");
            aji["UserCommandRS"]!.AsObject().Remove("Actions");
            aji["UserCommandRS"]!["ListedObjects"] = listed;
            return aji;
        }
        async Task Give(JsonObject aji) => await jsi.ProcessAsync(new Message { MessageId = Guid.NewGuid().ToString(), Ports = new() { [JsiRig.Response(Ports)] = aji.ToJsonString() } });

        // The scene, with where its members are.
        var detailed = (JsonObject)((JsonArray)((JsonObject)JsiRig.Context(JsiRig.Scene())["UserCommandRS"]!)["ListedObjects"]!)[0]!;
        await Give(Listing("a", new JsonArray(detailed.DeepClone())));
        // Everything: the sounds kept, and the scene only by the names of its members.
        await Give(Listing("b", new JsonArray(
            new JsonObject { ["ObjectID"] = "flute", ["Kind"] = "Basic Audio Object" },
            new JsonObject { ["ObjectID"] = "concert", ["Kind"] = "Audio Scene", ["MemberObjectIDs"] = new JsonArray("violin", "cello", "voice", "drums", "piano") },
            new JsonObject { ["ObjectID"] = "violin", ["Kind"] = "Basic Audio Object" })));
        await Ask(jsi, "move the violin 1 metre to the left");
        var told = calls[0].Turns[^1].Content;
        Assert.Contains("violin: position = (-1.5, 2, 0)", told);                         // where it is, from the first
        Assert.Contains("Stored sounds that can be added: flute", told);                    // what is kept, from the second (the violin is in the scene)
        Assert.DoesNotContain("Stored sounds that can be added: flute, violin", told);
    }

    // WITH AN APPLICATION CONNECTED (RefreshState) the interface asks it what it holds, one question after another, before it works out a request, and again after every response it
    // tells: the request waits for the answers, and is then worked out from the scene as it is.
    [Fact]
    public async Task AnApplicationThatIsConnectedIsAskedForTheStateBeforeARequestIsWorkedOut()
    {
        var (model, calls) = Fake(MoveViolin);
        var jsi = JsiRig.Processor(model, JsiRig.Jsi, refreshesState: true);
        JsonObject Answer(JsonObject command, JsonArray listed)
        {
            var aji = JsiRig.ResponseTo((string)command["UserCommandID"]!, "Done");
            aji["UserCommandRS"]!.AsObject().Remove("Actions");
            aji["UserCommandRS"]!["ListedObjects"] = listed;
            return aji;
        }
        Task<Message> Give(JsonObject aji) => jsi.ProcessAsync(new Message { MessageId = Guid.NewGuid().ToString(), Ports = new() { [JsiRig.Response(Ports)] = aji.ToJsonString() } });

        // The request waits; the first question goes: everything.
        var first = await Ask(jsi, "move the violin 1 metre to the left");
        Assert.Empty(calls);
        Assert.Null(JsiRig.FactsOf(first, Ports));
        var all = JsiRig.CommandOf(first, Ports)!;
        Assert.Equal("All", (string?)all["UserCommandRQ"]!["ListedObjects"]!["Kind"]);

        // Its answer: the second question goes, the scene.
        var second = await Give(Answer(all, new JsonArray(
            new JsonObject { ["ObjectID"] = "flute", ["Kind"] = "Basic Audio Object" },
            new JsonObject { ["ObjectID"] = "stage", ["Kind"] = "Audio Scene", ["MemberObjectIDs"] = new JsonArray("violin") },
            new JsonObject { ["ObjectID"] = "violin", ["Kind"] = "Basic Audio Object" })));
        Assert.Empty(calls);
        Assert.Null(JsiRig.FactsOf(second, Ports));
        var scene = JsiRig.CommandOf(second, Ports)!;
        Assert.Equal("Audio Scene", (string?)scene["UserCommandRQ"]!["ListedObjects"]!["Kind"]);
        Assert.Equal("stage", (string?)scene["UserCommandRQ"]!["ListedObjects"]!["ObjectID"]);

        // Its answer: the state is fresh, and the request that waited is worked out from it.
        var members = new JsonArray(new JsonObject
        {
            ["ObjectID"] = "violin",
            ["PoV"] = JsiRig.Attitude([4, 5, 0], [0, 0, 180])
        });
        var third = await Give(Answer(scene, new JsonArray(new JsonObject { ["ObjectID"] = "stage", ["Kind"] = "Audio Scene", ["ObjectCount"] = 1, ["Members"] = members })));
        Assert.Single(calls);
        Assert.Contains("violin: position = (4, 5, 0)", calls[0].Turns[^1].Content);
        Assert.Contains("Stored sounds that can be added: flute", calls[0].Turns[^1].Content);
        var move = JsiRig.CommandOf(third, Ports)!;
        Assert.NotNull(move["UserCommandRQ"]!["MovedObjects"]);

        // The application answers the command: the person is told, and the state is asked for again.
        var told = await Give(JsiRig.ResponseTo((string)move["UserCommandID"]!, "Done", ("MovedObjects", "violin", "Done", null)) is { } r ? WithReplyTo(r, move) : null!);
        Assert.Contains("violin", JsiRig.FactsOf(told, Ports));
        Assert.Equal("All", (string?)JsiRig.CommandOf(told, Ports)!["UserCommandRQ"]!["ListedObjects"]!["Kind"]);
    }

    private static JsonObject WithReplyTo(JsonObject response, JsonObject command) => response;

    [Fact]
    public async Task WithoutAnApplicationConnectedARequestIsWorkedOutAtOnce()
    {
        var (model, calls) = Fake(MoveViolin);
        var answer = await Ask(JsiRig.Processor(model), "move the violin 1 metre to the left", Context());
        Assert.Single(calls);
        Assert.NotNull(JsiRig.CommandOf(answer, Ports));
    }

    [Fact]
    public async Task WithoutAStateNoObjectCanBeNamedAndTheModelIsToldSo()
    {
        var (model, calls) = Fake(MoveViolin);
        var answer = await Ask(JsiRig.Processor(model), "play the scene");
        Assert.Contains("The scene is not known", calls[0].Turns[^1].Content);
        var properties = calls[0].Schema["anyOf"]![0]!["properties"]!["Command"]!["properties"]!["UserCommandRQ"]!["properties"]!;
        Assert.Null(properties["DeliveredObject"]!["enum"]);                                       // nothing to limit the name to
    }

    // ---- a response becomes facts ---------------------------------------------------------------------------------------------------------------------

    // The UserCommandID of a command this interface sends for a request: the response that carries it is the application's answer to the person.
    private static async Task<string> Sent(JsiAimProcessor jsi) =>
        (string)JsiRig.CommandOf(await Ask(jsi, "move the violin 1 metre to the left", Context()), Ports)!["UserCommandID"]!;

    [Fact]
    public async Task AResponseBecomesFactsWithTheRequestItAnswers()
    {
        var (model, _) = Fake(MoveViolin);
        var jsi = JsiRig.Processor(model);
        var sent = JsiRig.CommandOf(await Ask(jsi, "move the violin 1 metre to the left", Context()), Ports)!;
        var id = (string)sent["UserCommandID"]!;

        var response = JsiRig.ResponseTo(id, "Done", ("MovedObjects", "violin", "Done", null));
        var answer = await jsi.ProcessAsync(new Message { MessageId = "r", Ports = new() { [JsiRig.Response(Ports)] = response.ToJsonString() } });

        Assert.Null(JsiRig.CommandOf(answer, Ports));
        var facts = JsiRig.FactsOf(answer, Ports)!;
        Assert.Contains("The person had asked: \"move the violin 1 metre to the left\"", facts);
        Assert.Contains("result done", facts);
        Assert.Contains("did moved, on violin", facts);
        Assert.DoesNotContain(id, facts);                                                           // no identifier in what is said
        Assert.DoesNotContain("Header", facts);
    }

    [Fact]
    public async Task AResponseThatFailedSaysWhy()
    {
        var jsi = JsiRig.Processor(Fake(MoveViolin).Model);
        var response = JsiRig.ResponseTo(await Sent(jsi), "Partly", ("MovedObjects", "violin", "Done", null), ("RemovedObjects", "harp", "Failed", "there is no harp in the scene"));
        var facts = JsiRig.FactsOf(await jsi.ProcessAsync(new Message { MessageId = "r", Ports = new() { [JsiRig.Response(Ports)] = response.ToJsonString() } }), Ports)!;
        Assert.Contains("result partly done", facts);
        Assert.Contains("did removed, on harp", facts);
        Assert.Contains("failed", facts);
        Assert.Contains("because there is no harp in the scene", facts);
    }

    [Fact]
    public async Task AListingBecomesTheNamesAndTheCounts()
    {
        var jsi = JsiRig.Processor(Fake(MoveViolin).Model);
        var listing = (JsonObject)Context();
        listing["UserCommandID"] = await Sent(jsi);                                                  // the response to a command this interface sent
        var facts = JsiRig.FactsOf(await jsi.ProcessAsync(new Message { MessageId = "r", Ports = new() { [JsiRig.Response(Ports)] = listing.ToJsonString() } }), Ports)!;
        Assert.Contains("concert", facts);
        Assert.Contains("objects 5", facts);
        Assert.Contains("on violin, at position -1.5, 2, 0, orientation 0, 0, 180", facts);
        Assert.Contains("violin", facts);
    }

    [Fact]
    public async Task AResponseThatDoesNotConformToTheSchemaIsNotTold()
    {
        var jsi = JsiRig.Processor(Fake(MoveViolin).Model);
        var id = await Sent(jsi);
        var broken = JsiRig.ResponseTo(id, "Done");
        broken["UserCommandRS"]!["Outcome"] = "Brilliant";
        var facts = JsiRig.FactsOf(await jsi.ProcessAsync(new Message { MessageId = "r", Ports = new() { [JsiRig.Response(Ports)] = broken.ToJsonString() } }), Ports)!;
        Assert.Contains("does not conform", facts);
    }

    // ---- an earlier command ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnEarlierCommandCanBeNamedToUndoIt()
    {
        var turnsSeen = new List<List<(string Role, string Content)>>();
        StructuredModel model = (system, turns, schema) =>
        {
            var list = turns.ToList();
            turnsSeen.Add(list);
            if (turnsSeen.Count == 1) return Task.FromResult(MoveViolin);
            // The model reads the identifier of the last command from the list it is given.
            var id = System.Text.RegularExpressions.Regex.Matches(list[^1].Content, "- ([0-9a-f-]{36}): ").Last().Groups[1].Value;
            return Task.FromResult("{\"Working\":\"RevokedUserCommandID the last.\",\"Command\":{\"UserCommandRQ\":{\"RevokedUserCommandID\":\"" + id + "\"}}}");
        };
        var jsi = JsiRig.Processor(model);
        var first = JsiRig.CommandOf(await Ask(jsi, "move the violin 1 metre to the left", Context()), Ports)!;
        var id = (string)first["UserCommandID"]!;

        var undone = JsiRig.CommandOf(await Ask(jsi, "undo that"), Ports)!;
        Assert.Contains("Commands sent before", turnsSeen[1][^1].Content);
        Assert.Contains(id + ": \"move the violin 1 metre to the left\"", turnsSeen[1][^1].Content);       // told which commands were sent, by their identifier
        Assert.Equal(id, (string?)undone["UserCommandRQ"]!["RevokedUserCommandID"]);
        var check = new SchemaCheck(JsiRig.UserCommandUri, JsiRig.UserCommandFile, Repository.Schemas);
        Assert.Empty(check.Violations(undone.ToJsonString())!);
    }

    // ---- no application, no language model ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithoutTheEntriesOfItsAccessItServesNoApplication()
    {
        var (model, calls) = Fake(MoveViolin);
        var jsi = JsiRig.Processor(model, JsiRig.NeutralJsi);
        Assert.False(jsi.HasApplication);
        var neutral = JsiRig.Ports(JsiRig.NeutralJsi);
        var answer = await jsi.ProcessAsync(JsiRig.Say(neutral, "move the violin 1 metre to the left"));
        Assert.Empty(calls);
        Assert.Null(JsiRig.CommandOf(answer, neutral));
        Assert.Contains("no application to serve", JsiRig.FactsOf(answer, neutral));
    }

    [Fact]
    public async Task ALanguageModelThatCannotBeReachedIsAnError()
    {
        StructuredModel down = (_, _, _) => throw new HttpRequestException("connection refused");
        var answer = await Ask(JsiRig.Processor(down), "move the violin 1 metre to the left", Context());
        Assert.Equal("Error", answer.MessageType);
        Assert.Contains("is Ollama running", answer.Payload);
    }

    [Fact]
    public async Task NothingSaidIsNothing()
    {
        var (model, calls) = Fake(MoveViolin);
        var answer = await Ask(JsiRig.Processor(model), "   ", Context());
        Assert.Empty(calls);
        Assert.Empty(answer.Ports);
    }

    // ---- the Ports -----------------------------------------------------------------------------------------------------------------------------------------------

    // By Data Type and Port Number, as its L2 has them: the words (OSD-BTO), the response (CAE-UCM 1: it carries the state too, when it lists); the command (CAE-UCM 1) and the Response Hint (OSD-BTO).
    [Fact]
    public void ItsPortsAreThoseOfItsL2()
    {
        Assert.Equal("OSD-BTO-V1.5#1", JsiRig.Request(Ports));
        Assert.Equal("CAE-UCM-V1.0#1", JsiRig.Response(Ports));
        Assert.Equal("CAE-UCM-V1.0#1", JsiRig.Command(Ports));
        Assert.Equal("OSD-BTO-V1.5#1", JsiRig.Facts(Ports));
    }

    private static bool Valid(string schema, string json)
    {
        var s = AIF.Metadata.PublishedSchemas.At(Repository.Schemas)[Path.GetFullPath(Path.Combine(Repository.Schemas, schema))];
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        lock (AIF.Metadata.PublishedSchemas.Lock) return s.Evaluate(doc.RootElement).IsValid;
    }
}
