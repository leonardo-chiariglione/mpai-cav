using Mpai.Aif.Api;

namespace Mpai.Aif.Tests;

// THE PICTURE A PERSON ASKS ABOUT, ONLINE: two routes.
// Route 1: the person provides a picture of their own: the User Agent shows it and hands it to the Controller as a Visual Object (the workflow's own
// "acquire Picture" / "offer Picture"); the Controller passes it to AMQ.
// Route 2: the Service offers a catalogue (Test/Data/Images); the person chooses one by name, and the Controller supplies the picture to AMQ.
// ONLY THE CONTROLLER-SIDE CATALOGUE IS BUILT AND TESTED HERE: the way the User Agent lists the catalogue and sends the name to the Controller is not in the
// Controller API (nor in the routes of MPAI-MAS, nor in a Port of the AMQ Module), so it is not wired; see MKG/pending-changes.md.
public class AmqPictureRoutesTests
{
    private static string Images => Path.Combine(Repository.Root, "Test", "Data", "Images");
    private static ImageCatalogueHolder Catalogue() => new(new Mpai.Core.ImageCatalogue(Images, new[] { "Monuments", "Animals" }));
    private sealed record ImageCatalogueHolder(Mpai.Core.ImageCatalogue C);

    // Runs the picture-question workflow once, as one person: the device gives this picture and this question; returns what the Controller was given.
    private static async Task<IReadOnlyList<ControllerApi.Datum>> AskAbout(Mpai.Core.BasicVisualObject picture, string question)
    {
        var log = new List<string>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var api = new ScriptedController(log, (turn, _) => new[] { new ControllerApi.Datum("PAF-PAV-V1.6", 1, AppWorkflow.PortableAvatar("answer")) });
        var yesNo = new Queue<string>(new[] { "yes", "no" });
        var devices = new Mpai.Ua.DeviceRegistry()
            .RegisterAcquire("OSD-BVO-V1.5", (_, _) => Task.FromResult<string?>(Mpai.Core.MpaiJson.ToJson(picture)))
            .RegisterAcquire("OSD-BSO-V1.5", (_, _, abandon) => AppWorkflow.NothingUntil(abandon, stop.Token))
            .RegisterAcquire("OSD-BTO-V1.5", (_, wanted, _) =>
                Task.FromResult<string?>(Mpai.Core.MpaiJson.ToJson(Mpai.Core.BasicTextObject.FromText((wanted ?? "").Contains("YesNo") ? yesNo.Dequeue() : question))))
            .RegisterPresent("stage", _ => Task.CompletedTask)
            .RegisterPresent("screen", _ => Task.CompletedTask)
            .RegisterPresent("avatar", _ => Task.CompletedTask);
        await AppWorkflow.RunAsync(AppWorkflow.Load("AMQ"), api, devices, stop.Token);
        Assert.Single(api.Turns);
        return api.Turns[0];
    }

    private static Mpai.Core.BasicVisualObject Offered(IReadOnlyList<ControllerApi.Datum> turn) =>
        Mpai.Core.MpaiJson.FromJson<Mpai.Core.BasicVisualObject>(turn.Single(d => d.DataType == "OSD-BVO-V1.5").Json)!;

    // Route 1: a picture of the person's own, with a question about it.
    [Fact]
    public async Task ThePersonProvidesAPictureOfTheirOwnAndAsks()
    {
        var path = Path.Combine(Images, "Monuments", "eiffel-tower", "01-pixabay-1853365.jpg");
        if (!File.Exists(path)) return;   // the test pictures are obtained separately
        var own = Mpai.Core.BasicVisualObject.FromFile("my-photo.jpg", File.ReadAllBytes(path), "Picture");

        var turn = await AskAbout(own, "What is this?");

        Assert.Equal(own.Data, Offered(turn).Data);
        Assert.Contains(turn, d => d.DataType == "OSD-BTO-V1.5" && d.PortNumber == 1 && d.Json.Contains("What is this?"));
    }

    // Route 2: the person chooses the Monuments picture by name; the Controller supplies it and the question is about it.
    [Fact]
    public async Task ThePersonChoosesAMonumentByNameAndAsks()
    {
        var catalogue = Catalogue().C;
        if (catalogue.List("Monuments").Count == 0) return;   // the test pictures are obtained separately

        Assert.Contains("Monuments", catalogue.Collections());
        Assert.Contains("Monuments/eiffel-tower/01-pixabay-1853365.jpg", catalogue.List("Monuments"));

        var chosen = catalogue.Supply("Monuments/eiffel-tower/01-pixabay-1853365.jpg");
        Assert.NotNull(chosen);
        Assert.Equal("01-pixabay-1853365.jpg", chosen!.FileName);
        Assert.Equal(File.ReadAllBytes(Path.Combine(Images, "Monuments", "eiffel-tower", "01-pixabay-1853365.jpg")), chosen.Data);

        // a folder's name stands for the first picture in it; the collection's name for the first of the collection
        Assert.Equal("01-pixabay-1853365.jpg", catalogue.Supply("Monuments/eiffel-tower")!.FileName);
        Assert.NotNull(catalogue.Supply("Monuments"));

        var turn = await AskAbout(chosen, "Which monument is this?");
        Assert.Equal(chosen.Data, Offered(turn).Data);
    }

    // The catalogue offers only what it was given, and nothing outside it.
    [Fact]
    public void TheCatalogueOffersOnlyItsCollectionsAndNothingOutsideThem()
    {
        var catalogue = Catalogue().C;
        Assert.Null(catalogue.Supply("Faces"));
        Assert.Null(catalogue.Supply("Faces/anything.jpg"));
        Assert.Null(catalogue.Supply("../README.md"));
        Assert.Null(catalogue.Supply("Monuments/../README.md"));
        Assert.Null(catalogue.Supply(@"..\..\Images\README.md"));
        Assert.Null(catalogue.Supply(""));
        Assert.Null(catalogue.Supply("no-such-picture"));
        Assert.DoesNotContain(catalogue.List(), n => n.StartsWith("Faces/") || n.StartsWith("People/") || n.EndsWith(".md"));
    }

    // Each person has their own picture: what the Controller is given for A is A's, and B's turn holds only B's.
    [Fact]
    public async Task OnePersonsPictureIsNeverUsedForAnother()
    {
        var catalogue = Catalogue().C;
        var a = catalogue.Supply("Monuments/eiffel-tower");
        var b = catalogue.Supply("Monuments/taj-mahal");
        if (a is null || b is null) return;   // the test pictures are obtained separately
        Assert.NotEqual(a.Data, b.Data);

        var forA = AskAbout(a, "What is this?");
        var forB = AskAbout(b, "What is this?");
        await Task.WhenAll(forA, forB);

        Assert.Equal(a.Data, Offered(forA.Result).Data);
        Assert.Equal(b.Data, Offered(forB.Result).Data);
        Assert.Single(forB.Result, d => d.DataType == "OSD-BVO-V1.5");
    }
}
