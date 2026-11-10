using System;
using System.Linq;

namespace Mpai.Core.OSD;

// WHERE A PART STANDS IN A WHOLE (the author, 2026/11/08: everything in ASM is Full, a Basic Object or Scene is the case of one part): the placement of a part inside an Object or a Scene,
// composed with the placement of that Object or Scene in its own frame. Used by Audio Object Editing (a Full Audio Object stored from its parts), Audio Scene Editing and Speech Scene
// Editing (a Full Object placed part by part).
public static class Placements
{
    // Where an inner placement stands in the frame of an outer one: position and yaw added, the time of the outer added to that of the inner.
    public static SpaceTime Within(SpaceTime? outer, SpaceTime? inner)
    {
        static double[] P(SpaceTime? s) => s?.SpatialAttitude1?.Position?.CartPosition is { Length: >= 3 } c ? c : new double[3];
        static double Yaw(SpaceTime? s) => s?.SpatialAttitude1?.Orientation?.EulerAngles is { Length: >= 3 } a ? a[2] : 0;
        static double Seconds(double v, string? unit) => unit switch { "01" => v / 1e3, "10" => v / 1e6, "11" => v / 1e9, _ => v };
        static TimeSegment? Segment(SpaceTime? s) => s?.Time?.SimpleTimeData?.FirstOrDefault(x => x.TimeType == false);
        double Start(SpaceTime? s) => Segment(s) is { } g ? Seconds(g.StartTime, g.TimeUnit) : 0;
        double? End(SpaceTime? s) => Segment(s) is { } g && g.EndTime > g.StartTime ? Seconds(g.EndTime, g.TimeUnit) : null;

        var (o, i) = (P(outer), P(inner));
        var from = Start(outer);
        var begins = from + Start(inner);
        var ends = End(inner) is { } e ? from + e : (double?)null;
        return new SpaceTime
        {
            SpatialAttitude1 = new SpatialAttitude
            {
                ObjectSpatialAttitudeID = Guid.NewGuid().ToString(),
                Position = new Position { PositionID = Guid.NewGuid().ToString(), CartPosition = [o[0] + i[0], o[1] + i[1], o[2] + i[2]] },
                Orientation = new Orientation { OrientationID = Guid.NewGuid().ToString(), EulerAngles = [0, 0, Yaw(outer) + Yaw(inner)] }
            },
            Time = new SimpleTime
            {
                SimpleTimeID = Guid.NewGuid().ToString(),
                SimpleTimeData = { new TimeSegment { FlagsByte = 0, StartTime = begins, EndTime = ends is { } x && x > begins ? x : begins, AccuracyMode = "single", AccuracyPlusMinus = 0.0, TimeType = false, TimeUnit = "00" } }
            }
        };
    }
}
