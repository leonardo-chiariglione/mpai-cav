namespace Mpai.Cav.Recordings;

// THE CAMERA OF THE SYNTHETIC CAV: a pinhole camera 1.5 m above the road, 640 x 360,
// a focal length of 500 pixels, the horizon at row 140, looking along a road of
// three lanes of 3.5 m, the ego in the middle one. Shared by the drives (Phase 6
// and 7) and the simulation (Phase 8), so that the ESS sees the same camera.
public static class CameraRenderer
{
    public const int Width = 640, Height = 360;
    public const double Focal = 500, CameraHeight = 1.5, Horizon = 140, Lane = 3.5, RoadHalf = 1.5 * Lane;

    public static readonly (byte R, byte G, byte B) Silver = (150, 150, 158), DarkRed = (120, 30, 35), Blue = (40, 70, 140), White = (220, 220, 225);

    // What a pinhole camera 1.5 m above the road sees: sky, grass, three lanes and
    // their lines, dashes moving with the ego, and the rear of each vehicle - 1.8 m
    // wide, 1.45 m high - at its distance and lateral offset, shaded as light falls
    // on a car, the farthest drawn first. Then the grain and the blur of a real
    // camera, without which a detector sees a drawing. Each vehicle's box on the
    // image is returned, in the order given; an empty box where it is not in view.
    // A detector takes the rear drawn so for a car from 12 m to 35 m; nearer, for
    // a bench (M3221 Step 1): the drives keep the vehicle ahead beyond 12 m.
    public static ((int X, int Y, int W, int H)[] Boxes, byte[] Png) Render(double odometer, (double Distance, double Lateral, (byte R, byte G, byte B) Paint)[] vehicles, Random grain)
    {
        var rgb = new byte[Width * Height * 3];
        void Put(int px, int py, int r, int g, int b)
        {
            if (px < 0 || px >= Width || py < 0 || py >= Height) return;
            var at = (py * Width + px) * 3;
            rgb[at] = (byte)Math.Clamp(r, 0, 255); rgb[at + 1] = (byte)Math.Clamp(g, 0, 255); rgb[at + 2] = (byte)Math.Clamp(b, 0, 255);
        }

        for (var y = 0; y < Height; y++)
        {
            if (y <= Horizon) { for (var px = 0; px < Width; px++) Put(px, y, 150 - y / 3, 190 - y / 4, 235); continue; }
            var z = Focal * CameraHeight / (y - Horizon);                      // the distance this row shows
            var half = Focal * RoadHalf / z;
            var laneHalf = Focal * Lane / 2 / z;
            var line = Math.Max(1, Focal * 0.15 / z);
            var dash = ((z + odometer) % 6) < 3;
            for (var px = 0; px < Width; px++)
            {
                var off = Math.Abs(px - Width / 2.0);
                if (off > half) Put(px, y, 70, 130, 60);                                            // grass
                else if (off > half - line) Put(px, y, 235, 235, 235);                              // edge lines
                else if (dash && Math.Abs(off - laneHalf) < line / 2) Put(px, y, 235, 235, 235);    // lane lines, dashed
                else { var n = 85 + (px * 7 + y * 13) % 9; Put(px, y, n, n, n); }                   // asphalt
            }
        }

        var boxes = new (int X, int Y, int W, int H)[vehicles.Length];
        foreach (var i in Enumerable.Range(0, vehicles.Length).OrderByDescending(i => vehicles[i].Distance))
        {
            var (d, lateral, paint) = vehicles[i];
            if (d < 2) continue;
            var s = Focal / d;
            int cw = (int)Math.Round(1.8 * s), ch = (int)Math.Round(1.45 * s);
            int bottom = (int)Math.Round(Horizon + Focal * CameraHeight / d);
            int left = (int)Math.Round(Width / 2.0 + Focal * lateral / d) - cw / 2, top = bottom - ch;
            boxes[i] = (left, top, cw, ch);
            Car(left, top, cw, ch, paint, Put);
        }

        // Grain, then a blur of 3 x 3.
        for (var k = 0; k < rgb.Length; k += 3)
        {
            var n = grain.Next(-3, 4);
            for (var c = 0; c < 3; c++) rgb[k + c] = (byte)Math.Clamp(rgb[k + c] + n, 0, 255);
        }
        var blurred = new byte[rgb.Length];
        for (var y = 0; y < Height; y++)
            for (var px = 0; px < Width; px++)
                for (var c = 0; c < 3; c++)
                {
                    int sum = 0, weight = 0;
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            int yy = y + dy, xx = px + dx;
                            if (yy < 0 || yy >= Height || xx < 0 || xx >= Width) continue;
                            var w = dx == 0 && dy == 0 ? 4 : (dx == 0 || dy == 0 ? 2 : 1);
                            sum += rgb[(yy * Width + xx) * 3 + c] * w; weight += w;
                        }
                    blurred[(y * Width + px) * 3 + c] = (byte)(sum / weight);
                }
        return (boxes, Png.Encode(Width, Height, blurred));
    }

    // The rear of a car in its box: shadow, tyres, a body shaded from light above,
    // the cabin tapering to the roof, the rear window with a reflection, lights,
    // indicators, the plate, the bumper, the lines of the trunk and the shoulder.
    private static void Car(int left, int top, int cw, int ch, (byte R, byte G, byte B) paint, Action<int, int, int, int, int> put)
    {
        void Fill(Func<double, double, bool> inside, Func<double, double, (double R, double G, double B)> colour)
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
        static (double, double, double) Shade((byte R, byte G, byte B) c, double k) => (c.R * k, c.G * k, c.B * k);
        static (double, double, double) Plain(int r, int g, int b) => (r, g, b);
        (byte, byte, byte) glass = (40, 55, 70), bumper = (60, 60, 64);

        Fill((u, v) => v > 0.93 && v < 1.06 && u > -0.06 && u < 1.06, (u, v) => Plain(35, 35, 38));                                   // shadow
        Fill((u, v) => v > 0.8 && v < 1.0 && ((u > 0.04 && u < 0.2) || (u > 0.8 && u < 0.96)), (u, v) => Plain(22, 22, 24));           // tyres
        Fill((u, v) => v > 0.38 && v < 0.88 && u > 0 && u < 1, (u, v) => Shade(paint, 1.15 - 0.45 * v - 0.25 * Math.Abs(u - 0.5)));   // body
        Fill((u, v) => v >= 0 && v <= 0.38 && u > 0.15 - 0.1 * v / 0.38 && u < 0.85 + 0.1 * v / 0.38, (u, v) => Shade(paint, 1.2 - 0.3 * v)); // cabin
        Fill((u, v) => v > 0.06 && v < 0.34 && u > 0.2 - 0.08 * v / 0.34 && u < 0.8 + 0.08 * v / 0.34,
             (u, v) => Shade(glass, 0.7 + 0.9 * (1 - v) * (u < 0.45 ? 1.0 : 0.6)));                                                     // rear window
        Fill((u, v) => v > 0.42 && v < 0.55 && ((u > 0.02 && u < 0.24) || (u > 0.76 && u < 0.98)), (u, v) => Plain(190, 25, 30));      // lights
        Fill((u, v) => v > 0.45 && v < 0.5 && ((u > 0.04 && u < 0.1) || (u > 0.9 && u < 0.96)), (u, v) => Plain(250, 150, 60));        // indicators
        Fill((u, v) => v > 0.62 && v < 0.72 && u > 0.39 && u < 0.61, (u, v) => Plain(235, 235, 225));                                   // plate
        Fill((u, v) => v > 0.75 && v < 0.88 && u > 0 && u < 1, (u, v) => Shade(bumper, 1.1 - 0.4 * (v - 0.75) / 0.13));                // bumper
        Fill((u, v) => Math.Abs(v - 0.58) < 0.012 && u > 0.05 && u < 0.95, (u, v) => Shade(paint, 0.55));                              // trunk line
        Fill((u, v) => Math.Abs(v - 0.38) < 0.01 && u > 0.08 && u < 0.92, (u, v) => Shade(paint, 0.6));                                // shoulder line
        Fill((u, v) => v > 0.02 && v < 0.05 && u > 0.35 && u < 0.65, (u, v) => Plain(160, 20, 25));                                    // third brake light
    }
}
