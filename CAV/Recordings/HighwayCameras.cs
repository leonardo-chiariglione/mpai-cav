namespace Mpai.Cav.Recordings;

// A CAMERA OF THE RIG: where it is on the car (metres: x forward of the car's centre, y to the left,
// z above the road), which way it looks (0: forward, 180: backward), how wide it sees, and what it
// captures - always at full resolution, 1920 x 1080 - and by what factor the configuration reduces
// that (1: not at all; 3: to 640 x 360, which is what the vehicle detector takes).
public sealed record RigCamera(string Name, double X, double Y, double Z, double YawDegrees, double HFovDegrees,
    int Width = 1920, int Height = 1080, int Reduction = 1)
{
    public double Focal => Width / 2.0 / Math.Tan(HFovDegrees * Math.PI / 360);          // pixels, at capture
    public int OutputWidth => Width / Reduction;
    public int OutputHeight => Height / Reduction;
    public double OutputFocal => Focal / Reduction;                                       // pixels, after reduction
}

// THE RIG OF THE HIGHWAY TESTS (M3253): two cameras in front, two behind, each pair a stereo pair 0.30 m
// apart, as the author asked (2026/10/08). The front pair sees 60 degrees; the rear pair 40, to reach far
// down the lanes behind, where the vehicle that would have to brake for an overtaking CAV comes from.
public static class HighwayRig
{
    public const double Baseline = 0.30;

    public static IReadOnlyList<RigCamera> Standard(int frontReduction = 3, int rearReduction = 3) =>
    [
        new("FrontLeft",   1.9,  Baseline / 2, 1.40,   0, 60, Reduction: frontReduction),
        new("FrontRight",  1.9, -Baseline / 2, 1.40,   0, 60, Reduction: frontReduction),
        new("RearLeft",   -2.0,  Baseline / 2, 1.30, 180, 40, Reduction: rearReduction),
        new("RearRight",  -2.0, -Baseline / 2, 1.30, 180, 40, Reduction: rearReduction),
    ];
}

// A VEHICLE AS A CAMERA SEES IT: its box in the picture (output pixels, as projected - it may reach beyond the
// edges), the distance of its nearest end along the camera's axis, what the disparity between the two
// cameras of the pair is for that distance (output pixels), and whether any of it is in the picture.
public sealed record SeenVehicle(string Id, int X, int Y, int W, int H, double Depth, double Disparity, bool Truck, bool InPicture);

public sealed record CameraFrame(string Camera, int Width, int Height, double Focal, byte[] Rgb, IReadOnlyList<SeenVehicle> Vehicles)
{
    public byte[] Png => Mpai.Cav.Recordings.Png.Encode(Width, Height, Rgb);
}

