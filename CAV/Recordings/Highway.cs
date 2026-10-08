namespace Mpai.Cav.Recordings;

// THE HIGHWAY OF PHASE 1, AS THE AUTHOR DREW IT (2026/10/07): a divided highway of which only
// the carriageway the CAV travels on is considered - two lanes, one direction - a guard rail on
// its left, which hides the opposite one, and a shoulder on its right. A straight road along x
// (east); y is the distance to the left of the right lane's centreline, so lane 0, the right
// one, the normal one, is at y = 0, and lane 1, the left one, where one overtakes, at y = 3.5.
public static class HighwayRoad
{
    public const int Lanes = 2;
    public const double LaneWidth = 3.5;
    public const double RightEdge = -LaneWidth / 2;             // the right edge line
    public const double LeftEdge = LaneWidth * 1.5;             // the left edge line
    public const double RightShoulder = 2.5;                    // paved, beyond the right edge line
    public const double LeftShoulder = 0.6;                     // paved, between the left edge line and the guard rail
    public const double GuardRail = LeftEdge + LeftShoulder;
    public const double GuardRailTop = 0.95, GuardRailBeamBottom = 0.35;

    public static double LaneCentre(int lane) => lane * LaneWidth;
}

// A VEHICLE OF THE TRAFFIC, the ego included: where it is (its centre), how it moves, what it is
// like. A driver has a speed he wants (BaseSpeed), which varies as he drives - a slow drift and
// now and then a slowing - and follows the vehicle ahead as the Intelligent Driver Model says.
public sealed class HighwayVehicle
{
    public required string Id { get; init; }
    public bool Truck { get; init; }
    public double Length { get; init; } = 4.5;
    public double Width { get; init; } = 1.8;
    public double Height { get; init; } = 1.45;
    public (byte R, byte G, byte B) Paint { get; init; } = CameraRenderer.Silver;
    public double BaseSpeed { get; init; }                      // m/s, what the driver wants

    public double X { get; internal set; }                      // m along the road, of the centre
    public double Y { get; internal set; }                      // m to the left of the right lane's centreline
    public double Heading { get; internal set; }                // radians, anticlockwise from the road's direction
    public double Speed { get; internal set; }
    public double Acceleration { get; internal set; }
    public bool ChangingLane => Changing;
    public int Lane => (int)Math.Clamp(Math.Round(Y / HighwayRoad.LaneWidth), 0, HighwayRoad.Lanes - 1);
    public double DesiredSpeed(double time) => BaseSpeed * (1 + Fluctuation) * (time < SlowUntil ? SlowFactor : 1);

    internal double Fluctuation, SlowFactor = 1, SlowUntil;
    internal bool Changing;
    internal double FromY, ToY, ChangeStart, ChangeDuration;
}

// THE TRAFFIC'S SETTINGS. A flow in vehicles an hour in each lane; drivers' speeds in the right
// lane, in the left lane and of the trucks, in m/s (31 is 112 km/h, 36 is 130 km/h, 24.5 is 88).
public sealed record TrafficOptions(
    double FlowPerLane = 900, double TruckShare = 0.15,
    double RightCarSpeed = 31.0, double LeftCarSpeed = 36.0, double SpeedSd = 3.0, double TruckSpeed = 24.5,
    double Ahead = 1600, double Behind = 500);

// THE STEPPED SIMULATION OF THE HIGHWAY. Traffic of the seed given, drivers with their own
// desired speeds that drift and sometimes dip, who follow whoever is ahead of them in their lane
// (and brake as hard as that takes when the ego pulls in front of them), and who change lanes as
// drivers do: left to pass a slower vehicle where the lane is free enough, back to the right
// when it can be done without making anyone brake hard. The ego is the vehicle of this
// simulation that the CAV drives: it holds the acceleration it was given, and moves to the lane
// it was told to take. Nothing runs against a clock; a step is 0.1 s of simulated time.
public sealed class HighwayWorld
{
    public const double Step = 0.1;
    private const double LaneChangeSeconds = 3.5;

    private readonly Random rng;
    private readonly TrafficOptions opt;
    private readonly List<HighwayVehicle> npcs = [];
    private int stepNumber;

    public int Seed { get; }
    public double Time => stepNumber * Step;
    public int StepNumber => stepNumber;
    public HighwayVehicle Ego { get; }
    public IReadOnlyList<HighwayVehicle> Others => npcs;

    // The acceleration the ego holds (m/s2), from the next step on.
    public double EgoAcceleration { get; set; }

    // Whether the other drivers change lane. A staged scenario may keep them in their lanes, so that what it is
    // meant to show happens as it was staged.
    public bool NpcsChangeLane { get; set; } = true;

