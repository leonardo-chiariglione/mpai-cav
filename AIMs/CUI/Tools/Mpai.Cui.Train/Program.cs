using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

using Mpai.Cui;

// TRAINS THE DEMONSTRATION FINANCIAL MODEL of CUI-CPP V2.1 (PdModel, format MPAI-CUI-LOGISTIC-1) from the Polish Companies Bankruptcy dataset, 5year.arff: ratios of a company five years into the
// observation and whether it was bankrupt a year later (so, an annual probability of default).
//
//   dotnet run --project AIMs/CUI/Tools/Mpai.Cui.Train -- <5year.arff> <model.json>
//
// The ten ratios of the model are computed from the dataset's 64 (Tomczak's list, X1 to X64), as the AIM computes them from the balance sheet and the income statement:
//   CurrentRatio X4, QuickRatio X46, EBITDAMargin X49, ROA X7, ROE X1/X10, ROS X42, DebtOnSales X2/X9, LongTermLiabilityRatio X2-X51, InterestCoverageRatio X27, InterestOnSales X42/X27.
// What the model is NOT: it is trained on companies of Poland (bankrupt 2000-2012, operating 2007-2013), a sample in which bankruptcies are over-represented; it is a demonstration of a trained model
// delivered as Access data, not a model for real companies.
internal static class Program
{
    private const double Lambda = 1.0, Seed = 20261102;

    // THE DIRECTION OF EACH RATIO: a model that can be explained does not give a weight the wrong way (more profit, more risk). The sign of the weight that each ratio may have - the direction of the
    // reference model - is kept; a ratio whose fitted weight goes the other way (the ratios overlap: EBITDA, operating profit and assets are nearly one signal) is left out of the model and the rest refitted.
    private static readonly int[] Direction = [-1, -1, -1, -1, -1, -1, +1, +1, -1, +1];

    private static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: Mpai.Cui.Train <5year.arff> <model.json>"); return 2; }
        var rows = Read(args[0]);
        Console.WriteLine($"{rows.Count} rows, {rows.Count(r => r.Bankrupt)} bankrupt");

        // 70/30 split by a fixed shuffle.
        var rng = new Random((int)Seed);
        var order = Enumerable.Range(0, rows.Count).OrderBy(_ => rng.Next()).ToList();
        var cut = (int)(rows.Count * 0.7);
        var train = order.Take(cut).Select(i => rows[i]).ToList();
        var test = order.Skip(cut).Select(i => rows[i]).ToList();

        var held = Fit(train, out var trainedOnTrain);
        var scoreTrained = test.Select(r => Predict(trainedOnTrain, r)).ToArray();
        var scoreReference = test.Select(r => Predict(Reference(), r)).ToArray();
        var y = test.Select(r => r.Bankrupt ? 1 : 0).ToArray();
        var metrics = new JsonObject
        {
            ["HeldOutRows"] = test.Count, ["HeldOutBankrupt"] = y.Sum(),
            ["TrainedAuc"] = Math.Round(Auc(scoreTrained, y), 4), ["ReferenceAuc"] = Math.Round(Auc(scoreReference, y), 4),
            ["TrainedBrier"] = Math.Round(Brier(scoreTrained, y), 4), ["ReferenceBrier"] = Math.Round(Brier(scoreReference, y), 4),
            ["TrainedLogLoss"] = Math.Round(LogLoss(scoreTrained, y), 4), ["ReferenceLogLoss"] = Math.Round(LogLoss(scoreReference, y), 4)
        };
        Console.WriteLine("held out: " + metrics.ToJsonString());

