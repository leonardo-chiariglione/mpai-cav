using Mpai.Cav.Driving;

namespace Mpai.Cav.DrivingSim;

// THE TYPICAL TRAFFIC CONTEXTS of request N-19, each with variants (speeds, distances, timing). Right-hand traffic.
// Frame: East (X), North (Y), metres; headings anticlockwise from East (ISO 8855).
public static class Contexts
{
    public static IEnumerable<Scenario> All() =>
        FollowingABrakingCar().Concat(CutIn()).Concat(OnRamp()).Concat(StopAndGo()).Concat(Roadworks())
            .Concat(OncomingLeftTurn()).Concat(Roundabout()).Concat(PedestrianBehindVan()).Concat(CyclistNarrowRoad()).Concat(BrokenDownAroundBend());

    // ---------- road builders ----------

    // A road with one lane each way: E going East at Y=0, W going West beside it.
    private static RoadMap TwoWay(Polyline east, double width, double limit)
    {
        var map = new RoadMap();
        var e = map.Add(new Lane { Id = "E", Centre = east, Width = width, SpeedLimit = limit });
        var w = map.Add(new Lane { Id = "W", Centre = new Polyline(east.Offset(width).Points.Reverse()), Width = width, SpeedLimit = limit });
        e.Oncoming = "W"; w.Oncoming = "E";
        return map;
    }

    // Two lanes going East: R (right) at Y=0 and L at Y=3.5.
    private static RoadMap TwoLanes(double length, double limit)
    {
        var map = new RoadMap();
        map.Add(new Lane { Id = "R", Centre = Polyline.Straight(new(0, 0), new(length, 0)), SpeedLimit = limit });
        map.Add(new Lane { Id = "L", Centre = Polyline.Straight(new(0, 3.5), new(length, 3.5)), SpeedLimit = limit });
        map.SideBySide("L", "R");
        return map;
    }

    private static Agent Car(int id, Polyline path, double s, double v, double desired, RoadUserKind kind = RoadUserKind.Car) =>
        new() { Id = id, Kind = kind, Path = path, S = s, V = v, DesiredSpeed = desired, Length = kind == RoadUserKind.Truck ? 10 : 4.5, Width = kind == RoadUserKind.Truck ? 2.5 : 1.8 };

    // ---------- 1. following a car that brakes ----------
    private static IEnumerable<Scenario> FollowingABrakingCar()
    {
        foreach (var (v, gap, decel) in new[] { (14.0, 25.0, 4.0), (22.0, 35.0, 6.0), (28.0, 45.0, 8.0), (22.0, 15.0, 8.0) })
        {
            var map = TwoWay(Polyline.Straight(new(0, 0), new(1500, 0)), 3.5, v);
            var e = map.Lanes["E"].Centre;
            var agents = new List<Agent> { new() { Id = 1, Kind = RoadUserKind.Car, Path = e, S = 50 + 4.55 + gap, V = v, DesiredSpeed = v, BrakeAt = 6, BrakeDecel = decel, BrakeHold = 6 } };
            yield return new Scenario
            {
                Context = "1-following-a-car-that-brakes", Variant = $"v{v:F0}-gap{gap:F0}-brake{decel:F0}", Map = map, Agents = agents,
                StartLane = "E", StartS = 50, StartSpeed = v, DestLane = "E", DestS = 1300, MaxTime = 120,
                Parameters = new() { ["speed m/s"] = v, ["gap m"] = gap, ["braking m/s2"] = decel, ["stopped s"] = 6 },
            };
        }
    }

    // ---------- 2. a car cutting in close ahead ----------
    private static IEnumerable<Scenario> CutIn()
    {
        foreach (var (gap, dv, dur) in new[] { (20.0, 8.0, 3.0), (12.0, 5.0, 2.5), (8.0, 3.0, 2.0), (5.0, 0.0, 1.5) })
        {
            var map = TwoLanes(1500, 25);
            const double v = 22;
            var agents = new List<Agent> {
                new() { Id = 1, Kind = RoadUserKind.Car, Path = map.Lanes["L"].Centre, S = 50 + 4.55 + gap, V = v - dv, DesiredSpeed = v - dv,
                        LaneChange = (1.0, map.Lanes["R"].Centre, dur) } };
            yield return new Scenario
            {
                Context = "2-car-cutting-in", Variant = $"gap{gap:F0}-slower{dv:F0}-in{dur:F1}s", Map = map, Agents = agents,
                StartLane = "R", StartS = 50, StartSpeed = v, DestLane = "R", DestS = 1200, MaxTime = 90,
                Parameters = new() { ["CAV speed m/s"] = v, ["gap m"] = gap, ["slower by m/s"] = dv, ["lane change s"] = dur },
            };
        }
    }