    // What the run has shown: collisions of the traffic among itself (there should be none) and of
    // the ego with anyone, the nearest the ego has been to a vehicle (bumper to bumper, m), the
    // hardest braking (m/s2) a driver behind the ego has done because of it, and the hardest of all.
    public List<(double Time, string A, string B)> TrafficCollisions { get; } = [];
    public List<(double Time, string With)> EgoCollisions { get; } = [];
    public double NearestToEgo { get; private set; } = double.PositiveInfinity;
    public double HardestBrakingForEgo { get; private set; }
    public double HardestBraking { get; private set; }

    public HighwayWorld(int seed, TrafficOptions? options = null, double egoSpeed = 30, int egoLane = 0, double warmUpSeconds = 20)
    {
        Seed = seed; opt = options ?? new TrafficOptions(); rng = new Random(seed);
        Ego = EgoVehicle(egoSpeed, egoLane);
        Generate();
        for (var i = 0; i < (int)(warmUpSeconds / Step); i++) Advance();
        ResetOrigin();
    }

    // A world of the vehicles given, no others: for scenarios, and tests.
    private HighwayWorld(int seed, IEnumerable<HighwayVehicle> others, double egoSpeed, int egoLane)
    {
        Seed = seed; opt = new TrafficOptions(); rng = new Random(seed);
        Ego = EgoVehicle(egoSpeed, egoLane);
        npcs.AddRange(others);
    }

    public static HighwayWorld Scenario(IEnumerable<HighwayVehicle> others, double egoSpeed = 30, int egoLane = 0, int seed = 0) =>
        new(seed, others, egoSpeed, egoLane);

    // A vehicle of a scenario: a car (or a truck) at x, in a lane, at a speed.
    public static HighwayVehicle Vehicle(string id, double x, int lane, double speed, bool truck = false, (byte R, byte G, byte B)? paint = null) =>
        new()
        {
            Id = id, Truck = truck, BaseSpeed = speed, X = x, Y = HighwayRoad.LaneCentre(lane), Speed = speed,
            Length = truck ? 12 : 4.5, Width = truck ? 2.5 : 1.8, Height = truck ? 3.6 : 1.45,
            Paint = paint ?? (truck ? TruckWhite : CameraRenderer.Silver)
        };

    private static readonly (byte R, byte G, byte B) TruckWhite = (225, 225, 230), TruckBlue = (60, 90, 150);

    private static HighwayVehicle EgoVehicle(double speed, int lane) => new()
    {
        Id = "Ego", Length = 4.8, Width = 1.85, Height = 1.5, BaseSpeed = speed, Speed = speed,
        X = 0, Y = HighwayRoad.LaneCentre(lane), Paint = (200, 200, 205)
    };

    // ------------------------------------------------------------------------ the traffic made
    private double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
    private double Exp(double mean) => -mean * Math.Log(1 - rng.NextDouble());

    private static readonly (byte R, byte G, byte B)[] Paints =
        [CameraRenderer.Silver, CameraRenderer.DarkRed, CameraRenderer.Blue, CameraRenderer.White, (40, 40, 44), (90, 110, 90), (150, 140, 110)];

    private void Generate()
    {
        var number = 0;
        for (var lane = 0; lane < HighwayRoad.Lanes; lane++)
        {
            var lineUp = new List<HighwayVehicle>();
            var meanGap = Math.Max(10, 3600 * (lane == 0 ? 28.0 : 34.0) / opt.FlowPerLane - 30);
            var x = -opt.Behind + rng.NextDouble() * 40;
            while (x < opt.Ahead)
            {
                var truck = lane == 0 && rng.NextDouble() < opt.TruckShare;
                var wanted = truck ? opt.TruckSpeed + Gauss()
                                   : (lane == 0 ? opt.RightCarSpeed : opt.LeftCarSpeed) + Gauss() * opt.SpeedSd;
                var paint = truck ? (rng.Next(3) == 0 ? TruckBlue : TruckWhite) : Paints[rng.Next(Paints.Length)];
                var v = Vehicle($"V{++number:00}", x, lane, 0, truck, paint);
                lineUp.Add(new HighwayVehicle
                {
                    Id = v.Id, Truck = truck, BaseSpeed = Math.Clamp(wanted, 18, 45), X = x, Y = v.Y,
                    Length = v.Length, Width = v.Width, Height = v.Height, Paint = paint
                });
                x += 30 + Exp(meanGap) + (truck ? 8 : 0);
            }
            // Their speeds, from the front: no faster than the gap ahead allows.
            HighwayVehicle? ahead = null;
            foreach (var each in lineUp.OrderByDescending(a => a.X))
            {
                var allowed = ahead is null ? each.BaseSpeed
                    : Math.Max(0, (ahead.X - each.X - (ahead.Length + each.Length) / 2 - 2.5) / 1.5);
                each.Speed = Math.Min(each.BaseSpeed, allowed);
                ahead = each;
            }
            npcs.AddRange(lineUp);
        }
        // Nobody is on the ego at the start.
        npcs.RemoveAll(a => Math.Abs(a.X - Ego.X) < 30 && Math.Abs(a.Y - Ego.Y) < 3);
    }

