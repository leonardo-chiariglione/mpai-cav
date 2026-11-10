using Mpai.Mmc.Jsi;

namespace Mpai.Aif.Tests;

// THE WELCOME OF THE ASM WINDOW. The workflow's first request is the proof that the Module is up, and the Module answers it: it says hello to a hello. The request must not be a sentence the grammar
// reads as something else: the words "press Load a sound" in it made the Module answer with the refusal of loading a sound by voice, the first thing a person saw.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class AsmWelcomeTests
{
    private static string Orch => File.ReadAllText(Path.Combine(Repository.Root, "Apps", "CAE", "ASM", "ASM-VCI.orch"));

    private static ApplicationContext Guide => ApplicationContext.Parse(File.ReadAllText(Path.Combine(Repository.Root, "Apps", "CAE", "ASM", "Access", "AsmApplicationContext.json")));

    [Fact]
    public void TheFirstRequestIsAHelloAndTheModelSaysHelloToIt()
    {
        var offered = System.Text.RegularExpressions.Regex.Match(Orch, "offer\\s+Welcome\\s+\\(OSD-BTO-V1\\.5:1\\)\\s*=\\s*\"([^\"]*)\"").Groups[1].Value;
        Assert.Equal("Hello.", offered);
        var said = Guide.Asks.First(a => a.Words.Any(w => offered.ToLowerInvariant().Contains(w.ToLowerInvariant()))).Say;
        Assert.StartsWith("Hello.", said);
        Assert.Contains("press Load a sound", said);        // what the welcome tells is said, not asked: the words of an answer are not read as a request
    }
    [Theory]
    [InlineData("which materials are available?")]
    [InlineData("What materials do you know")]
    [InlineData("list the materials")]
    [InlineData("show materials")]
    public void AskedWhichMaterialsAreAvailableTheModelNamesThemAll(string question)
    {
        var said = Guide.Asks.First(a => a.Words.Any(w => question.ToLowerInvariant().Contains(w.ToLowerInvariant()))).Say;
        var grammar = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Root, "Apps", "CAE", "ASM", "Grammar", "asm-grammar.json")))!;
        var names = grammar["Materials"]!.AsObject().Where(m => m.Key is not ("_" or "Bands")).Select(m => m.Key).ToList();
        Assert.NotEmpty(names);
        foreach (var name in names) Assert.Contains(name, said);           // the answer is the list the rules are made from
    }
}
