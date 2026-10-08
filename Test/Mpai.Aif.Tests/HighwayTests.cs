using Mpai.Cav.Recordings;

namespace Mpai.Aif.Tests;

// THE HIGHWAY OF TEST PHASE 1 (M3253): the road and its traffic, and the four cameras of the rig. The
// traffic must be repeatable from a seed, never collide among itself, and have both slower and faster
// drivers than the CAV; the cameras must be what the specification says - a stereo pair in front, one
// behind, 1920 x 1080, reducible - and see the road and the vehicles where the geometry puts them.
[Trait("Group", "Fast")]
[Trait("Blocks", "No")]
public class HighwayTests
{
    [Fact]
    public void TheSameSeedMakesTheSameTraffic()
    {
        HighwayWorld Run(int seed)
        {
            var w = new HighwayWorld(seed);
            for (var i = 0; i < 600; i++) w.Advance();
            return w;
        }
        var a = Run(5); var b = Run(5); var other = Run(6);
        Assert.Equal(a.Others.Count, b.Others.Count);
        for (var i = 0; i < a.Others.Count; i++)
        {
            Assert.Equal(a.Others[i].X, b.Others[i].X);
            Assert.Equal(a.Others[i].Y, b.Others[i].Y);
            Assert.Equal(a.Others[i].Speed, b.Others[i].Speed);
        }
        Assert.NotEqual(a.Others.Select(v => v.X).Sum(), other.Others.Select(v => v.X).Sum());
    }

    [Fact]
    public void TheTrafficDoesNotCollideAmongItself()
    {
        var dense = new TrafficOptions(FlowPerLane: 1400);
        var swaps = 0;
        foreach (var seed in Enumerable.Range(1, 8))
        {
            var w = new HighwayWorld(seed, dense, egoSpeed: 30);
            for (var i = 0; i < 1200; i++)                              // 120 s, the ego driving blind: out to the left and back
            {
                if (i == 300) w.ChangeEgoLane(1);
                if (i == 450) w.ChangeEgoLane(0);
                w.Advance();
            }
            swaps += w.Others.Count(v => v.Lane == 1);
            Assert.True(w.TrafficCollisions.Count == 0, $"seed {seed}: {string.Join(", ", w.TrafficCollisions.Take(3))}");
        }
        Assert.True(swaps > 0, "nobody in the left lane");
    }

    [Fact]
    public void TheTrafficHasSlowerDriversAndFasterOnesThanTheCav()
    {
        var w = new HighwayWorld(3);                                    // the CAV wants 30 m/s, 108 km/h
        Assert.True(w.Others.Count(v => v.BaseSpeed < 28) >= 2, "no slower driver");
        Assert.True(w.Others.Count(v => v.BaseSpeed > 33) >= 2, "no faster driver");
        Assert.True(w.Others.Count(v => v.Lane == 0) >= 3 && w.Others.Count(v => v.Lane == 1) >= 3, "a lane is empty");
        Assert.Contains(w.Others, v => v.Truck);

        // A driver's speed varies as he drives.
        var one = w.Others.First(v => v.Lane == 1 && !v.Truck);
        var speeds = new List<double>();
        for (var i = 0; i < 600; i++) { w.Advance(); speeds.Add(one.Speed); }
        var mean = speeds.Average();
        var sd = Math.Sqrt(speeds.Select(s => (s - mean) * (s - mean)).Average());
        Assert.True(sd > 0.15, $"speed hardly varies (sd {sd:N3} m/s)");
    }