    // After the warm-up, the ego is at 0 again, and what is around it is shown as it is.
    private void ResetOrigin()
    {
        var shift = Ego.X;
        Ego.X = 0;
        foreach (var a in npcs) a.X -= shift;
        // The clock goes back to 0, so what is timed by it goes with it: a lane change begun, a slowing.
        var elapsed = Time;
        foreach (var a in Everyone()) { a.ChangeStart -= elapsed; a.SlowUntil -= elapsed; }
        stepNumber = 0;
        TrafficCollisions.Clear(); EgoCollisions.Clear();
        NearestToEgo = double.PositiveInfinity; HardestBrakingForEgo = 0; HardestBraking = 0;
    }

    // ------------------------------------------------------------------------ driving
    // The Intelligent Driver Model: v the speed, v0 the speed wanted, gap the bumper-to-bumper
    // distance to the vehicle ahead (infinite: none), dv the closing speed.
    private static double Idm(double v, double v0, double gap, double dv)
    {
        const double MaxAcceleration = 1.6, ComfortableBraking = 2.0, TimeGap = 1.5, MinimumGap = 2.5;
        var free = 1 - Math.Pow(v / Math.Max(v0, 0.1), 4);
        var interaction = 0.0;
        if (!double.IsPositiveInfinity(gap))
        {
            var wanted = MinimumGap + Math.Max(0, v * TimeGap + v * dv / (2 * Math.Sqrt(MaxAcceleration * ComfortableBraking)));
            interaction = Math.Pow(wanted / Math.Max(gap, 0.1), 2);
        }
        return Math.Clamp(MaxAcceleration * (free - interaction), -9, MaxAcceleration);
    }

    private IEnumerable<HighwayVehicle> Everyone() => npcs.Append(Ego);

    // Who is in the way of a vehicle at lateral place y: the nearest ahead (or behind) whose body
    // overlaps its path, with the gap between their bumpers.
    public (HighwayVehicle? Vehicle, double Gap) Leader(HighwayVehicle me, double y)
    {
        HighwayVehicle? best = null; var gap = double.PositiveInfinity;
        foreach (var other in Everyone())
        {
            if (ReferenceEquals(other, me) || other.X <= me.X || Math.Abs(other.Y - y) >= (other.Width + me.Width) / 2 + 0.25) continue;
            var g = other.X - me.X - (other.Length + me.Length) / 2;
            if (g < gap) { gap = g; best = other; }
        }
        return (best, best is null ? double.PositiveInfinity : Math.Max(g0(gap), 0.01));
        static double g0(double g) => g;
    }

    public (HighwayVehicle? Vehicle, double Gap) Follower(HighwayVehicle me, double y)
    {
        HighwayVehicle? best = null; var gap = double.PositiveInfinity;
        foreach (var other in Everyone())
        {
            if (ReferenceEquals(other, me) || other.X >= me.X || Math.Abs(other.Y - y) >= (other.Width + me.Width) / 2 + 0.25) continue;
            var g = me.X - other.X - (other.Length + me.Length) / 2;
            if (g < gap) { gap = g; best = other; }
        }
        return (best, best is null ? double.PositiveInfinity : Math.Max(gap, 0.01));
    }

    private double AccelerationOf(HighwayVehicle me, double time)
    {
        var (lead, gap) = Leader(me, me.Y);
        return Idm(me.Speed, me.DesiredSpeed(time), gap, lead is null ? 0 : me.Speed - lead.Speed);
    }

    // Can a driver move to the lane at y without a vehicle having to brake harder than
    // maxBraking for him, and without his being too near the one ahead there.
    private bool RoomAt(HighwayVehicle me, double y, double maxBraking, double time)
    {
        var (lead, gap) = Leader(me, y);
        if (lead is not null && gap < Math.Max(15, 0.8 * me.Speed)) return false;
        if (lead is not null && Idm(me.Speed, me.DesiredSpeed(time), gap, me.Speed - lead.Speed) < -1.0) return false;
        var (behind, behindGap) = Follower(me, y);
        if (behind is null) return true;
        if (behindGap < 8) return false;
        return Idm(behind.Speed, behind.DesiredSpeed(time), behindGap, behind.Speed - me.Speed) >= -maxBraking;
    }