// WHAT A CAMERA OF THE RIG SEES (M3253). A pinhole camera on the ego, level, looking along the road or back
// along it: sky, the road - two lanes, edge lines, the dashed line between them, a shoulder each side -, the
// guard rail on the left, grass, and the vehicles, each as a box drawn from the side it shows: the rear of
// those ahead, the front of those behind, their flank when the camera is not between their sides, the farthest
// first. The capture is clean at full resolution; it is then reduced by the camera's factor, and given the grain
// and the blur of a real camera when a source of grain is given (each camera its own: the noise of two cameras
// is not the same noise).
public static class HighwayCameras
{
    public static CameraFrame Capture(RigCamera cam, HighwayVehicle ego, IEnumerable<HighwayVehicle> others, Random? grain = null)
    {
        int W = cam.Width, H = cam.Height;
        double f = cam.Focal;
        var rgb = new byte[W * H * 3];

        // The camera's place and the way it looks (the ego's heading turns both).
        double phi = ego.Heading, c = Math.Cos(phi), s = Math.Sin(phi);
        double cx = ego.X + c * cam.X - s * cam.Y, cy = ego.Y + s * cam.X + c * cam.Y, cz = cam.Z;
        double psi = phi + cam.YawDegrees * Math.PI / 180;
        double dx = Math.Cos(psi), dy = Math.Sin(psi), rx = Math.Sin(psi), ry = -Math.Cos(psi);   // along, and to the right

        (double U, double V, double Depth) Project(double X, double Y, double Z)
        {
            var depth = (X - cx) * dx + (Y - cy) * dy;
            var right = (X - cx) * rx + (Y - cy) * ry;
            return (W / 2.0 + f * right / depth, H / 2.0 - f * (Z - cz) / depth, depth);
        }

        Background(rgb, W, H, f, cx, cy, cz, dx, dy, rx, ry);

        void Put(int px, int py, int r, int g, int b)
        {
            if (px < 0 || px >= W || py < 0 || py >= H) return;
            var at = (py * W + px) * 3;
            rgb[at] = (byte)Math.Clamp(r, 0, 255); rgb[at + 1] = (byte)Math.Clamp(g, 0, 255); rgb[at + 2] = (byte)Math.Clamp(b, 0, 255);
        }

        var seen = new List<SeenVehicle>();
        var order = others
            .Select(v => (Vehicle: v, Depth: (v.X - cx) * dx + (v.Y - cy) * dy))
            .Where(a => a.Depth > 1.0 && a.Depth < 700)
            .OrderByDescending(a => a.Depth).ToList();
        foreach (var (v, _) in order)
        {
            double xRear = v.X - v.Length / 2, xFront = v.X + v.Length / 2;
            double Depth(double x) => (x - cx) * dx + (v.Y - cy) * dy;
            var showRear = Depth(xRear) < Depth(xFront);                 // the nearer end is the one in view
            var xf = showRear ? xRear : xFront;
            var near = Depth(xf);
            if (near < 1.0) continue;

            // The flank, if the camera is outside the vehicle's width: drawn first, the end over it.
            double? plane = cy > v.Y + v.Width / 2 ? v.Y + v.Width / 2 : cy < v.Y - v.Width / 2 ? v.Y - v.Width / 2 : null;
            if (plane is { } yp) Flank(rgb, W, H, f, cx, cy, cz, dx, dy, rx, ry, v, yp, xf, showRear ? xFront : xRear);

            var corners = new List<(double U, double V, double D)>();
            foreach (var x in new[] { xRear, xFront })
                foreach (var y in new[] { v.Y - v.Width / 2, v.Y + v.Width / 2 })
                    foreach (var z in new[] { 0.0, v.Height })
                        if (Depth(x) > 0.2) corners.Add(Project(x, y, z));
            if (corners.Count == 0) continue;

            var endCorners = new[] { (v.Y - v.Width / 2, 0.0), (v.Y + v.Width / 2, 0.0), (v.Y - v.Width / 2, v.Height), (v.Y + v.Width / 2, v.Height) }
                .Select(p => Project(xf, p.Item1, p.Item2)).ToList();
            int left = (int)Math.Round(endCorners.Min(p => p.U)), top = (int)Math.Round(endCorners.Min(p => p.V));
            int cw = (int)Math.Round(endCorners.Max(p => p.U)) - left, ch = (int)Math.Round(endCorners.Max(p => p.V)) - top;
            if (cw >= 1 && ch >= 1 && left < W && left + cw > 0 && top < H && top + ch > 0)
            {
                if (v.Truck) Shapes.TruckEnd(left, top, cw, ch, v.Paint, !showRear, Put);
                else if (showRear) CameraRenderer.Car(left, top, cw, ch, v.Paint, Put);
                else Shapes.CarFront(left, top, cw, ch, v.Paint, Put);
            }

            int bl = (int)Math.Floor(corners.Min(p => p.U)), bt = (int)Math.Floor(corners.Min(p => p.V));
            int bw = (int)Math.Ceiling(corners.Max(p => p.U)) - bl, bh = (int)Math.Ceiling(corners.Max(p => p.V)) - bt;
            var k = cam.Reduction;
            seen.Add(new SeenVehicle(v.Id, bl / k, bt / k, Math.Max(1, bw / k), Math.Max(1, bh / k), near,
                cam.OutputFocal * HighwayRig.Baseline / near, v.Truck, bl < W && bl + bw > 0 && bt < H && bt + bh > 0));
        }

        var reduced = Reduce(rgb, W, H, cam.Reduction);
        if (grain is not null) Finish(reduced, cam.OutputWidth, cam.OutputHeight, grain);
        return new CameraFrame(cam.Name, cam.OutputWidth, cam.OutputHeight, cam.OutputFocal, reduced, seen);
    }

