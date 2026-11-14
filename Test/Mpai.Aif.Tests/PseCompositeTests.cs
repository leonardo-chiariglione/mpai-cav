using System.Text.Json.Nodes;
using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Providers;
using SixLabors.ImageSharp.Processing;
using UscAudio = Mpai.Osd.Speech.Audio;
using UscSpeech = Mpai.Osd.Speech.SpeechObject;

namespace Mpai.Aif.Tests;

// MMC-PSE-V2.5 as the composite of Figure 2 of its specification (instance I02): Entity Text / Speech / Face / Body Description, PS-Text / PS-Speech / PS-Face / PS-Body Interpretation and Personal Status
// Multiplexing, run through the Controller. Each medium enters as the medium or as its descriptors; the Entity Personal Status has the modalities that arrived.
// The models are the GoEmotions classifier, wav2vec 2.0 (affect), HSEmotion and BlazePose: Slow.
public class PseCompositeTests
{
    private const string Pse = "1MMC-PSE-V2.5-I02";

    private static string Settings(out string work)
    {
        work = Path.Combine(Path.GetTempPath(), "pse-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        var settings = Path.Combine(work, "aim-settings.json");
        File.Copy(Path.Combine(Repository.Root, "AIMs", "aim-settings.json"), settings);
        return settings;
    }

    [Fact, Trait("Group", "Fast")]
    public void TheCompositeHasTheNineAimsOfTheSpecification()
    {
        var l3 = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Amds, Pse + ".json")))!;
        var subs = l3["SubAIMs"]!.AsArray().Select(s => s!["Identifier"]!["ImplementationID"]!.GetValue<string>().Split("-V")[0]).ToArray();
        Assert.Equal(new[] { "MMC-ETD", "MMC-ESD", "PAF-EFD", "PAF-EBD", "MMC-PTI", "MMC-PSI", "PAF-PFI", "PAF-PBI", "MMC-PMX" }, subs);
        // Each medium enters as the medium or as its descriptors.
        var inputs = l3["ExternalPorts"]!.AsArray().Where(p => p!["Direction"]!.GetValue<string>() == "Input").Select(p => p!["DataType"]!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "OSD-BTO-V1.5", "MMC-TDO-V2.5", "OSD-BSO-V1.5", "MMC-SDO-V2.5", "OSD-BVO-V1.5", "PAF-FDO-V1.6", "OSD-BVO-V1.5", "PAF-BDO-V1.6" }, inputs);
    }

    private static string FaceOf(string kind)
    {
        var file = Directory.GetFiles(Path.Combine(MpaiPaths.TestData, "Images", "Faces", kind), "*.jpg").OrderBy(f => f, StringComparer.Ordinal).First();
        return MpaiJson.ToJson(BasicVisualObject.FromFile(Path.GetFileName(file), File.ReadAllBytes(file), "Face"));
    }

    private static string BodyOfThePlayer()
    {
        using var image = SixLabors.ImageSharp.Image.Load(File.ReadAllBytes(Path.Combine(MpaiPaths.TestData, "Images", "People", "sports", "03-pexels-8694457.jpg")));
        image.Mutate(c => c.Crop(new SixLabors.ImageSharp.Rectangle(150, 430, 650, 1450)));
        using var ms = new MemoryStream(); SixLabors.ImageSharp.ImageExtensions.SaveAsJpeg(image, ms);
        return MpaiJson.ToJson(BasicVisualObject.FromFile("player.jpg", ms.ToArray(), "Body"));
    }

    private static EntityPersonalStatus Run(string settings, params (string Type, int Port, string Json)[] inputs)
    {
        using var api = new ControllerApi(Repository.Amds, settings, s => new LeafAimsProvider(s));
        Assert.Equal(AifError.OK, api.StartFlow(Pse, out var moduleId));
        foreach (var (type, port, json) in inputs) Assert.Equal(AifError.OK, api.InputWrite(moduleId, type, port, json));
        var r = api.OutputRead(moduleId, "MMC-EPS-V2.5", 1, 300_000);
        Assert.Equal(AifError.OK, r.Error);
        return MpaiJson.FromJson<EntityPersonalStatus>(r.Json!)!;
    }

    [SkippableFact, Trait("Group", "Slow")]
    public void TheFourMediaGiveAPersonalStatusEach()
    {
        Skip.IfNot(TestModels.Can("text-emotion", "text-emotion-vocab", "text-emotion-merges", "spe-model", "face-emotion", "pose-landmarks"), "the models are not obtained");
        var settings = Settings(out var work);
        try
        {
            var speech = UscSpeech.Make("said", UscAudio.FromWav(File.ReadAllBytes(Path.Combine(MpaiPaths.Clips, "clip1272.wav"))));
            var eps = Run(settings,
                ("OSD-BTO-V1.5", 1, MpaiJson.ToJson(BasicTextObject.FromText("I am so happy today, this is the best news I have had in years!"))),
                ("OSD-BSO-V1.5", 1, speech),
                ("OSD-BVO-V1.5", 1, FaceOf("angry")),
                ("OSD-BVO-V1.5", 2, BodyOfThePlayer()));
            Assert.Equal("HAPPINESS", eps.TextPersonalStatus?.TextEmotion?.Category);
            Assert.NotNull(eps.SpeechPersonalStatus?.SpeechEmotion);
            Assert.NotNull(eps.FacePersonalStatus);
            Assert.NotNull(eps.BodyPersonalStatus);
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    [SkippableFact, Trait("Group", "Slow")]
    public void ADescriptorsObjectEntersAsItIsAndAMediumNotGivenLeavesItsModalityOut()
    {
        Skip.IfNot(TestModels.Can("text-emotion", "text-emotion-vocab", "text-emotion-merges", "spe-model", "face-emotion", "pose-landmarks"), "the models are not obtained");
        var settings = Settings(out var work);
        try
        {
            // The text arrives as descriptors (GoEmotions scores: anger), the speech as descriptors (valence 0.1, arousal 0.9, dominance 0.8); no face, no body.
            var scores = new float[Mpai.Mmc.Etd.GoEmotionsClassifier.Labels.Length]; scores[Array.IndexOf(Mpai.Mmc.Etd.GoEmotionsClassifier.Labels, "anger")] = 0.8f;
            var tdo = new TextDescriptorsObject { TextDescriptorsObjectID = "t", TextDescriptorsData = new() { new TextDescriptorsDataItem { Data = EmbeddingCodec.ToBase64(scores) } },
                                                  TextDescriptorsQualifier = TextDescriptorsQualifier.ForOther(Mpai.Mmc.Etd.EtdAimProcessor.DescriptorFormat) };
            var sdo = SpeechDescriptorsObject.FromEmbedding(new[] { 0.1f, 0.9f, 0.8f }, Mpai.Mmc.Spe.EsdAffectAimProcessor.DescriptorFormat);
            var eps = Run(settings, ("MMC-TDO-V2.5", 1, MpaiJson.ToJson(tdo)), ("MMC-SDO-V2.5", 1, MpaiJson.ToJson(sdo)));
            Assert.Equal("ANGER", eps.TextPersonalStatus?.TextEmotion?.Category);
            Assert.Equal("ANGER", eps.SpeechPersonalStatus?.SpeechEmotion?.Category);
            Assert.Null(eps.FacePersonalStatus); Assert.Null(eps.BodyPersonalStatus);
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }
}
