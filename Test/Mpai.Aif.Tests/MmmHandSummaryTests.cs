using System.Text.Json.Nodes;
using Mpai.Mmm;
using Mpai.Mmm.Client;
using Mpai.Mmm.Server;

namespace Mpai.Aif.Tests;

// TWO FEATURES OF THE MMM CONFERENCE (M3283): a hand raised, and the summary on request.
//  MC-3: a hand is up until its owner lowers it, the convener lowers it, or its owner has spoken for 5 s; every participant is told who has one up, the minutes note it.
//  MC-4/5: no summary exists unless somebody asks; it goes only to who asked; it is made in English, in pieces if the minutes are long, translated into the language
//  chosen, and spoken if the User wanted it spoken.
[Trait("Group", "Fast")]
[Collection("MmmMeeting")]                       // they share the static state of the Meeting: one at a time
[Trait("Blocks", "Yes")]
public class MmmHandSummaryTests
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

    // A meeting of Anna (the convener) and Bruno, who are seated; the Secretary has the minutes. What the ASR Service finds in a Speech Object is its "words" argument.
    private sealed class Room : IAsyncDisposable
    {
        public MInstance M = null!; public Microsoft.AspNetCore.Builder.WebApplication App = null!; public HttpClient Http = null!;
        public MeetingServices Services = null!; public MmmClient Anna = null!, Bruno = null!, Carla = null!;
        public string Words = "we agreed to meet on Friday"; private int n;

        public static async Task<Room> Open()
        {
            var r = new Room();
            r.M = new MInstance(Repository.Schemas);
            Uc2.Setup(r.M); Meeting.Setup(r.M);
            r.App = MmmServer.Build(r.M, "http://127.0.0.1:0");
            await r.App.StartAsync();
            r.Http = new HttpClient { BaseAddress = new Uri(r.App.Urls.First()) };
            r.Services = new MeetingServices(r.Http, r.M, _ => Task.FromResult(r.Words));
            r.Services.Preinvite(["Anna", "Bruno"]);
            async Task<MmmClient> Register(string name) => new(r.Http, name, (await new MmmClient(r.Http, $"{name}-human").PerformAsync("Register", new Complements { With = [Profile(name)] })).Tokens[name]);
            r.Anna = await Register("Anna"); r.Bruno = await Register("Bruno"); r.Carla = await Register("Carla");
            await r.Services.StartAsync();
            foreach (var u in new[] { r.Anna, r.Bruno })
                await u.PerformAsync("MM-Send", Uc2.C($"Join-{u.ProcessID}", to: Meeting.Avs, with: Uc2.Message($"Join-{u.ProcessID}", Meeting.JoinText($"{u.ProcessID}-Persona"))));
            for (var i = 0; i < 4; i++) await r.Services.PumpAsync();
            return r;
        }

        public async Task Send(MmmClient from, string to, string text)
        {
            var id = $"M-{from.ProcessID}-{++n}";
            Assert.Equal(200, (await from.PerformAsync("MM-Send", Uc2.C(id, to: to, with: Uc2.Message(id, text)))).Http);
            await Settle();
        }

        public async Task Settle() { for (var i = 0; i < 3; i++) await Services.PumpAsync(); }

        // Somebody speaks for the seconds given; the words are recognised and reach the minutes.
        public async Task Speak(MmmClient who, double seconds, string? words = null)
        {
            if (words is not null) Words = words;
            var pcm = new byte[(int)(seconds * Speech.Rate) * 2];
            var id = $"Speech-{who.ProcessID}-{++n}";
            Assert.Equal(200, (await who.PerformAsync("MM-Send", Uc2.C(id, to: Meeting.Secretary, with: Speech.Make(id, pcm, 1_767_254_400_000)))).Http);
            await Services.PumpAsync(); await Services.PumpAsrAsync(); await Settle();
        }

        public async Task<string?> HandsSeenBy(MmmClient u) => Meeting.TextOf((await u.ReadAsync($"Hands-for-{u.ProcessID}")).Item);

        public async ValueTask DisposeAsync() { await App.StopAsync(); Http.Dispose(); }
    }

    [Fact]
    public async Task AHandRaisedIsSeenByEveryoneAndNotedInTheMinutes()
    {
        await using var room = await Room.Open();
        Assert.Null(await room.HandsSeenBy(room.Bruno));          // nobody has a hand up: nothing was sent
        await room.Send(room.Bruno, Meeting.Avs, "hand;up");
        Assert.Equal("hands:Bruno", await room.HandsSeenBy(room.Anna));
        Assert.Equal("hands:Bruno", await room.HandsSeenBy(room.Bruno));
        Assert.Contains("AVS: Bruno raised a hand", room.Services.TheMinutes);
        await room.Send(room.Bruno, Meeting.Avs, "hand;down");
        Assert.Equal("hands:", await room.HandsSeenBy(room.Anna));
        Assert.Contains("AVS: Bruno lowered the hand", room.Services.TheMinutes);
    }

    [Fact]
    public async Task OnlyTheConvenerLowersAnotherHand()
    {
        await using var room = await Room.Open();
        await room.Send(room.Bruno, Meeting.Avs, "hand;up");
        await room.Send(room.Anna, Meeting.Avs, "hand;up");
        Assert.Equal(["Bruno", "Anna"], room.Services.HandsUp);
        await room.Send(room.Bruno, Meeting.Avs, "hand;down;user=Anna");              // Bruno is not the convener: this lowers his own hand, not Anna's
        Assert.Equal(["Anna"], room.Services.HandsUp);
        await room.Send(room.Anna, Meeting.Avs, "hand;down;user=Anna");
        Assert.Empty(room.Services.HandsUp);
        await room.Send(room.Bruno, Meeting.Avs, "hand;up");
        await room.Send(room.Anna, Meeting.Avs, "hand;down;user=Bruno");              // the convener lowers it
        Assert.Empty(room.Services.HandsUp);
    }

    [Fact]
    public async Task OnlyTheConvenerLetsAParticipantShareAScreen()
    {
        await using var room = await Room.Open();
        string? Sharing(MmmClient u) => Meeting.TextOf(u.ReadAsync($"Sharing-for-{u.ProcessID}").Result.Item);
        Assert.Null(Sharing(room.Bruno));                                                   // nobody was given anything
        await room.Send(room.Bruno, Meeting.Avs, "share;grant;user=Bruno");                // Bruno is not the convener: refused
        Assert.Null(Sharing(room.Bruno));
        await room.Send(room.Anna, Meeting.Avs, "share;grant;user=Bruno");                 // the convener lets him
        Assert.Equal("granted", Sharing(room.Bruno));
        await room.Send(room.Anna, Meeting.Avs, "share;revoke;user=Bruno");                // and takes it back
        Assert.Equal("revoked", Sharing(room.Bruno));
    }

    [Fact]
    public async Task AHandComesDownByItselfAfterFiveSecondsOfSpeechAndNotBefore()
    {
        await using var room = await Room.Open();
        await room.Send(room.Bruno, Meeting.Avs, "hand;up");
        await room.Speak(room.Anna, 8);                                               // somebody else speaking does nothing to it
        Assert.Equal(["Bruno"], room.Services.HandsUp);
        await room.Speak(room.Bruno, 2);                                              // a short remark does not lower it
        await room.Speak(room.Bruno, 2);
        Assert.Equal(["Bruno"], room.Services.HandsUp);
        await room.Speak(room.Bruno, 1.5);                                            // 5.5 s in all
        Assert.Empty(room.Services.HandsUp);
        Assert.Equal("hands:", await room.HandsSeenBy(room.Anna));
        Assert.Contains("AVS: Bruno lowered the hand (after speaking)", room.Services.TheMinutes);
    }

    [Fact]
    public async Task SpeechWithNoWordsDoesNotCountTowardsLoweringTheHand()
    {
        await using var room = await Room.Open();
        await room.Send(room.Bruno, Meeting.Avs, "hand;up");
        await room.Speak(room.Bruno, 6, "");                                          // noise: no words
        Assert.Equal(["Bruno"], room.Services.HandsUp);
    }

    [Fact]
    public async Task NoSummaryExistsUnlessSomebodyAsksAndThenItGoesToTheParticipants()
    {
        await using var room = await Room.Open();
        var condensed = new List<string>();
        room.Services.Summarise = text => { condensed.Add(text); return Task.FromResult("Friday was agreed."); };
        await room.Speak(room.Anna, 3);
        await room.Speak(room.Bruno, 3, "I will bring the slides");
        Assert.Empty(condensed);                                                      // nobody asked: nothing was made
        await room.Send(room.Bruno, Meeting.Secretary, "summary;lang=en;mode=written");
        await room.Services.PumpSummaryAsync();
        var answer = Assert.Single(await room.Bruno.InboxAsync(), i => i.ID.StartsWith("Summary-for-", StringComparison.Ordinal));
        var reply = JsonNode.Parse(Meeting.TextOf((await room.Bruno.ReadAsync(answer.ID)).Item)!)!;
        Assert.Equal("MMC-SUM-V2.5", (string)reply["Summary"]!["Header"]!);
        Assert.Equal("Friday was agreed.", (string)reply["Summary"]!["SummaryData"]![0]!["Data"]!);
        Assert.Null(reply["TranslatedSummary"]); Assert.Null(reply["Speech"]);
        Assert.Contains("Anna: we agreed to meet on Friday", condensed.Single());
        Assert.Contains("Bruno: I will bring the slides", condensed.Single());
        Assert.DoesNotContain("AVS:", condensed.Single());
        // Anna did not ask, and cannot read Bruno's own answer - but the summary is for the participants: she is sent it too, written, in English as she chose no other language.
        Assert.DoesNotContain(await room.Anna.InboxAsync(), i => i.ID.StartsWith("Summary-for-", StringComparison.Ordinal));
        Assert.Equal(403, (await room.Anna.ReadAsync(answer.ID)).Http);
        var hers = JsonNode.Parse(Meeting.TextOf((await room.Anna.ReadAsync("Summary-Latest-Anna")).Item)!)!;
        Assert.Equal("Bruno", (string)hers["AskedBy"]!); Assert.Equal("en", (string)hers["Language"]!); Assert.Equal("written", (string)hers["Mode"]!);
        Assert.Equal("Friday was agreed.", (string)hers["Summary"]!["SummaryData"]![0]!["Data"]!);
        // Carla is not a participant: she is sent nothing.
        Assert.NotEqual(200, (await room.Carla.ReadAsync("Summary-Latest-Carla")).Http);
    }

    [Fact]
    public async Task EveryParticipantIsSentTheSummaryInItsOwnLanguage()
    {
        await using var room = await Room.Open();
        room.Services.Summarise = _ => Task.FromResult("Friday was agreed.");
        room.Services.Translate = (english, language) => Task.FromResult(language == "el" ? "Η Παρασκευή συμφωνήθηκε." : "Vendredi a été convenu.");
        await room.Speak(room.Anna, 3);
        await room.Send(room.Anna, Meeting.Secretary, "pref;lang=el");                 // Anna reads Greek
        await room.Send(room.Bruno, Meeting.Secretary, "summary;lang=fr;mode=written");   // Bruno asks, in French
        await room.Services.PumpSummaryAsync();
        var hers = JsonNode.Parse(Meeting.TextOf((await room.Anna.ReadAsync("Summary-Latest-Anna")).Item)!)!;
        Assert.Equal("el", (string)hers["Language"]!);
        Assert.Equal("Η Παρασκευή συμφωνήθηκε.", (string)hers["TranslatedSummary"]!["SummaryData"]![0]!["Data"]!);
        var his = JsonNode.Parse(Meeting.TextOf((await room.Bruno.ReadAsync(room.Bruno.InboxAsync().Result.Single(i => i.ID.StartsWith("Summary-for-", StringComparison.Ordinal)).ID)).Item)!)!;
        Assert.Equal("fr", (string)his["Language"]!);
    }

    [Fact]
    public async Task ASummaryIsMadeInEnglishThenTranslatedAndSpokenInTheLanguageChosen()
    {
        await using var room = await Room.Open();
        var translated = new List<(string Text, string Language)>();
        room.Services.Summarise = _ => Task.FromResult("Friday was agreed.");
        room.Services.Translate = (english, language) => { translated.Add((english, language)); return Task.FromResult("Η Παρασκευή συμφωνήθηκε."); };
        var spoken = new List<(string Text, string Language)>();
        room.Services.Synthesise = (text, language) => { spoken.Add((text, language)); return Task.FromResult(new byte[] { 1, 2, 3, 4 }); };
        await room.Speak(room.Anna, 3);

        await room.Send(room.Bruno, Meeting.Secretary, "summary;lang=el;mode=written");
        await room.Send(room.Anna, Meeting.Secretary, "summary;lang=el;mode=spoken");
        await room.Services.PumpSummaryAsync();

        JsonNode ReplyTo(MmmClient who) => JsonNode.Parse(Meeting.TextOf(ReadSummary(room, who))!)!;
        var written = ReplyTo(room.Bruno); var speech = ReplyTo(room.Anna);
        Assert.Equal("el", (string)written["Language"]!);
        Assert.Equal("Friday was agreed.", (string)written["Summary"]!["SummaryData"]![0]!["Data"]!);                 // the Summary is English
        Assert.Equal("Η Παρασκευή συμφωνήθηκε.", (string)written["TranslatedSummary"]!["SummaryData"]![0]!["Data"]!);   // and translated for who asked
        Assert.Null(written["Speech"]);                                              // written: the original voices go on, nothing to read aloud
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }), (string)speech["Speech"]!);
        Assert.All(translated, t => { Assert.Equal("Friday was agreed.", t.Text); Assert.Equal("el", t.Language); });
        Assert.Equal([("Η Παρασκευή συμφωνήθηκε.", "el")], spoken);                // spoken from the translation, in the language
    }

    private static JsonObject ReadSummary(Room room, MmmClient who)
    {
        var id = who.InboxAsync().Result.Single(i => i.ID.StartsWith("Summary-for-", StringComparison.Ordinal)).ID;
        return who.ReadAsync(id).Result.Item!;
    }

    [Fact]
    public async Task AVeryLongMeetingIsSummarisedInPiecesThenThePieces()
    {
        await using var room = await Room.Open();
        var calls = new List<string>();
        room.Services.Summarise = text => { calls.Add(text); return Task.FromResult($"summary {calls.Count}"); };
        for (var i = 0; i < 40; i++) await room.Speak(i % 2 == 0 ? room.Anna : room.Bruno, 1, new string('w', 900) + $" {i}");   // about 36 000 characters of minutes
        await room.Send(room.Bruno, Meeting.Secretary, "summary;lang=en;mode=written");
        await room.Services.PumpSummaryAsync();
        Assert.True(calls.Count > 3, $"{calls.Count} calls");
        Assert.All(calls.Take(calls.Count - 1).Where(c => !c.StartsWith("summary ", StringComparison.Ordinal)), c => Assert.True(c.Length <= MeetingServices.PieceChars + 1000));
        Assert.StartsWith("summary ", calls[^1]);                                     // the last asks to condense the summaries of the pieces
        var reply = JsonNode.Parse(Meeting.TextOf(ReadSummary(room, room.Bruno))!)!;
        Assert.Equal($"summary {calls.Count}", (string)reply["Summary"]!["SummaryData"]![0]!["Data"]!);
    }

    [Fact]
    public async Task ThereIsNoSummaryForWhoIsNotInTheMeetingOrWithoutAService()
    {
        await using var room = await Room.Open();
        await room.Speak(room.Anna, 3);
        await room.Send(room.Bruno, Meeting.Secretary, "summary;lang=en;mode=written");              // there is no summary service
        await room.Send(room.Carla, Meeting.Secretary, "summary;lang=en;mode=written");              // Carla was not seated
        await room.Services.PumpSummaryAsync();
        Assert.StartsWith("refused: this meeting has no summary service", Meeting.TextOf(ReadSummary(room, room.Bruno)));
        Assert.StartsWith("refused: the summary is for the participants", Meeting.TextOf(ReadSummary(room, room.Carla)));
    }
}
