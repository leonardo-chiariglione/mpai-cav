using System.Text.Json.Nodes;
using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Mmc.Etd;
using Mpai.Mmc.Psi;
using Mpai.Mmc.Pti;
using Mpai.Mmc.Spe;
using Mpai.Paf.Ebd;
using Mpai.Paf.Fpe;
using Mpai.Paf.Pbi;
using Mpai.Paf.Pfi;
using Mpai.Providers;
using SixLabors.ImageSharp.Processing;
using UscAudio = Mpai.Osd.Speech.Audio;

namespace Mpai.Aif.Tests;

// The description and the interpretation of the Personal Status, one modality at a time: Entity Text Description (MMC-ETD) and PS-Text Interpretation (MMC-PTI), the instance I03 of Entity
// Speech Description (affect) and PS-Speech Interpretation (MMC-PSI), the instance I02 of Entity Face Description (expression) and PS-Face Interpretation (PAF-PFI).
//
// The descriptors are what a model reads from the medium (GoEmotions scores, wav2vec 2.0 valence/arousal/dominance, HSEmotion probabilities), the interpretation is the mapping of those numbers to
// the vocabulary of the Personal Status. The mappings and the refusals are quick (Fast); the models are Slow.
public class PersonalStatusInterpretationTests
{
    private static AIF.Store.AmdStore Store() { var s = new AIF.Store.AmdStore(Repository.Amds); s.Scan(); return s; }
    private static float[] Scores(params (string Label, float Value)[] set)
    {
        var all = new float[GoEmotionsClassifier.Labels.Length];
        foreach (var (l, v) in set) all[Array.IndexOf(GoEmotionsClassifier.Labels, l)] = v;
        return all;
    }

    // ---- PS-Text Interpretation: the mapping ---------------------------------------------------------------------------------------------------