    // ------------------------------------------------------------------------ the scene behind the vehicles
    private static void Background(byte[] rgb, int W, int H, double f, double cx, double cy, double cz,
        double dx, double dy, double rx, double ry)
    {
        Parallel.For(0, H, py =>
        {
            var row = py * W * 3;
            var dv = py + 0.5 - H / 2.0;
            if (dv <= 0)                                                  // sky, paler toward the horizon
            {
                var k = Math.Clamp((py + 0.5) / (H / 2.0), 0, 1);
                byte r = (byte)(105 + 80 * k), g = (byte)(155 + 55 * k), b = (byte)(230 + 10 * k);
                for (var px = 0; px < W; px++) { rgb[row + px * 3] = r; rgb[row + px * 3 + 1] = g; rgb[row + px * 3 + 2] = b; }
                return;
            }
            var zc = f * cz / dv;                                         // the depth this row shows on the road
            var footprint = zc / f;                                       // metres across a pixel there
            for (var px = 0; px < W; px++)
            {
                var du = (px + 0.5 - W / 2.0) / f;
                double X = cx + zc * (dx + rx * du), Y = cy + zc * (dy + ry * du);
                byte r, g, b;
                if (!GuardRailPixel(cx, cy, cz, dx, dy, rx, ry, du, dv, f, zc, out r, out g, out b))
                    Ground(X, Y, footprint, out r, out g, out b);
                rgb[row + px * 3] = r; rgb[row + px * 3 + 1] = g; rgb[row + px * 3 + 2] = b;
            }
        });
    }

    // Does the ray of this pixel meet the guard rail before it meets the ground? The rail is a plane at
    // y = GuardRail, 0.95 m high: a beam above 0.35 m, posts every 4 m below it.
    private static bool GuardRailPixel(double cx, double cy, double cz, double dx, double dy, double rx, double ry,
        double du, double dv, double f, double groundDepth, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        var dirY = dy + ry * du;
        if (Math.Abs(dirY) < 1e-9) return false;
        var t = (HighwayRoad.GuardRail - cy) / dirY;
        if (t <= 0 || t >= groundDepth) return false;
        var zh = cz - t * dv / f;
        if (zh < 0 || zh > HighwayRoad.GuardRailTop) return false;
        var xh = cx + t * (dx + rx * du);
        var post = ((xh % 4) + 4) % 4 < 0.12;
        if (zh < HighwayRoad.GuardRailBeamBottom)
        {
            if (!post) return false;                                      // under the beam, between the posts: the ground behind
            r = g = b = 62; return true;
        }
        var stripe = ((int)Math.Floor(zh * 28)) % 2 == 0 ? 0 : -14;       // the corrugation of the beam
        var shine = zh > 0.88 ? 38 : 0;
        var v = post ? 120 : 168 + stripe + shine;
        r = (byte)v; g = (byte)(v + 1); b = (byte)(v + 4);
        return true;
    }

    private static void Ground(double X, double Y, double footprint, out byte r, out byte g, out byte b)
    {
        var lo = HighwayRoad.RightEdge - HighwayRoad.RightShoulder;
        var n = (int)((((long)Math.Floor(X * 3) * 73856093L) ^ ((long)Math.Floor(Y * 3) * 19349663L)) & 15);
        if (Y < lo || Y > HighwayRoad.GuardRail) { r = (byte)(58 + n / 2); g = (byte)(118 + n); b = 52; return; }   // grass
        var shoulder = Y < HighwayRoad.RightEdge || Y > HighwayRoad.LeftEdge;
        double v = (shoulder ? 108 : 86) + (n & 7);
        var hw = Math.Max(0.075, 0.5 * footprint);                        // a line stays visible, fainter, when it is thinner than a pixel
        var cover = 0.075 / hw;
        var white = 0.0;
        if (Math.Abs(Y - HighwayRoad.RightEdge) < hw || Math.Abs(Y - HighwayRoad.LeftEdge) < hw) white = cover;
        else if (Math.Abs(Y - HighwayRoad.LaneWidth / 2) < hw && ((X % 16) + 16) % 16 < 4) white = cover;      // dashes of 4 m, every 16
        v += (235 - v) * white;
        r = g = b = (byte)v;
    }

