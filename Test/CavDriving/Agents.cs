using Mpai.Cav.Driving;

namespace Mpai.Cav.DrivingSim;

// A road user of the simulation (the truth, not what the CAV senses). It moves along a line; a vehicle follows the one
// ahead of it on its line (Intelligent Driver Model) and slows for curves; a script may make it brake, change lane, turn,
// give way, or (a pedestrian) start walking.
public sealed class Agent
{
    public required int Id { get; init; }
    public required RoadUserKind Kind { get; init; }
    public double Length { get; init; } = 4.5;
    public double Width { get; init; } = 1.8;
    public required Polyline Path { get; set; }
    public double S { get; set; }
    public double D { get; set; }                 // lateral offset from the line, positive to the left
    public double V { get; set; }
    public double DesiredSpeed { get; set; } = 13.9;
    public int Indicator { get; set; }
    public bool Parked { get; init; }             // never moves
    public double A { get; private set; }

    // the script
    public double? BrakeAt { get; init; }         // time it starts braking
    public double BrakeDecel { get; init; } = 6;
    public double BrakeHold { get; init; } = double.MaxValue;   // seconds it stays stopped, then drives on
    public (double Time, Polyline NewPath, double Duration)? LaneChange { get; init; }
    public Func<World, bool>? StartWhen { get; init; }          // a pedestrian waits for this, then walks
    public (double StopS, Vec2 Conflict, double GapTime)? GiveWay { get; init; }   // waits at StopS until the CAV is not within GapTime of Conflict
    public double FollowGap { get; init; } = 1.2;               // IDM time headway
    public double MaxAccel { get; init; } = 1.5;
    public Func<double, double>? DesiredSpeedAt { get; init; }   // a speed that changes with time (the head of a stop-and-go queue)

    private bool _started;
    private double? _stoppedAt;
    private double _lcStart = -1, _lcD0;

    public Vec2 Position => Path.At(S, D);
    public double Heading => Path.HeadingAt(S);
    public bool Started => _started || StartWhen is null;

    public void Step(World w, double dt)
    {
        if (Parked) { V = 0; A = 0; return; }
        if (Kind == RoadUserKind.Pedestrian)
        {
            if (!_started && StartWhen is not null && StartWhen(w)) _started = true;
            if (!Started) { A = 0; return; }
            if (S >= Path.Length) { V = 0; return; }
            S += V * dt; return;
        }
        if (LaneChange is var (lt, np, dur) && w.Time >= lt && _lcStart < 0)
        {
            var p = Position; var (ns, nd) = np.Project(p);
            Path = np; S = ns; _lcD0 = nd; D = nd; _lcStart = w.Time; Indicator = Math.Sign(-nd);
            _lcDuration = dur;
        }
        if (_lcStart >= 0)
        {
            double x = Math.Clamp((w.Time - _lcStart) / _lcDuration, 0, 1);
            D = _lcD0 * (1 - x * x * (3 - 2 * x));
            if (x >= 1) Indicator = 0;
        }

        if (DesiredSpeedAt is not null) DesiredSpeed = Math.Max(0, DesiredSpeedAt(w.Time));
        double a = Idm(w);
        // curves ahead: no more than 3 m/s2 sideways
        for (double ahead = 0; ahead < 40; ahead += 4)
        {
            double k = Math.Abs(Path.CurvatureAt(S + ahead));
            if (k < 1e-3) continue;
            double vc = Math.Sqrt(3.0 / k);
            double need = (vc * vc - V * V) / (2 * Math.Max(ahead, 2));
            a = Math.Min(a, Math.Max(need, -4));
        }
        if (BrakeAt is double bt && w.Time >= bt)
        {
            _stoppedAt ??= V < 0.05 ? w.Time : null;
            if (_stoppedAt is double st && w.Time - st > BrakeHold) { /* drives on */ }
            else a = Math.Min(a, -BrakeDecel);
        }
        if (GiveWay is var (stopS, conflict, gapTime))
        {
            var ego = w.Ego;
            double egoDist = (conflict - ego.Position).Dot(Vec2.FromHeading(ego.Heading));
            bool egoComing = egoDist > -3 && egoDist / Math.Max(ego.Speed, 0.5) < gapTime;
            if (egoComing && S < stopS)
            {
                double dist = stopS - S;
                double need = dist < 0.5 ? -6 : -(V * V) / (2 * dist);
                a = Math.Min(a, need);
            }
        }
        A = a;
        V = Math.Max(0, V + a * dt);
        S += V * dt;
    }

    private double _lcDuration = 3;

    // Intelligent Driver Model, the leader being the nearest road user (or the CAV) ahead on this line.
    private double Idm(World w)
    {
        double s0 = 2.0, T = FollowGap, b = 2.0, v0 = DesiredSpeed;
        double free = MaxAccel * (1 - Math.Pow(V / Math.Max(v0, 0.1), 4));
        double? gap = null; double vl = 0;
        foreach (var (pos, heading, len, wid, speed, self) in w.Occupants())
        {
            if (ReferenceEquals(self, this)) continue;
            var (s, d) = Path.Project(pos);
            if (s <= S || s - S > 120) continue;
            if (Math.Abs(d - D) > (Width + wid) / 2 + 0.3) continue;
            double g = s - S - (Length + len) / 2;
            if (gap is null || g < gap) { gap = g; vl = speed * Math.Cos(Angles.Diff(heading, Heading)); }
        }
        if (v0 < 0.5) free = V > 0.05 ? -2.5 : 0;               // asked to stop: a gentle stop
        if (gap is null) return free;
        double dv = V - vl;
        double sStar = s0 + Math.Max(0, V * T + V * dv / (2 * Math.Sqrt(MaxAccel * b)));
        double interaction = -MaxAccel * Math.Pow(sStar / Math.Max(gap.Value, 0.1), 2);
        return Math.Max(-9, v0 < 0.5 ? Math.Min(free, MaxAccel + interaction) : free + interaction);
    }
}
