using System;
using System.Collections.Generic;
using System.Linq;

namespace Mpai.Osd.Aus;

// One speaker's turn (OSD-AUS-V1.5): one or more adjacent utterances of the same speaker.
// Speaker is numbered from 1 in the order the speakers first speak; Start and End are
// seconds from the start of the audio; Overlap is how likely it is that someone else
// speaks at the same time (0 to 1) - an ESTIMATE, see AudioSegmenter.
public sealed record Turn(int Speaker, double Start, double End, double Overlap);

// AUDIO SEGMENTATION, the logic (OSD-AUS-V1.5). From 16 kHz mono samples to the turns of
// the speakers in them. No model of its own and no training: a voice-activity detector
// finds where there is speech, an embedder - the ECAPA speaker embedder, given as a
// function - describes the voice in each short window of it, and windows whose voices are
// alike are one speaker.
//
//  1 SPEECH REGIONS. The RMS energy of 20 ms frames against a threshold that follows the
//    noise (4 times the 10th percentile, never below MinEnergy); pauses shorter than
//    MaxGapSeconds are bridged, regions shorter than MinRegionSeconds dropped.
//  2 WINDOWS. A region is cut into windows of WindowSeconds every HopSeconds: a window is
//    long enough for a voice, short enough for a speaker change to be found within a
//    region (a second speaker answering at once).
//  3 SPEAKERS. Windows are grouped by the cosine of their embeddings: a window joins the
//    nearest group when it is at least SameSpeaker like it, else starts a group; the
//    groups are then refined (centroids recomputed, windows reassigned) and groups whose
//    centroids are alike are one.
//  4 TURNS. Within a region, each window owns the time nearest its centre; neighbours of
//    one speaker are joined, and so are same-speaker segments nearer than TurnGapSeconds.
//
// OVERLAP is estimated, not detected: from one channel, with no model trained for it, the
// only evidence is that a window's voice resembles another speaker's as well as its own
// (the embedding of two voices at once lies between them). The estimate is that
// resemblance; it is 0 for a window that is only its own speaker's.
public sealed class AudioSegmenter
{
    public const int Rate = 16000;
    public const int FrameSamples = 320;                    // 20 ms

    public double MinEnergy        { get; init; } = 0.010;  // RMS, samples in [-1, 1]
    public double NoiseFactor      { get; init; } = 4.0;
    public double MaxGapSeconds    { get; init; } = 0.35;
    public double MinRegionSeconds { get; init; } = 0.30;
    public double WindowSeconds    { get; init; } = 1.6;
    public double HopSeconds       { get; init; } = 0.8;
    public double SameSpeaker      { get; init; } = 0.50;   // cosine of two embeddings of one speaker
    public double TurnGapSeconds   { get; init; } = 1.0;
    public int    MaxSpeakers      { get; init; } = 8;

    private readonly Func<float[], float[]> embed;

    public AudioSegmenter(Func<float[], float[]> embed) => this.embed = embed;

    // ---- 1 --------------------------------------------------------------------------
    public List<(double Start, double End)> SpeechRegions(float[] samples)
    {
        int frames = samples.Length / FrameSamples;
        var rms = new double[frames];
        for (int i = 0; i < frames; i++)
        {
            double sum = 0;
            for (int j = 0; j < FrameSamples; j++) { float v = samples[i * FrameSamples + j]; sum += v * v; }
            rms[i] = Math.Sqrt(sum / FrameSamples);
        }
        if (frames == 0) return [];

        var sorted = rms.OrderBy(x => x).ToArray();
        double floor = sorted[(int)(sorted.Length * 0.10)];
        double threshold = Math.Max(MinEnergy, floor * NoiseFactor);

        var voiced = rms.Select(x => x >= threshold).ToArray();
        int gap = (int)Math.Round(MaxGapSeconds * Rate / FrameSamples);
        for (int i = 0; i < frames;)                          // bridge the short pauses
        {
            if (voiced[i]) { i++; continue; }
            int j = i; while (j < frames && !voiced[j]) j++;
            if (i > 0 && j < frames && j - i <= gap) for (int k = i; k < j; k++) voiced[k] = true;
            i = j;
        }

        var regions = new List<(double, double)>();
        double frameSeconds = (double)FrameSamples / Rate;
        for (int i = 0; i < frames;)
        {
            if (!voiced[i]) { i++; continue; }
            int j = i; while (j < frames && voiced[j]) j++;
            if ((j - i) * frameSeconds >= MinRegionSeconds) regions.Add((i * frameSeconds, j * frameSeconds));
            i = j;
        }
        return regions;
    }

