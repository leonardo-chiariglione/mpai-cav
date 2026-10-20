using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Providers;
using Mpai.Rca;
using Mpai.Ua;

namespace Mpai.Aif.Tests;

// THE "ABOUT MPAI" APP: a video, played entirely by the User Agent. The workflow acquires a Basic AudioVisual Object (OSD-BMO-V1.5) that designates
// the video by a DataURI and presents it; the Controller is never asked for anything. The step ends when the device that plays it returns.
public class AboutMpaiTests
{
    [Fact]
    public async Task TheVideoIsPresentedByItsLinkAndTheControllerIsNotAsked()
    {
        var store = new AIF.Store.AmdStore(Repository.Amds); store.Scan();
        using var api = new ControllerApi(Repository.Amds, Path.Combine(Repository.Root, "AIMs", "aim-settings.json"), _ => new CompositeProvider());
        string? played = null;
        var devices = new DeviceRegistry()
            .RegisterAcquire("OSD-BMO-V1.5", (_, wanted) => Task.FromResult(wanted))
            .RegisterPresent("video", data =>
            {
                if (data.TryGetValue("OSD-BMO-V1.5", out var json)) played = AudioVisualLink.UriOf(json);
                return Task.CompletedTask;
            });
        var said = new List<string>();
        var workflow = new Mpai.Wdl.WorkflowReader().Read(File.ReadAllText(Path.Combine(Repository.Root, "Apps", "MAS", "AboutMPAI", "MPAI-AboutMPAI.orch")));
        await new WorkflowInterpreter(api.Async(), devices, line => said.Add(line)).RunAsync(workflow, CancellationToken.None);

        Assert.Equal("https://youtu.be/ITG6fff89yM", played);
        Assert.Equal("ITG6fff89yM", AudioVisualLink.YouTubeId(played!));
        Assert.DoesNotContain(said, l => l.StartsWith("[C]"));
    }

    [Theory]
    [InlineData("https://youtu.be/ITG6fff89yM", "ITG6fff89yM")]
    [InlineData("https://www.youtube.com/watch?v=ITG6fff89yM&t=5", "ITG6fff89yM")]
    [InlineData("https://www.youtube.com/embed/ITG6fff89yM", "ITG6fff89yM")]
    [InlineData("https://example.org/video.mp4", null)]
    public void YouTubeLinks(string uri, string? id) => Assert.Equal(id, AudioVisualLink.YouTubeId(uri));
}
