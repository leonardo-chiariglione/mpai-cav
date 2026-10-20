using System.Text.Json.Nodes;
using AIF.Controller;
using Mpai.Cae.Usc;
using Mpai.Cae.Usc.Smc;
using Mpai.Cae.Usc.Voice;

namespace Mpai.Aif.Tests;

// CAE-USC V2.5, the REAL Speech Model: Speech Model Creation makes the tone-colour embedding of a voice from its Speech Segments (OpenVoice V2) and the Text To Speech that uses it gives speech
// in that voice. Needs the voice engine (Python with torch, D:\AI\openvoice): a few seconds for each call on a CPU, so it is Slow. The speech of two speakers of the LibriSpeech clips
// (D:\AI\Datasets\libri_wav) is used: speaker 1272 is the voice to model, speaker 1462 says what is to be said.
[Trait("Group", "Slow")]
public class CaeUscV25VoiceTests
{
    private const string Clips = @"D:\AI\Datasets\libri_wav";
    private static Audio Clip(string speaker) => Audio.FromWav(File.ReadAllBytes(Path.Combine(Clips, speaker, "clip.wav")));
    private static double Distance(float[] a, float[] b) => Math.Sqrt(a.Zip(b, (x, y) => (double)(x - y) * (x - y)).Sum());

    [SkippableFact]
    public async Task TheSpeechModelIsTheEmbeddingOfTheVoiceAndTheSpeechThatUsesItSoundsLikeIt()
    {
        Skip.IfNot(Directory.Exists(@"D:\AI\openvoice\converter") && File.Exists(@"D:\AI\pyannote-venv\Scripts\python.exe"), "the voice engine is not installed");
        var worker = new OpenVoiceWorker();
        var target = Clip("1272"); var other = Clip("1462");

        // Speech Model Creation, as the AIM: the segments in, the ML Model Object out.
        var smc = new SmcAimProcessor("smc", "in", "out", new OpenVoiceSpeechTrainer(worker));
        var made = await smc.ProcessAsync(new Message { MessageId = "m", Ports = new() { ["in"] = SpeechObject.Make("segments", target) } });
        Assert.False(made.IsError, made.Payload);
        var model = made.Ports["out"];
        Assert.Contains("OpenVoice V2", JsonNode.Parse(model)!["DescrMetadata"]!.GetValue<string>());
        var embedding = worker.Extract([target]);
        Assert.Equal(256, embedding.Length);

        // The Text To Speech: a base voice (here the other speaker's clip stands for what Piper says) becomes the voice of the model.
        var said = SpeechObject.MakeSynthetic("said", other);
        var tts = new VoiceCloningTtsAimProcessor("tts", new Say(said), "model", "speech", worker);
        var spoken = await tts.ProcessAsync(new Message { MessageId = "m", Ports = new() { ["model"] = model } });
        Assert.False(spoken.IsError, spoken.Payload);
        var piece = SpeechObject.Read(spoken.Ports["speech"]).Single();
        Assert.Equal(16000, piece.Rate); Assert.InRange(piece.Seconds, other.Seconds * 0.9, other.Seconds * 1.1);      // as long as what was said
        var heard = worker.Extract([piece]);
        Assert.True(Distance(heard, embedding) < 0.7 * Distance(worker.Extract([other]), embedding), "the speech is nearer the modelled voice than the voice that said it was");
    }

    [SkippableFact]
    public async Task ASpeechModelThatIsNotAnEmbeddingIsRefused()
    {
        var standIn = SmcAimProcessor.ModelObject(new TrainedSpeechModel([0.1f, 0.2f, 1f], "stand-in", 1, 1)).ToJsonString();
        var tts = new VoiceCloningTtsAimProcessor("tts", new Say(SpeechObject.MakeSynthetic("said", Clip("1462"))), "model", "speech", new OpenVoiceWorker());
        var refused = await tts.ProcessAsync(new Message { MessageId = "m", Ports = new() { ["model"] = standIn } });
        Assert.True(refused.IsError); Assert.Contains("not a tone-colour embedding", refused.Payload);
        Assert.True((await tts.ProcessAsync(new Message { MessageId = "m", Ports = new() })).IsError);
    }

    private sealed class Say(string speech) : IAimProcessor
    {
        public string InstanceId => "say";
        public Task<Message> ProcessAsync(Message m) => Task.FromResult(new Message { MessageId = m.MessageId, MessageType = "BasicSpeechObject", Payload = speech, Ports = new() });
    }
}