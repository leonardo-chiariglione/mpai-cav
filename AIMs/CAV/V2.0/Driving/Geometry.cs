namespace Mpai.Cav.Driving;

// Plane geometry on the frame of the map: East (X), North (Y), metres. A heading is the ISO 8855 yaw:
// anticlockwise from East, radians, so a heading h moves along (cos h, sin h).
public readonly record struct Vec2(double X, double Y)
{
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 a, double k) => new(a.X * k, a.Y * k);
    public static Vec2 operator *(double k, Vec2 a) => new(a.X * k, a.Y * k);
    public double Length => Math.Sqrt(X * X + Y * Y);
    public double Dot(Vec2 o) => X * o.X + Y * o.Y;
    public double Cross(Vec2 o) => X * o.Y - Y * o.X;
    public Vec2 Unit => Length < 1e-12 ? new(1, 0) : this * (1 / Length);
    public Vec2 Left => new(-Y, X);
    public static Vec2 FromHeading(double h) => new(Math.Cos(h), Math.Sin(h));
    public double Heading => Math.Atan2(Y, X);
    public double DistanceTo(Vec2 o) => (this - o).Length;
    public override string ToString() => $"({X:F2}, {Y:F2})";
}

public static class Angles
{
    // The difference a - b brought to (-pi, pi].
    public static double Diff(double a, double b)
    {
        var d = (a - b) % (2 * Math.PI);
        if (d > Math.PI) d -= 2 * Math.PI;
        if (d <= -Math.PI) d += 2 * Math.PI;
        return d;
    }
}

// A line of points with the distance along it (s). A point off the line is given by (s, d): d positive to the left.
public sealed class Polyline
{
    public IReadOnlyList<Vec2> Points { get; }
    private readonly double[] _s;
    public double Length => _s[^1];

    public Polyline(IEnumerable<Vec2> points)
    {
        var list = new List<Vec2>();
        foreach (var p in points)
            if (list.Count == 0 || list[^1].DistanceTo(p) > 1e-6) list.Add(p);
        if (list.Count < 2) throw new ArgumentException("A polyline needs two distinct points.");
        Points = list;
        _s = new double[list.Count];
        for (int i = 1; i < list.Count; i++) _s[i] = _s[i - 1] + list[i].DistanceTo(list[i - 1]);
    }

    public static Polyline Straight(Vec2 from, Vec2 to, double step = 5)
    {
        int n = Math.Max(1, (int)Math.Ceiling(from.DistanceTo(to) / step));
        return new Polyline(Enumerable.Range(0, n + 1).Select(i => from + (to - from) * (i / (double)n)));
    }

    // An arc around a centre, from angle a0 to a1 (radians; a1 > a0 anticlockwise, a1 < a0 clockwise).
    public static Polyline Arc(Vec2 centre, double radius, double a0, double a1, double step = 2)
    {
        int n = Math.Max(2, (int)Math.Ceiling(Math.Abs(a1 - a0) * radius / step));
        return new Polyline(Enumerable.Range(0, n + 1).Select(i => { var a = a0 + (a1 - a0) * i / n; return centre + new Vec2(Math.Cos(a), Math.Sin(a)) * radius; }));
    }

    public Polyline Then(Polyline next) => new(Points.Concat(next.Points));

    // The same line moved sideways by d (positive to the left).
    public Polyline Offset(double d) => new(Enumerable.Range(0, Points.Count).Select(i => Points[i] + NormalAtIndex(i) * d));

    private Vec2 NormalAtIndex(int i)
    {
        var a = Points[Math.Max(0, i - 1)]; var b = Points[Math.Min(Points.Count - 1, i + 1)];
        return (b - a).Unit.Left;
    }

    private int Segment(double s)
    {
        if (s <= 0) return 0;
        if (s >= Length) return Points.Count - 2;
        int lo = 0, hi = _s.Length - 1;
        while (hi - lo > 1) { int m = (lo + hi) / 2; if (_s[m] <= s) lo = m; else hi = m; }
        return lo;
    }

    // The point at s; beyond the ends the line goes on straight.
    public Vec2 At(double s)
    {
        int i = Segment(s);
        var dir = (Points[i + 1] - Points[i]).Unit;
        return Points[i] + dir * (s - _s[i]);
    }

