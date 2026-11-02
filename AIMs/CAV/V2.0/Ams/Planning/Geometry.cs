namespace Mpai.Cav.Ams.Planning;

// THE GENERAL PLANNER'S GEOMETRY (the author, 2026/10/29: the CAV follows what it perceives, predicts the trajectories of the cars it perceives,
// checks the predictions against what it goes on perceiving, and computes its own trajectory to its destination without colliding with
// anything in the scene). Map frame: x east, y north, headings in radians anticlockwise from east, metres, seconds.
public readonly record struct Pose(double X, double Y, double Heading);

// A LINE ON THE GROUND, as a polyline with the distance along it.
public sealed class PolyPath
{
    private readonly double[] xs, ys, s;

    public double Length { get; }
    public int Count => xs.Length;

    public PolyPath(IEnumerable<(double X, double Y)> points)
    {
        var x = new List<double>(); var y = new List<double>(); var d = new List<double>();
        double along = 0;
        foreach (var (px, py) in points)
        {
            if (x.Count > 0)
            {
                var step = Math.Sqrt(Math.Pow(px - x[^1], 2) + Math.Pow(py - y[^1], 2));
                if (step < 1e-6) continue;
                along += step;
            }
            x.Add(px); y.Add(py); d.Add(along);
        }
        if (x.Count < 2) throw new ArgumentException("A path has at least two different points.");
        (xs, ys, s, Length) = (x.ToArray(), y.ToArray(), d.ToArray(), along);
    }

    public static PolyPath Line(double x, double y, double heading, double length, double step = 5)
    {
        var points = new List<(double, double)>();
        for (double d = 0; d < length + step / 2; d += step) points.Add((x + d * Math.Cos(heading), y + d * Math.Sin(heading)));
        return new PolyPath(points);
    }

    // The path of a vehicle that goes on straight for `before` metres, then turns by `angle` radians (positive to the left) on an arc of the
    // radius, then goes on straight for `after` metres.
    public static PolyPath Turn(double x, double y, double heading, double before, double angle, double radius, double after, double step = 2)
    {
        var points = new List<(double, double)>();
        for (double d = 0; d < before; d += step) points.Add((x + d * Math.Cos(heading), y + d * Math.Sin(heading)));
        var (cx, cy) = (x + before * Math.Cos(heading), y + before * Math.Sin(heading));
        var side = Math.Sign(angle); var arc = Math.Abs(angle) * radius;
        var (ox, oy) = (cx - side * radius * Math.Sin(heading), cy + side * radius * Math.Cos(heading));      // the centre of the arc
        var bearing0 = heading - side * Math.PI / 2;
        for (double d = 0; d < arc; d += step)
        {
            var b = bearing0 + side * d / radius;
            points.Add((ox + radius * Math.Cos(b), oy + radius * Math.Sin(b)));
        }
        var h = heading + angle; var b1 = bearing0 + angle;
        var (ex, ey) = (ox + radius * Math.Cos(b1), oy + radius * Math.Sin(b1));
        for (double d = 0; d <= after; d += step) points.Add((ex + d * Math.Cos(h), ey + d * Math.Sin(h)));
        return new PolyPath(points);
    }

    public Pose At(double dist)
    {
        dist = Math.Clamp(dist, 0, Length);
        var i = Array.BinarySearch(s, dist);
        if (i < 0) i = ~i - 1;
        i = Math.Clamp(i, 0, xs.Length - 2);
        var seg = s[i + 1] - s[i]; var f = seg > 0 ? (dist - s[i]) / seg : 0;
        return new Pose(xs[i] + f * (xs[i + 1] - xs[i]), ys[i] + f * (ys[i + 1] - ys[i]), Math.Atan2(ys[i + 1] - ys[i], xs[i + 1] - xs[i]));
    }

