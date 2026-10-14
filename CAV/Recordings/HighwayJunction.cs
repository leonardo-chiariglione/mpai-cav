namespace Mpai.Cav.Recordings;

// WHICH WAY A VEHICLE TURNS at the junction: what its indicator says, and what it does.
public enum Turn { None, Left, Right }

// A PATH ON THE GROUND: straight stretches and circular arcs, followed at the speed a profile gives. A turning vehicle
// of the junction goes along one: a line to where it begins to turn, the arc, a line out. (x east, y north, headings in
// radians anticlockwise from east - the frame of the highway.)
public sealed class HighwayPath
{
    private readonly record struct Piece(double Length, double X, double Y, double Heading, double Curvature);   // Curvature: 1/m, positive to the left

    private readonly List<Piece> pieces = [];
    private double x, y, heading, length;

    public HighwayPath(double x, double y, double heading) { this.x = x; this.y = y; this.heading = heading; }

    public double Length => length;

    public HighwayPath Line(double metres)
    {
        pieces.Add(new Piece(metres, x, y, heading, 0));
        x += metres * Math.Cos(heading); y += metres * Math.Sin(heading); length += metres;
        return this;
    }

    // An arc of the radius, a quarter turn to the left or the right.
    public HighwayPath Arc(bool left, double radius)
    {
        var curvature = (left ? 1 : -1) / radius;
        var metres = Math.PI / 2 * radius;
        pieces.Add(new Piece(metres, x, y, heading, curvature));
        var turned = heading + (left ? Math.PI / 2 : -Math.PI / 2);
        // The centre is at a radius to the side the vehicle turns to.
        var cx = x - Math.Sin(heading) * radius * (left ? 1 : -1);
        var cy = y + Math.Cos(heading) * radius * (left ? 1 : -1);
        // The end of the arc: the centre and the vehicle's bearing from it.
        var bearing = turned + (left ? -Math.PI / 2 : Math.PI / 2);
        x = cx + radius * Math.Cos(bearing); y = cy + radius * Math.Sin(bearing);
        heading = turned; length += metres;
        return this;
    }

    // Where the vehicle is, and which way it points, a distance s along.
    public (double X, double Y, double Heading) At(double s)
    {
        s = Math.Clamp(s, 0, length);
        double start = 0;
        foreach (var p in pieces)
        {
            if (s > start + p.Length) { start += p.Length; continue; }
            var d = s - start;
            if (p.Curvature == 0) return (p.X + d * Math.Cos(p.Heading), p.Y + d * Math.Sin(p.Heading), p.Heading);
            var r = 1 / Math.Abs(p.Curvature); var left = p.Curvature > 0;
            var cx = p.X - Math.Sin(p.Heading) * r * (left ? 1 : -1);
            var cy = p.Y + Math.Cos(p.Heading) * r * (left ? 1 : -1);
            var turn = d / r * (left ? 1 : -1);
            var bearing = p.Heading + turn + (left ? -Math.PI / 2 : Math.PI / 2);
            return (cx + r * Math.Cos(bearing), cy + r * Math.Sin(bearing), p.Heading + turn);
        }
        return (x, y, heading);
    }

    // Where the arc of the path begins and ends (distances along): what a driver signals for.
    public (double Begin, double End) TurnSpan()
    {
        double start = 0;
        foreach (var p in pieces)
        {
            if (p.Curvature != 0) return (start, start + p.Length);
            start += p.Length;
        }
        return (length, length);
    }
}

// THE SPEED A VEHICLE HAS along its path: knots (distance, m/s), the speed between them changing steadily with the
// distance.
public sealed class SpeedProfile(params (double S, double V)[] knots)
{
    public double At(double s)
    {
        if (s <= knots[0].S) return knots[0].V;
        for (var i = 1; i < knots.Length; i++)
            if (s <= knots[i].S) return knots[i - 1].V + (knots[i].V - knots[i - 1].V) * (s - knots[i - 1].S) / Math.Max(1e-9, knots[i].S - knots[i - 1].S);
        return knots[^1].V;
    }
}

// THE JUNCTION: another road crossing the highway at right angles, at X metres along it - two lanes, one each way, the same
// width as the highway's - and what a driver does at it. Drivers keep to the right: northbound on the east side of the cross
// road's axis, southbound on the west.
public sealed record Junction(double X, double TurnRadius = 10)
{
    public const double LaneWidth = HighwayRoad.LaneWidth;
    public double Northbound => X + LaneWidth / 2;
    public double Southbound => X - LaneWidth / 2;

    // THE ROUTES. How far along the highway (x) a vehicle in lane 0 going east, or in lane 1 coming west, begins its turn:
    // so that it ends in the lane of the cross road it turns into.
    public double EastboundLeftTurnStartsAt => Northbound - TurnRadius;
    public double EastboundRightTurnStartsAt => Southbound - TurnRadius;
    public double WestboundLeftTurnStartsAt => Southbound + TurnRadius;
    public double WestboundRightTurnStartsAt => Northbound + TurnRadius;

    // A vehicle that comes the other way (west, in the highway's second lane) and turns: how it goes from where it is.
    public (HighwayPath Path, double TurnBegin) Oncoming(double startX, Turn turn)
    {
        var begin = turn == Turn.Left ? WestboundLeftTurnStartsAt : WestboundRightTurnStartsAt;
        var path = new HighwayPath(startX, HighwayRoad.LaneCentre(1), Math.PI).Line(startX - begin);
        path.Arc(left: turn == Turn.Left, TurnRadius).Line(250);
        return (path, startX - begin);
    }

    // A vehicle that goes the same way as the ego (east, in the first lane) and turns.
    public (HighwayPath Path, double TurnBegin) Leading(double startX, Turn turn)
    {
        var begin = turn == Turn.Left ? EastboundLeftTurnStartsAt : EastboundRightTurnStartsAt;
        var path = new HighwayPath(startX, HighwayRoad.LaneCentre(0), 0).Line(begin - startX);
        path.Arc(left: turn == Turn.Left, TurnRadius).Line(250);
        return (path, begin - startX);
    }

    // A vehicle that comes from the cross road, from the north (southbound) or the south (northbound), and turns onto the
    // highway - to the east (in the first lane) or to the west (in the second).
    public (HighwayPath Path, double TurnBegin) FromSide(bool fromNorth, double distanceToHighway, Turn turn)
    {
        var lane = fromNorth ? Southbound : Northbound;
        double begin, y0;
        if (fromNorth)
        {
            // South-bound, turning left is east (the first lane), right is west (the second).
            begin = turn == Turn.Left ? HighwayRoad.LaneCentre(0) + TurnRadius : HighwayRoad.LaneCentre(1) + TurnRadius;
            y0 = begin + distanceToHighway;
        }
        else
        {
            // North-bound, turning right is east (the first lane), left is west (the second).
            begin = turn == Turn.Right ? HighwayRoad.LaneCentre(0) - TurnRadius : HighwayRoad.LaneCentre(1) - TurnRadius;
            y0 = begin - distanceToHighway;
        }
        var path = new HighwayPath(lane, y0, fromNorth ? -Math.PI / 2 : Math.PI / 2).Line(distanceToHighway);
        path.Arc(left: turn == Turn.Left, TurnRadius).Line(250);
        return (path, distanceToHighway);
    }
}