    [Fact]
    public void TheRigIsAsSpecified()
    {
        var rig = HighwayRig.Standard();
        Assert.Equal(["FrontLeft", "FrontRight", "RearLeft", "RearRight"], rig.Select(c => c.Name));
        Assert.All(rig, c => { Assert.Equal(1920, c.Width); Assert.Equal(1080, c.Height); });
        var front = rig[0]; var rear = rig[2];
        Assert.Equal(1662.8, front.Focal, 0.5);                         // 960 / tan 30
        Assert.Equal(2637.4, rear.Focal, 0.5);                          // 960 / tan 20
        Assert.Equal(0.30, rig[0].Y - rig[1].Y, 1e-9);
        Assert.Equal(0.30, rig[2].Y - rig[3].Y, 1e-9);
        Assert.All(rig.Take(2), c => Assert.Equal(0, c.YawDegrees));
        Assert.All(rig.Skip(2), c => Assert.Equal(180, c.YawDegrees));
        // What the configuration reduces.
        Assert.Equal((640, 360), (front.OutputWidth, front.OutputHeight));
        Assert.Equal(front.Focal / 3, front.OutputFocal, 1e-9);
        var full = HighwayRig.Standard(1, 2);
        Assert.Equal((1920, 1080), (full[0].OutputWidth, full[0].OutputHeight));
        Assert.Equal((960, 540), (full[2].OutputWidth, full[2].OutputHeight));
    }

    [Fact]
    public void TheTwoCamerasOfThePairSeeTheDisparityTheGeometryGives()
    {
        // A car whose rear is 60 m from the front cameras, in the CAV's lane.
        var car = HighwayWorld.Vehicle("A", x: 1.9 + 60 + 2.25, lane: 0, speed: 25);
        var w = HighwayWorld.Scenario([car], egoSpeed: 30, egoLane: 0);
        var rig = HighwayRig.Standard(1, 1);
        var left = HighwayCameras.Capture(rig[0], w.Ego, w.Others);
        var right = HighwayCameras.Capture(rig[1], w.Ego, w.Others);
        var l = left.Vehicles.Single(); var r = right.Vehicles.Single();
        Assert.Equal(60, l.Depth, 0.01);
        var expected = rig[0].Focal * HighwayRig.Baseline / 60;         // 8.3 px
        Assert.Equal(expected, l.Disparity, 0.01);
        Assert.Equal(expected, l.X - r.X, 1.01);                        // the boxes, one against the other, to a pixel
        // The car is in the picture: its middle is not the road's.
        int mx = l.X + l.W / 2, my = l.Y + l.H / 2, below = l.Y + l.H + 12;
        int Red(CameraFrame f, int x, int y) => f.Rgb[(y * f.Width + x) * 3];
        Assert.True(Math.Abs(Red(left, mx, my) - Red(left, mx, below)) > 25, "the car is not drawn");
    }

    [Fact]
    public void TheRearCameraSeesTheFastCarComingUpInTheLeftLane()
    {
        var fast = HighwayWorld.Vehicle("F", x: -100, lane: 1, speed: 38);
        var w = HighwayWorld.Scenario([fast], egoSpeed: 30, egoLane: 0);
        var rig = HighwayRig.Standard(1, 1);
        var frame = HighwayCameras.Capture(rig[2], w.Ego, w.Others);
        var seen = Assert.Single(frame.Vehicles);
        Assert.Equal(95.75, seen.Depth, 0.05);                          // -100 + 2.25 m of its length, from a camera at -2
        Assert.True(seen.InPicture);
        Assert.True(seen.W >= rig[2].Focal * 1.8 / seen.Depth * 0.9, $"{seen.W} px is narrower than a car should be");
        Assert.True(seen.X + seen.W / 2 > frame.Width / 2, "the left lane is on the right of a camera that looks back");
    }