    // ---------- 3. merging from an on-ramp ----------
    private static IEnumerable<Scenario> OnRamp()
    {
        foreach (var (spacing, vm, truck) in new[] { (60.0, 25.0, false), (40.0, 22.0, false), (25.0, 22.0, false), (30.0, 12.0, true) })
        {
            var map = new RoadMap();
            map.Add(new Lane { Id = "Ma", Centre = Polyline.Straight(new(0, 0), new(300, 0)), SpeedLimit = 25 });
            map.Add(new Lane { Id = "Mb", Centre = Polyline.Straight(new(300, 0), new(450, 0)), SpeedLimit = 25 });
            map.Add(new Lane { Id = "Mc", Centre = Polyline.Straight(new(450, 0), new(1500, 0)), SpeedLimit = 25 });
            map.Add(new Lane { Id = "A", Centre = Polyline.Straight(new(100, -40), new(300, -3.5)), SpeedLimit = 20, Kind = LaneKind.Ramp });
            map.Add(new Lane { Id = "P", Centre = Polyline.Straight(new(300, -3.5), new(450, -3.5)), SpeedLimit = 25, Kind = LaneKind.Ramp });
            map.Connect("Ma", "Mb"); map.Connect("Mb", "Mc"); map.Connect("A", "P");
            map.SideBySide("Mb", "P");
            var main = RoadMap.Line([map.Lanes["Ma"], map.Lanes["Mb"], map.Lanes["Mc"]]);
            var agents = new List<Agent>();
            int id = 1;
            for (double s = 600; s >= -400; s -= spacing)
            {
                bool isTruck = truck && id % 3 == 2;
                agents.Add(Car(id++, main, s, vm, vm, isTruck ? RoadUserKind.Truck : RoadUserKind.Car));
            }
            yield return new Scenario
            {
                Context = "3-merging-from-on-ramp", Variant = $"spacing{spacing:F0}-v{vm:F0}{(truck ? "-trucks" : "")}", Map = map, Agents = agents,
                StartLane = "A", StartS = 0, StartSpeed = 15, DestLane = "Mc", DestS = 500, MaxTime = 90,
                Parameters = new() { ["main spacing m"] = spacing, ["main speed m/s"] = vm, ["trucks"] = truck },
            };
        }
    }

    // ---------- 4. a traffic jam (stop and go) ----------
    private static IEnumerable<Scenario> StopAndGo()
    {
        foreach (var (period, mean, leftFaster) in new[] { (20.0, 4.0, false), (12.0, 3.0, false), (30.0, 5.0, true) })
        {
            var map = TwoLanes(1500, 25);
            var agents = new List<Agent>();
            int id = 1;
            foreach (var (laneId, phase, m) in new[] { ("R", 0.0, mean), ("L", 2.0, leftFaster ? mean + 3 : mean) })
            {
                var path = map.Lanes[laneId].Centre;
                double front = 200;
                agents.Add(new Agent { Id = id++, Kind = RoadUserKind.Car, Path = path, S = front, V = m, DesiredSpeed = m,
                                       DesiredSpeedAt = t => Math.Clamp(m + (m + 2) * Math.Sin(2 * Math.PI * (t + phase) / period), 0, 2 * m + 2) });
                for (int i = 1; i <= 6; i++) agents.Add(Car(id++, path, front - i * 9, m, 14));
            }
            yield return new Scenario
            {
                Context = "4-traffic-jam-stop-and-go", Variant = $"period{period:F0}-mean{mean:F0}{(leftFaster ? "-left-faster" : "")}", Map = map, Agents = agents,
                StartLane = "R", StartS = 20, StartSpeed = 12, DestLane = "R", DestS = 450, MaxTime = 180,
                Parameters = new() { ["period s"] = period, ["mean speed m/s"] = mean, ["left lane faster"] = leftFaster },
            };
        }
    }

