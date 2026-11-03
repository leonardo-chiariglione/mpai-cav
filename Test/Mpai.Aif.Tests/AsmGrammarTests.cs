using System.Text.Json.Nodes;

using Mpai.Mmc.Jsi;
using Xunit;

namespace Mpai.Aif.Tests;

// THE GRAMMAR OF ASM, as the Application Context it is compiled to (Apps/CAE/ASM/Grammar: asm-grammar.json -> compile.py -> Access/AsmApplicationContext.json): what a person says becomes the command the
// recorded file says, and every command a sentence comes to is a User Command that validates against CAE-UCM once the JSON Interface has completed it (the Headers, the identifiers). No language model,
// no audio, a few hundred milliseconds.
public class AsmGrammarTests
{
    private static string ContextFile => Path.Combine(Repository.Root, "Apps", "CAE", "ASM", "Access", "AsmApplicationContext.json");

    private static ApplicationState.Member M(string name, string position, string orientation) => new(name, [("position", position), ("orientation", orientation)]);

    // The scene "stage" holds the violin, the cello and the drums; pluto, pippo and the flute are kept and wait to be put in (the flute was loaded last).
    private static ApplicationState Waiting() => new()
    {
        Scenes = [new ApplicationState.Scene("stage", [M("violin", "(1, 2, 0)", "(0, 0, 180)"), M("cello", "(-2, 3, 0)", "(0, 0, 90)"), M("drums", "(3, 1, 1)", "(0, 0, 180)")])],
        Stored = ["pluto", "pippo", "flute"], ListsStored = true, Seen = ["pluto", "pippo", "flute"], Saved = ["stage"]
    };

    // The same, with nothing waiting: the sound last spoken of is the violin.
    private static ApplicationState Placed() => new()
    {
        Scenes = Waiting().Scenes, Stored = [], ListsStored = true, Seen = [], Saved = ["stage"]
    };

    private static readonly string[] Sentences =
    [
        // where: by a direction and an amount, a step, the listener's side, the centre, coordinates
        "move the violin 2 metres to the right", "move the cello to the left by 1.5 metres", "push the drums forward 3", "bring the violin back half a metre", "shift the cello up 1", "slide the drums down 2 metres",
        "move the violin to the right", "put the cello behind", "move the sound on the left to the front by 2 metres", "move the object on the left 1 metre to the right",
        "put pluto 2 metres to the left", "place pippo to the right by 3 metres", "add the flute 1 metre ahead", "bring in the flute 2 metres to the right", "put pluto on my left", "place pippo behind me", "put the flute in front of me",
        "move the violin behind me", "move the cello on my right", "move the drums in front of me",
        "put it 2 metres to the right", "move it 2 metres to the right", "two metres to the right", "it 1 to the left", "put it on my left", "move it behind me", "bring it closer by 1", "push it farther 2 metres", "raise it by 1", "lower it 2 metres",
        "move the violin closer by 1 metre", "bring the cello farther 2", "raise the drums by half a metre", "lower the violin 1 metre", "lift the cello 2", "drop the drums by 1",
        "put pluto at the centre", "move the violin here", "place the cello in the middle", "add the drums at the origin", "put it at the centre", "here",
        "move the violin to x 1, y 2, z 0", "put the cello at x = 2, y = 1", "place the drums at 3, 1, 1", "move the violin to 2 1", "set pluto at x -1, y 3, z 0", "put pippo at x 1, y 2", "put it to 1 2 3", "at 2, 1", "1 2 3",
        // which way
        "turn the violin to 90 degrees", "rotate the cello by 45 degrees", "make the drums face me", "have the violin look at me", "turn it to minus 90 degrees", "make it face me",
        // how loud, take out, delete
        "set the cello to minus 18 lufs", "make the violin -12 db", "set the level of the drums to -20 lufs", "remove the violin", "take out the cello from the scene", "get rid of the drums", "delete pluto", "erase the flute from the library",
        // when
        "set violin's duration 0s to 5s", "set the duration of the cello from 1 to 3", "the drums start at 2 and end at 5", "set the start of the violin to 2 seconds and the end to 5", "set the end time of the cello to 6 seconds 2", "cello from 0 to 10 seconds",
        "make the drums play between 2 and 8", "set the timing of the violin 1.5 to 4",
        // the listener
        "move me to x 1, y 2", "put the listener at x 1 y 2 facing 90", "move me to the centre", "let me listen from the centre", "turn me by 30 degrees", "face 180", "rotate myself to 90 degrees",
        // the room
        "place a room 6 by 4 with a ceiling of 3", "make a square room 5 by 5", "put a room 6, 4, 3 m in the centre", "place a room in the center. size: 6m, 4m, 3m", "6m by 4m by 3m", "make the ceiling 3 metres", "the ceiling height is 3.5 metres",
        "create a room 5 by 4 at the centre of the scene", "make the room 7 by 5", "give me a square room 4 by 4", "make a room",
        // the acoustics: a property, a kept profile
        "set the absorption of the walls to 0.4", "absorption 0.1", "set the diffusion to 0.6", "set the reverberation time to 1.2 seconds", "rt60 0.8", "set the reflection times to 0.05 and 0.3", "reflection times early 0.01 late 0.03",
        "apply warm to the scene", "use the concert hall acoustics", "save the acoustics as warm", "delete the profile hall",
        // the full property table (M3281): set/change/modify PROPERTY of TARGET to VALUE
        "set the position of the violin to x 1, y 2, z 0", "change the cello's position to 2 3 0", "modify the orientation of the drums to 90 degrees", "set the time of the violin to 2 and 5",
        "set the volume of the cello to -20 lufs", "set the name of the drums to harp", "change the description of the violin to a warm sound", "set the device of the cello to microphone", "set the percept status of the violin to off",
        "set the point of view of the cello to x 1, y 1, z 0", "set the parent of the drums to stage", "set the frequency range of the violin from 50 to 12000 hz", "set the profile loudness of the violin to -23 lufs",
        "set my position to x 1, y 2", "change the listener's position to x 0, y 3", "set my orientation to 90 degrees", "modify the listener's heading to 180",
        "set the name of the scene to party", "change the scene's name to studio",
        "set the size of the room to 6 by 4 by 3", "change the room size to 8 by 5", "set the room height to 3.5 metres", "modify the ceiling height to 2.8",
        "set the floor to carpet", "make the walls concrete", "change the ceiling material to foam", "set the back wall to glass", "cover the left wall with curtain", "the floor is wood",
        "set the floor absorption to 0.6", "change the walls absorption to 0.3", "set the ceiling's absorption to 0.8",
        "modify the diffusion of the room to 0.7", "change the reverberation time to 1.4 seconds", "set rt30 to 0.9", "set rt20 to 0.8 seconds", "set the early decay time to 0.5",
        "set the doppler direct sound factor to 0.5", "set the doppler indirect sound to 0.7",
        // translating a speech object
        "translate the violin into french", "translate the cello to german", "say the drums in spanish", "translate it into italian", "translate portuguese the violin", "translate the violin into japanese",
        // the scenes, undoing, hearing
        "new scene called concert", "start a new scene", "open the scene stage", "load the two objects scene", "save the scene as demo", "name this scene party", "save it as mix", "undo", "undo that", "go back", "play the scene", "let me hear it", "play the violin", "playcello", "let me hear the drums", "hear the violin", "play the piccolo",
        // what no rule understands
        "banana split sunday", "hello", "make it louder", "a bit to the left", "move it a bit", "what is the meaning of life"
    ];

