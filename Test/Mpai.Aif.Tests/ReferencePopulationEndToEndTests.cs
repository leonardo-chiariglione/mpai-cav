using System.Text.Json;
using System.Text.Json.Nodes;

using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Core;
using Mpai.Providers;

namespace Mpai.Aif.Tests;

// THE REFERENCE POPULATION, END TO END: FIR and SIR run as Modules of the Controller with the models and the reference
// features taken from Access - the face detector, the face recognition model, the speaker model, and the features of 5,731
// faces and 40 voices - and recognise a person of that population from a photograph or a speech that is not the one it was
// made from. Nobody is enrolled: what is recognised is only in Access.
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
public class ReferencePopulationEndToEndTests
{
    private const string Fir = "1PAF-FIR-V1.6-I01", Sir = "1MMC-SIR-V2.5-I01";
    private static readonly string Lfw = Path.Combine(MpaiPaths.TestData, "Images", "lfw");
    private static readonly string Libri = MpaiPaths.Clips;

    // The reference features are obtained from the address their L3 gives, on GitHub. Until the files are pushed there is nothing there, and the
    // tests wait for it.
    private static bool Published(string aim, string entry)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository.Amds, aim + ".json")));
        var uri = AIF.Controller.AccessEntry.ReadFrom(doc.RootElement).First(e => e.Name == entry).Uri;
        try { using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) }; using var r = http.Send(new HttpRequestMessage(HttpMethod.Head, uri)); return r.IsSuccessStatusCode; }
        catch (Exception) { return false; }
    }

    // A photograph as a camera gives it: the tight crop of LFW (250 x 250) set in a larger frame, which is what the face detector
    // is made for - it finds no face in a crop that is only a face.
    private static byte[] InAFrame(string file)
    {
        using var face = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgb24>(file);
        using var frame = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(640, 640, new SixLabors.ImageSharp.PixelFormats.Rgb24(120, 120, 120));
        SixLabors.ImageSharp.Processing.ProcessingExtensions.Mutate(face, x => SixLabors.ImageSharp.Processing.ResizeExtensions.Resize(x, 300, 300));
        SixLabors.ImageSharp.Processing.ProcessingExtensions.Mutate(frame, x => SixLabors.ImageSharp.Processing.DrawImageExtensions.DrawImage(x, face, new SixLabors.ImageSharp.Point(170, 170), 1f));
        using var ms = new MemoryStream();
        SixLabors.ImageSharp.ImageExtensions.SaveAsJpeg(frame, ms);
        return ms.ToArray();
    }

    private static string? Label(string? identifier)
    {
        static string? Find(JsonNode? n) => n switch
        {
            JsonObject o => o.TryGetPropertyValue("InstanceLabel", out var l) && l is JsonValue v && v.TryGetValue<string>(out var s) ? s : o.Select(p => Find(p.Value)).FirstOrDefault(x => x is not null),
            JsonArray a => a.Select(Find).FirstOrDefault(x => x is not null),
            _ => null
        };
        return identifier is null ? null : Find(JsonNode.Parse(identifier));
    }

    [SkippableFact]
    public void ThePeopleOfTheReferenceFacesAreRecognised()
    {
        Skip.IfNot(Directory.Exists(Lfw) && Published(Fir, "reference-faces"), "The LFW images are absent, or the reference faces are not published yet (their L3 gives the address on GitHub).");
        var gallery = Path.Combine(Path.GetTempPath(), "mpai-ref-gallery-" + Guid.NewGuid().ToString("N"));
        using var api = new ControllerApi(Repository.Amds, Path.Combine(Repository.Root, "AIMs", "aim-settings.json"), store => new MacProvider(store));
        try
        {
            Assert.Equal(AifError.OK, api.StartFlow(Fir));
            api.SharedStorageInit(Fir, gallery);
            var people = new[] { "George_W_Bush", "Colin_Powell", "Tony_Blair", "Donald_Rumsfeld", "Gerhard_Schroeder", "Ariel_Sharon", "Hugo_Chavez", "Jean_Chretien" };
            var right = new List<string>();
            var wrong = new List<string>();
            foreach (var person in people)
            {
                var photo = Directory.EnumerateFiles(Path.Combine(Lfw, person), "*.jpg").OrderBy(f => f, StringComparer.Ordinal).ElementAt(1);   // not the first
                var json = MpaiJson.ToJson(BasicVisualObject.FromFile(Path.GetFileName(photo), InAFrame(photo), "Face"));
                var r = api.Advance(Fir, [new("OSD-BVO-V1.5", 1, json)]);
                Assert.Equal(AifError.OK, r.Error);
                var label = Label(r.ByType("OSD-IID-V1.5"));
                (label == person ? right : wrong).Add($"{person} -> {label ?? "nobody"}");
            }
            Console.WriteLine($"FACES right {right.Count}/{people.Length}; wrong: {string.Join("; ", wrong)}");
            Assert.True(right.Count >= people.Length - 1, $"recognised {right.Count} of {people.Length}; wrong: {string.Join("; ", wrong)}");
        }
        finally { api.StopFlow(Fir); try { Directory.Delete(gallery, true); } catch { } }
    }

    [SkippableFact]
    public void TheSpeakersOfTheReferenceVoicesAreRecognised()
    {
        Skip.IfNot(Directory.Exists(Libri) && Published(Sir, "reference-voices"), "The LibriSpeech clips are absent, or the reference voices are not published yet (their L3 gives the address on GitHub).");
        var gallery = Path.Combine(Path.GetTempPath(), "mpai-ref-gallery-" + Guid.NewGuid().ToString("N"));
        using var api = new ControllerApi(Repository.Amds, Path.Combine(Repository.Root, "AIMs", "aim-settings.json"), store => new MacProvider(store));
        try
        {
            Assert.Equal(AifError.OK, api.StartFlow(Sir));
            api.SharedStorageInit(Sir, gallery);
            var speakers = Directory.EnumerateFiles(Libri, "clip*.wav").Select(f => Path.GetFileNameWithoutExtension(f)["clip".Length..]).OrderBy(s => s, StringComparer.Ordinal).Take(6).ToList();
            var right = new List<string>();
            var wrong = new List<string>();
            foreach (var speaker in speakers)
            {
                var speech = BasicSpeechObject.FromData(File.ReadAllBytes(Path.Combine(Libri, "clip" + speaker + ".wav")), new SpeechQualifier
                {
                    SpeechQualifierID = Guid.NewGuid().ToString(),
                    Format = new SpeechFormat
                    {
                        ContentFormats = new SpeechContentFormats { RawData = new Pcm { SamplingFrequency = 16000, Precision = 16 } },
                        TransportFormats = new SpeechTransportFormats { FileFormat = SpeechFileFormat.Wav }
                    },
                    Attributes = new SpeechAttributes { Metadata = new SpeechMetadata { Language = new Language { LanguageCode = "en", LanguageFormat = LanguageFormat.Iso639_1 } } }
                });
                var r = api.Advance(Sir, [new("OSD-BSO-V1.5", 1, MpaiJson.ToJson(speech))]);
                Assert.Equal(AifError.OK, r.Error);
                var label = Label(r.ByType("OSD-IID-V1.5"));
                ("spk" + speaker == label ? right : wrong).Add($"spk{speaker} -> {label ?? "nobody"}");
            }
            Console.WriteLine($"VOICES right {right.Count}/{speakers.Count}; wrong: {string.Join("; ", wrong)}");
            Assert.True(right.Count >= speakers.Count - 1, $"recognised {right.Count} of {speakers.Count}; wrong: {string.Join("; ", wrong)}");
        }
        finally { api.StopFlow(Sir); try { Directory.Delete(gallery, true); } catch { } }
    }
}
