using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

using AIF.Controller;
using AIF.SharedStorage;
using Mpai.Cae.Usc;
using Mpai.Core;
using Mpai.Mmc.Sir;
using Mpai.Osd.Speech;

namespace Mpai.Aif.Tests;

// THE REFERENCE VOICES, MADE FROM THE PUBLIC DATABASE. The tool that makes reference-voices.features, so that the file can be made again by
// anyone: the speaker recognition model is the one the L3 of SIR declares and the database is LibriSpeech dev-clean (40 speakers), both
// obtained through Access (checked by their SHA-256). The database holds FLAC, which the C# code cannot decode: flac_to_wav.py, run with the
// Python of MpaiPaths.Python, writes the first utterances of each speaker as WAV. The vector of a speaker is the mean of the embeddings of
// those utterances, normalised.
//
// Run with MPAI_BUILD_REFERENCE=1; it writes AIMs/MMC/V2.5/SIR/Data/reference-voices.features (the previous one is kept as .previous).
[Trait("Group", "Tools")]
[Trait("Blocks", "Yes")]
public class BuildReferenceVoices
{
    private const int UtterancesPerSpeaker = 10;

    [SkippableFact]
    public void ReferenceVoicesFromLibriSpeech()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("MPAI_BUILD_REFERENCE") == "1", "A tool: set MPAI_BUILD_REFERENCE=1 to run it.");
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository.Amds, "1MMC-SIR-V2.5-I01.json")));
        using var databases = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository.Root, "Test", "Mpai.Aif.Tests", "ReferenceDatabases.access.json")));
        var entries = AccessEntry.ReadFrom(doc.RootElement).Where(e => e.Name == "speaker-embedder")
            .Concat(AccessEntry.ReadFrom(databases.RootElement).Where(e => e.Name == "librispeech-dev-clean")).ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal("", AccessEntry.Check(entries));
        var store = new AccessStore(MpaiPaths.Access);
        var fetcher = new AccessFetcher(store);
        foreach (var e in entries) Assert.Equal(AccessOutcome.OK, fetcher.Ensure(e, "reference-builder", out _));
        var access = new BoundAccess(store.As(new StorageHolder("builder", "reference-builder")), entries, fetcher, "reference-builder");
        var paths = AccessEntry.Overlay(new Dictionary<string, string>(), entries, access, "reference-builder");
        var archive = paths["LibriSpeechDevClean"];
        var archiveSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive)));

        var wavs = Path.Combine(Repository.Root, "Test", "Results", "ReferenceVoices");
        if (Directory.Exists(wavs)) Directory.Delete(wavs, true);
        var script = Path.Combine(Repository.Root, "Test", "Mpai.Aif.Tests", "flac_to_wav.py");
        var start = new ProcessStartInfo(MpaiPaths.Python) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { script, archive, wavs, UtterancesPerSpeaker.ToString() }) start.ArgumentList.Add(a);
        using (var python = Process.Start(start)!)
        {
            var output = python.StandardOutput.ReadToEnd(); var error = python.StandardError.ReadToEnd();
            python.WaitForExit();
            Assert.True(python.ExitCode == 0, error);
            Console.WriteLine(output);
        }

        using var embedder = new SpeakerEmbedder(paths["EcapaModel"]);
        var people = new List<(string Id, float[] Vector)>();
        foreach (var folder in Directory.GetDirectories(wavs).OrderBy(f => f, StringComparer.Ordinal))
        {
            var sum = new double[192]; var n = 0;
            foreach (var file in Directory.GetFiles(folder, "*.wav").OrderBy(f => f, StringComparer.Ordinal))
            {
                var audio = Audio.FromWav(File.ReadAllBytes(file));
                var e = embedder.Embed(audio.Samples.Select(s => s / 32768f).ToArray());
                for (var i = 0; i < sum.Length; i++) sum[i] += e[i];
                n++;
            }
            var norm = Math.Sqrt(sum.Sum(x => x * x));
            people.Add(("spk" + Path.GetFileName(folder), sum.Select(x => (float)(x / norm)).ToArray()));
        }

        var header = new ReferenceFeaturesHeader("voice", "ECAPA-TDNN", 192, "cosine", people.Count,
            $"LibriSpeech dev-clean, OpenSLR 12 (SHA-256 {archiveSha}, https://www.openslr.org/resources/12/dev-clean.tar.gz): per speaker the normalised mean of the ECAPA-TDNN embeddings of the first {UtterancesPerSpeaker} utterances in the archive");
        var target = Path.Combine(Repository.Root, "AIMs", "MMC", "V2.5", "SIR", "Data", "reference-voices.features");
        if (File.Exists(target)) File.Move(target, target + ".previous", true);
        using (var to = File.Create(target)) ReferenceFeatures.Write(to, header, people);
        Console.WriteLine($"BUILT speakers {people.Count} sha {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target)))}");
    }
}