    public double HeadingAt(double s)
    {
        int i = Segment(s);
        return (Points[i + 1] - Points[i]).Heading;
    }

    public Vec2 At(double s, double d) => At(s) + Vec2.FromHeading(HeadingAt(s)).Left * d;

    // The curvature near s (1/m, positive to the left), from the change of heading over +-4 m.
    public double CurvatureAt(double s)
    {
        double a = Math.Max(0, s - 4), b = Math.Min(Length, s + 4);
        if (b - a < 1) return 0;
        return Angles.Diff(HeadingAt(b), HeadingAt(a)) / (b - a);
    }

    // The nearest point of the line: s along it, d to the left (signed). Beyond the ends s goes below 0 or above Length.
    public (double S, double D) Project(Vec2 p)
    {
        double best = double.MaxValue, bs = 0, bd = 0;
        for (int i = 0; i < Points.Count - 1; i++)
        {
            var a = Points[i]; var ab = Points[i + 1] - a; var len = ab.Length;
            var t = (p - a).Dot(ab) / (len * len);
            bool first = i == 0, last = i == Points.Count - 2;
            if (!first) t = Math.Max(t, 0);
            if (!last) t = Math.Min(t, 1);
            var q = a + ab * t;
            var dist = p.DistanceTo(q);
            if (dist < best) { best = dist; bs = _s[i] + t * len; bd = ab.Unit.Cross(p - a); }
        }
        return (bs, bd);
    }
}

// An oriented box, for the extent of a road user or of something that hides the view (a building, a hedge).
public readonly record struct Box(Vec2 Centre, double Heading, double Length, double Width)
{
    public Vec2[] Corners()
    {
        var f = Vec2.FromHeading(Heading) * (Length / 2); var l = Vec2.FromHeading(Heading).Left * (Width / 2);
        return [Centre + f + l, Centre + f - l, Centre - f - l, Centre - f + l];
    }

    // Does the segment a-b cross the box?
    public bool Blocks(Vec2 a, Vec2 b)
    {
        var c = Corners();
        if (Inside(a) || Inside(b)) return false;   // a point in the box (the observer or the target) does not hide itself
        for (int i = 0; i < 4; i++) if (SegmentsCross(a, b, c[i], c[(i + 1) % 4])) return true;
        return false;
    }

    public bool Inside(Vec2 p)
    {
        var r = p - Centre; var f = Vec2.FromHeading(Heading);
        return Math.Abs(r.Dot(f)) <= Length / 2 && Math.Abs(r.Dot(f.Left)) <= Width / 2;
    }

    private static bool SegmentsCross(Vec2 p1, Vec2 p2, Vec2 q1, Vec2 q2)
    {
        double d1 = (p2 - p1).Cross(q1 - p1), d2 = (p2 - p1).Cross(q2 - p1), d3 = (q2 - q1).Cross(p1 - q1), d4 = (q2 - q1).Cross(p2 - q1);
        return d1 * d2 < 0 && d3 * d4 < 0;
    }
}

// The discs that cover a road user (for distances and collisions): along its length, of radius half its width.
public static class Discs
{
    public static IEnumerable<(Vec2 C, double R)> Of(Vec2 centre, double heading, double length, double width)
    {
        double r = width / 2;
        int n = Math.Max(1, (int)Math.Ceiling(length / width));
        var f = Vec2.FromHeading(heading);
        if (n == 1) { yield return (centre, Math.Max(r, length / 2)); yield break; }
        double half = length / 2 - r;
        for (int i = 0; i < n; i++) yield return (centre + f * (-half + 2 * half * i / (n - 1)), r);
    }

    // The gap between two road users (0 when they touch or overlap).
    public static double Gap(Vec2 c1, double h1, double l1, double w1, Vec2 c2, double h2, double l2, double w2)
    {
        if (c1.DistanceTo(c2) - (l1 + l2) / 2 > 5) return c1.DistanceTo(c2) - (l1 + l2) / 2;
        double best = double.MaxValue;
        foreach (var a in Of(c1, h1, l1, w1))
            foreach (var b in Of(c2, h2, l2, w2))
                best = Math.Min(best, a.C.DistanceTo(b.C) - a.R - b.R);
        return Math.Max(0, best);
    }
}
