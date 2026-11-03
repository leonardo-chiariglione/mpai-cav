namespace Mpai.Cav.Ams.Planning;

// WHAT THE CAV PERCEIVES OF AN OBJECT AT ONE INSTANT, in the map's frame: where it is and how it moves.
public sealed record Observation(string Id, double Time, double X, double Y, double VX, double VY, double Length = 4.5, double Width = 1.8, string Class = "car")
{
    public double Speed => Math.Sqrt(VX * VX + VY * VY);
    public double Heading => Speed > 0.5 ? Math.Atan2(VY, VX) : double.NaN;
}

// WHAT AN OBJECT SAYS IT WILL DO (a Sender Info trajectory a Remote CAV communicated): its places at times, in seconds from the instant it was
// said. It is trusted: nothing the CAV perceives discards it.
public sealed record Told(string Id, double Time, IReadOnlyList<(double T, double X, double Y)> Places);

// THE ROADS THE CAV KNOWS (its Offline Map): where a vehicle that is here, going this way, can go on - along its lane, and at a junction each of
// the ways open to it. The CAV does not guess which it takes: it keeps them all until what it perceives discards them.
public interface IRoadModel
{
    IReadOnlyList<(string Name, PolyPath Path)> Continuations(double x, double y, double heading);
}

// A WAY AN OBJECT MAY GO: a line on the ground, the distance along it where it is now, the speed it has along it, and the reach - the stretch of
// the line it can occupy t seconds from now, between braking as hard as drivers do and speeding up.
public sealed class Hypothesis
{
    public required string Kind { get; init; }
    public required PolyPath Path { get; init; }
    public double S0 { get; init; }
    public double Speed { get; init; }
    public bool Trusted { get; init; }
    public double Spread { get; init; }                           // how far out the direction may be, radians: a line drawn from a place along a heading that is estimated is as far out as that, times the distance
    public Func<double, (double Lo, double Hi)>? Told { get; init; }     // a trusted hypothesis knows where it will be, at each time

    // A ROAD USER THAT MAY GO ANYWHERE (a pedestrian, a cyclist: nothing of where it goes is assumed): not a line but a disc. Its centre is where it
    // would be going on at the velocity it has; its radius how far from there it can be, by changing its velocity at Acceleration m/s2 - by any
    // change of velocity of MaxSpeed at the most: a road user does not reverse, or swerve, faster than that.
    public bool Disc { get; init; }
    public double MaxChange { get; init; }
    public double Acceleration { get; init; }

    // The radius of the disc t seconds on.
    public double DiscRadius(double t)
    {
        var cap = MaxChange;
        var t1 = Acceleration > 0 ? cap / Acceleration : 0;
        return t <= t1 ? Acceleration * t * t / 2 : Acceleration * t1 * t1 / 2 + cap * (t - t1);
    }

    // `brake`: how hard drivers brake (m/s2) and `speedUp`: how much they speed up, each for no more than `seconds`: a driver brakes or speeds up
    // for a while - not to a stop, or to any speed, however long the horizon.
    public (double Lo, double Hi) Reach(double t, double brake, double speedUp, double seconds = 3)
    {
        if (Told is not null) return Told(t);
        var v = Math.Max(0, Speed);
        var tb = Math.Min(v / brake, seconds);
        var lo = t <= tb ? v * t - brake * t * t / 2 : v * tb - brake * tb * tb / 2 + (v - brake * tb) * (t - tb);
        var tu = Math.Min(t, seconds);
        var hi = v * t + speedUp * tu * tu / 2 + speedUp * tu * (t - tu);
        return (S0 + lo, Math.Min(Path.Length, S0 + hi));
    }
}

// ONE OBJECT: what has been perceived of it, and the hypotheses of where it goes that what has been perceived has not discarded.
public sealed class TrackedObject
{
    public required string Id { get; init; }
    public double Length { get; set; } = 4.5;
    public double Width { get; set; } = 1.8;
    public string Class { get; internal set; } = "car";
    public double? MaxSpeed { get; internal set; }                 // for a road user that may go anywhere: the speed the CAV allows it, raised where it went faster
    public double? MaxChange { get; internal set; }                // and the change of velocity it allows it
    internal List<Observation> History { get; } = [];
    internal Dictionary<string, Remembered> Remembered { get; } = [];
    public IReadOnlyList<Hypothesis> Hypotheses { get; internal set; } = [];
    public Observation Last => History[^1];
    public bool Confirms { get; internal set; }       // at least one hypothesis made before has been borne out by what has been perceived since
}