    [Fact, Trait("Group", "Fast")]
    public void EveryLabelOfGoEmotionsIsMappedOrDeclaredLost()
    {
        // Each of the 28 labels, alone at 0.9, gives a Factor, except those declared lost; and the words are words of the vocabulary.
        var emotion = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Schemas, "MMC", "V2.5", "data", "Emotion.json")))!["properties"]!["EmotionCategory"]!["properties"]!.AsObject();
        for (int i = 0; i < GoEmotionsClassifier.Labels.Length; i++)
        {
            var label = GoEmotionsClassifier.Labels[i];
            var one = new float[GoEmotionsClassifier.Labels.Length]; one[i] = 0.9f;
            var ps = PtiAimProcessor.Interpret(one);
            bool given = ps.TextEmotion is not null || ps.TextCognitiveState is not null || ps.TextSocialAttitude is not null;
            Assert.Equal(!PtiAimProcessor.Lost.Contains(label), given);
            if (ps.TextEmotion is { } e)
            {
                Assert.NotNull(emotion[e.Category!]);
                if (e.SpecificAdjectival is not null)
                    Assert.Contains(emotion[e.Category!]!["properties"]!["GeneralAdjectival"]!["properties"]![e.GeneralAdjectival!]!["enum"]!.AsArray(), s => s!.GetValue<string>() == e.SpecificAdjectival);
            }
        }
    }

    [Fact, Trait("Group", "Fast")]
    public void TheHighestScoreOfEachFactorIsGivenWithItsScoreAsDegree()
    {
        var ps = PtiAimProcessor.Interpret(Scores(("joy", 0.9f), ("sadness", 0.4f), ("curiosity", 0.7f), ("surprise", 0.3f), ("approval", 0.5f), ("neutral", 0.95f)));
        Assert.Equal("HAPPINESS", ps.TextEmotion!.Category); Assert.Equal("joyful", ps.TextEmotion.SpecificAdjectival); Assert.Equal(0.9, ps.TextEmotion.Degree!.Value, 3);
        Assert.Equal("INTEREST", ps.TextCognitiveState!.Category); Assert.Equal("curious", ps.TextCognitiveState.SpecificAdjectival);
        Assert.Equal("APPROVAL/DISAPPROVAL", ps.TextSocialAttitude!.Category);
    }

    [Fact, Trait("Group", "Fast")]
    public void ScoresBelowTheThresholdGiveNoPersonalStatus()
    {
        var ps = PtiAimProcessor.Interpret(Scores(("joy", 0.2f), ("neutral", 0.97f)));
        Assert.Null(ps.TextEmotion); Assert.Null(ps.TextCognitiveState); Assert.Null(ps.TextSocialAttitude);
    }

    [Fact, Trait("Group", "Fast")]
    public async Task OnlyTheDescriptorsOfTheRightFormatAreInterpreted()
    {
        var ports = AimPortReader.Load(Store(), "1MMC-PTI-V2.5-I01");
        var pti = new PtiAimProcessor("pti", ports);
        var inPort = ports.Input("MMC-TDO-V2.5", 1); var outPort = ports.Output("MMC-TPS-V2.5");

        var none = await pti.ProcessAsync(new Message { MessageId = "m", Ports = new() });
        Assert.True(none.IsError); Assert.Contains("no Text Descriptors Object", none.Payload);

        var taggings = TextDescriptorsObject.FromTaggings(new TextTaggings());           // the descriptors of MMC-NLU, not scores
        var wrong = await pti.ProcessAsync(new Message { MessageId = "m", Ports = new() { [inPort] = MpaiJson.ToJson(taggings) } });
        Assert.True(wrong.IsError); Assert.Contains("this interpretation takes", wrong.Payload);

        var scores = new TextDescriptorsObject { TextDescriptorsObjectID = "t", TextDescriptorsData = new() { new TextDescriptorsDataItem { Data = EmbeddingCodec.ToBase64(Scores(("anger", 0.8f))) } },
                                                 TextDescriptorsQualifier = TextDescriptorsQualifier.ForOther(EtdAimProcessor.DescriptorFormat) };
        var right = await pti.ProcessAsync(new Message { MessageId = "m", Ports = new() { [inPort] = MpaiJson.ToJson(scores) } });
        Assert.False(right.IsError, right.Payload);
        Assert.Equal("ANGER", MpaiJson.FromJson<TextPersonalStatus>(right.Ports[outPort])!.TextEmotion!.Category);
    }

    [Fact, Trait("Group", "Fast")]
    public async Task WithoutATextThereIsNothingToDescribe()
    {
        var ports = AimPortReader.Load(Store(), "1MMC-ETD-V2.5-I01");
        var etd = new EtdAimProcessor("etd", null!, ports);                                // refused before the model is used
        var none = await etd.ProcessAsync(new Message { MessageId = "m", Ports = new() });
        Assert.True(none.IsError); Assert.Contains("no Text Object", none.Payload);
        var empty = await etd.ProcessAsync(new Message { MessageId = "m", Ports = new() { [ports.Input("OSD-BTO-V1.5", 1)] = MpaiJson.ToJson(BasicTextObject.FromText("  ")) } });
        Assert.True(empty.IsError); Assert.Contains("no text", empty.Payload);
    }

    // ---- PS-Speech Interpretation --------------------------------------------------------------------------------------------------------------

    [Fact, Trait("Group", "Fast")]
    public async Task TheSpeechAffectIsInterpretedAsMmcSpeDoesAndNothingElseIs()
    {
        var ports = AimPortReader.Load(Store(), "1MMC-PSI-V2.5-I01");
        var psi = new PsiAimProcessor("psi", ports);
        var inPort = ports.Input("MMC-SDO-V2.5"); var outPort = ports.Output("MMC-SPS-V2.5");

        var happy = SpeechDescriptorsObject.FromEmbedding(new[] { 0.9f, 0.8f, 0.5f }, EsdAffectAimProcessor.DescriptorFormat);        // valence, arousal, dominance
        var r = await psi.ProcessAsync(new Message { MessageId = "m", Ports = new() { [inPort] = MpaiJson.ToJson(happy) } });
        Assert.False(r.IsError, r.Payload);
        var sps = MpaiJson.FromJson<SpeechPersonalStatus>(r.Ports[outPort])!;
        Assert.Equal("HAPPINESS", sps.SpeechEmotion!.Category);
        Assert.Equal(SpeAimProcessor.ToSpeechPersonalStatus(new SpeechAffect { Valence = 0.9f, Arousal = 0.8f, Dominance = 0.5f }).SpeechEmotion!.Category, sps.SpeechEmotion.Category);

        var angry = SpeechDescriptorsObject.FromEmbedding(new[] { 0.1f, 0.9f, 0.8f }, EsdAffectAimProcessor.DescriptorFormat);
        var a = await psi.ProcessAsync(new Message { MessageId = "m", Ports = new() { [inPort] = MpaiJson.ToJson(angry) } });
        var spsA = MpaiJson.FromJson<SpeechPersonalStatus>(a.Ports[outPort])!;
        Assert.Equal("ANGER", spsA.SpeechEmotion!.Category); Assert.Equal("SOCIAL DOMINANCE/CONFIDENCE", spsA.SpeechSocialAttitude!.Category);

        var voice = SpeechDescriptorsObject.FromEmbedding(new float[192], "ECAPA-TDNN (192-d)");                                       // a voice, not an affect
        var refused = await psi.ProcessAsync(new Message { MessageId = "m", Ports = new() { [inPort] = MpaiJson.ToJson(voice) } });
        Assert.True(refused.IsError); Assert.Contains("this interpretation takes", refused.Payload);
        var none = await psi.ProcessAsync(new Message { MessageId = "m", Ports = new() });
        Assert.True(none.IsError);
    }

    // ---- PS-Face Interpretation ----------------------------------------------------------------------------------------------------------------

    [Fact, Trait("Group", "Fast")]
    public async Task TheFaceExpressionIsInterpretedAsPafFpeDoesAndNothingElseIs()
    {
        var ports = AimPortReader.Load(Store(), "1PAF-PFI-V1.6-I01");
        var pfi = new PfiAimProcessor("pfi", ports);
        var inPort = ports.Input("PAF-FDO-V1.6", 1); var outPort = ports.Output("MMC-FPS-V2.5");

        // Anger 0.7, Happiness 0.3 (the order of AffectNet: Anger, Contempt, Disgust, Fear, Happiness, Neutral, Sadness, Surprise), valence -0.4, arousal 0.5.
        var numbers = new float[] { 0.7f, 0, 0, 0, 0.3f, 0, 0, 0, -0.4f, 0.5f };
        var fdo = FaceDescriptorsObject.FromEmbedding(numbers, EfdAffectAimProcessor.DescriptorFormat);
        var r = await pfi.ProcessAsync(new Message { MessageId = "m", Ports = new() { [inPort] = MpaiJson.ToJson(fdo) } });
        Assert.False(r.IsError, r.Payload);
        var fps = MpaiJson.FromJson<FacePersonalStatus>(r.Ports[outPort])!;
        Assert.Equal("ANGER", fps.FaceEmotion!.Category); Assert.Equal(0.7, fps.FaceEmotion.Degree!.Value, 3);

        var surprise = FaceDescriptorsObject.FromEmbedding(new float[] { 0, 0, 0, 0, 0, 0, 0, 1, 0, 0 }, EfdAffectAimProcessor.DescriptorFormat);
        var s = await pfi.ProcessAsync(new Message { MessageId = "m", Ports = new() { [inPort] = MpaiJson.ToJson(surprise) } });
        Assert.Equal("SURPRISE", MpaiJson.FromJson<FacePersonalStatus>(s.Ports[outPort])!.FaceCognitiveState!.Category);

        var identity = FaceDescriptorsObject.FromEmbedding(new float[512], "ArcFace (ResNet-100, 512-d)");                           // who, not how
        var refused = await pfi.ProcessAsync(new Message { MessageId = "m", Ports = new() { [inPort] = MpaiJson.ToJson(identity) } });
        Assert.True(refused.IsError); Assert.Contains("this interpretation takes", refused.Payload);
    }

    [Fact, Trait("Group", "Fast")]
    public void TheNewDescriptorFormatsAreInTheEnumerations()
    {
        string[] Consts(string file) => JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Schemas, "TFA", "V1.5", "formats", file)))!["oneOf"]!.AsArray().Select(f => f!["const"]!.GetValue<string>()).ToArray();
        Assert.Contains(EtdAimProcessor.DescriptorFormat, Consts("TextDescriptorsFormats.json"));
        Assert.Contains(EsdAffectAimProcessor.DescriptorFormat, Consts("SpeechDescriptorsFormats.json"));
        Assert.Contains(EfdAffectAimProcessor.DescriptorFormat, Consts("FaceDescriptorsContentFormats.json"));
    }

    // ---- the models ----------------------------------------------------------------------------------------------------------------------------

    private async Task<TextPersonalStatus> TextStatus(string text, EtdAimProcessor etd, AimPortReader etdPorts, PtiAimProcessor pti, AimPortReader ptiPorts)
    {
        var d = await etd.ProcessAsync(new Message { MessageId = "m", Ports = new() { [etdPorts.Input("OSD-BTO-V1.5", 1)] = MpaiJson.ToJson(BasicTextObject.FromText(text)) } });
        Assert.False(d.IsError, d.Payload);
        var p = await pti.ProcessAsync(new Message { MessageId = "m", Ports = new() { [ptiPorts.Input("MMC-TDO-V2.5", 1)] = d.Ports[etdPorts.Output("MMC-TDO-V2.5")] } });
        Assert.False(p.IsError, p.Payload);
        return MpaiJson.FromJson<TextPersonalStatus>(p.Ports[ptiPorts.Output("MMC-TPS-V2.5")])!;
    }

    [SkippableFact, Trait("Group", "Slow")]
    public async Task TheEmotionOfATextIsReadFromItsDescriptors()
    {
        Skip.IfNot(TestModels.Can("text-emotion", "text-emotion-vocab", "text-emotion-merges"), "the GoEmotions model is not obtained");
        var store = Store();
        var etdPorts = AimPortReader.Load(store, "1MMC-ETD-V2.5-I01"); var ptiPorts = AimPortReader.Load(store, "1MMC-PTI-V2.5-I01");
        using var model = EtdFactory.Create(TestModels.Settings("1MMC-ETD-V2.5-I01"));
        var etd = new EtdAimProcessor("etd", model, etdPorts); var pti = new PtiAimProcessor("pti", ptiPorts);

        var joy = await TextStatus("I am so happy today, this is the best news I have had in years!", etd, etdPorts, pti, ptiPorts);
        Assert.Equal("HAPPINESS", joy.TextEmotion?.Category);
        var anger = await TextStatus("I am absolutely furious with you. This is outrageous and I will not accept it.", etd, etdPorts, pti, ptiPorts);
        Assert.Equal("ANGER", anger.TextEmotion?.Category);
        var sad = await TextStatus("My dog died this morning and I cannot stop crying.", etd, etdPorts, pti, ptiPorts);
        Assert.Equal("SADNESS", sad.TextEmotion?.Category);
        var curious = await TextStatus("I wonder how this machine works. Could you explain it to me?", etd, etdPorts, pti, ptiPorts);
        Assert.Equal("INTEREST", curious.TextCognitiveState?.Category);

        // The descriptors carry 28 numbers, and a long text is cut at 512 tokens without failing.
        var d = await etd.ProcessAsync(new Message { MessageId = "m", Ports = new() { [etdPorts.Input("OSD-BTO-V1.5", 1)] = MpaiJson.ToJson(BasicTextObject.FromText(string.Concat(Enumerable.Repeat("What a wonderful day. ", 400)))) } });
        Assert.False(d.IsError, d.Payload);
        Assert.Equal(28, EtdAimProcessor.Scores(d.Ports[etdPorts.Output("MMC-TDO-V2.5")]).Length);
    }

    [SkippableFact, Trait("Group", "Slow")]
    public async Task TheAffectOfASpeechThroughDescriptionAndInterpretationIsWhatMmcSpeGives()
    {
        Skip.IfNot(TestModels.Can("spe-model"), "the speech emotion model is not declared");
        var store = Store();
        var settings = TestModels.Settings("1MMC-ESD-V2.5-I03");
        var esdPorts = AimPortReader.Load(store, "1MMC-ESD-V2.5-I03"); var psiPorts = AimPortReader.Load(store, "1MMC-PSI-V2.5-I01"); var spePorts = AimPortReader.Load(store, "1MMC-SPE-V2.5-I01");
        using var w2v2 = new Wav2Vec2EmotionEstimator(MpaiPaths.Given(settings, "W2v2Model"));
        var esd = new EsdAffectAimProcessor("esd", w2v2, esdPorts); var psi = new PsiAimProcessor("psi", psiPorts); var spe = new SpeAimProcessor("spe", w2v2, spePorts);
        foreach (var clip in new[] { "1272", "1462", "174" })
        {
            var speech = Mpai.Osd.Speech.SpeechObject.Make("s", UscAudio.FromWav(File.ReadAllBytes(Path.Combine(MpaiPaths.Clips, "clip" + clip + ".wav"))));
            var d = await esd.ProcessAsync(new Message { MessageId = "m", Ports = new() { [esdPorts.Input("OSD-BSO-V1.5")] = speech } });
            Assert.False(d.IsError, d.Payload);
            var p = await psi.ProcessAsync(new Message { MessageId = "m", Ports = new() { [psiPorts.Input("MMC-SDO-V2.5")] = d.Ports[esdPorts.Output("MMC-SDO-V2.5")] } });
            Assert.False(p.IsError, p.Payload);
            var direct = await spe.ProcessAsync(new Message { MessageId = "m", Ports = new() { [spePorts.Input("OSD-BSO-V1.5")] = speech } });
            var viaPsi = MpaiJson.FromJson<SpeechPersonalStatus>(p.Ports[psiPorts.Output("MMC-SPS-V2.5")])!;
            var viaSpe = MpaiJson.FromJson<SpeechPersonalStatus>(direct.Ports[spePorts.Output("MMC-SPS-V2.5")])!;
            Assert.Equal(viaSpe.SpeechEmotion!.Category, viaPsi.SpeechEmotion!.Category);
            Assert.Equal(viaSpe.SpeechEmotion.Degree!.Value, viaPsi.SpeechEmotion.Degree!.Value, 2);
        }
    }

    [SkippableFact, Trait("Group", "Slow")]
    public async Task TheExpressionOfAFaceThroughDescriptionAndInterpretationIsWhatPafFpeGives()
    {
        Skip.IfNot(TestModels.Can("face-emotion"), "the face emotion model is not declared");
        var store = Store();
        var settings = TestModels.Settings("1PAF-EFD-V1.6-I02");
        var efdPorts = AimPortReader.Load(store, "1PAF-EFD-V1.6-I02"); var pfiPorts = AimPortReader.Load(store, "1PAF-PFI-V1.6-I01"); var fpePorts = AimPortReader.Load(store, "1PAF-FPE-V1.6-I01");
        using var hse = new HSEmotionEstimator(MpaiPaths.Given(settings, "HseModel"));
        var efd = new EfdAffectAimProcessor("efd", hse, efdPorts); var pfi = new PfiAimProcessor("pfi", pfiPorts); var fpe = new FpeAimProcessor("fpe", hse, fpePorts);
        var faces = Path.Combine(MpaiPaths.TestData, "Images", "Faces");
        var seen = new HashSet<string?>();
        foreach (var (kind, expected) in new[] { ("happy", "HAPPINESS"), ("angry", "ANGER"), ("sad", "SADNESS") })
        {
            var file = Directory.GetFiles(Path.Combine(faces, kind), "*.jpg").OrderBy(f => f, StringComparer.Ordinal).First();
            var picture = MpaiJson.ToJson(BasicVisualObject.FromFile(Path.GetFileName(file), File.ReadAllBytes(file), "Face"));
            var d = await efd.ProcessAsync(new Message { MessageId = "m", Ports = new() { [efdPorts.Input("OSD-BVO-V1.5")] = picture } });
            Assert.False(d.IsError, d.Payload);
            var p = await pfi.ProcessAsync(new Message { MessageId = "m", Ports = new() { [pfiPorts.Input("PAF-FDO-V1.6", 1)] = d.Ports[efdPorts.Output("PAF-FDO-V1.6")] } });
            Assert.False(p.IsError, p.Payload);
            var direct = await fpe.ProcessAsync(new Message { MessageId = "m", Ports = new() { [fpePorts.Input("OSD-BVO-V1.5")] = picture } });
            var viaPfi = MpaiJson.FromJson<FacePersonalStatus>(p.Ports[pfiPorts.Output("MMC-FPS-V2.5")])!;
            var viaFpe = MpaiJson.FromJson<FacePersonalStatus>(direct.Ports[fpePorts.Output("MMC-FPS-V2.5")])!;
            Assert.Equal(viaFpe.FaceEmotion?.Category, viaPfi.FaceEmotion?.Category);
            Assert.Equal(viaFpe.FaceCognitiveState?.Category, viaPfi.FaceCognitiveState?.Category);
            seen.Add(viaPfi.FaceEmotion?.Category);
        }
        Assert.True(seen.Count >= 2, "the three pictures (happy, angry, sad) are not all given the same status");
    }

    // ---- PS-Body Interpretation (rules) and Entity Body Description -----------------------------------------------------------------------------

    // A standing body, front view, as 33 x (x, y, z, visibility): shoulders at y 0.30, hips at y 0.60 (torso 0.30), nose at 0.18, arms hanging; the unused keypoints are unseen.
    private static float[] Body(Action<float[]>? change = null)
    {
        var k = new float[33 * 4];
        void Set(int i, float x, float y) { k[i * 4] = x; k[i * 4 + 1] = y; k[i * 4 + 3] = 1f; }
        Set(PoseLandmarker.Nose, 0.50f, 0.18f);
        Set(PoseLandmarker.LeftShoulder, 0.58f, 0.30f); Set(PoseLandmarker.RightShoulder, 0.42f, 0.30f);       // shoulder width 0.16
        Set(PoseLandmarker.LeftHip, 0.56f, 0.60f); Set(PoseLandmarker.RightHip, 0.44f, 0.60f);
        Set(PoseLandmarker.LeftWrist, 0.60f, 0.62f); Set(PoseLandmarker.RightWrist, 0.40f, 0.62f);               // hanging, beside the hips
        change?.Invoke(k);
        return k;
    }
    private static void Move(float[] k, int i, float x, float y) { k[i * 4] = x; k[i * 4 + 1] = y; }

    [Fact, Trait("Group", "Fast")]
    public void TheBodyRulesGiveThePostureTheyName()
    {
        // Standing with the arms hanging (elbows seen, close to the body): none of the rules applies.
        var rest = PbiAimProcessor.Interpret(Body(k => { Move(k, PoseLandmarker.LeftElbow, 0.60f, 0.46f); Move(k, PoseLandmarker.RightElbow, 0.40f, 0.46f); k[PoseLandmarker.LeftElbow * 4 + 3] = 1f; k[PoseLandmarker.RightElbow * 4 + 3] = 1f; }));
        Assert.Null(rest.BodyEmotion); Assert.Null(rest.BodyCognitiveState); Assert.Null(rest.BodySocialAttitude);

        var raised = PbiAimProcessor.Interpret(Body(k => { Move(k, PoseLandmarker.LeftWrist, 0.62f, 0.02f); Move(k, PoseLandmarker.RightWrist, 0.38f, 0.02f); }));
        Assert.Equal("HAPPINESS", raised.BodyEmotion!.Category); Assert.Equal("AROUSAL", raised.BodyCognitiveState!.Category);

        var thinking = PbiAimProcessor.Interpret(Body(k => Move(k, PoseLandmarker.RightWrist, 0.46f, 0.22f)));
        Assert.Equal("ATTENTION", thinking.BodyCognitiveState!.Category); Assert.Equal("thoughtful", thinking.BodyCognitiveState.SpecificAdjectival);

        var crossed = PbiAimProcessor.Interpret(Body(k => { Move(k, PoseLandmarker.LeftWrist, 0.46f, 0.40f); Move(k, PoseLandmarker.RightWrist, 0.54f, 0.40f); }));
        Assert.Equal("OPENNESS/TRUST", crossed.BodySocialAttitude!.Category); Assert.Equal("closed/distant", crossed.BodySocialAttitude.GeneralAdjectival);

        var open = PbiAimProcessor.Interpret(Body(k => { Move(k, PoseLandmarker.LeftWrist, 0.85f, 0.30f); Move(k, PoseLandmarker.RightWrist, 0.15f, 0.30f); }));
        Assert.Equal("open", open.BodySocialAttitude!.GeneralAdjectival);

        var hips = PbiAimProcessor.Interpret(Body(k => { Move(k, PoseLandmarker.LeftWrist, 0.56f, 0.60f); Move(k, PoseLandmarker.RightWrist, 0.44f, 0.60f);
            Move(k, PoseLandmarker.LeftElbow, 0.74f, 0.45f); Move(k, PoseLandmarker.RightElbow, 0.26f, 0.45f); k[PoseLandmarker.LeftElbow * 4 + 3] = 1f; k[PoseLandmarker.RightElbow * 4 + 3] = 1f; }));
        Assert.Equal("SOCIAL DOMINANCE/CONFIDENCE", hips.BodySocialAttitude!.Category);

        var dropped = PbiAimProcessor.Interpret(Body(k => Move(k, PoseLandmarker.Nose, 0.50f, 0.34f)));
        Assert.Equal("SADNESS", dropped.BodyEmotion!.Category);

        // A keypoint that is not seen is not used: the same raised arms, with a wrist hidden, give nothing.
        var hidden = PbiAimProcessor.Interpret(Body(k => { Move(k, PoseLandmarker.LeftWrist, 0.62f, 0.02f); Move(k, PoseLandmarker.RightWrist, 0.38f, 0.02f); k[PoseLandmarker.RightWrist * 4 + 3] = 0.1f; }));
        Assert.Null(hidden.BodyEmotion);
    }

    [Fact, Trait("Group", "Fast")]
    public async Task OnlyKeypointsAreInterpretedAndWithoutAPictureThereIsNothingToDescribe()
    {
        var store = Store();
        var pbiPorts = AimPortReader.Load(store, "1PAF-PBI-V1.6-I01");
        var pbi = new PbiAimProcessor("pbi", pbiPorts);
        var inPort = pbiPorts.Input("PAF-BDO-V1.6", 1); var outPort = pbiPorts.Output("MMC-BPS-V2.5");
        var bdo = BodyDescriptorsObject.FromContent(EmbeddingCodec.ToBase64(Body(k => Move(k, PoseLandmarker.Nose, 0.50f, 0.34f))), EbdAimProcessor.DescriptorFormat);
        var r = await pbi.ProcessAsync(new Message { MessageId = "m", Ports = new() { [inPort] = MpaiJson.ToJson(bdo) } });
        Assert.False(r.IsError, r.Payload);
        Assert.Equal("SADNESS", MpaiJson.FromJson<BodyPersonalStatus>(r.Ports[outPort])!.BodyEmotion!.Category);

        var motion = BodyDescriptorsObject.FromNodeAttitudes(new[] { "{}" });                                       // the nodes of a skeleton, not keypoints
        var refused = await pbi.ProcessAsync(new Message { MessageId = "m", Ports = new() { [inPort] = MpaiJson.ToJson(motion) } });
        Assert.True(refused.IsError); Assert.Contains("this interpretation takes", refused.Payload);

        var ebdPorts = AimPortReader.Load(store, "1PAF-EBD-V1.6-I01");
        var ebd = new EbdAimProcessor("ebd", null!, ebdPorts);                                                      // refused before the model is used
        var none = await ebd.ProcessAsync(new Message { MessageId = "m", Ports = new() });
        Assert.True(none.IsError); Assert.Contains("no Body Object", none.Payload);
    }

    [Fact, Trait("Group", "Fast")]
    public void TheBodyFormatIsOneOfTheFormatsOfTheBodyDescriptors()
    {
        var formats = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Schemas, "TFA", "V1.5", "formats", "BodyDescriptorsContentFormats.json")))!["enum"]!.AsArray();
        Assert.Contains(formats, f => f!.GetValue<string>() == EbdAimProcessor.DescriptorFormat);
    }

    [SkippableFact, Trait("Group", "Slow")]
    public async Task TheKeypointsOfABodyAreFoundAndInterpreted()
    {
        Skip.IfNot(TestModels.Can("pose-landmarks"), "the body keypoint model is not obtained");
        var store = Store();
        var ebdPorts = AimPortReader.Load(store, "1PAF-EBD-V1.6-I01"); var pbiPorts = AimPortReader.Load(store, "1PAF-PBI-V1.6-I01");
        using var pose = new PoseLandmarker(MpaiPaths.Given(TestModels.Settings("1PAF-EBD-V1.6-I01"), "PoseModel"));
        var ebd = new EbdAimProcessor("ebd", pose, ebdPorts); var pbi = new PbiAimProcessor("pbi", pbiPorts);

        // The player in black, arm raised (a crop of one body from a picture of four players).
        var source = File.ReadAllBytes(Path.Combine(MpaiPaths.TestData, "Images", "People", "sports", "03-pexels-8694457.jpg"));
        using var image = SixLabors.ImageSharp.Image.Load(source);
        image.Mutate(c => c.Crop(new SixLabors.ImageSharp.Rectangle(150, 430, 650, 1450)));
        using var ms = new MemoryStream(); SixLabors.ImageSharp.ImageExtensions.SaveAsJpeg(image, ms);
        var body = MpaiJson.ToJson(BasicVisualObject.FromFile("player.jpg", ms.ToArray(), "Body"));

        var d = await ebd.ProcessAsync(new Message { MessageId = "m", Ports = new() { [ebdPorts.Input("OSD-BVO-V1.5")] = body } });
        Assert.False(d.IsError, d.Payload);
        var keypoints = EbdAimProcessor.Keypoints(d.Ports[ebdPorts.Output("PAF-BDO-V1.6")]);
        Assert.Equal(132, keypoints.Length);
        // The head is above the hips and the shoulders are above the hips: a body standing, found.
        Assert.True(keypoints[PoseLandmarker.Nose * 4 + 1] < keypoints[PoseLandmarker.LeftHip * 4 + 1]);
        Assert.True(keypoints[PoseLandmarker.LeftShoulder * 4 + 1] < keypoints[PoseLandmarker.LeftHip * 4 + 1]);
        // A raised arm: a wrist above the nose.
        Assert.True(Math.Min(keypoints[PoseLandmarker.LeftWrist * 4 + 1], keypoints[PoseLandmarker.RightWrist * 4 + 1]) < keypoints[PoseLandmarker.Nose * 4 + 1]);
        var p = await pbi.ProcessAsync(new Message { MessageId = "m", Ports = new() { [pbiPorts.Input("PAF-BDO-V1.6", 1)] = d.Ports[ebdPorts.Output("PAF-BDO-V1.6")] } });
        Assert.False(p.IsError, p.Payload);

        // A picture with no body (a blue field): no body found, an error, not keypoints.
        var field = MpaiJson.ToJson(BasicVisualObject.FromFile("blue.jpg", File.ReadAllBytes(Path.Combine(MpaiPaths.TestData, "blue.jpg")), "Body"));
        var none = await ebd.ProcessAsync(new Message { MessageId = "m", Ports = new() { [ebdPorts.Input("OSD-BVO-V1.5")] = field } });
        Assert.True(none.IsError); Assert.Contains("no body found", none.Payload);
    }
}
