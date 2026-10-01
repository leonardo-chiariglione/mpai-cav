using Mpai.Mmm;
using Mpai.Mmm.Client;
using Mpai.Mmm.Server;

namespace Mpai.Aif.Tests;

// USE CASE 2 OF MMM-TEC V2.2, "FRIENDS MEET IN THE METAVERSE" (Verification Use
// Cases, 2), END TO END: an M-Instance behind its MMM-API, the Processes of the Use
// Case as clients of it, the 19 steps of its workflow in order (MMM/Client/Uc2.cs).
// Each step is recorded with the HTTP status and PA Status it got, and with what can
// be seen of the M-Environment after it - where the Personae are, which Locations are
// perceptible - which is what the viewer draws. Recorded too are the checks the
// workflow implies: Friend2 may not enter the Room before Friend1 grants access, nor
// after Friend1 revokes it; a retried Request is not performed twice; Friend2 reads
// the invitation sent to it, and a Process that is not its recipient does not.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class MmmUc2Tests
{
    [Fact]
    public async Task FriendsMeetInTheMetaverse()
    {
        var m = new MInstance(Repository.Schemas);
        Uc2.Setup(m);
        await using var app = MmmServer.Build(m, "http://127.0.0.1:0");
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        var seen = new Dictionary<string, string>();
        var result = await Uc2.RunAsync(http, m, (step, _) => { seen[$"{step} - seen"] = m.View(); return Task.CompletedTask; });
        foreach (var (k, v) in seen) result[k] = v;
        await app.StopAsync();
        Expected.Match("mmm-uc2.json", result);
    }
}