    // ---------- 5. a lane closed by roadworks ----------
    private static IEnumerable<Scenario> Roadworks()
    {
        foreach (var spacing in new[] { 0.0, 50.0, 30.0, 18.0 })
        {
            var map = TwoLanes(1000, 13.9);
            var r = map.Lanes["R"].Centre; var l = map.Lanes["L"].Centre;
            var agents = new List<Agent>();
            int id = 1;
            for (double x = 300; x < 360; x += 10)
                agents.Add(new Agent { Id = id++, Kind = RoadUserKind.Obstacle, Path = r, S = x + 5, Length = 9.5, Width = 1.6, Parked = true });
            if (spacing > 0)
                for (double s = 500; s >= -300; s -= spacing) agents.Add(Car(id++, l, s, 12, 12));
            yield return new Scenario
            {
                Context = "5-lane-closed-by-roadworks", Variant = spacing == 0 ? "no-traffic" : $"traffic-every{spacing:F0}m", Map = map, Agents = agents,
                StartLane = "R", StartS = 100, StartSpeed = 13, DestLane = "R", DestS = 700, MaxTime = 120,
                Parameters = new() { ["closed from x"] = 300, ["closed to x"] = 360, ["traffic spacing m (0 = none)"] = spacing, ["traffic speed m/s"] = 12 },
            };
        }
    }

    // ---------- 6. an oncoming car turning left across the CAV's way at a junction ----------
    private static IEnumerable<Scenario> OncomingLeftTurn()
    {
        foreach (var (variant, turns, givesWay, indicator, startS) in new[] {
                     ("turns-gives-way-indicator", true, true, 1, 330.0),
                     ("turns-no-give-way-indicator", true, false, 1, 330.0),
                     ("turns-no-give-way-no-indicator", true, false, 0, 330.0),
                     ("turns-no-give-way-late", true, false, 0, 300.0),
                     ("goes-straight", false, false, 0, 330.0) })
        {
            var map = new RoadMap();
            const double lim = 13.9;
            map.Add(new Lane { Id = "Ea", Centre = Polyline.Straight(new(0, 0), new(490, 0)), SpeedLimit = lim });
            map.Add(new Lane { Id = "JE", Centre = Polyline.Straight(new(490, 0), new(510, 0)), SpeedLimit = lim, Kind = LaneKind.Junction });
            map.Add(new Lane { Id = "Eb", Centre = Polyline.Straight(new(510, 0), new(1000, 0)), SpeedLimit = lim });
            map.Add(new Lane { Id = "Wa", Centre = Polyline.Straight(new(1000, 3.5), new(510, 3.5)), SpeedLimit = lim });
            map.Add(new Lane { Id = "JW", Centre = Polyline.Straight(new(510, 3.5), new(490, 3.5)), SpeedLimit = lim, Kind = LaneKind.Junction });
            map.Add(new Lane { Id = "Wb", Centre = Polyline.Straight(new(490, 3.5), new(0, 3.5)), SpeedLimit = lim });
            map.Add(new Lane { Id = "JWS", Centre = Polyline.Arc(new(510, -6.5), 10, Math.PI / 2, Math.PI), SpeedLimit = 6, Kind = LaneKind.Junction });
            map.Add(new Lane { Id = "Sout", Centre = Polyline.Straight(new(500, -6.5), new(500, -300)), SpeedLimit = lim });
            map.Connect("Ea", "JE"); map.Connect("JE", "Eb"); map.Connect("Wa", "JW", "JWS"); map.Connect("JW", "Wb"); map.Connect("JWS", "Sout");
            map.Lanes["Ea"].Oncoming = "Wb"; map.Lanes["Wb"].Oncoming = "Ea"; map.Lanes["Eb"].Oncoming = "Wa"; map.Lanes["Wa"].Oncoming = "Eb";
            map.Lanes["JE"].Oncoming = "JW"; map.Lanes["JW"].Oncoming = "JE";
            var path = turns ? RoadMap.Line([map.Lanes["Wa"], map.Lanes["JWS"], map.Lanes["Sout"]]) : RoadMap.Line([map.Lanes["Wa"], map.Lanes["JW"], map.Lanes["Wb"]]);
            var conflict = new Vec2(502.4, 0);
            var car = new Agent { Id = 1, Kind = RoadUserKind.Car, Path = path, S = startS, V = 12, DesiredSpeed = 12, Indicator = indicator,
                                  GiveWay = givesWay ? (489.0, conflict, 5.0) : null };
            yield return new Scenario
            {
                Context = "6-oncoming-car-turning-left", Variant = variant, Map = map, Agents = [car],
                StartLane = "Ea", StartS = 300, StartSpeed = 13, DestLane = "Eb", DestS = 300, MaxTime = 90,
                Parameters = new() { ["turns"] = turns, ["gives way"] = givesWay, ["indicator"] = indicator, ["car starts m before the junction"] = 490 - startS, ["car speed m/s"] = 12 },
            };
        }
    }

