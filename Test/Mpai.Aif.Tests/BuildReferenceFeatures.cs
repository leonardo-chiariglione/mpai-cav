using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

using AIF.Controller;
using AIF.SharedStorage;
using Mpai.Core;
using Mpai.Osd.VisualScene;
using Mpai.Paf.Fir;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Mpai.Aif.Tests;

// THE REFERENCE FACES, MADE FROM THE PUBLIC DATABASE. The tool that makes reference-faces.features, so that the file can be made
// again by anyone: the face detector and the face recognition model are the ones the L3 of FIR declares (obtained through Access,
// checked by their SHA-256), the database is Labeled Faces in the Wild (lfw.tgz, 13,233 images of 5,749 people) and the vector of a
// person is the mean of the embeddings of the faces of that person, normalised. Each picture goes the way a probe does in FIR:
// set in a frame (the database holds tight crops, in which the detector finds nothing), the face found, cropped, embedded; a picture
// in which no face is found is embedded whole.
//
// The database is Access data too (ReferenceDatabases.access.json): the Controller obtains lfw.tgz from its URI, checks its SHA-256 and does
// not download it again when it already holds it. Run with MPAI_BUILD_REFERENCE=1; it writes AIMs/PAF/V1.6/FIR/Data/reference-faces.features.
[Trait("Group", "Tools")]
[Trait("Blocks", "Yes")]
public class BuildReferenceFeatures
{
    private const string LfwUrl = "https://ndownloader.figshare.com/files/5976018";

    private static byte[] InAFrame(byte[] jpeg)
    {
        using var face = Image.Load<Rgb24>(jpeg);
        using var frame = new Image<Rgb24>(640, 640, new Rgb24(120, 120, 120));
        face.Mutate(x => x.Resize(300, 300));
        frame.Mutate(x => x.DrawImage(face, new Point(170, 170), 1f));
        using var ms = new MemoryStream();
        frame.SaveAsJpeg(ms);
        return ms.ToArray();
    }

    [SkippableFact]
    public void ReferenceFacesFromLabeledFacesInTheWild()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("MPAI_BUILD_REFERENCE") == "1", "A tool: set MPAI_BUILD_REFERENCE=1 to run it.");
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository.Amds, "1PAF-FIR-V1.6-I01.json")));
        using var databases = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository.Root, "Test", "Mpai.Aif.Tests", "ReferenceDatabases.access.json")));
        var entries = AccessEntry.ReadFrom(doc.RootElement).Where(e => e.Name is "face-detector" or "face-embedder")
            .Concat(AccessEntry.ReadFrom(databases.RootElement).Where(e => e.Name == "lfw")).ToList();
        Assert.Equal("", AccessEntry.Check(entries));
        var store = new AccessStore(MpaiPaths.Access);
        var fetcher = new AccessFetcher(store);
        foreach (var e in entries) Assert.Equal(AccessOutcome.OK, fetcher.Ensure(e, "reference-builder", out var why));
        var access = new BoundAccess(store.As(new StorageHolder("builder", "reference-builder")), entries, fetcher, "reference-builder");
        var models = AccessEntry.Overlay(new Dictionary<string, string>(), entries, access, "reference-builder");
        var tgz = models["LfwArchive"];
        var tgzSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(tgz)));

        using var detector = new ScrfdFaceDetector(models["ScrfdModel"]);
        using var arc = new ArcFaceRecogniser(models["ArcFaceModel"]);

        var sums = new SortedDictionary<string, double[]>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>();
        int images = 0, whole = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using (var gz = new GZipStream(File.OpenRead(tgz), CompressionMode.Decompress))
        using (var tar = new TarReader(gz))
            while (tar.GetNextEntry() is { } entry)
            {
                if (entry.EntryType != TarEntryType.RegularFile || !entry.Name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) continue;
                var person = entry.Name.Split('/')[^2];
                using var ms = new MemoryStream();
                entry.DataStream!.CopyTo(ms);
                var bytes = ms.ToArray();
                var framed = InAFrame(bytes);
                float[] embedding;
                var faces = detector.Detect(framed);
                if (faces.Count > 0)
                {
                    var face = faces.OrderByDescending(f => (f.X2 - f.X1) * (f.Y2 - f.Y1)).First();
                    using var crop = FaceCrop.Crop(framed, face.X1, face.Y1, face.X2, face.Y2);
                    embedding = arc.Embed(crop);
                }
                else { embedding = arc.Embed(bytes); whole++; }
                if (!sums.TryGetValue(person, out var sum)) sums[person] = sum = new double[embedding.Length];
                for (var i = 0; i < sum.Length; i++) sum[i] += embedding[i];
                counts[person] = counts.GetValueOrDefault(person) + 1;
                if (++images % 500 == 0) File.AppendAllText(Path.Combine(Path.GetTempPath(), "reference-faces-progress.txt"), $"{images} images, {sums.Count} people, {clock.Elapsed.TotalMinutes:0.0} min{Environment.NewLine}");
            }

        var people = sums.Select(p =>
        {
            var norm = Math.Sqrt(p.Value.Sum(x => x * x));
            return (p.Key, p.Value.Select(x => (float)(x / norm)).ToArray());
        }).ToList();
        var header = new ReferenceFeaturesHeader("face", "ArcFace glintr100 (InsightFace antelopev2)", 512, "cosine", people.Count,
            $"Labeled Faces in the Wild, lfw.tgz (SHA-256 {tgzSha}, {LfwUrl}): per person the normalised mean of the ArcFace embeddings of the faces found by SCRFD in its pictures; {whole} of {images} pictures were embedded whole, no face being found");
        var output = Path.Combine(Repository.Root, "AIMs", "PAF", "V1.6", "FIR", "Data", "reference-faces.features");
        if (File.Exists(output)) File.Move(output, output + ".previous", true);
        using (var to = File.Create(output)) ReferenceFeatures.Write(to, header, people);
        var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output)));
        File.WriteAllText(Path.Combine(Repository.Root, "AIMs", "PAF", "V1.6", "FIR", "Data", "reference-faces.build.txt"),
            $"people {people.Count}{Environment.NewLine}images {images}{Environment.NewLine}embedded whole {whole}{Environment.NewLine}sha256 {sha}{Environment.NewLine}minutes {clock.Elapsed.TotalMinutes:0.0}{Environment.NewLine}");
        Console.WriteLine($"BUILT people {people.Count} images {images} whole {whole} sha {sha} minutes {clock.Elapsed.TotalMinutes:0.0}");
    }
}