    [Fact]
    public void AReducedPictureHoldsTheSameScene()
    {
        var car = HighwayWorld.Vehicle("A", x: 50, lane: 0, speed: 25);
        var w = HighwayWorld.Scenario([car], egoSpeed: 30, egoLane: 0);
        var full = HighwayCameras.Capture(HighwayRig.Standard(1, 1)[0], w.Ego, w.Others);
        var third = HighwayCameras.Capture(HighwayRig.Standard(3, 3)[0], w.Ego, w.Others);
        Assert.Equal((1920, 1080), (full.Width, full.Height));
        Assert.Equal((640, 360), (third.Width, third.Height));
        Assert.Equal(full.Focal / 3, third.Focal, 1e-9);
        Assert.Equal(full.Vehicles.Single().W / 3.0, third.Vehicles.Single().W, 2.0);
        Assert.Equal(full.Vehicles.Single().Depth, third.Vehicles.Single().Depth, 1e-9);
    }

    [Fact]
    public void TheRoadIsWhereTheGeometryPutsIt()
    {
        // The CAV in the left lane, a front camera: 20 m ahead, the guard rail, the lane's asphalt and, past the
        // rail, grass - each at the pixel the camera model gives.
        var w = HighwayWorld.Scenario([], egoSpeed: 30, egoLane: 1);
        var cam = HighwayRig.Standard(1, 1)[0];
        var frame = HighwayCameras.Capture(cam, w.Ego, w.Others);
        double f = cam.Focal, camY = 3.5 + 0.15;
        (int, int) At(double depth, double y, double z) =>
            ((int)Math.Round(960 + f * (camY - y) / depth), (int)Math.Round(540 - f * (z - 1.40) / depth));
        byte[] Pixel((int X, int Y) p) => [frame.Rgb[(p.Y * frame.Width + p.X) * 3], frame.Rgb[(p.Y * frame.Width + p.X) * 3 + 1], frame.Rgb[(p.Y * frame.Width + p.X) * 3 + 2]];

        var rail = Pixel(At(20, HighwayRoad.GuardRail, 0.65));
        Assert.InRange(rail[0], 140, 205);                              // the silver beam
        Assert.True(Math.Abs(rail[0] - rail[2]) < 12);

        var asphalt = Pixel(At(20, 3.5, 0));
        Assert.InRange(asphalt[0], 80, 100);

        var grass = Pixel(At(20, 11.35, 0));                            // seen over the top of the rail
        Assert.True(grass[1] > grass[0] + 40 && grass[1] > grass[2] + 40, $"not grass: {grass[0]},{grass[1]},{grass[2]}");

        var sky = Pixel((960, 100));
        Assert.True(sky[2] > sky[0] + 30, "the sky is not blue");
    }

    // Pictures to look at: HighwayTests writes them where MPAI_HIGHWAY_SAMPLES says, and does nothing otherwise.
    [Fact]
    public void Samples()
    {
        var dir = Environment.GetEnvironmentVariable("MPAI_HIGHWAY_SAMPLES");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var w = new HighwayWorld(7, new TrafficOptions(FlowPerLane: 1100), egoSpeed: 30);
        for (var i = 0; i < 300; i++) w.Advance();
        foreach (var reduction in new[] { 3, 1 })
            foreach (var cam in HighwayRig.Standard(reduction, reduction))
            {
                var frame = HighwayCameras.Capture(cam, w.Ego, w.Others, new Random(cam.Name.GetHashCode()));
                File.WriteAllBytes(Path.Combine(dir, $"{cam.Name}-r{reduction}.png"), frame.Png);
            }
        // And the same road from the left lane, a car ahead and a fast one behind.
        var scene = HighwayWorld.Scenario([
            HighwayWorld.Vehicle("Slow", 55, 0, 24, truck: true), HighwayWorld.Vehicle("Ahead", 38, 1, 33, paint: CameraRenderer.DarkRed),
            HighwayWorld.Vehicle("Behind", -70, 1, 40, paint: CameraRenderer.Blue), HighwayWorld.Vehicle("Beside", -9, 0, 31)], 30, 0);
        foreach (var cam in HighwayRig.Standard(3, 3))
            File.WriteAllBytes(Path.Combine(dir, $"scene-{cam.Name}.png"), HighwayCameras.Capture(cam, scene.Ego, scene.Others, new Random(5)).Png);
    }
}