internal sealed class Remembered
{
    public required Hypothesis Hypothesis { get; init; }
    public required double Time { get; init; }
    public int Bad, Good;
    public bool Discarded;
}

// THE TRACKER: predicts the trajectories of the objects the CAV perceives, and verifies each prediction against what it goes on perceiving - a
// hypothesis the perceived places keep departing from is discarded - and anchors those that remain on the latest perception. A hypothesis
// that an object told is trusted. Nothing is assumed of what a driver does beyond what is physical: a vehicle brakes and accelerates within
// limits (`Acceleration`), and stays within the width of a lane unless it moves out of it, which what is perceived shows.
public sealed class ObjectTracker(IRoadModel? road = null, double horizon = 6, double acceleration = 2, double crossTolerance = 1.0, double headingTolerance = 0.35, double speedUp = 0.5,
                                  IReadOnlyDictionary<string, (double MaxSpeed, double Acceleration, double MaxChange)>? vulnerable = null)
{
    // THE ROAD USERS THAT MAY GO ANYWHERE, by class, with the speed they are allowed (a jog, a fast cycle), how hard they change velocity and by how much at the most (a swerve).
    public IReadOnlyDictionary<string, (double MaxSpeed, double Acceleration, double MaxChange)> Vulnerable { get; } =
        vulnerable ?? new Dictionary<string, (double, double, double)> { ["person"] = (3.0, 2.0, 3.0), ["bicycle"] = (8.0, 2.0, 4.0) };

    // WHAT WAS NOT AS PREDICTED: a road user that went faster than it was allowed - the object, when, its speed, the speed it was allowed.
    public List<(string Id, double Time, double Speed, double Allowed)> Unpredicted { get; } = [];

    private readonly Dictionary<string, TrackedObject> tracks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Told> told = new(StringComparer.Ordinal);
    private readonly double memory = 1.5;                         // seconds an object is kept after it was last perceived
    private const int Rejections = 3;                             // consecutive perceptions that contradict a hypothesis: it is discarded

    public double Horizon { get; } = horizon;
    public double Acceleration { get; } = acceleration;          // how hard objects brake, at most
    public double SpeedUp { get; } = speedUp;                    // how much they speed up, at most
    public IEnumerable<TrackedObject> Objects => tracks.Values;
    public int Perceptions { get; private set; }

    public void Hear(Told what) => told[what.Id] = what;

    // What is perceived at this instant (all of it: an object not in it is not perceived now).
    public void Update(double time, IEnumerable<Observation> seen)
    {
        Perceptions++;
        foreach (var o in seen)
        {
            if (!tracks.TryGetValue(o.Id, out var t)) tracks[o.Id] = t = new TrackedObject { Id = o.Id };
            (t.Length, t.Width, t.Class) = (o.Length, o.Width, o.Class);
            if (Vulnerable.TryGetValue(o.Class, out var limits))
            {
                // Verified against what is perceived: a road user faster than it was allowed is not as predicted, and is allowed more - the faster
                // it went and a half again - from now on; the event is kept for the record.
                t.MaxSpeed ??= limits.MaxSpeed;
                t.MaxChange ??= limits.MaxChange;
                if (o.Speed > t.MaxSpeed) { Unpredicted.Add((o.Id, o.Time, o.Speed, t.MaxSpeed.Value)); t.MaxSpeed = o.Speed * 1.5; t.MaxChange = Math.Max(t.MaxChange.Value, o.Speed * 1.5); }
                t.History.Add(o);
                if (t.History.Count > 12) t.History.RemoveAt(0);
                t.Hypotheses = [new Hypothesis { Kind = "Anywhere:" + o.Class, Path = PolyPath.Line(o.X, o.Y, 0, 2, 1), Speed = o.Speed, Disc = true, MaxChange = t.MaxChange.Value, Acceleration = limits.Acceleration }];
                continue;
            }
            Verify(t, o);
            t.History.Add(o);
            if (t.History.Count > 12) t.History.RemoveAt(0);
            t.Hypotheses = Anchor(t, o);
        }
        foreach (var gone in tracks.Where(k => time - k.Value.Last.Time > memory).Select(k => k.Key).ToList()) tracks.Remove(gone);
    }

    // Each hypothesis remembered is compared with the perception: where the object is, across the line and along it, against the line and the
    // reach the hypothesis gave for the time that has passed.
    private void Verify(TrackedObject t, Observation o)
    {
        t.Confirms = false;
        foreach (var (_, r) in t.Remembered.ToList())
        {
            var dt = o.Time - r.Time;
            if (dt < 0.05) continue;
            var h = r.Hypothesis;
            var (s, offset) = h.Path.Project(o.X, o.Y);
            var tolerance = crossTolerance + (h.Trusted ? 0.5 : 0.3) * dt;
            var heading = double.IsNaN(o.Heading) ? 0 : Math.Abs(Math.IEEERemainder(o.Heading - h.Path.At(s).Heading, 2 * Math.PI));
            // What a hypothesis says is WHERE the object goes - the line. How fast it goes along it is not what is verified: the next perception says it,
            // and the hypothesis is anchored on it - a driver that brakes harder than expected has not left the road.
            var contradicted = Math.Abs(offset) > tolerance || heading > headingTolerance + 0.1 * dt;
            if (contradicted) { r.Bad++; r.Good = 0; }
            else { r.Bad = 0; r.Good++; if (dt >= 0.3) t.Confirms = true; }
            if (r.Bad >= Rejections && !h.Trusted) r.Discarded = true;
            else if (r.Discarded && r.Good >= 5) r.Discarded = false;           // it fits again
        }
    }

    // The hypotheses of the object from now: the roads' ways it may go (all the ways open to it that what is perceived has not discarded), the
    // motion it has (straight on, or turning as it is turning), and what it told; where none is left, the object may be anywhere its speed and
    // its brakes allow.
    private List<Hypothesis> Anchor(TrackedObject t, Observation o)
    {
        var list = new List<Hypothesis>();
        // The velocity of the object from its last perceptions together, and the new one: a perception is noisy, and a line drawn from a place along a
        // velocity that is a little out is a long way out a hundred metres on.
        var recent = t.History.TakeLast(4).Append(o).ToList();
        var (vx, vy) = (recent.Average(r => r.VX), recent.Average(r => r.VY));
        var speed = Math.Sqrt(vx * vx + vy * vy);
        var heading = speed > 0.5 ? Math.Atan2(vy, vx) : t.History.Count > 1 ? Math.Atan2(o.Y - t.History[^2].Y, o.X - t.History[^2].X) : 0;

        // How sure the heading is: the scatter of the headings perceived, over the square root of how many there are.
        var headings = recent.Where(r => !double.IsNaN(r.Heading)).Select(r => r.Heading).ToList();
        double spread = 0.01;
        if (headings.Count >= 3)
        {
            var mean = Math.Atan2(headings.Average(Math.Sin), headings.Average(Math.Cos));
            var scatter = Math.Sqrt(headings.Average(x => Math.Pow(Math.IEEERemainder(x - mean, 2 * Math.PI), 2)));
            spread = Math.Clamp(Math.Max(scatter / Math.Sqrt(headings.Count), 0.5 / Math.Max(10, speed * Horizon)) * 2, 0.003, 0.06);
        }

        void Keep(string key, PolyPath path, double? s0 = null, double? v = null, double sigma = 0)
        {
            var h = new Hypothesis { Kind = key, Path = path, S0 = s0 ?? path.Project(o.X, o.Y).S, Speed = v ?? speed, Spread = sigma };
            if (!t.Remembered.TryGetValue(key, out var r) || o.Time - r.Time > 0.8)              // a fresh basis for the next comparison, each 0.8 s
            {
                var (bad, discarded, good) = r is null ? (0, false, 0) : (r.Bad, r.Discarded, r.Good);
                t.Remembered[key] = r = new Remembered { Hypothesis = h, Time = o.Time, Bad = bad, Discarded = discarded, Good = good };
            }
            if (!r.Discarded) list.Add(h);
        }

        if (told.TryGetValue(o.Id, out var what) && what.Places.Count >= 2)
        {
            var places = what.Places.Select(p => (p.X, p.Y)).ToList();
            var path = new PolyPath(places);
            var stamp = what.Places.Select(p => p.T).ToArray();
            var along = new double[places.Count]; along[0] = 0;
            for (var i = 1; i < places.Count; i++) along[i] = along[i - 1] + Math.Sqrt(Math.Pow(places[i].X - places[i - 1].X, 2) + Math.Pow(places[i].Y - places[i - 1].Y, 2));
            var elapsed = o.Time - what.Time;
            double S(double at)       // the distance along, at `at` seconds from the instant it was said
            {
                if (at <= stamp[0]) return along[0];
                for (var i = 1; i < stamp.Length; i++)
                    if (at <= stamp[i]) return along[i - 1] + (along[i] - along[i - 1]) * (at - stamp[i - 1]) / Math.Max(1e-9, stamp[i] - stamp[i - 1]);
                return along[^1];
            }
            list.Add(new Hypothesis
            {
                Kind = "Told", Path = path, Trusted = true, S0 = S(elapsed), Speed = (S(elapsed + 0.5) - S(elapsed)) / 0.5,
                Told = dt => (Math.Max(0, S(elapsed + dt) - 0.5), Math.Min(path.Length, S(elapsed + dt) + 0.5))
            });
        }

        var onLane = false;                           // the object is in a lane the map knows, going along it: straight on is the lane
        if (road is not null && speed > 0.3)
            foreach (var (name, path) in road.Continuations(o.X, o.Y, heading))
            {
                Keep("Road:" + name, path);
                var (s, offset) = path.Project(o.X, o.Y);
                if (name == "Lane" && Math.Abs(offset) < 1.2 && Math.Abs(Math.IEEERemainder(heading - path.At(s).Heading, 2 * Math.PI)) < 0.15) onLane = true;
            }
        if (!onLane) Keep("Straight", PolyPath.Line(o.X, o.Y, heading, speed * Horizon + 80), 0, sigma: spread);
        else t.Remembered.Remove("Straight");
        var yaw = Yaw(t);
        if (Math.Abs(yaw) > (onLane ? 0.1 : 0.06) && speed > 1)
        {
            var radius = Math.Clamp(speed / Math.Abs(yaw), 5, 2000);
            Keep("Turning", PolyPath.Turn(o.X, o.Y, heading, 0, Math.Sign(yaw) * Math.Min(Math.PI, (speed * Horizon + 80) / radius), radius, 0, 2), 0, sigma: spread);
        }
        else t.Remembered.Remove("Turning");

        // Where nothing the perception has not contradicted is left: anywhere it can reach, which a line as long as it can go, with the same reach.
        if (list.Count == 0) list.Add(new Hypothesis { Kind = "Anywhere", Path = PolyPath.Line(o.X, o.Y, heading, 300), S0 = 0, Speed = speed });
        return list;
    }

    // The yaw rate of the object: the slope of its headings over the last perceptions - a second or so of them, as many as there are - which is
    // steadier than the difference of two. Zero where there are too few, or the object is slow: its heading then is noise.
    private static double Yaw(TrackedObject t)
    {
        var h = t.History.Where(o => !double.IsNaN(o.Heading)).TakeLast(10).ToList();
        if (h.Count < 6 || h[^1].Time - h[0].Time < 0.5) return 0;
        var unwrapped = new double[h.Count]; unwrapped[0] = h[0].Heading;
        for (var i = 1; i < h.Count; i++) unwrapped[i] = unwrapped[i - 1] + Math.IEEERemainder(h[i].Heading - h[i - 1].Heading, 2 * Math.PI);
        var (mt, mh) = (h.Average(o => o.Time), unwrapped.Average());
        var num = 0.0; var den = 0.0;
        for (var i = 0; i < h.Count; i++) { num += (h[i].Time - mt) * (unwrapped[i] - mh); den += Math.Pow(h[i].Time - mt, 2); }
        return den > 0 ? num / den : 0;
    }
}