        // The published model is fitted to all the rows.
        var model = Fit(rows, out var final);
        var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0])));
        var training = new JsonObject
        {
            ["Dataset"] = "Polish Companies Bankruptcy (UCI Machine Learning Repository), 5year.arff: ratios of companies a year before the outcome",
            ["Source"] = "https://archive.ics.uci.edu/dataset/365/polish+companies+bankruptcy+data", ["Licence"] = "CC-BY-4.0",
            ["Attribution"] = "Tomczak, S. (2016). Polish Companies Bankruptcy [Dataset]. UCI Machine Learning Repository. https://doi.org/10.24432/C5F600",
            ["SourceFileSha256"] = sha, ["Rows"] = rows.Count, ["Bankrupt"] = rows.Count(r => r.Bankrupt),
            ["Method"] = "L2-regularised logistic regression (lambda 1) of ten ratios, each winsorised at the 1st and 99th percentile, standardised and clamped to three scales; a missing ratio counts as the mean; a ratio whose weight would go the wrong way (more profit, more risk) is left out and the others refitted",
            ["Ratios"] = "CurrentRatio=X4, QuickRatio=X46, EBITDAMargin=X49, ROA=X7, ROE=X1/X10, ROS=X42, DebtOnSales=X2/X9, LongTermLiabilityRatio=X2-X51, InterestCoverageRatio=X27, InterestOnSales=X42/X27",
            ["Calibration"] = "to the sample (bankruptcies are over-represented): not to any population of companies",
            ["HeldOut"] = metrics,
            ["Caution"] = "A demonstration of a trained model delivered as Access data. Trained on companies of Poland; not for real companies."
        };
        var file = new PdModel("polish-5year-logistic", final.Intercept, final.Features, training);
        File.WriteAllText(args[1], file.ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Console.WriteLine($"intercept {final.Intercept:F4}; " + string.Join(", ", final.Features.Select(f => $"{f.Name} {f.Weight:+0.000;-0.000}")));
        Console.WriteLine("written " + args[1]);
        return 0;
    }

    // ---- data
    private sealed record Row(double?[] X, bool Bankrupt);

    private static List<Row> Read(string path)
    {
        var rows = new List<Row>();
        var data = false;
        foreach (var line in File.ReadLines(path))
        {
            if (!data) { data = line.Trim().Equals("@data", StringComparison.OrdinalIgnoreCase); continue; }
            if (line.Length == 0) continue;
            var cells = line.Split(',');
            if (cells.Length != 65) continue;
            var x = new double?[64];
            for (var i = 0; i < 64; i++) x[i] = cells[i] != "?" && double.TryParse(cells[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
            rows.Add(new Row(x, cells[64].Trim() == "1"));
        }
        return rows;
    }

    // The ten ratios of the model from the dataset's: null where they cannot be computed.
    private static double?[] Features(Row r)
    {
        double? X(int n) => r.X[n - 1];
        double? Div(double? a, double? b) => a.HasValue && b is { } d && Math.Abs(d) > 1e-6 ? a / d : null;
        return
        [
            X(4), X(46), X(49), X(7), Div(X(1), X(10)), X(42), Div(X(2), X(9)), X(2) - X(51), X(27), Div(X(42), X(27))
        ];
    }

    // ---- model
    private static PdModel Reference() => PdModel.Reference;

    private static double Predict(PdModel model, Row r)
    {
        var f = Features(r);
        var z = model.Intercept;
        for (var i = 0; i < model.Features.Count; i++)
        {
            var feature = model.Features[i];
            var at = Array.IndexOf(ReferenceModels.RatioNames, feature.Name);
            if (f[at] is { } x && double.IsFinite(x)) z += feature.Weight * CuiAim.Clamp((x - feature.Mean) / feature.Scale, -3, 3);
        }
        return CuiAim.Sigmoid(z);
    }

    // Mean and scale of each ratio after winsorising at the 1st and 99th percentile.
    private static (double Mean, double Scale)[] Standardise(IReadOnlyList<Row> rows)
    {
        var result = new (double, double)[ReferenceModels.RatioNames.Length];
        for (var i = 0; i < result.Length; i++)
        {
            var values = rows.Select(r => Features(r)[i]).Where(v => v.HasValue && double.IsFinite(v.Value)).Select(v => v!.Value).Order().ToArray();
            var lo = values[(int)(values.Length * 0.01)];
            var hi = values[Math.Min(values.Length - 1, (int)(values.Length * 0.99))];
            var w = values.Select(v => Math.Min(hi, Math.Max(lo, v))).ToArray();
            var mean = w.Average();
            var scale = Math.Sqrt(w.Sum(v => (v - mean) * (v - mean)) / w.Length);
            result[i] = (mean, scale > 1e-9 ? scale : 1);
        }
        return result;
    }

    private static PdModel Fit(IReadOnlyList<Row> rows, out PdModel model)
    {
        var active = Enumerable.Repeat(true, ReferenceModels.RatioNames.Length).ToArray();
        while (true)
        {
            var w = FitActive(rows, active, out model);
            var wrong = Enumerable.Range(0, active.Length).Where(i => active[i] && w[i + 1] * Direction[i] < 0).OrderByDescending(i => Math.Abs(w[i + 1])).ToList();
            if (wrong.Count == 0) return model;
            active[wrong[0]] = false;
            Console.WriteLine($"  {ReferenceModels.RatioNames[wrong[0]]} left out: its weight went the wrong way ({w[wrong[0] + 1]:+0.000;-0.000})");
        }
    }

    private static double[] FitActive(IReadOnlyList<Row> rows, bool[] active, out PdModel model)
    {
        var st = Standardise(rows);
        var n = rows.Count;
        var d = st.Length;
        var z = new double[n][];
        var y = new double[n];
        for (var k = 0; k < n; k++)
        {
            var f = Features(rows[k]);
            z[k] = new double[d + 1];
            z[k][0] = 1;
            for (var i = 0; i < d; i++) z[k][i + 1] = f[i] is { } x && double.IsFinite(x) ? CuiAim.Clamp((x - st[i].Mean) / st[i].Scale, -3, 3) : 0;
            y[k] = rows[k].Bankrupt ? 1 : 0;
        }
        // Newton's method with an L2 penalty on the weights (not on the intercept).
        var w = new double[d + 1];
        var free = Enumerable.Range(0, d + 1).Where(j => j == 0 || active[j - 1]).ToArray();
        for (var iteration = 0; iteration < 50; iteration++)
        {
            var gradient = new double[d + 1];
            var hessian = new double[d + 1, d + 1];
            for (var k = 0; k < n; k++)
            {
                var p = CuiAim.Sigmoid(Enumerable.Range(0, d + 1).Sum(j => w[j] * z[k][j]));
                for (var a = 0; a <= d; a++)
                {
                    gradient[a] += (p - y[k]) * z[k][a];
                    for (var b = 0; b <= d; b++) hessian[a, b] += p * (1 - p) * z[k][a] * z[k][b];
                }
            }
            for (var a = 1; a <= d; a++) { gradient[a] += Lambda * w[a]; hessian[a, a] += Lambda; }
            var sub = new double[free.Length, free.Length];
            for (var a = 0; a < free.Length; a++) for (var b = 0; b < free.Length; b++) sub[a, b] = hessian[free[a], free[b]];
            var small = Solve(sub, free.Select(j => gradient[j]).ToArray());
            var step = new double[d + 1];
            for (var a = 0; a < free.Length; a++) step[free[a]] = small[a];
            var size = 0.0;
            for (var a = 0; a <= d; a++) { w[a] -= step[a]; size += step[a] * step[a]; }
            if (Math.Sqrt(size) < 1e-9) break;
        }
        // The model's features are the standardised ratios; the AIM computes (ratio - mean) / scale and clamps, as here.
        model = new PdModel("polish-5year-logistic", w[0], Enumerable.Range(0, d).Where(i => active[i]).Select(i => new PdModel.Feature(ReferenceModels.RatioNames[i], Math.Round(st[i].Mean, 6), Math.Round(st[i].Scale, 6), Math.Round(w[i + 1], 6))).ToList());
        return w;
    }

    private static double[] Solve(double[,] a, double[] b)
    {
        var n = b.Length;
        var m = (double[,])a.Clone();
        var x = (double[])b.Clone();
        for (var c = 0; c < n; c++)
        {
            var pivot = c;
            for (var r = c + 1; r < n; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[pivot, c])) pivot = r;
            for (var k = 0; k < n; k++) (m[c, k], m[pivot, k]) = (m[pivot, k], m[c, k]);
            (x[c], x[pivot]) = (x[pivot], x[c]);
            for (var r = c + 1; r < n; r++)
            {
                var f = m[r, c] / m[c, c];
                for (var k = c; k < n; k++) m[r, k] -= f * m[c, k];
                x[r] -= f * x[c];
            }
        }
        for (var r = n - 1; r >= 0; r--)
        {
            for (var k = r + 1; k < n; k++) x[r] -= m[r, k] * x[k];
            x[r] /= m[r, r];
        }
        return x;
    }

    // ---- scores
    private static double Auc(double[] score, int[] y)
    {
        var order = Enumerable.Range(0, score.Length).OrderBy(i => score[i]).ToArray();
        double rankSum = 0;
        for (var r = 0; r < order.Length; r++) if (y[order[r]] == 1) rankSum += r + 1;
        var positives = y.Sum();
        var negatives = y.Length - positives;
        return (rankSum - positives * (positives + 1) / 2.0) / (positives * (double)negatives);
    }

    private static double Brier(double[] p, int[] y) => p.Select((v, i) => (v - y[i]) * (v - y[i])).Average();
    private static double LogLoss(double[] p, int[] y) => -p.Select((v, i) => y[i] == 1 ? Math.Log(Math.Max(v, 1e-12)) : Math.Log(Math.Max(1 - v, 1e-12))).Average();
}