    // ---------- 7. a roundabout ----------
    private static IEnumerable<Scenario> Roundabout()
    {
        foreach (var (variant, exit, streams, every) in new[] {
                     ("empty-right-turn", 0, Array.Empty<int>(), 0.0),
                     ("stream-from-west-every35m-straight", 1, new[] { 2 }, 35.0),
                     ("stream-from-west-every20m-right-turn", 0, new[] { 2 }, 20.0),
                     ("stream-from-west-every50m-left-turn", 2, new[] { 2 }, 50.0),
                     ("stream-from-north-every30m-left-turn", 2, new[] { 1 }, 30.0) })
        {
            var map = RoundaboutMap();
            var agents = new List<Agent>();
            int id = 1;
            foreach (var from in streams)
            {
                // from arm 'from' to the exit three quarters round: from the west (2) to the north (1), from the north (1) to the east (0); both pass the CAV's entry (arm 3)
                int to = (from + 3) % 4;
                var route = map.Route($"In{from}", $"Out{to}")!;
                var line = RoadMap.Line(route.Select(r => map.Lanes[r]).ToList());
                // a car entering gives way to the CAV on the ring (it waits at the line while the CAV is within 3 s of the entry)
                var entry = map.Lanes[$"In{from}"].Centre;
                for (double s = 150; s >= -400; s -= every)
                    agents.Add(s < entry.Length - 3
                        ? new Agent { Id = id++, Kind = RoadUserKind.Car, Path = line, S = s, V = 8, DesiredSpeed = 10, GiveWay = (entry.Length - 5.0, entry.At(entry.Length), 3.0) }
                        : Car(id++, line, s, 8, 10));
            }
            yield return new Scenario
            {
                Context = "7-roundabout", Variant = variant, Map = map, Agents = agents,
                StartLane = "In3", StartS = 90, StartSpeed = 12, DestLane = $"Out{exit}", DestS = 100, MaxTime = 120,
                Parameters = new() { ["radius m"] = 20, ["CAV exit (0 E, 1 N, 2 W)"] = exit, ["streams from arms"] = string.Join(",", streams), ["spacing m"] = every },
            };
        }
    }

    public static RoadMap RoundaboutMap()
    {
        var map = new RoadMap();
        const double R = 20, alpha = 15 * Math.PI / 180;
        double Th(int k) => k * Math.PI / 2;
        for (int k = 0; k < 4; k++)
        {
            double th = Th(k);
            var u = new Vec2(Math.Cos(th), Math.Sin(th));
            var ring = (double a) => new Vec2(Math.Cos(a), Math.Sin(a)) * R;
            // ring: A_k passes arm k (from its exit to its entry), B_k goes on to the exit of the next arm
            map.Add(new Lane { Id = $"A{k}", Centre = Polyline.Arc(new(0, 0), R, th - alpha, th + alpha, 1), SpeedLimit = 8, Kind = LaneKind.Roundabout });
            map.Add(new Lane { Id = $"B{k}", Centre = Polyline.Arc(new(0, 0), R, th + alpha, th + Math.PI / 2 - alpha, 1.5), SpeedLimit = 8, Kind = LaneKind.Roundabout });
            var right = new Vec2(Math.Sin(th), -Math.Cos(th));       // right of the way out
            map.Add(new Lane { Id = $"Out{k}", Centre = Polyline.Straight(ring(th - alpha), u * 200 + right * 3), SpeedLimit = 13.9 });
            map.Add(new Lane { Id = $"In{k}", Centre = Polyline.Straight(u * 200 - right * 3, ring(th + alpha)), SpeedLimit = 13.9, YieldAtEnd = true });
        }
        for (int k = 0; k < 4; k++)
        {
            map.Connect($"A{k}", $"B{k}");
            map.Connect($"B{k}", $"A{(k + 1) % 4}", $"Out{(k + 1) % 4}");
            map.Connect($"In{k}", $"B{k}");
        }
        return map;
    }