    // ---- 2, 3, 4 ----------------------------------------------------------------------
    public List<Turn> Segment(float[] samples)
    {
        var regions = SpeechRegions(samples);
        var windows = new List<Window>();
        for (int r = 0; r < regions.Count; r++)
        {
            var (s, e) = regions[r];
            if (e - s <= WindowSeconds * 1.5) { windows.Add(new Window(r, s, e)); continue; }
            double t = s;
            for (; t + WindowSeconds < e; t += HopSeconds) windows.Add(new Window(r, t, t + WindowSeconds));
            windows.Add(new Window(r, e - WindowSeconds, e));
        }
        if (windows.Count == 0) return [];

        foreach (var w in windows)
        {
            int a = (int)(w.Start * Rate), b = Math.Min(samples.Length, (int)(w.End * Rate));
            w.Embedding = embed(samples[a..b]);
        }

        var centroids = Cluster(windows);
        // Every window's similarity to its own speaker and to the nearest other one.
        foreach (var w in windows)
        {
            double own = Cosine(w.Embedding!, centroids[w.Speaker]), other = 0;
            for (int c = 0; c < centroids.Count; c++) if (c != w.Speaker) other = Math.Max(other, Cosine(w.Embedding!, centroids[c]));
            w.Overlap = centroids.Count > 1
                ? Math.Clamp((other - 0.20) / 0.30, 0, 1) * (own < 0.80 ? 1.0 : 0.5)
                : (own < 0.45 ? 0.3 : 0);
        }

        // 4: each window owns the time nearest its centre, within its region.
        var segments = new List<(int Speaker, double Start, double End, double Overlap, int Count)>();
        foreach (var region in windows.GroupBy(w => w.Region).OrderBy(g => g.Key))
        {
            var ws = region.OrderBy(w => w.Centre).ToList();
            for (int i = 0; i < ws.Count; i++)
            {
                double start = i == 0 ? ws[i].Start : (ws[i - 1].Centre + ws[i].Centre) / 2;
                double end   = i == ws.Count - 1 ? ws[i].End : (ws[i].Centre + ws[i + 1].Centre) / 2;
                if (end <= start) continue;
                if (segments.Count > 0 && segments[^1].Speaker == ws[i].Speaker && Math.Abs(segments[^1].End - start) < 1e-9)
                {
                    var last = segments[^1];
                    segments[^1] = (last.Speaker, last.Start, end, last.Overlap + ws[i].Overlap, last.Count + 1);
                }
                else segments.Add((ws[i].Speaker, start, end, ws[i].Overlap, 1));
            }
        }

        // Turns: same-speaker segments nearer than TurnGapSeconds are one turn.
        var turns = new List<(int Speaker, double Start, double End, double Overlap, int Count)>();
        foreach (var seg in segments)
        {
            if (turns.Count > 0 && turns[^1].Speaker == seg.Speaker && seg.Start - turns[^1].End <= TurnGapSeconds)
            {
                var last = turns[^1];
                turns[^1] = (last.Speaker, last.Start, seg.End, last.Overlap + seg.Overlap, last.Count + seg.Count);
            }
            else turns.Add(seg);
        }

        // Speakers are numbered from 1, in the order they first speak.
        var number = new Dictionary<int, int>();
        foreach (var t in turns) if (!number.ContainsKey(t.Speaker)) number[t.Speaker] = number.Count + 1;
        return turns.Select(t => new Turn(number[t.Speaker], Math.Round(t.Start, 3), Math.Round(t.End, 3), Math.Round(t.Overlap / t.Count, 3))).ToList();
    }

