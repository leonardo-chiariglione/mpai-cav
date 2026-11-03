using System.Text.Json;
using AIF.Metadata;
using Json.Schema;

namespace Mpai.Aif.Tests;

// THE DATA TYPES OF CAE-USC V2.5 (schemas/CAE1/V2.5/data): an instance of each is valid against its schema, and what the schema forbids is refused.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class CaeUscV25DataTypeTests
{
    private static bool Valid(string schema, string json, string family = "CAE1", string version = "V2.5", string folder = "data")
    {
        var root = Path.GetFullPath(Repository.Schemas);
        var file = Path.Combine(root, family, version, folder, schema + ".json");
        var compiled = PublishedSchemas.At(root)[file];
        using var document = JsonDocument.Parse(json);
        lock (PublishedSchemas.Lock) return compiled.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;
    }

    private const string Time = """{ "Header": "OSD-TIM-V1.5", "TimeID": "t-1", "Data": "2026-10-18T10:00:00.000Z" }""";
    private const string Uuid = "7f0e1c3a-5b2d-4f6e-9a8b-1c2d3e4f5a6b";

    [Fact]
    public void ADamagedListHasItsHeaderAndAtLeastOneDamagedSection()
    {
        Assert.True(Valid("DamagedList", $$"""{ "Header": "CAE-DLS-V2.5", "DamagedListData": [ { "DamagedSectionTime": {{Time}} } ] }"""));
        Assert.False(Valid("DamagedList", $$"""{ "Header": "CAE-DLS-V1.5", "DamagedListData": [ { "DamagedSectionTime": {{Time}} } ] }"""));    // V1.5 is not this Data Type
        Assert.False(Valid("DamagedList", """{ "Header": "CAE-DLS-V2.5" }"""));
    }

    [Fact]
    public void AnEditingListLabelsTheTimeOfARestorationAsHoursMinutesSecondsAndMilliseconds()
    {
        string List(string start) => $$"""
            { "Header": "CAE-ELS-V2.5", "OriginalSamplingFrequency": 48000, "OriginalSpeedStandard": 7.5, "OriginalEqualisationStandard": "IEC",
              "Restorations": [ { "RestorationID": "{{Uuid}}", "RestoredAudioFileURI": "https://example.org/restored.wav",
                                  "PreservationAudioFileStart": "{{start}}", "PreservationAudioFileEnd": "00:02:00.000", "AppliedSamplingFrequency": 96000, "ReadingBackwards": false } ] }
            """;
        Assert.True(Valid("EditingList", List("00:01:02.345")));
        Assert.False(Valid("EditingList", List("0:1:2")));
        Assert.False(Valid("EditingList", List("00:61:02.345")));
    }

    [Fact]
    public void AnIrregularityFileNeedsItsHeaderAndEachIrregularityItsSource()
    {
        string File(string header, string source) => $$"""
            { {{header}} "Offset": 120, "Irregularities": [ { "IrregularityID": "{{Uuid}}", "Source": "{{source}}", "TimeLabel": {{Time}}, "IrregularityType": "sp" } ] }
            """;
        Assert.True(Valid("IrregularityFile", File("\"Header\": \"CAE-IRR-V2.5\",", "a")));
        Assert.False(Valid("IrregularityFile", File("\"Header\": \"CAE-IRF-V2.5\",", "a")));             // the Header is CAE-IRR
        Assert.False(Valid("IrregularityFile", File("", "a")));                                           // it is required
        Assert.False(Valid("IrregularityFile", File("\"Header\": \"CAE-IRR-V2.5\",", "x")));              // a, v or b
    }

    [Fact]
    public void AnAudioSceneReferenceBelongsToCaeAsmAndIsAPolynomialOfTerms()
    {
        // what was the Audio Source Model of CAE-USC V2.4 is the Audio Scene Reference of CAE-ASM V1.0 (CAE-ASR)
        Assert.False(File.Exists(Path.Combine(Repository.Schemas, "CAE1", "V2.5", "data", "AudioSourceModel.json")));
        Assert.True(Valid("AudioSceneReference", """{ "Header": "CAE-ASR-V1.0", "PolynomialVariable": "AngleSine", "Polynomial": [ { "Coefficient": 1.5, "Exponent": 2 }, { "Coefficient": -0.5, "Exponent": 0 } ] }""", "CAE3", "V1.0"));
        Assert.False(Valid("AudioSceneReference", """{ "Header": "MMM-PLN-V2.5", "Polynomial": [ { "Coefficient": 1, "Exponent": 0 } ] }""", "CAE3", "V1.0"));
        Assert.False(Valid("AudioSceneReference", """{ "Header": "CAE-ASR-V1.0", "PolynomialVariable": "AngleSine" }""", "CAE3", "V1.0"));
        Assert.False(Valid("AudioSceneReference", """{ "Header": "CAE-ASR-V1.0", "Polynomial": [ { "Coefficient": 1, "Exponent": -1 } ] }""", "CAE3", "V1.0"));
    }

    // The features of Emotion Enhanced Speech travel as the Data of a Speech Descriptors Object (MMC-SDO-V2.5); their formats are those of CAE V2.1, brought to V2.5.
    [Fact]
    public void TheFormatsOfTheSpeechFeaturesOfEmotionEnhancedSpeechAreThoseOfV21InV25()
    {
        Assert.True(Valid("ProsodicSpeechFeatures", """{ "intonations": [ { "pitch": 220.0, "intensity": 0.6, "duration": 120 } ], "unit": "Hz" }""", folder: "formats"));
        Assert.False(Valid("ProsodicSpeechFeatures", """{ "intonations": [ { "pitch": 220.0, "intensity": 0.6 } ], "unit": "Hz" }""", folder: "formats"));
        Assert.False(Valid("ProsodicSpeechFeatures", """{ "intonations": [] }""", folder: "formats"));
        Assert.True(Valid("EmotionlessSpeechFeatures", "[0.1, -2, 3.5]", folder: "formats"));
        Assert.False(Valid("EmotionlessSpeechFeatures", """["a"]""", folder: "formats"));
        Assert.True(Valid("NeuralSpeechFeatures", "[1, 2, 3]", folder: "formats"));
        Assert.False(Valid("NeuralSpeechFeatures", """{ "features": [1] }""", folder: "formats"));
    }

    [Fact]
    public void TheMicrophoneArrayGeometrySchemaLoadsAndRequiresItsHeader()
    {
        Assert.False(Valid("MicrophoneArrayGeometry", """{ "MInstanceID": "m" }"""));
    }
}
