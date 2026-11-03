using System.Text.Json;
using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.Metadata;
using Json.Schema;
using Mpai.Cae.Usc;
using Mpai.Cae.Usc.Ees;

namespace Mpai.Aif.Tests;

// CAE-USC V2.5, Emotion Enhanced Speech SubAIM by SubAIM (the AIMs alone): two pathways add emotion to emotionless speech. Pathway 1: Speech Feature Analysis 1 measures a model
// utterance and Prosodic Emotion Insertion moves the speech to its pitch, level and rate. Pathway 2: Speech Feature Analysis 2 measures the speech, Emotion Feature Production
// says where speech with the emotion lies (a rule: the specification has a neural network) and Neural Emotion Insertion moves the speech there.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class CaeUscV25EesTests
{
    private static Audio Syllables(double hertz = 200, double perSecond = 4, double level = 8000, double seconds = 3)
    {
        const int rate = 16000;
        var s = new short[(int)(seconds * rate)];
        for (var i = 0; i < s.Length; i++) s[i] = (short)(level * Math.Pow(Math.Sin(Math.PI * perSecond * i / rate), 2) * Math.Sin(2 * Math.PI * hertz * i / rate));
        return new Audio(s, rate);
    }

    private static bool Valid(string[] schema, string json)
    {
        var root = Path.GetFullPath(Repository.Schemas);
        var compiled = PublishedSchemas.At(root)[Path.Combine([root, .. schema])];
        using var document = JsonDocument.Parse(json);
        lock (PublishedSchemas.Lock) return compiled.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;
    }

    private static string Selector(string id) => new JsonObject { ["Header"] = "OSD-SEL-V1.5", ["SelectorID"] = id }.ToJsonString();
    private static string Emotion(string category, double? degree = null) =>
        new JsonObject { ["Header"] = "MMC-EEM-V2.5", ["EmotionCategory"] = new JsonObject { [category] = new JsonObject() }, ["Degree"] = degree }.ToJsonString();
    private static Message Of(params (string Key, string Json)[] ports) => new() { MessageId = "m", Ports = ports.ToDictionary(p => p.Key, p => p.Json) };

    private static async Task<Message> Ok(Task<Message> task) { var m = await task; Assert.False(m.IsError, m.Payload); return m; }

    // ---- pathway 1 ----------------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheProsodicPathwayMovesTheSpeechToTheModelUtterance()
    {
        var model = Syllables(300, 5, 16000); var emotionless = Syllables(200, 3, 8000);
        var sfa = await Ok(new SfaAimProcessor("sfa", "sel", "utt", "out").ProcessAsync(Of(("sel", Selector("s1")), ("utt", SpeechObject.Make("model", model)))));
        Assert.True(Valid(["MMC", "V2.5", "data", "SpeechDescriptorsObject.json"], sfa.Ports["out"]));
        Assert.Equal("Prosodic", SpeechDescriptors.Read(sfa.Ports["out"]).Kind);

        var pei = await Ok(new PeiAimProcessor("pei", "speech", "features", "emotion", "out").ProcessAsync(
            Of(("speech", SpeechObject.Make("neutral", emotionless)), ("features", sfa.Ports["out"]))));
        Assert.True(Valid(["OSD", "V1.5", "data", "BasicSpeechObject.json"], pei.Ports["out"]));
        var f = Prosody.Measure(SpeechObject.Read(pei.Ports["out"]).Single());
        Assert.InRange(f.MeanF0Hz, 285, 315);                                                // the pitch of the model utterance
        Assert.InRange(f.SpeakingRate, 4.0, 6.0);                                            // its rate
        Assert.InRange(f.RmsDb - Prosody.Measure(emotionless).RmsDb, 5, 7);                  // twice the level
    }

    [Fact]
    public async Task TheEmotionListSaysHowFarTheProsodicPathwayGoes()
    {
        var model = Syllables(300, 3, 8000); var emotionless = Syllables(200, 3, 8000);
        var sfa = await Ok(new SfaAimProcessor("sfa", "sel", "utt", "out").ProcessAsync(Of(("sel", Selector("s1")), ("utt", SpeechObject.Make("model", model)))));
        async Task<double> Pitch(double degree)
        {
            var pei = await Ok(new PeiAimProcessor("pei", "speech", "features", "emotion", "out").ProcessAsync(
                Of(("speech", SpeechObject.Make("neutral", emotionless)), ("features", sfa.Ports["out"]), ("emotion", Emotion("HAPPINESS", degree)))));
            return Prosody.Measure(SpeechObject.Read(pei.Ports["out"]).Single()).MeanF0Hz;
        }
        var low = await Pitch(0.25); var medium = await Pitch(0.5);
        Assert.InRange(medium, 285, 315);                                                    // Medium: all the way
        Assert.InRange(low, 235, 265);                                                       // half of the way (in ratio) at 0.25
    }

    // ---- pathway 2 ----------------------------------------------------------------------------------------------------------------------------------------------------------

    private static async Task<Audio> Neural(Audio emotionless, string category, double? degree, string? language = null)
    {
        var sfb = await Ok(new SfbAimProcessor("sfb", "sel", "speech", "out").ProcessAsync(Of(("sel", Selector("s2")), ("speech", SpeechObject.Make("neutral", emotionless)))));
        Assert.True(Valid(["MMC", "V2.5", "data", "SpeechDescriptorsObject.json"], sfb.Ports["out"]));
        var efpInputs = new List<(string, string)> { ("features", sfb.Ports["out"]), ("emotion", Emotion(category, degree)) };
        if (language is not null) efpInputs.Add(("lang", new JsonObject { ["Header"] = "OSD-SEL-V1.5", ["Language"] = new JsonObject { ["InputLanguage"] = language, ["OutputLanguage"] = language } }.ToJsonString()));
        var efp = await Ok(new EfpAimProcessor("efp", "features", "lang", "emotion", "out").ProcessAsync(Of(efpInputs.ToArray())));
        Assert.Equal("Neural", SpeechDescriptors.Read(efp.Ports["out"]).Kind);
        Assert.Equal(language, SpeechDescriptors.Read(efp.Ports["out"]).Language);           // the language is carried
        var nei = await Ok(new NeiAimProcessor("nei", "speech", "features", "out").ProcessAsync(Of(("speech", SpeechObject.Make("neutral", emotionless)), ("features", efp.Ports["out"]))));
        Assert.True(Valid(["OSD", "V1.5", "data", "BasicSpeechObject.json"], nei.Ports["out"]));
        return SpeechObject.Read(nei.Ports["out"]).Single();
    }

    [Fact]
    public async Task TheNeuralPathwayRaisesThePitchLevelAndRateForHappinessAndLowersThemForSadness()
    {
        var emotionless = Syllables(200, 3, 8000); var own = Prosody.Measure(emotionless);
        var happy = Prosody.Measure(await Neural(emotionless, "HAPPINESS", 0.5, "it"));
        Assert.InRange(happy.MeanF0Hz, 228, 252);                                            // 200 Hz x 1.20
        Assert.InRange(happy.RmsDb - own.RmsDb, 2, 4);                                       // +3 dB
        Assert.True(happy.SpeakingRate > own.SpeakingRate);
        var sad = Prosody.Measure(await Neural(emotionless, "SADNESS", 0.5));
        Assert.InRange(sad.MeanF0Hz, 170, 190);                                              // 200 Hz x 0.90
        Assert.InRange(sad.RmsDb - own.RmsDb, -5, -3);                                       // -4 dB
        Assert.True(sad.SpeakingRate < own.SpeakingRate);
    }

    [Fact]
    public async Task ADegreeStrengthensOrWeakensTheEmotionAndNoDegreeIsMedium()
    {
        var emotionless = Syllables(200, 3, 8000);
        var weak = Prosody.Measure(await Neural(emotionless, "HAPPINESS", 0.25)).MeanF0Hz;
        var medium = Prosody.Measure(await Neural(emotionless, "HAPPINESS", null)).MeanF0Hz;      // no Degree: Medium (0.5)
        var strong = Prosody.Measure(await Neural(emotionless, "HAPPINESS", 1.0)).MeanF0Hz;
        Assert.True(weak < medium && medium < strong);
        Assert.InRange(medium, 228, 252);
    }

    // ---- what the SubAIMs refuse -----------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASubAimThatLacksAnInputOrIsGivenWhatItDoesNotKnowSaysSo()
    {
        Assert.True((await new SfaAimProcessor("sfa", "sel", "utt", "out").ProcessAsync(Of(("utt", SpeechObject.Make("m", Syllables()))))).IsError);          // no Mode Selector
        Assert.True((await new NeiAimProcessor("nei", "speech", "features", "out").ProcessAsync(Of(("speech", SpeechObject.Make("m", Syllables()))))).IsError);   // no features
        var notFeatures = await new NeiAimProcessor("nei", "speech", "features", "out").ProcessAsync(Of(("speech", SpeechObject.Make("m", Syllables())), ("features", "{\"Header\":\"X\"}")));
        Assert.True(notFeatures.IsError); Assert.Contains("Not Speech Descriptors", notFeatures.Payload);
        var notEmotion = await new EfpAimProcessor("efp", "f", "l", "e", "out").ProcessAsync(Of(("f", SpeechDescriptors.Make("EmotionlessSpeech", new SpeechFeatures(200, -20, 3))), ("e", "{\"Header\":\"X\"}")));
        Assert.True(notEmotion.IsError); Assert.Contains("Not an Emotion", notEmotion.Payload);
    }

    [Fact]
    public async Task AnEmotionOfNoCategoryLeavesTheFeaturesWhereTheyAre()
    {
        var emotionless = Syllables(200, 3, 8000);
        var f = Prosody.Measure(await Neural(emotionless, "CALMNESS", 0.0));                 // Degree 0: no emotion
        Assert.InRange(f.MeanF0Hz, 195, 205);
    }
}