    // ---- speakers -----------------------------------------------------------------------
    private List<float[]> Cluster(List<Window> windows)
    {
        var centroids = new List<float[]>();
        var sums = new List<double[]>();
        int dim = windows[0].Embedding!.Length;

        foreach (var w in windows)                               // sequential, nearest group or a new one
        {
            int best = -1; double bestCos = -2;
            for (int c = 0; c < centroids.Count; c++) { var cos = Cosine(w.Embedding!, centroids[c]); if (cos > bestCos) { bestCos = cos; best = c; } }
            if (best < 0 || (bestCos < SameSpeaker && centroids.Count < MaxSpeakers))
            {
                best = centroids.Count; centroids.Add(new float[dim]); sums.Add(new double[dim]);
            }
            w.Speaker = best;
            Accumulate(sums[best], w.Embedding!); centroids[best] = Normalise(sums[best]);
        }

        for (int pass = 0; pass < 3; pass++)                     // refine: reassign, recompute, join the alike
        {
            foreach (var w in windows)
            {
                int best = 0; double bestCos = -2;
                for (int c = 0; c < centroids.Count; c++) { var cos = Cosine(w.Embedding!, centroids[c]); if (cos > bestCos) { bestCos = cos; best = c; } }
                w.Speaker = best;
            }
            Recompute(windows, centroids, dim);
            JoinTheAlike(windows, centroids, dim);
        }

        // A group of a window or two is noise, not a speaker: its windows go to the nearest other group.
        if (centroids.Count > 1)
        {
            var small = Enumerable.Range(0, centroids.Count).Where(c => windows.Count(w => w.Speaker == c) * HopSeconds < 0.9 && windows.Count(w => w.Speaker == c) < 2).ToList();
            if (small.Count > 0 && small.Count < centroids.Count)
            {
                foreach (var w in windows.Where(w => small.Contains(w.Speaker)))
                {
                    int best = -1; double bestCos = -2;
                    for (int c = 0; c < centroids.Count; c++) if (!small.Contains(c)) { var cos = Cosine(w.Embedding!, centroids[c]); if (cos > bestCos) { bestCos = cos; best = c; } }
                    w.Speaker = best;
                }
                Recompute(windows, centroids, dim);
                JoinTheAlike(windows, centroids, dim);
            }
        }
        return centroids;
    }

    private void Recompute(List<Window> windows, List<float[]> centroids, int dim)
    {
        for (int c = 0; c < centroids.Count; c++)
        {
            var sum = new double[dim]; int n = 0;
            foreach (var w in windows.Where(w => w.Speaker == c)) { Accumulate(sum, w.Embedding!); n++; }
            if (n > 0) centroids[c] = Normalise(sum);
        }
    }

    // Groups whose centroids are SameSpeaker alike are one speaker; the groups are renumbered from 0.
    private void JoinTheAlike(List<Window> windows, List<float[]> centroids, int dim)
    {
        bool joined;
        do
        {
            joined = false;
            for (int a = 0; a < centroids.Count && !joined; a++)
                for (int b = a + 1; b < centroids.Count && !joined; b++)
                    if (Cosine(centroids[a], centroids[b]) >= SameSpeaker)
                    {
                        foreach (var w in windows.Where(w => w.Speaker == b)) w.Speaker = a;
                        foreach (var w in windows.Where(w => w.Speaker > b)) w.Speaker--;
                        centroids.RemoveAt(b); Recompute(windows, centroids, dim); joined = true;
                    }
        } while (joined);
        for (int c = centroids.Count - 1; c >= 0; c--)           // a group nobody is in is gone
            if (!windows.Any(w => w.Speaker == c)) { foreach (var w in windows.Where(w => w.Speaker > c)) w.Speaker--; centroids.RemoveAt(c); }
    }

    private static void Accumulate(double[] sum, float[] v) { for (int i = 0; i < sum.Length; i++) sum[i] += v[i]; }

    private static float[] Normalise(double[] sum)
    {
        double norm = Math.Sqrt(sum.Sum(x => x * x));
        return norm == 0 ? new float[sum.Length] : sum.Select(x => (float)(x / norm)).ToArray();
    }

    public static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * (double)b[i]; na += a[i] * (double)a[i]; nb += b[i] * (double)b[i]; }
        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }

    private sealed class Window
    {
        public Window(int region, double start, double end) { Region = region; Start = start; End = end; }
        public int Region { get; }
        public double Start { get; }
        public double End { get; }
        public double Centre => (Start + End) / 2;
        public float[]? Embedding { get; set; }
        public int Speaker { get; set; }
        public double Overlap { get; set; }
    }
}