    // ------------------------------------------------------------------------ the flank of a vehicle
    private static void Flank(byte[] rgb, int W, int H, double f, double cx, double cy, double cz,
        double dx, double dy, double rx, double ry, HighwayVehicle v, double yp, double x0, double x1)
    {
        double z0 = (x0 - cx) * dx + (yp - cy) * dy, z1 = (x1 - cx) * dx + (yp - cy) * dy;
        double xr0 = (x0 - cx) * rx + (yp - cy) * ry, xr1 = (x1 - cx) * rx + (yp - cy) * ry;
        if (z0 < 0.5 || z1 < 0.5) return;
        double u0 = W / 2.0 + f * xr0 / z0, u1 = W / 2.0 + f * xr1 / z1;
        int from = Math.Max(0, (int)Math.Ceiling(Math.Min(u0, u1))), to = Math.Min(W - 1, (int)Math.Floor(Math.Max(u0, u1)));
        double dz = z1 - z0, dxr = xr1 - xr0;
        var paint = v.Paint;
        for (var u = from; u <= to; u++)
        {
            var du = u + 0.5 - W / 2.0;
            var den = du * dz - f * dxr;
            if (Math.Abs(den) < 1e-9) continue;
            var sAlong = (f * xr0 - du * z0) / den;                       // 0 at the end nearer the camera, 1 at the other
            if (sAlong < 0 || sAlong > 1) continue;
            var z = z0 + sAlong * dz;
            double vTop = H / 2.0 - f * (v.Height - cz) / z, vBot = H / 2.0 + f * cz / z;
            int y0 = Math.Max(0, (int)Math.Floor(vTop)), y1 = Math.Min(H - 1, (int)Math.Ceiling(vBot));
            for (var y = y0; y <= y1; y++)
            {
                var vv = (y + 0.5 - vTop) / (vBot - vTop);
                if (vv < 0 || vv > 1) continue;
                double r, g, b;
                if (v.Truck)
                {
                    var k = 0.86 - 0.22 * vv;
                    (r, g, b) = (paint.R * k, paint.G * k, paint.B * k);
                    if (vv > 0.86) (r, g, b) = (38, 38, 42);
                    else if (vv > 0.78 && (Math.Abs(sAlong - 0.1) < 0.045 || (sAlong > 0.38 && ((sAlong - 0.38) % 0.14) < 0.07))) (r, g, b) = (22, 22, 24);
                    else if (vv > 0.42 && vv < 0.46) (r, g, b) = (r * 0.6, g * 0.6, b * 0.6);
                }
                else
                {
                    var k = 0.80 - 0.35 * vv;
                    (r, g, b) = (paint.R * k, paint.G * k, paint.B * k);
                    if (vv > 0.62 && vv < 0.70) (r, g, b) = (r * 0.5, g * 0.5, b * 0.5);
                    if (vv > 0.10 && vv < 0.40 && sAlong > 0.28 && sAlong < 0.72) { var gl = 0.8 + 0.6 * (1 - vv); (r, g, b) = (40 * gl, 55 * gl, 70 * gl); }
                    if (vv > 0.70 && (Math.Abs(sAlong - 0.18) < 0.075 || Math.Abs(sAlong - 0.82) < 0.075)) (r, g, b) = (22, 22, 24);
                }
                var at = (y * W + u) * 3;
                rgb[at] = (byte)Math.Clamp(r, 0, 255); rgb[at + 1] = (byte)Math.Clamp(g, 0, 255); rgb[at + 2] = (byte)Math.Clamp(b, 0, 255);
            }
        }
    }

