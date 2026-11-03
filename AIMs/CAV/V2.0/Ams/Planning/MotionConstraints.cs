namespace Mpai.Cav.Ams.Planning;

// THE MOTION CONSTRAINTS (M3287): what a trajectory of a CAV may not do, whatever the driving strategy that gives it. A strategy decides where to go and how
// fast; this decides what is not allowed. It holds the parameters (one place, each with its source), the two sets of limits (normal and emergency), the safe
// distances of the Responsibility-Sensitive Safety model, the following distance of UN R157, and the speed cap along a route (the corners ahead, the grip of the
// road, the distance seen clear). It contains no strategy.
public sealed record ConstraintParameters
{
    public string Version { get; init; } = "M3287 draft 2";

    // C1: the safe distance (RSS formulas; the values of UN R157 Annex 3, Table 3, where it has them)
    public double ResponseTime { get; init; } = 0.75;            // s, R157 Annex 3
    public double AccelerationInResponse { get; init; } = 1.5;   // m/s2, choice (RSS gives no value)
    public double OwnBrake { get; init; } = 4;                   // m/s2, R157 Annex 3, comfortable: the braking of the following distance and of the speed cap
    public double RssBrake { get; init; } = 6;                   // m/s2, the braking the CAV is taken to apply after the response, in the safe distance: the greatest it has (R157 Annex 3)
    public double MaxBrake { get; init; } = 6;                   // m/s2, R157 Annex 3: the emergency set
    public double OtherMaxBrake { get; init; } = 7;              // m/s2, the hardest another vehicle brakes: R157 Annex 3
    public double LateralDistance { get; init; } = 0.3;          // m, mu of RSS, choice (R157's wandering zone, 0.375 m, is a reference)
    public double LateralAccelerationInResponse { get; init; } = 1;   // m/s2, choice
    public double LateralBrake { get; init; } = 1;                    // m/s2, choice

    // C4: control. The limits of the dry road; the grip scales them.
    public double LateralAcceleration { get; init; } = 2;        // m/s2 in a curve: the ceiling quoted by a test-track study (secondary)
    public double LeastGrip { get; init; } = 0.4;                // taken where the grip is not known: choice
    public double EmergencyJerk { get; init; } = 12.65;          // m/s3, R157 Annex 3

    // C6: comfort (target) and the normal limits (ISO 15622, from secondary papers)
    public double ComfortBrake { get; init; } = 1.0;             // m/s2, Horn et al. 2024
    public double ComfortJerk { get; init; } = 0.6;              // m/s3, Horn et al. 2024
    public double NormalBrake { get; init; } = 3.5;              // m/s2, ISO 15622 (secondary): at high speed; 5 at low speed
    public double NormalAcceleration { get; init; } = 2.0;       // m/s2, ISO 15622 (secondary): at high speed; 4 at low speed
    public double NormalJerk { get; init; } = 2.5;               // m/s3, ISO 15622 (secondary)
    public double LaneChangeBrake { get; init; } = 2.0;          // m/s2, R157 5.2.6.7.5
    public double LaneChangeLateral { get; init; } = 1.0;        // m/s2 over the curvature's, R157 5.2.1.3.3

    // C3: the vehicles behind (R157 5.2.6.7)
    public double FollowerBrake { get; init; } = 3.0;            // m/s2
    public double FollowerBrakeStopping { get; init; } = 3.7;    // m/s2, in a stopping manoeuvre
    public double FollowerSeconds { get; init; } = 1.0;          // s of its travel, the distance to keep

    // The trajectory is a plan that is made again at every perception: what the check refuses is what is to be carried out before the CAV can change it - its commitment, a
    // few times the response time - not the intentions beyond. What lies beyond is reported.
    public double CommitHorizon { get; init; } = 2.5;           // s, choice

    // C8: stopping
    public double StopBrake { get; init; } = 4.0;                // m/s2, R157 5.5.2
    public double EmergencyThreshold { get; init; } = 5.0;       // m/s2: a braking demand above it is an emergency manoeuvre, R157 5.3.1.1
}

// A SET OF LIMITS, for a grip: the greatest braking, acceleration, jerk and lateral acceleration the CAV may ask of the vehicle.
public readonly record struct Limits(double Brake, double Acceleration, double Jerk, double Lateral);

public sealed partial class MotionConstraints(ConstraintParameters? parameters = null)
{
    private double? known;

    public ConstraintParameters P { get; } = parameters ?? new();

    // THE GRIP of the road now, as a factor of the dry road's (1): from the Road State of the MAS. Where it is not known, the least grip is taken (C4.3).
    public double Grip
    {
        get => known ?? P.LeastGrip;
        set => known = Math.Clamp(value, 0.05, 1);
    }
    public bool GripIsKnown => known is not null;
    public void GripUnknown() => known = null;
    private double grip => Grip;

    // THE SETS OF LIMITS (S-6): in normal driving, and in an emergency. The grip scales what the tyres give.
    public Limits Normal => new(P.NormalBrake * grip, P.NormalAcceleration * grip, P.NormalJerk, P.LateralAcceleration * grip);
    public Limits Emergency => new(P.MaxBrake * grip, P.NormalAcceleration * grip, P.EmergencyJerk, P.LateralAcceleration * grip);
    public Limits Comfort => new(Math.Min(P.ComfortBrake, P.NormalBrake * grip), P.NormalAcceleration * grip, P.ComfortJerk, P.LateralAcceleration * grip);

    // ---- C1: the safe distance (RSS, Lemmas 2, 3, 4)

