using Mpai.Mmm;
using Mpai.Mmm.Client;
using Mpai.Mmm.Server;

namespace Mpai.Aif.Tests;

// A MEETING IN THE MMM, END TO END (MMM Phase 6; MMM/Client/MeetingUc.cs). Three bots and the Secretary meet in a room of an M-Instance, through its MMM-API only:
// convening, invitations (no meeting code), seats assigned by the AVS Service, Speech Objects through the Secretary and the ASR Service into the minutes,
// the minutes for the participants only, a participant taken out. Each step is recorded with what it gave; the record is the expected file.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class MmmMeetingUseCaseTests
{
    [Fact]
    public async Task ThreeParticipantsAndTheSecretaryHoldAMeeting()
    {
        var m = new MInstance(Repository.Schemas);
        Uc2.Setup(m);
        Meeting.Setup(m);
        await using var app = MmmServer.Build(m, "http://127.0.0.1:0");
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        var services = new MeetingServices(http, m, MeetingUseCase.ScriptedRecognizer());

        var result = await MeetingUseCase.RunAsync(http, m, services);
        await app.StopAsync();
        Expected.Match("mmm-meeting-usecase.json", result);
    }
}
