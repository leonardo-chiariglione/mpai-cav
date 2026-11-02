using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;

using Mpai.Mmm;
using Mpai.Mmm.Client;
using Mpai.Mmm.Server;

namespace Mpai.Aif.Tests;

// A USER'S SPEECH THROUGH UM-CAPTURE (MMM-TEC V2.2, the Verification Use Cases: Lecture, Hybrid working). A human's Device UM-Captures speech at its U-Location With the Speech Qualifier,
// the Data is Identified as an Item - here a Speech Object - and the User MM-Adds it at its seat. The M-Instance gives no Item for Data that was only captured, and refuses what the
// Qualifier does not make possible.
[Trait("Group", "Fast")]
[Collection("MmmMeeting")]
public class MmmSpeechCaptureTests
{
    private static async Task<(MInstance M, WebApplication App, HttpClient Http, MmmClient Anna, MmmClient Bruno)> Open()
    {
        var m = new MInstance(Repository.Schemas);
        Uc2.Setup(m); Meeting.Setup(m);
        var app = MmmServer.Build(m, "http://127.0.0.1:0");
        await app.StartAsync();
        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        MmmClient anna = null!, bruno = null!;
        foreach (var name in new[] { "Anna", "Bruno" })
        {
            var reg = await new MmmClient(http, $"{name}-human").PerformAsync("Register", new Complements
            {
                With = [new JsonObject
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
                }]
            });
            var user = new MmmClient(http, name, reg.Tokens[name]);
            if (name == "Anna") anna = user; else bruno = user;
        }
        return (m, app, http, anna, bruno);
    }

    private static JsonObject Q(string id) => Speech.StreamQualifier(id);

    [Fact]
    public async Task CapturedDataIsNotAnItemUntilItIsIdentified_AndThenItIsASpeechObject()
    {
        var (m, app, http, anna, bruno) = await Open();
        await using var _ = app; using var __ = http;
        var q = Q("Speech-Anna-1");
        Assert.Equal(201, (await anna.PerformAsync("UM-Capture", Uc2.C("Speech-Anna-1", at: "ULoc-Anna", with: q))).Http);
        Assert.False(m.IsStream("Speech-Anna-1"));                                          // Data, not yet an Item of the M-Instance: nobody feeds it
        var again = await anna.PerformAsync("UM-Capture", Uc2.C("Speech-Anna-1", at: "ULoc-Anna", with: q.DeepClone()));
        Assert.Equal(409, again.Http);                                                      // Clash: the Data exists

        var identified = await anna.PerformAsync("Identify", Uc2.C("Speech-Anna-1", with: q.DeepClone()));
        Assert.Equal(201, identified.Http);
        Assert.Equal("Speech-Anna-1", identified.Response.Complements!.Nil);                // its Identifier is the one the Data was named with
        Assert.True(m.IsStream("Speech-Anna-1"));
        var item = (await anna.ReadAsync("Speech-Anna-1")).Item!;
        Assert.Null(item["ItemValueOrRef"]![0]!["BasicSpeechObjectTime"]);                  // a stream has no duration: the Speech Object has no Time until the stream is over
        Assert.Equal(0, Speech.DurationMs(item));
    }

    [Fact]
    public async Task OnlyTheRequesterIdentifiesItsData_AndTheQualifierMustSayItIsAStream()
    {
        var (m, app, http, anna, bruno) = await Open();
        await using var _ = app; using var __ = http;
        await anna.PerformAsync("UM-Capture", Uc2.C("Speech-Anna-1", at: "ULoc-Anna", with: Q("Speech-Anna-1")));
        Assert.Equal(403, (await bruno.PerformAsync("Identify", Uc2.C("Speech-Anna-1", with: Q("Speech-Anna-1")))).Http);   // InsRights
        Assert.Equal(404, (await anna.PerformAsync("Identify", Uc2.C("Speech-Nobody", with: Q("Speech-Nobody")))).Http);    // IncID

        var still = Q("Speech-Anna-2"); still["Formats"]!["TransportFormats"] = new JsonObject();
        await anna.PerformAsync("UM-Capture", Uc2.C("Speech-Anna-2", at: "ULoc-Anna", with: still));
        var refused = await anna.PerformAsync("Identify", Uc2.C("Speech-Anna-2", with: still.DeepClone()));
        Assert.Equal(422, refused.Http);                                                    // IncDQ: no stream format, no way for the data to arrive
        Assert.False(m.IsStream("Speech-Anna-2"));
    }

    [Fact]
    public async Task ASpeechCapturedIsPlacedAtAnMLocationAndItsDataIsComplete()
    {
        var (m, app, http, anna, bruno) = await Open();
        await using var _ = app; using var __ = http;
        var pcm = new byte[16000 * 2];
        for (var i = 0; i < pcm.Length; i += 2) pcm[i] = (byte)(i / 2 % 50);
        var added = await Speech.CaptureAsync(anna, "Speech-Anna-9", "ULoc-Anna", "MVS", pcm);
        Assert.Equal(200, added.Http);
        Assert.False(m.IsStream("Speech-Anna-9"));                                          // the stream is over: the data is the Speech Object's
        var done = (await anna.ReadAsync("Speech-Anna-9")).Item;
        Assert.Equal(pcm.Length, Speech.PcmOf(done).Length);
        Assert.InRange(Speech.DurationMs(done), 1, 5000);                                    // the stream is over: its duration is in the Speech Object now, as its own Time
    }
}
