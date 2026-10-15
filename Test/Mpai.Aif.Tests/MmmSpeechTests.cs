using System.Diagnostics;
using System.Text.Json.Nodes;
using Mpai.Mmm;
using Mpai.Mmm.Client;
using Mpai.Mmm.Server;

namespace Mpai.Aif.Tests;

// SPEECH AS SPEECH OBJECTS (MMM Phase 4). A participant sends what it said once, as a Speech Object that carries its own duration - not a request per chunk of
// the microphone. The Secretary forwards it to the ASR Service, a Process of its own, which answers with the words; the words go into the minutes.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class MmmSpeechTests
{
    private static JsonObject Profile(string name) => new()
    {
        ["Header"] = "MMM-PPR-V2.2", ["MInstanceID"] = "MI1", ["humanID"] = $"{name}-human", ["PersonalProfileID"] = $"Profile-{name}-human",
        ["PersonalProfile"] = new JsonObject
        {
            ["Data"] = new JsonArray(new JsonObject { ["FirstName"] = name }),
            ["Processes"] = new JsonArray(new JsonObject
            {
                ["ProcessIDOrProcess"] = new JsonArray(new JsonObject { ["ProcessID"] = name }),
                ["PersonaIDOrPersona"] = new JsonArray(new JsonObject { ["PersonaID"] = $"{name}-Persona" })
            })
        }
    };

    private static async Task<(MInstance M, Microsoft.AspNetCore.Builder.WebApplication App, HttpClient Http, MeetingServices Services, MmmClient Anna)> Start(Recognizer? recognize)
    {
        var m = new MInstance(Repository.Schemas);
        Uc2.Setup(m);
        Meeting.Setup(m);
        var app = MmmServer.Build(m, "http://127.0.0.1:0");
        await app.StartAsync();
        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        var reg = await new MmmClient(http, "Anna-human").PerformAsync("Register", new Complements { With = [Profile("Anna")] });
        var anna = new MmmClient(http, "Anna", reg.Tokens["Anna"]);
        return (m, app, http, new MeetingServices(http, m, recognize), anna);
    }

    // The PCM of one second of a tone: the content does not matter to the logic.
    private static byte[] Pcm(double seconds)
    {
        var pcm = new byte[(int)(seconds * Speech.Rate) * 2];
        for (var i = 0; i < pcm.Length / 2; i++) BitConverter.GetBytes((short)(8000 * Math.Sin(i * 0.05))).CopyTo(pcm, i * 2);
        return pcm;
    }

    [Fact]
    public async Task ASpeechObjectCarriesItsOwnDurationAndReachesTheMinutesThroughTheAsrService()
    {
        var heard = new List<int>();
        var (m, app, http, services, anna) = await Start(pcm => { heard.Add(pcm.Length); return Task.FromResult("good morning everybody"); });
        var speech = Speech.Make("Speech-Anna-1", Pcm(2.5), 1_767_254_400_000);
        Assert.Equal(2500, Speech.DurationMs(speech));                      // it lasts as long as it says: no request per chunk
        Assert.Equal(200, (await anna.PerformAsync("MM-Send", Uc2.C("Speech-Anna-1", to: Meeting.Secretary, with: speech))).Http);

        await services.PumpAsync();            // the Secretary forwards it to the ASR Service
        await services.PumpAsrAsync();         // the ASR Service recognises it and answers
        await services.PumpAsync();            // the Secretary puts the words in the minutes
        Assert.Equal([2.5 * Speech.Rate * 2], heard.Select(h => (double)h));   // the recognition got the whole utterance, once
        Assert.Equal(["Anna: good morning everybody"], services.TheMinutes);
        Assert.Equal("Anna: good morning everybody", Meeting.TextOf(new MmmClient(http, Meeting.Avs, m.TokenOf(Meeting.Avs)).ReadAsync(Meeting.Minutes).Result.Item));
        var latency = Assert.Single(services.Latencies);
        Assert.Equal(2500, latency.SpeechMs);
        await app.StopAsync();
    }

    [Fact]
    public async Task SpeechThatHasNoWordsLeavesNothingInTheMinutes()
    {
        var (_, app, _, services, anna) = await Start(_ => Task.FromResult(""));
        await anna.PerformAsync("MM-Send", Uc2.C("Speech-Anna-2", to: Meeting.Secretary, with: Speech.Make("Speech-Anna-2", Pcm(1), 1_767_254_400_000)));
        await services.PumpAsync(); await services.PumpAsrAsync(); await services.PumpAsync();
        Assert.Empty(services.TheMinutes);
        Assert.Single(services.Latencies);
        await app.StopAsync();
    }

    // The same with Whisper and a recorded question: the words, and how long they take.
    [SkippableFact]
    [Trait("Group", "Models")]
    [Trait("Duration", "Long")]
    public async Task WhisperRecognisesARecordedUtteranceForTheMinutes()
    {
        using var whisper = new WhisperAsr(Repository.Root);
        Skip.IfNot(whisper.Available, "Models/Whisper is absent: the model files are obtained separately.");
        var wavFile = Path.Combine(Repository.Root, "Test", "Data", "question.wav");
        Skip.IfNot(File.Exists(wavFile), "Test/Data/question.wav is absent.");
        var wav = File.ReadAllBytes(wavFile);
        Skip.IfNot(BitConverter.ToInt32(wav, 24) == Speech.Rate && BitConverter.ToInt16(wav, 22) == 1, "question.wav is not 16 kHz mono.");
        whisper.Start();
        Assert.True(await whisper.ReadyAsync(), "Whisper did not come up");
        var pcm = Speech.PcmOfWav(wav);

        var (_, app, _, services, anna) = await Start(whisper.RecognizeAsync);
        await anna.PerformAsync("MM-Send", Uc2.C("Speech-Anna-3", to: Meeting.Secretary, with: Speech.Make("Speech-Anna-3", pcm, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));
        await services.PumpAsync();
        await services.PumpAsrAsync();
        await services.PumpAsync();
        var line = Assert.Single(services.TheMinutes);
        Assert.StartsWith("Anna: ", line);
        Assert.True(line.Length > 12, line);
        var l = services.Latencies.Single();
        Directory.CreateDirectory(Path.Combine(Repository.Root, "Test", "Reports"));
        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "mmm-speech-latency.json"),
            $"{{ \"words\": {System.Text.Json.JsonSerializer.Serialize(line)}, \"speechMs\": {l.SpeechMs}, \"fromSecretaryToMinutesMs\": {l.TotalMs} }}");
        await app.StopAsync();
    }
}