    private void Decide(HighwayVehicle me, double time)
    {
        if (me.Changing) return;
        if (me.Lane == 0)
        {
            var (lead, gap) = Leader(me, me.Y);
            if (lead is null || gap > 70 || lead.Speed > me.DesiredSpeed(time) - 1.5) return;
            if (RoomAt(me, HighwayRoad.LaneCentre(1), 3.0, time)) BeginChange(me, 1, time, LaneChangeSeconds);
        }
        else
        {
            var (lead, gap) = Leader(me, HighwayRoad.LaneCentre(0));
            if (lead is not null && gap < 80 && lead.Speed < me.DesiredSpeed(time) - 1.0) return;
            if (RoomAt(me, HighwayRoad.LaneCentre(0), 2.0, time)) BeginChange(me, 0, time, LaneChangeSeconds);
        }
    }

    private static void BeginChange(HighwayVehicle me, int lane, double time, double seconds)
    {
        me.Changing = true; me.FromY = me.Y; me.ToY = HighwayRoad.LaneCentre(lane);
        me.ChangeStart = time; me.ChangeDuration = seconds;
    }

    // The ego is told to take a lane. It takes a few seconds to get there, as a vehicle does.
    public void ChangeEgoLane(int lane, double seconds = 4.0)
    {
        if (Ego.Changing || lane == Ego.Lane) return;
        BeginChange(Ego, lane, Time, seconds);
    }

    private void Move(HighwayVehicle a, double acceleration, double time)
    {
        a.Acceleration = acceleration;
        var v = Math.Max(0, a.Speed + acceleration * Step);
        a.X += (a.Speed + v) / 2 * Step;
        a.Speed = v;
        if (!a.Changing) { a.Heading = 0; return; }
        var t = (time + Step - a.ChangeStart) / a.ChangeDuration;
        if (t >= 1) { a.Y = a.ToY; a.Changing = false; a.Heading = 0; return; }
        a.Y = a.FromY + (a.ToY - a.FromY) * t * t * (3 - 2 * t);
        a.Heading = Math.Atan2((a.ToY - a.FromY) * 6 * t * (1 - t) / a.ChangeDuration, Math.Max(a.Speed, 1));
    }

    // One step of 0.1 s.
    public void Advance()
    {
        var time = Time;
        var acceleration = new double[npcs.Count];
        for (var i = 0; i < npcs.Count; i++) acceleration[i] = AccelerationOf(npcs[i], time);
        if (NpcsChangeLane) for (var i = 0; i < npcs.Count; i++) if ((stepNumber + i) % 5 == 0) Decide(npcs[i], time);

        for (var i = 0; i < npcs.Count; i++)
        {
            var a = npcs[i];
            // What he wants drifts; and now and then he slows for a while.
            a.Fluctuation = Math.Clamp(a.Fluctuation - a.Fluctuation / 15 * Step + 0.03 * Math.Sqrt(Step) * Gauss(), -0.12, 0.12);
            if (time >= a.SlowUntil && !a.Truck && rng.NextDouble() < 0.0003)
            { a.SlowFactor = 0.75 + 0.15 * rng.NextDouble(); a.SlowUntil = time + 8 + 12 * rng.NextDouble(); }
            HardestBraking = Math.Max(HardestBraking, -acceleration[i]);
            if (Leader(a, a.Y).Vehicle is { } lead && ReferenceEquals(lead, Ego))
                HardestBrakingForEgo = Math.Max(HardestBrakingForEgo, -acceleration[i]);
        }
        for (var i = 0; i < npcs.Count; i++) Move(npcs[i], acceleration[i], time);
        Move(Ego, EgoAcceleration, time);
        stepNumber++;
        Observe();
    }

    private void Observe()
    {
        var all = Everyone().ToList();
        for (var i = 0; i < all.Count; i++)
            for (var j = i + 1; j < all.Count; j++)
            {
                var a = all[i]; var b = all[j];
                if (Math.Abs(a.Y - b.Y) >= (a.Width + b.Width) / 2) continue;
                var gap = Math.Abs(a.X - b.X) - (a.Length + b.Length) / 2;
                var ego = ReferenceEquals(a, Ego) ? b : ReferenceEquals(b, Ego) ? a : null;
                if (ego is not null) NearestToEgo = Math.Min(NearestToEgo, gap);
                if (gap >= 0) continue;
                if (ego is not null) EgoCollisions.Add((Time, ego.Id)); else TrafficCollisions.Add((Time, a.Id, b.Id));
            }
    }

    // What there is around the ego: each other vehicle, how far ahead (negative: behind) of the ego
    // its centre is, its lane, its speed.
    public IReadOnlyList<(string Id, int Lane, double Ahead, double Speed)> Around() =>
        npcs.Select(a => (a.Id, a.Lane, a.X - Ego.X, a.Speed)).OrderBy(a => a.Item3).ToList();
}