    // ------------------------------------------------------------------------ resolution, grain, blur
    private static byte[] Reduce(byte[] src, int W, int H, int k)
    {
        if (k <= 1) return src;
        int w = W / k, h = H / k, n = k * k;
        var dst = new byte[w * h * 3];
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
                for (var ch = 0; ch < 3; ch++)
                {
                    var sum = 0;
                    for (var j = 0; j < k; j++)
                        for (var i = 0; i < k; i++) sum += src[((y * k + j) * W + x * k + i) * 3 + ch];
                    dst[(y * w + x) * 3 + ch] = (byte)(sum / n);
                }
        });
        return dst;
    }

    // The grain and the 3 x 3 blur of a real camera, at the size the picture has been given.
    private static void Finish(byte[] rgb, int w, int h, Random grain)
    {
        for (var k = 0; k < rgb.Length; k += 3)
        {
            var n = grain.Next(-3, 4);
            for (var c = 0; c < 3; c++) rgb[k + c] = (byte)Math.Clamp(rgb[k + c] + n, 0, 255);
        }
        var src = (byte[])rgb.Clone();
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
                for (var c = 0; c < 3; c++)
                {
                    int sum = 0, weight = 0;
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            int yy = y + dy, xx = x + dx;
                            if (yy < 0 || yy >= h || xx < 0 || xx >= w) continue;
                            var wt = dx == 0 && dy == 0 ? 4 : (dx == 0 || dy == 0 ? 2 : 1);
                            sum += src[(yy * w + xx) * 3 + c] * wt; weight += wt;
                        }
                    rgb[(y * w + x) * 3 + c] = (byte)(sum / weight);
                }
        });
    }
}

// THE ENDS OF VEHICLES the existing renderer does not draw: the front of a car (seen from behind it) and
// either end of a truck. Drawn in a box, as the rear of a car is, in coordinates u and v from 0 to 1.
internal static class Shapes
{
    private static void Fill(int left, int top, int cw, int ch, Func<double, double, bool> inside,
        Func<double, double, (double R, double G, double B)> colour, Action<int, int, int, int, int> put)
    {
        for (var y = top - 2; y < top + ch + ch / 10 + 2; y++)
            for (var x = left - cw / 10; x < left + cw + cw / 10; x++)
            {
                double u = (x - left + 0.5) / cw, v = (y - top + 0.5) / ch;
                if (!inside(u, v)) continue;
                var (r, g, b) = colour(u, v);
                put(x, y, (int)r, (int)g, (int)b);
            }
    }

    private static (double, double, double) Shade((byte R, byte G, byte B) c, double k) => (c.R * k, c.G * k, c.B * k);
    private static (double, double, double) Plain(int r, int g, int b) => (r, g, b);

    public static void CarFront(int left, int top, int cw, int ch, (byte R, byte G, byte B) paint, Action<int, int, int, int, int> put)
    {
        (byte, byte, byte) glass = (40, 55, 70), bumper = (60, 60, 64);
        Fill(left, top, cw, ch, (u, v) => v > 0.93 && v < 1.06 && u > -0.06 && u < 1.06, (u, v) => Plain(35, 35, 38), put);                           // shadow
        Fill(left, top, cw, ch, (u, v) => v > 0.8 && v < 1.0 && ((u > 0.04 && u < 0.2) || (u > 0.8 && u < 0.96)), (u, v) => Plain(22, 22, 24), put);   // tyres
        Fill(left, top, cw, ch, (u, v) => v > 0.40 && v < 0.88 && u > 0 && u < 1, (u, v) => Shade(paint, 1.15 - 0.45 * v - 0.25 * Math.Abs(u - 0.5)), put);   // body
        Fill(left, top, cw, ch, (u, v) => v >= 0 && v <= 0.40 && u > 0.14 - 0.1 * v / 0.40 && u < 0.86 + 0.1 * v / 0.40, (u, v) => Shade(paint, 1.2 - 0.3 * v), put);   // cabin
        Fill(left, top, cw, ch, (u, v) => v > 0.05 && v < 0.37 && u > 0.19 - 0.08 * v / 0.37 && u < 0.81 + 0.08 * v / 0.37,
             (u, v) => Shade(glass, 0.7 + 0.9 * (1 - v) * (u < 0.55 ? 1.0 : 0.6)), put);                                                              // windscreen
        Fill(left, top, cw, ch, (u, v) => v > 0.46 && v < 0.58 && ((u > 0.03 && u < 0.26) || (u > 0.74 && u < 0.97)), (u, v) => Plain(235, 232, 205), put);   // headlights
        Fill(left, top, cw, ch, (u, v) => v > 0.50 && v < 0.54 && ((u > 0.05 && u < 0.24) || (u > 0.76 && u < 0.95)), (u, v) => Plain(255, 250, 235), put);   // daytime lights
        Fill(left, top, cw, ch, (u, v) => v > 0.60 && v < 0.74 && u > 0.30 && u < 0.70, (u, v) => Plain(28, 28, 32), put);                              // grille
        Fill(left, top, cw, ch, (u, v) => Math.Abs(v - 0.66) < 0.012 && u > 0.31 && u < 0.69, (u, v) => Plain(150, 150, 156), put);                    // its chrome bar
        Fill(left, top, cw, ch, (u, v) => v > 0.75 && v < 0.88 && u > 0 && u < 1, (u, v) => Shade(bumper, 1.1 - 0.4 * (v - 0.75) / 0.13), put);         // bumper
        Fill(left, top, cw, ch, (u, v) => v > 0.77 && v < 0.85 && u > 0.40 && u < 0.60, (u, v) => Plain(235, 235, 225), put);                           // plate
        Fill(left, top, cw, ch, (u, v) => Math.Abs(v - 0.40) < 0.012 && u > 0.08 && u < 0.92, (u, v) => Shade(paint, 0.6), put);                        // bonnet line
    }