    // The nearest point of the path: how far along it is, and how far to its left (negative: to its right).
    public (double S, double Offset) Project(double x, double y)
    {
        double best = double.MaxValue, bestS = 0, bestOffset = 0;
        for (var i = 0; i + 1 < xs.Length; i++)
        {
            var (dx, dy) = (xs[i + 1] - xs[i], ys[i + 1] - ys[i]); var len2 = dx * dx + dy * dy;
            var f = Math.Clamp(((x - xs[i]) * dx + (y - ys[i]) * dy) / len2, 0, 1);
            var (px, py) = (xs[i] + f * dx, ys[i] + f * dy);
            var d2 = Math.Pow(x - px, 2) + Math.Pow(y - py, 2);
            if (d2 < best) { best = d2; bestS = s[i] + f * Math.Sqrt(len2); bestOffset = (dx * (y - py) - dy * (x - px)) / Math.Sqrt(len2); }
        }
        return (bestS, bestOffset);
    }

    // How far a point is from the stretch of the path between two distances along it.
    public double Distance(double x, double y, double from, double to)
    {
        from = Math.Clamp(from, 0, Length); to = Math.Clamp(to, from, Length);
        var i = Array.BinarySearch(s, from);
        if (i < 0) i = ~i - 1;
        i = Math.Clamp(i, 0, xs.Length - 2);
        var best = double.MaxValue;
        for (; i + 1 < xs.Length && s[i] <= to; i++)
        {
            var (a, b) = (Math.Max(from, s[i]), Math.Min(to, s[i + 1]));
            if (b < a) continue;
            var len = s[i + 1] - s[i]; var (ux, uy) = ((xs[i + 1] - xs[i]) / len, (ys[i + 1] - ys[i]) / len);
            var (ax, ay) = (xs[i] + ux * (a - s[i]), ys[i] + uy * (a - s[i]));
            var f = Math.Clamp((x - ax) * ux + (y - ay) * uy, 0, b - a);
            best = Math.Min(best, Math.Sqrt(Math.Pow(x - ax - ux * f, 2) + Math.Pow(y - ay - uy * f, 2)));
        }
        return best == double.MaxValue ? Math.Sqrt(Math.Pow(x - At(from).X, 2) + Math.Pow(y - At(from).Y, 2)) : best;
    }

    // The path moved sideways (to the left, for a positive distance): the line of another lane of the same road.
    public PolyPath Offset(double left) =>
        new(Enumerable.Range(0, Count).Select(i =>
        {
            var p = At(s[i]); var a = i + 1 < Count ? At(s[i] + 0.01).Heading : p.Heading;
            return (p.X - left * Math.Sin(p.Heading), p.Y + left * Math.Cos(p.Heading));
        }));
}

// THE FOOTPRINT OF A VEHICLE: circles along its axis, as wide as it is. Two vehicles are clear of each other where no circle of one is nearer a
// circle of the other than the sum of their radii and a margin.
public static class Footprint
{
    public static int Circles(Pose pose, double length, double width, Span<(double X, double Y, double R)> into)
    {
        var r = width / 2; var reach = Math.Max(0, length / 2 - r);
        if (reach < 0.3) { into[0] = (pose.X, pose.Y, r); return 1; }
        var (c, s) = (Math.Cos(pose.Heading), Math.Sin(pose.Heading));
        into[0] = (pose.X - reach * c, pose.Y - reach * s, r);
        into[1] = (pose.X, pose.Y, r);
        into[2] = (pose.X + reach * c, pose.Y + reach * s, r);
        return 3;
    }

    // How far apart two vehicles are, outline to outline (negative: they overlap).
    public static double Clearance(Pose a, double lengthA, double widthA, Pose b, double lengthB, double widthB)
    {
        Span<(double X, double Y, double R)> ca = stackalloc (double, double, double)[3], cb = stackalloc (double, double, double)[3];
        var (na, nb) = (Circles(a, lengthA, widthA, ca), Circles(b, lengthB, widthB, cb));
        var best = double.MaxValue;
        for (var i = 0; i < na; i++)
            for (var j = 0; j < nb; j++)
                best = Math.Min(best, Math.Sqrt(Math.Pow(ca[i].X - cb[j].X, 2) + Math.Pow(ca[i].Y - cb[j].Y, 2)) - ca[i].R - cb[j].R);
        return best;
    }
}