    // The least distance between the front of a CAV that follows, at vr, and the rear of the vehicle in front of it, at vf, in the same direction (m).
    public double SafeLongitudinal(double vr, double vf)
    {
        var rho = P.ResponseTime; var a = P.AccelerationInResponse; var vRho = vr + rho * a;
        var d = vr * rho + 0.5 * a * rho * rho + vRho * vRho / (2 * P.RssBrake * Math.Min(1, grip)) - vf * vf / (2 * P.OtherMaxBrake);
        return Math.Max(0, d);
    }

    // The same for two vehicles that come towards each other, at v1 and v2 (m).
    public double SafeLongitudinalOpposite(double v1, double v2)
    {
        var rho = P.ResponseTime; var a = P.AccelerationInResponse; var b = P.RssBrake * Math.Min(1, grip);
        double Part(double v) { var vr = v + rho * a; return (v + vr) / 2 * rho + vr * vr / (2 * b); }
        return Part(Math.Abs(v1)) + Part(Math.Abs(v2));
    }

    // The least lateral distance between the right side of the vehicle on the left, moving sideways at v1 (to the left positive), and the left side of the
    // one on the right, at v2 (m): mu plus what they get nearer by in the response time and in lateral braking.
    public double SafeLateral(double v1, double v2)
    {
        var rho = P.ResponseTime; var a = P.LateralAccelerationInResponse; var b = P.LateralBrake;
        var v1r = v1 + rho * a; var v2r = v2 - rho * a;
        var left = (v1 + v1r) / 2 * rho + v1r * v1r / (2 * b);
        var right = (v2 + v2r) / 2 * rho - v2r * v2r / (2 * b);
        return P.LateralDistance + Math.Max(0, left - right);
    }

    // ---- C3: the following distance (R157 5.2.3.3): the time gap, by speed, and the least distance. Above 60 km/h the rules of the country apply: the gap of 60 km/h
    // is kept, as the least.
    private static readonly double[] GapSpeeds = [7.2, 10, 20, 30, 40, 50, 60];             // km/h, the table of R157 5.2.3.3
    private static readonly double[] GapCars = [1.0, 1.1, 1.2, 1.3, 1.4, 1.5, 1.6];          // s, M1/N1
    private static readonly double[] GapHeavy = [1.2, 1.4, 1.6, 1.8, 2.0, 2.2, 2.4];         // s, M2/M3/N2/N3

    public double FollowingTimeGap(double speed, bool heavy = false)
    {
        var kmh = Math.Clamp(speed * 3.6, GapSpeeds[0], GapSpeeds[^1]);
        var gaps = heavy ? GapHeavy : GapCars;
        var i = Array.FindLastIndex(GapSpeeds, v => v <= kmh);
        if (i >= GapSpeeds.Length - 1) return gaps[^1];
        return gaps[i] + (gaps[i + 1] - gaps[i]) * (kmh - GapSpeeds[i]) / (GapSpeeds[i + 1] - GapSpeeds[i]);
    }

    public double FollowingDistance(double speed, bool heavy = false) =>
        Math.Max(heavy ? 2.4 : 2.0, speed * FollowingTimeGap(speed, heavy));

    // ---- C4, C5: the speed the route allows

    // The speed a curve of the route allows at s: the radius of the route there, at the lateral acceleration the grip gives (C4.1).
    public double CornerSpeed(PolyPath route, double s)
    {
        s = Math.Clamp(s, 5, Math.Max(5, route.Length - 5));
        var (a, b) = (route.At(s - 5).Heading, route.At(s + 5).Heading);
        var turn = Math.Abs(Math.IEEERemainder(b - a, 2 * Math.PI));
        if (turn < 0.02) return double.MaxValue;
        var radius = 10 / turn;
        return Math.Sqrt(P.LateralAcceleration * Math.Min(1, grip) * radius);
    }

    // The speed from which the CAV stops, in the response time and then braking, within the distance it sees to be clear (C5.2). `brake` is at most the
    // normal limit.
    public double SightSpeed(double clearDistance, double brake)
    {
        brake = Math.Min(brake, Normal.Brake);
        var rho = P.ResponseTime; var br = brake * rho;
        return Math.Max(0, -br + Math.Sqrt(br * br + 2 * brake * Math.Max(0, clearDistance)));
    }

    // THE SPEED CAP along a route from s0 (C4.2): at each point, the corner's speed and the limit - and no more than the speed from which the CAV still reaches the
    // cap of every point further on, braking at `brake` (the strategy's, at most the normal limit, scaled by the grip). Where `clearDistance` is given, the sight
    // speed caps the first point. Evaluated each CapStep metres.
    public SpeedCap Cap(PolyPath route, double s0, double limit, double brake, double? clearDistance = null)
    {
        const double step = SpeedCap.Step;
        var b = Math.Min(brake, P.NormalBrake) * Math.Min(1, grip);
        var n = Math.Max(2, Math.Min((int)((route.Length - s0) / step) + 2, 200));
        var cap = new double[n];
        for (var i = n - 1; i >= 0; i--)
        {
            var corner = Math.Min(limit, CornerSpeed(route, s0 + i * step));
            cap[i] = i == n - 1 ? corner : Math.Min(corner, Math.Sqrt(cap[i + 1] * cap[i + 1] + 2 * b * step));
        }
        if (clearDistance is { } clear) cap[0] = Math.Min(cap[0], SightSpeed(clear, b));
        return new SpeedCap(cap, s0);
    }
}

// THE SPEED CAP of a route, point by point.
public sealed class SpeedCap(double[] cap, double s0)
{
    public const double Step = 5;

    public double First => cap[0];
    public double Least => cap.Min();
    public double LeastAt => Array.IndexOf(cap, cap.Min()) * Step;

    public double At(double s)
    {
        var x = Math.Max(0, (s - s0) / Step);
        var i = (int)x;
        if (i >= cap.Length - 1) return cap[^1];
        return cap[i] + (cap[i + 1] - cap[i]) * (x - i);
    }
}