    private static string? Command(JsonObject? made) => made?.ToJsonString();

    [Fact]
    public void WhatIsSaidBecomesTheCommandTheRecordSaysAndEveryCommandIsOneOfTheSchema()
    {
        var guide = ApplicationContext.Parse(File.ReadAllText(ContextFile));
        var view = new ModelView(new SchemaLibrary(Repository.Schemas), guide);
        view.Build("https://schemas.mpai.community/CAE3/V1.0/data/UserCommand.json");
        var check = new SchemaCheck("https://schemas.mpai.community/CAE3/V1.0/data/UserCommand.json", Path.Combine(Repository.Schemas, "CAE3", "V1.0", "data", "UserCommand.json"), Repository.Schemas);

        var found = new Dictionary<string, string>();
        var invalid = new List<string>();
        foreach (var (label, state, newest) in new[] { ("sound waiting", Waiting(), "flute"), ("nothing waiting", Placed(), "violin") })
        {
            var sets = new Dictionary<string, IReadOnlyList<string>>(state.NameSets) { ["Newest"] = [newest] };
            foreach (var words in Sentences)
            {
                var memory = new Dictionary<string, double>();
                foreach (var (name, value) in guide.Root["Memory"] as JsonObject ?? []) memory[name] = (double)value!;           // where the listener is, the size of the room: what the application remembers from the start
                var made = RuleGrammar.Match(words, guide.Rules, sets, state, "UCM000001", memory, null, new Dictionary<string, string> { ["scene"] = "stage" }, null);
                found[$"{label}: {words}"] = made is null ? "(no command)" : Command(made)!;
                if (made is not null && check.Violations(view.Complete(made).ToJsonString()) is { Count: > 0 } violations)
                    invalid.Add($"{label}: {words}: {violations[0]}");
            }
        }
        Assert.True(invalid.Count == 0, "a command that is not a User Command of the schema:\n" + string.Join("\n", invalid));
        Expected.Match("asm-grammar.json", found);
    }

    [Fact]
    public void ARuleIsNeverWrittenByHand()
    {
        // The Rules of the Application Context are compiled from Grammar/asm-grammar.json: the file carries the header of that, and the rules carry no sentence of their own.
        var guide = ApplicationContext.Parse(File.ReadAllText(ContextFile));
        Assert.NotEmpty(guide.Rules);
        Assert.True(File.Exists(Path.Combine(Repository.Root, "Apps", "CAE", "ASM", "Grammar", "asm-grammar.json")));
        Assert.True(File.Exists(Path.Combine(Repository.Root, "Apps", "CAE", "ASM", "Grammar", "compile.py")));
    }
}
