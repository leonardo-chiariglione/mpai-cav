using System.Text;
using System.Text.Json;
using Mpai.Cav.DrivingSim;
using Xunit.Abstractions;

namespace Mpai.Aif.Tests;

// N-19 (night of 2026-11-12): every typical traffic context, with its variants, driven by the new driving core in the simulation.
// Each run is kept as a record in Test/Data/CAV/Knowledge/<date>/ (the scene descriptions, the inferences and their errors, the
// decisions, the outcome), with summary.md (the table and, per context, what went well, what went wrong and why) and summary.json.
// THE HARD RULE is asserted: no collision, of any kind. A run that ends stopped (no gap came) is knowledge, not a failure.
[Trait("Group", "Slow")]
[Trait("Blocks", "No")]
public class CavDrivingContextsTests(ITestOutputHelper output)
{
    public static string KnowledgeFolder(DateTime day) => Path.Combine(Repository.Root, "Test", "Data", "CAV", "Knowledge", day.ToString("yyyy-MM-dd"));

    [Fact]
    public void EveryContextRunsWithoutACollision()
    {
        var folder = KnowledgeFolder(DateTime.Now);
        var scenarios = Contexts.All().ToList();
        var runs = new RunRecord[scenarios.Count];
        Parallel.For(0, scenarios.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, i =>
        {
            runs[i] = Simulation.Run(scenarios[i]);
            runs[i].Save(folder);
        });
        File.WriteAllText(Path.Combine(folder, "summary.md"), "# CAV driving contexts\n\n" + Summary.Markdown(runs), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(folder, "summary.json"), JsonSerializer.Serialize(runs.Select(r => new
        {
            r.Context, r.Variant, r.Parameters, r.Outcome.Result, r.Outcome.Time, MinGap = double.IsNaN(r.Outcome.MinGap) ? (double?)null : r.Outcome.MinGap,
            r.Outcome.Closest, r.Outcome.EmergencyStops, r.Outcome.MaxBraking, r.Outcome.HeldBack, r.Outcome.LastReasons,
            r.Inference.Mean1, r.Inference.Mean3, r.Inference.Worst3, r.Inference.Worst3OfBest, r.Inference.CoverageAt3, r.Inference.WorstCase
        }), new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

        foreach (var r in runs)
            output.WriteLine($"{r.Context}/{r.Variant}: {r.Outcome.Result} {r.Outcome.Time:F1} s, closest {r.Outcome.MinGap:F2} m, worst inference error at 3 s {r.Inference.Worst3:F1} m");
        Assert.Equal(scenarios.Count, runs.Length);
        var collisions = runs.Where(r => r.Outcome.Result.Contains("collision", StringComparison.OrdinalIgnoreCase)).Select(r => $"{r.Context}/{r.Variant}: {r.Outcome.Result}").ToList();
        Assert.True(collisions.Count == 0, string.Join("; ", collisions));
        Assert.True(runs.Count(r => r.Outcome.Result == "arrived") >= runs.Length * 3 / 4, "fewer than three quarters arrived");
    }
}
