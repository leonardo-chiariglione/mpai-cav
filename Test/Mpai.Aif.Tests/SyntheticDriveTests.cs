using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using AIF.SharedStorage;
using Mpai.Aif.PortData;
using Mpai.Cav.Recordings;
using Mpai.Rca;

namespace Mpai.Aif.Tests;

// THE FIRST RECORDINGS (M3219 3.6): a synthetic drive of the inputs of ESS
// Stage 1, made from a seed - GNSS Objects, Spatial Attitude, the frames of a
// forward camera - every Object valid against its schema, with the ground truth
// it was made from.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class SyntheticDriveTests
{
    private static string Hash(IEnumerable<DriveMessage> messages) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", messages.Select(m => $"{m.At.Ticks} {m.DataType} {m.Json}")))))[..16];

    [Fact]
    public async Task Drive()
    {
        var result = new Dictionary<string, string>();
        var drive = new SyntheticDrive(7, TimeSpan.FromSeconds(4));
        var (messages, truth) = drive.Make();

        result["the Messages of 4 s"] = string.Join("; ", messages.GroupBy(m => m.DataType).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"));
        result["in the order of their times"] = messages.Zip(messages.Skip(1)).All(p => p.First.At <= p.Second.At) ? "yes" : "no";

        // Every Object against the schema of its Data Type.
        foreach (var type in messages.Select(m => m.DataType).Distinct().Order())
        {
            var invalid = messages.Where(m => m.DataType == type)
                .Select(m => PortDataSchema.Violations(type, m.Json))
                .Where(v => v is null || v.Count > 0).ToList();
            result[$"{type} against its schema"] = invalid.Count == 0 ? "every one valid"
                : $"{invalid.Count} not: " + string.Join("; ", invalid[0] ?? ["no schema"]);
        }

        result["the same seed, the same drive"] = Hash(new SyntheticDrive(7, TimeSpan.FromSeconds(4)).Make().Messages) == Hash(messages) ? "yes" : "no";
        result["another seed, another drive"] = Hash(new SyntheticDrive(8, TimeSpan.FromSeconds(4)).Make().Messages) != Hash(messages) ? "yes" : "no";

        // The frames: PNG, 320 x 180.
        var frame = Convert.FromBase64String(JsonDocument.Parse(messages.First(m => m.DataType == SyntheticDrive.Camera).Json)
            .RootElement.GetProperty("BasicVisualObjectData")[0].GetProperty("Data").GetString()!);
        var width = (frame[16] << 24) | (frame[17] << 16) | (frame[18] << 8) | frame[19];
        var height = (frame[20] << 24) | (frame[21] << 16) | (frame[22] << 8) | frame[23];
        result["a frame"] = (frame.Take(8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) ? "PNG" : "not PNG") + $", {width} x {height}";
        var sizes = messages.Where(m => m.DataType == SyntheticDrive.Camera).Select(m => m.Json.Length).ToList();

        // The GNSS data: NMEA sentences with their checksums.
        var sentences = messages.Where(m => m.DataType == SyntheticDrive.Gnss)
            .SelectMany(m => Encoding.ASCII.GetString(Convert.FromBase64String(JsonDocument.Parse(m.Json).RootElement.GetProperty("GNSSData")[0].GetProperty("Data").GetString()!))
                .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)).ToList();
        result["the GNSS data"] = $"{sentences.Count} NMEA sentences, " + (sentences.All(Nmea.Valid) ? "every checksum right" : "checksums wrong") +
                                  $", {sentences.Count(s => s.StartsWith("$GPGGA"))} GGA, {sentences.Count(s => s.StartsWith("$GPRMC"))} RMC";

        // The ground truth: the vehicle ahead nearer than 15 m in the middle of the
        // drive, and larger on the image the nearer it is.
        var frames = truth["Frames"]!.AsArray();
        result["the ground truth"] = $"{frames.Count} frames; near from {truth["Near"]?["From"]} s to {truth["Near"]?["To"]} s";
        var nearest = frames.OrderBy(f => (double)f!["AheadDistance"]!).First()!;
        var farthest = frames.OrderByDescending(f => (double)f!["AheadDistance"]!).First()!;
        result["the vehicle ahead on the image"] =
            $"{(double)farthest["AheadDistance"]!:0.0} m: {(int)farthest["AheadBox"]![2]!} px wide; {(double)nearest["AheadDistance"]!:0.0} m: {(int)nearest["AheadBox"]![2]!} px wide";

        // As a record, played by a StoredRecord.
        var location = Path.Combine(Path.GetTempPath(), "mpai-phase6-" + Guid.NewGuid().ToString("N"), "private", "1CAV-ESS-V1.1-I01");
        var store = new RuledStore(() => location, () => DateTimeOffset.UtcNow, everyoneReads: false, centralControl: null);
        var id = drive.WriteRecord(store.For(new StorageHolder("1CAV-ESS-V1.1-I01", "SyntheticDrive"), "", ""), messages);
        var ua = store.For(StorageHolder.UserAgent, "", "");
        var inputs = await new StoredRecord(ua, id).InputsAsync();
        ua.MPAI_AIFM_RuledStorage_Trace(id + "/header", out var trace);
        result["as a record, read by the User Agent"] = $"{inputs.Count} inputs, " +
            (inputs.Select(i => i.Json).SequenceEqual(messages.Select(m => m.Json)) ? "as made" : "different") +
            $", at their times: {(inputs.Select(i => i.At).SequenceEqual(messages.Select(m => m.At)) ? "yes" : "no")}";
        result["the record's writer"] = trace?.Writer ?? "none";

        var path = Path.Combine(Repository.Root, "Test", "Reports", "synthetic-drive.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Seconds = 4, Messages = messages.Count,
            Bytes = messages.GroupBy(m => m.DataType).ToDictionary(g => g.Key, g => g.Sum(m => m.Json.Length)),
            FrameBytes = new { Min = sizes.Min(), Max = sizes.Max() }
        }, new JsonSerializerOptions { WriteIndented = true }));
        Expected.Match("synthetic-drive.json", result);
    }
}