    // ---------- 8. a pedestrian stepping out from behind a parked van ----------
    private static IEnumerable<Scenario> PedestrianBehindVan()
    {
        foreach (var (variant, trigger, speed) in new[] { ("steps-out-at-20m", 20.0, 1.4), ("steps-out-at-12m", 12.0, 1.4), ("child-runs-at-25m", 25.0, 2.5), ("steps-out-at-8m", 8.0, 1.4), ("no-pedestrian", 0.0, 0.0) })
        {
            var map = TwoWay(Polyline.Straight(new(0, 0), new(600, 0)), 3.5, 13.9);
            map.Walkways.Add(new Walkway("pavement", Polyline.Straight(new(0, -4.5), new(600, -4.5)), false));
            // the van parked in a parking strip: its side 0.95 m from the edge of the lane (Y -1.75)
            var agents = new List<Agent> { new() { Id = 1, Kind = RoadUserKind.Truck, Path = Polyline.Straight(new(0, -3.8), new(600, -3.8)), S = 200, Length = 6, Width = 2.2, Parked = true } };
            if (speed > 0)
            {
                var start = new Vec2(204, -4.3);
                agents.Add(new Agent { Id = 2, Kind = RoadUserKind.Pedestrian, Path = Polyline.Straight(start, new(204, 9)), S = 0, V = speed, Length = 0.5, Width = 0.5,
                                       StartWhen = w => (start - w.Ego.Position).Dot(Vec2.FromHeading(w.Ego.Heading)) < trigger });
            }
            yield return new Scenario
            {
                Context = "8-pedestrian-behind-parked-van", Variant = variant, Map = map, Agents = agents,
                StartLane = "E", StartS = 50, StartSpeed = 13.9, DestLane = "E", DestS = 450, MaxTime = 90,
                Parameters = new() { ["van at x"] = 200, ["van side from lane edge m"] = 0.95, ["steps out when the CAV is m away"] = trigger, ["walking speed m/s"] = speed },
            };
        }
    }

    // ---------- 9. a cyclist on a narrow road with oncoming traffic ----------
    private static IEnumerable<Scenario> CyclistNarrowRoad()
    {
        foreach (var (variant, cyclist, spacing) in new[] { ("no-oncoming", 5.5, 0.0), ("oncoming-every150m", 5.5, 150.0), ("oncoming-every70m", 5.5, 70.0), ("slow-cyclist-oncoming-every100m", 3.0, 100.0) })
        {
            var map = TwoWay(Polyline.Straight(new(0, 0), new(1500, 0)), 3.0, 19.4);
            var agents = new List<Agent> { new() { Id = 1, Kind = RoadUserKind.Cyclist, Path = map.Lanes["E"].Centre, S = 150, D = -0.7, V = cyclist, DesiredSpeed = cyclist, Length = 1.8, Width = 0.7 } };
            int id = 2;
            if (spacing > 0) for (double s = 1200; s >= 0; s -= spacing) agents.Add(Car(id++, map.Lanes["W"].Centre, s, 18, 18));
            yield return new Scenario
            {
                Context = "9-cyclist-narrow-road", Variant = variant, Map = map, Agents = agents,
                StartLane = "E", StartS = 20, StartSpeed = 15, DestLane = "E", DestS = 1100, MaxTime = 180,
                Parameters = new() { ["lane width m"] = 3.0, ["cyclist speed m/s"] = cyclist, ["oncoming spacing m (0 = none)"] = spacing, ["oncoming speed m/s"] = 18 },
            };
        }
    }

    // ---------- 10. a broken-down vehicle around a bend ----------
    private static IEnumerable<Scenario> BrokenDownAroundBend()
    {
        foreach (var (variant, at, spacing) in new[] { ("after-bend-no-oncoming", 472.0, 0.0), ("after-bend-oncoming-every150m", 472.0, 150.0), ("after-bend-oncoming-every80m", 472.0, 80.0), ("in-bend-oncoming-every150m", 420.0, 150.0) })
        {
            var e = Polyline.Straight(new(0, 0), new(300, 0)).Then(Polyline.Arc(new(300, 100), 100, -Math.PI / 2, 0, 3)).Then(Polyline.Straight(new(400, 100), new(400, 700)));
            var map = TwoWay(e, 3.5, 22);
            var agents = new List<Agent> { new() { Id = 1, Kind = RoadUserKind.Car, Path = e, S = at, Parked = true } };
            int id = 2;
            if (spacing > 0) for (double s = 800; s >= 0; s -= spacing) agents.Add(Car(id++, map.Lanes["W"].Centre, s, 19, 19));
            yield return new Scenario
            {
                Context = "10-broken-down-vehicle-around-bend", Variant = variant, Map = map, Agents = agents,
                Occluders = [new Box(new(340, 50), 0, 40, 40)],
                StartLane = "E", StartS = 100, StartSpeed = 20, DestLane = "E", DestS = 700, MaxTime = 150,
                Parameters = new() { ["bend radius m"] = 100, ["broken-down car at m along the lane"] = at, ["oncoming spacing m (0 = none)"] = spacing, ["building inside the bend"] = "40 x 40 m at (340, 50)" },
            };
        }
    }
}
