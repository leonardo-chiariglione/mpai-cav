using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mpai.Cav.Driving;

namespace Mpai.Cav.DrivingSim;

// THE KNOWLEDGE KEPT OF A RUN: the scene descriptions, what the CAV inferred and what then happened (the error of each
// inference), what it decided and when, and the outcome. Written as JSON, one file a run, to be read by later runs and by people.
public sealed class RunRecord
{
    public string Context { get; set; } = "";
    public string Variant { get; set; } = "";
    public Dictionary<string, object> Parameters { get; set; } = [];
    public List<string> Route { get; set; } = [];
    public Outcome Outcome { get; set; } = new();
    public InferenceSummary Inference { get; set; } = new();
    public List<Snapshot> Timeline { get; set; } = [];
    public List<ErrorEntry> Errors { get; set; } = [];

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    public string Save(string folder)
    {
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"{Context}__{Variant}.json");
        File.WriteAllText(file, JsonSerializer.Serialize(this, Json));
        return file;
    }
}

public sealed class Outcome
{
    public string Result { get; set; } = "";
    public double Time { get; set; }
    public int EmergencyStops { get; set; }
    public double MaxBraking { get; set; }
    public int? CollisionWith { get; set; }
    public double CollisionSpeed { get; set; }
    public Dictionary<string, double> Closest { get; set; } = [];
    public List<string> LastReasons { get; set; } = [];
    public List<string> HeldBack { get; set; } = [];
    public double MinGap => Closest.Count == 0 ? double.NaN : Closest.Values.Min();
}

public sealed class InferenceSummary
{
    public int Count { get; set; }
    public double Mean1 { get; set; }
    public double Mean3 { get; set; }
    public double Worst3 { get; set; }               // the largest error at 3 s of the most likely hypothesis
    public string WorstCase { get; set; } = "";
    public double CoverageAt3 { get; set; }          // share of predictions where a respected hypothesis was within its 2 sigma + 0.5 m at 3 s
    public double Worst3OfBest { get; set; }         // the largest error at 3 s of the nearest respected hypothesis
}

public sealed record ErrorEntry(double T, int Track, int Agent, string Kind, double Horizon, double ErrorMostLikely, double ErrorBest, string MostLikely);

public sealed record UserSnap(int Id, string Kind, double X, double Y, double Vx, double Vy, string? Lane, bool Stationary);
public sealed record HypSnap(string Label, double P, double[] At1, double[] At2, double[] At3);
public sealed record PredSnap(int Id, bool Assumed, List<HypSnap> Hypotheses);
public sealed record Snapshot(double T, double X, double Y, double Heading, double Speed, string? Lane, string Option, double Acceleration, bool Emergency, string Reason,
                              List<string> Alternatives, List<UserSnap> Scene, List<string> Features, List<PredSnap> Predictions)
{
    public static Snapshot Of(World w, Scene s, List<Prediction> preds, Decision d) =>
        new(R(w.Time), R(w.Ego.Position.X), R(w.Ego.Position.Y), R(w.Ego.Heading), R(w.Ego.Speed), s.Ego.LaneId, d.Option, R(d.Acceleration), d.EmergencyStop, d.Reason, d.Alternatives,
            s.RoadUsers.Select(u => new UserSnap(u.Id, u.Kind.ToString(), R(u.Position.X), R(u.Position.Y), R(u.Velocity.X), R(u.Velocity.Y), u.LaneId, u.Stationary)).ToList(),
            s.Features.ToList(),
            preds.Select(p => new PredSnap(p.RoadUserId, p.Assumed, p.Hypotheses.Select(h => new HypSnap(h.Label, R(h.Probability), P(h.At(1)), P(h.At(2)), P(h.At(3)))).ToList())).ToList());
    private static double R(double v) => Math.Round(v, 2);
    private static double[] P(PredictedPoint p) => [R(p.Position.X), R(p.Position.Y)];
}