    // A truck seen from behind (front = false) or from ahead (front = true): a tall box, dark underneath.
    public static void TruckEnd(int left, int top, int cw, int ch, (byte R, byte G, byte B) paint, bool front, Action<int, int, int, int, int> put)
    {
        Fill(left, top, cw, ch, (u, v) => v > 0.96 && v < 1.03 && u > -0.04 && u < 1.04, (u, v) => Plain(35, 35, 38), put);                            // shadow
        Fill(left, top, cw, ch, (u, v) => v > 0.80 && v < 1.0 && ((u > 0.04 && u < 0.2) || (u > 0.8 && u < 0.96)), (u, v) => Plain(22, 22, 24), put);   // tyres
        Fill(left, top, cw, ch, (u, v) => v > 0 && v < 0.84 && u > 0 && u < 1, (u, v) => Shade(paint, 1.05 - 0.35 * v - 0.12 * Math.Abs(u - 0.5)), put);   // box
        Fill(left, top, cw, ch, (u, v) => v >= 0.84 && v < 0.93 && u > 0 && u < 1, (u, v) => Plain(52, 52, 56), put);                                   // underrun bar
        if (front)
        {
            Fill(left, top, cw, ch, (u, v) => v > 0.06 && v < 0.40 && u > 0.10 && u < 0.90, (u, v) => Shade((40, 55, 70), 0.8 + 0.8 * (1 - v)), put);   // windscreen
            Fill(left, top, cw, ch, (u, v) => v > 0.52 && v < 0.64 && ((u > 0.04 && u < 0.2) || (u > 0.8 && u < 0.96)), (u, v) => Plain(235, 232, 205), put);   // headlights
            Fill(left, top, cw, ch, (u, v) => v > 0.52 && v < 0.78 && u > 0.26 && u < 0.74, (u, v) => Plain(34, 34, 38), put);                          // grille
            Fill(left, top, cw, ch, (u, v) => v > 0.80 && v < 0.84 && u > 0.40 && u < 0.60, (u, v) => Plain(235, 235, 225), put);                       // plate
        }
        else
        {
            Fill(left, top, cw, ch, (u, v) => v > 0.62 && v < 0.74 && ((u > 0.03 && u < 0.14) || (u > 0.86 && u < 0.97)), (u, v) => Plain(190, 25, 30), put);   // lights
            Fill(left, top, cw, ch, (u, v) => v > 0.70 && v < 0.78 && u > 0.40 && u < 0.60, (u, v) => Plain(235, 235, 225), put);                       // plate
            Fill(left, top, cw, ch, (u, v) => Math.Abs(u - 0.5) < 0.006 && v > 0.04 && v < 0.80, (u, v) => Shade(paint, 0.55), put);                    // the doors' seam
        }
    }
}
