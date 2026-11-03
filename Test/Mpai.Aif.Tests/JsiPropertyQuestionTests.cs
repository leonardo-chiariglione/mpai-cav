using System.Text.Json.Nodes;

using Mpai.Mmc.Jsi;

namespace Mpai.Aif.Tests;

// A QUESTION ABOUT A PROPERTY OF A SOUND IN THE SCENE ("what is the duration of clip777?"), answered from the listing of the scene with the words the Application Context of ASM gives to each fact.
public sealed class JsiPropertyQuestionTests
{
    private static (ApplicationContext Guide, ApplicationState State) Asm()
    {
        var guide = ApplicationContext.Parse(File.ReadAllText(Path.Combine(Repository.Root, "Apps", "CAE", "ASM", "Access", "AsmApplicationContext.json")));
        var listing = JsonNode.Parse("""
        { "UserCommandRS": { "ListedObjects": [ { "Kind": "Audio Scene", "ObjectID": "Two Objects", "ObjectCount": 2, "Members": [
            { "ObjectID": "clip777", "PoV": { "Position": { "CartPosition": [2.8, 1.8, 0] }, "Orientation": { "Orientation": [90, 0, 0] } },
              "Time": { "SimpleTimeData": [ { "StartTime": 10, "EndTime": 20 } ] }, "Level": -14 },
            { "ObjectID": "piano", "PoV": { "Position": { "CartPosition": [-1, 2, 0] } } } ] } ] } }
        """)!;
        return (guide, ApplicationState.Read(listing, guide));
    }

    [Theory]
    [InlineData("what is the duration of clip777?", "clip777: starts at 10 s; ends at 20 s.")]
    [InlineData("How long is clip777", "clip777: starts at 10 s; ends at 20 s.")]
    [InlineData("when does clip777 start?", "clip777: starts at 10 s; ends at 20 s.")]
    [InlineData("what is the level of clip777?", "clip777: level -14 LUFS.")]
    [InlineData("where is clip777?", "clip777: position (2.8, 1.8, 0) m.")]
    [InlineData("what is the position of the piano?", "piano: position (-1, 2, 0) m.")]
    public void AQuestionAboutAPropertyIsAnsweredWithThatProperty(string request, string said)
    {
        var (guide, state) = Asm();
        var asked = PropertyQuestions.Match(request, guide, state);
        Assert.NotNull(asked);
        Assert.Equal(said, PropertyQuestions.Answer(asked!, state, guide));
    }

    [Theory]
    [InlineData("put clip777 at x 2, y 1")]                      // not a question
    [InlineData("what is in the scene")]                         // names no sound
    [InlineData("what is the duration of clip999?")]              // a sound that is not in the scene
    [InlineData("what is clip777?")]                             // names no property
    [InlineData("move clip777 one metre to the right")]
    public void ARequestThatIsNotAQuestionAboutAPropertyIsLeftToTheRest(string request)
    {
        var (guide, state) = Asm();
        Assert.Null(PropertyQuestions.Match(request, guide, state));
    }

    [Theory]
    [InlineData("display the acoustic provile", "display the acoustic profile")]
    [InlineData("show the acustic profile", "show the acoustic profile")]
    [InlineData("what is in the sceen", "what is in the scene")]
    [InlineData("frobnicate the thing", null)]
    [InlineData("hello", null)]
    public void ARequestNotUnderstoodIsToldBackWithTheSentenceItIsNearTo(string request, string? near)
    {
        var (guide, _) = Asm();
        Assert.Equal(near, JsiAimProcessor.NearestSentence(request, guide));
    }
}