public static class InferenceErrors
{
    public static InferenceSummary Of(List<(double T, int Track, Vec2 TrackPos, RoadUserKind Kind, Prediction P)> kept,
                                      Dictionary<int, List<(double T, Vec2 P)>> truth, List<Agent> agents, RunRecord rec)
    {
        var sum = new InferenceSummary();
        var e1 = new List<double>(); var e3 = new List<double>(); int covered = 0, n3 = 0;
        foreach (var (t, track, pos, kind, p) in kept)
        {
            // the agent the track is: the nearest of the same kind at that instant
            Agent? who = null; double best = 3.0;
            foreach (var a in agents)
            {
                if (a.Kind != kind || !truth.TryGetValue(a.Id, out var tr)) continue;
                var at = At(tr, t); if (at is null) continue;
                var d = at.Value.DistanceTo(pos); if (d < best) { best = d; who = a; }
            }
            if (who is null) continue;
            foreach (var k in new[] { 1.0, 2.0, 3.0 })
            {
                var real = At(truth[who.Id], t + k); if (real is null) continue;
                var ml = p.MostLikely;
                double eMl = ml.At(k).Position.DistanceTo(real.Value);
                var respected = p.Hypotheses.Where(h => h.Probability >= 0.1 || ReferenceEquals(h, ml)).ToList();
                var nearest = respected.MinBy(h => h.At(k).Position.DistanceTo(real.Value))!;
                double eBest = nearest.At(k).Position.DistanceTo(real.Value);
                rec.Errors.Add(new ErrorEntry(Math.Round(t, 2), track, who.Id, kind.ToString(), k, Math.Round(eMl, 2), Math.Round(eBest, 2), ml.Label));
                if (k == 1) e1.Add(eMl);
                if (k == 3)
                {
                    e3.Add(eMl); n3++;
                    if (eBest <= 2 * nearest.Sigma(3) + 0.5) covered++;
                    if (eMl > sum.Worst3) { sum.Worst3 = eMl; sum.WorstCase = $"t {t:F1} s, {kind} {who.Id}: most likely '{ml.Label}' (p {ml.Probability:F2}), off by {eMl:F1} m at 3 s; nearest respected '{nearest.Label}' off by {eBest:F1} m"; }
                    sum.Worst3OfBest = Math.Max(sum.Worst3OfBest, eBest);
                }
            }
        }
        sum.Count = kept.Count;
        sum.Mean1 = e1.Count > 0 ? Math.Round(e1.Average(), 2) : 0;
        sum.Mean3 = e3.Count > 0 ? Math.Round(e3.Average(), 2) : 0;
        sum.Worst3 = Math.Round(sum.Worst3, 2); sum.Worst3OfBest = Math.Round(sum.Worst3OfBest, 2);
        sum.CoverageAt3 = n3 > 0 ? Math.Round((double)covered / n3, 3) : 1;
        return sum;
    }

    private static Vec2? At(List<(double T, Vec2 P)> tr, double t)
    {
        if (tr.Count == 0 || t < tr[0].T - 1e-6 || t > tr[^1].T + 1e-6) return null;
        int i = (int)Math.Round((t - tr[0].T) / Simulation.Dt);
        i = Math.Clamp(i, 0, tr.Count - 1);
        return tr[i].P;
    }
}

public static class Summary
{
    // The table and, per context, what went well, what went wrong and why.
    public static string Markdown(IReadOnlyList<RunRecord> runs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Context | Variant | Outcome | Time s | Closest m | Worst inference error at 3 s (m) | Emergency stops | Max braking m/s2 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var r in runs)
            sb.AppendLine($"| {r.Context} | {r.Variant} | {r.Outcome.Result} | {r.Outcome.Time:F1} | {(double.IsNaN(r.Outcome.MinGap) ? "-" : r.Outcome.MinGap.ToString("F2"))} | {r.Inference.Worst3:F1} (nearest hypothesis {r.Inference.Worst3OfBest:F1}) | {r.Outcome.EmergencyStops} | {r.Outcome.MaxBraking:F1} |");
        sb.AppendLine();
        foreach (var g in runs.GroupBy(r => r.Context))
        {
            sb.AppendLine($"### {g.Key}");
            int arrived = g.Count(r => r.Outcome.Result == "arrived"), coll = g.Count(r => r.Outcome.Result.StartsWith("COLLISION")), hit = g.Count(r => r.Outcome.Result.StartsWith("collision"));
            sb.AppendLine($"- Runs {g.Count()}: arrived {arrived}, stopped {g.Count() - arrived - coll - hit}, collisions by the CAV {coll}, hit while stopped {hit}.");
            foreach (var r in g)
            {
                var o = r.Outcome;
                sb.Append($"- {r.Variant}: {o.Result} at {o.Time:F1} s, closest {(double.IsNaN(o.MinGap) ? "-" : o.MinGap.ToString("F2"))} m");
                if (o.HeldBack.Count > 0) sb.Append($"; held back most by: {string.Join("; ", o.HeldBack)}");
                if (o.Result != "arrived" && o.LastReasons.Count > 0) sb.Append($"; last decisions: {string.Join("; ", o.LastReasons)}");
                sb.AppendLine($"; inference mean error at 1 s {r.Inference.Mean1:F2} m, at 3 s {r.Inference.Mean3:F2} m, coverage {r.Inference.CoverageAt3:P0}; worst: {r.Inference.WorstCase}");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
