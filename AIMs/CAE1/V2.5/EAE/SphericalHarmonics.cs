using System;
using System.Numerics;

namespace Mpai.Cae.Usc.Eae;

// The real spherical harmonics and the radial functions of a sound field, for Sound Field Description and the AIMs that work in its domain. A direction is an azimuth (radians,
// anticlockwise from the x axis) and an elevation (radians above the xy plane). The harmonics are orthonormal real ones in the order ACN (index n^2 + n + m): 0 omnidirectional;
// 1, 2, 3 the y, z and x dipoles of order one, and so on.
public static class SphericalHarmonics
{
    public static int Count(int order) => (order + 1) * (order + 1);

    // The order a microphone array of this many microphones can resolve: the greatest N with (N + 1)^2 microphones or fewer, at most four.
    public static int OrderOf(int microphones) => Math.Clamp((int)Math.Floor(Math.Sqrt(microphones)) - 1, 0, 4);

    public static double[] Real(int order, double azimuth, double elevation)
    {
        var x = Math.Sin(elevation);                                        // cos of the inclination
        var result = new double[Count(order)];
        for (var n = 0; n <= order; n++)
            for (var m = -n; m <= n; m++)
            {
                var am = Math.Abs(m);
                var norm = Math.Sqrt((2.0 * n + 1) / (4 * Math.PI) * Factorial(n - am) / Factorial(n + am)) * (m == 0 ? 1.0 : Math.Sqrt(2.0));
                var angular = m >= 0 ? Math.Cos(m * azimuth) : Math.Sin(am * azimuth);
                result[n * n + n + m] = norm * Legendre(n, am, x) * angular;
            }
        return result;
    }

    // The associated Legendre function P_n^m(x), without the Condon-Shortley phase.
    private static double Legendre(int n, int m, double x)
    {
        var pmm = 1.0;
        if (m > 0) { var s = Math.Sqrt(Math.Max(0, (1 - x) * (1 + x))); var f = 1.0; for (var i = 1; i <= m; i++) { pmm *= f * s; f += 2; } }
        if (n == m) return pmm;
        var pm1 = x * (2 * m + 1) * pmm;
        if (n == m + 1) return pm1;
        double pll = 0;
        for (var l = m + 2; l <= n; l++) { pll = (x * (2 * l - 1) * pm1 - (l + m - 1) * pmm) / (l - m); pmm = pm1; pm1 = pll; }
        return pll;
    }

    private static double Factorial(int n) { double f = 1; for (var i = 2; i <= n; i++) f *= i; return f; }

    // The spherical Bessel function j_n(x): the series where x is small, the recurrence upwards where it is large.
    public static double Bessel(int n, double x)
    {
        if (x < 1e-12) return n == 0 ? 1 : 0;
        if (x <= 8)
        {
            double doubleFactorial = 1; for (var i = 2 * n + 1; i > 1; i -= 2) doubleFactorial *= i;
            double sum = 1, term = 1;
            for (var s = 1; s < 40; s++) { term *= -x * x / 2 / (s * (2.0 * n + 2 * s + 1)); sum += term; if (Math.Abs(term) < 1e-16 * Math.Abs(sum)) break; }
            return Math.Pow(x, n) / doubleFactorial * sum;
        }
        double j0 = Math.Sin(x) / x, j1 = Math.Sin(x) / (x * x) - Math.Cos(x) / x;
        if (n == 0) return j0; if (n == 1) return j1;
        for (var k = 1; k < n; k++) { var next = (2.0 * k + 1) / x * j1 - j0; j0 = j1; j1 = next; }
        return j1;
    }

    // The radial function of an open sphere: b_n(kr) = 4 pi i^n j_n(kr).
    public static Complex Radial(int n, double kr) => 4 * Math.PI * Complex.Pow(Complex.ImaginaryOne, n) * Bessel(n, kr);

    // Solve A x = b for a small complex system (Gaussian elimination with partial pivoting).
    public static Complex[] Solve(Complex[,] a, Complex[] b)
    {
        var n = b.Length; var m = (Complex[,])a.Clone(); var x = (Complex[])b.Clone();
        for (var c = 0; c < n; c++)
        {
            var pivot = c; for (var r = c + 1; r < n; r++) if (m[r, c].Magnitude > m[pivot, c].Magnitude) pivot = r;
            if (m[pivot, c].Magnitude < 1e-300) throw new InvalidOperationException("The system of the decomposition is singular.");
            if (pivot != c) { for (var k = 0; k < n; k++) (m[c, k], m[pivot, k]) = (m[pivot, k], m[c, k]); (x[c], x[pivot]) = (x[pivot], x[c]); }
            for (var r = c + 1; r < n; r++) { var f = m[r, c] / m[c, c]; for (var k = c; k < n; k++) m[r, k] -= f * m[c, k]; x[r] -= f * x[c]; }
        }
        for (var r = n - 1; r >= 0; r--) { var s = x[r]; for (var k = r + 1; k < n; k++) s -= m[r, k] * x[k]; x[r] = s / m[r, r]; }
        return x;
    }
}
